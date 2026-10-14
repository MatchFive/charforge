using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CharForge.EditorTools.Util;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CharForge.EditorTools.Services
{
    /// <summary>
    /// 姿势参考集条目。目录结构即索引：<c>pose_refs/&lt;动作&gt;/&lt;朝向&gt;/*.png</c>——
    /// 一个目录就是一组帧序列，文件名排序即播放顺序。keep_pose 是**一次一帧**的工作流，
    /// 每帧需要一张「照片级姿势参考」（视频抽帧图实测可用；骨架 stick figure 未经 SAM 验证）。
    /// </summary>
    public class PoseRefSet
    {
        public string Action;
        public string Direction;
        /// <summary>逐帧姿势参考图路径，已按文件名排序（短名优先 = 自然数字序）。</summary>
        public List<string> FramePaths = new List<string>();
    }

    /// <summary>
    /// 第 1 步的成果。**把种子一起交回调用方**，而不是留在服务层内部——留痕要用它。
    ///
    /// 立绘出图不满意时，第一件要做的事是「换一颗种子重跑」还是「改提示词」，
    /// 取决于上一次用的是哪颗种子；拿不到就只能盲试。帧动画那边同理（种子由调方给）。
    /// </summary>
    public class PortraitResult
    {
        public byte[] Png;
        public int Seed;
        /// <summary>实际送进 ComfyUI 的提示词——与界面上显示的那串是同一段字符。</summary>
        public string Prompt = "";
    }

    /// <summary>
    /// 角色资产流水线的**计算侧**：只做纯计算与 HTTP，不碰 AssetDatabase、不碰 UI。
    ///
    /// 三步各对应一个入口：
    /// - 第 1 步 立绘  = t2i 工作流（charforge_qwen21_txt2img.workflow.json），提示词由
    ///   <see cref="PromptForgeService"/> 从角色描述锻造而来；
    /// - 第 2 步 帧动画 = keep_pose 动作模仿（charforge_keepose_pose.workflow.json），
    ///   立绘 + 逐帧姿势参考 → 一次一帧；提示词由 VNCCS_PoseStudio 前端实时生成，插件不碰；
    /// - 第 3 步 装配   = 不在此类，见 <see cref="FrameNpcAssembler"/>（那部分必须主线程）。
    ///
    /// **唯一的例外**：PNG 编解码得借 Unity 的 <c>Texture2D</c>，而它是主线程 API。
    /// 本类里只有 keep_pose 收割后的 alpha 压平（<see cref="FlattenAlphaNoise"/>）碰它——
    /// 调用点经 <see cref="EditorMainThread"/> 自封送。
    ///
    /// 线程模型（本项目反复踩过）：本类内部全程 <c>ConfigureAwait(false)</c>，
    /// 于是调用方的 await 续体才可能落在别的线程上；**所有 UI 写入由窗口侧封送**。
    /// 这里产出的 <c>byte[]</c> 只是托管数组，跨线程安全。
    ///
    /// ⚠️ **本类自己也会踩这个坑**（v0.4.5 实锤）：<c>ConfigureAwait(false)</c> 的下一句
    /// 就已经在线程池上了，此时再 <c>new Texture2D</c> 会撞
    /// 「SupportsTextureFormatNative can only be called from the main thread」。
    /// 凡用到纹理 API 的步骤，一律经 <see cref="EditorMainThread"/> 送回主线程 ——
    /// **不要靠「调用方大概在主线程」蒙**。
    ///
    /// 与 ComfyUiBackend 同款的教训直接内置：HttpClient **必须禁用系统代理**
    /// （multipart 上传会被 127.0.0.1 代理重置连接，v0.3.4 实测）。
    /// </summary>
    public class CharacterAssetService
    {
        private static readonly HttpClient Http = new HttpClient(new HttpClientHandler
        {
            UseProxy = false
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        private static readonly System.Random rng = new System.Random();

        private readonly string _baseUrl;

        public CharacterAssetService(string baseUrl)
        {
            _baseUrl = string.IsNullOrWhiteSpace(baseUrl)
                ? "http://127.0.0.1:8188"
                : baseUrl.TrimEnd('/');
        }

        /// <summary>
        /// 由一把「任务钥匙」派生 stable seed。同钥匙 → 同种子（可复现），换钥匙 → 换一版。
        ///
        /// 这是**角色同一性的总开关**，不是细节：同一次任务里所有帧/所有动作必须共用一个种子。
        /// 逐次各取一个随机种子 = 每个动作长一张脸，而症状看起来像「模型不行」，
        /// 实为种子没管住（本项目在旧流水线上实锤过一次，见 <c>CharacterForgePipeline.SeedFromJobId</c>）。
        /// 钥匙由调用方给：立绘用不上（只出一张），帧动画用「角色名 + 本次时间戳」，
        /// 于是同角色各动作/朝向恒定共用、重跑一次换新的一版。
        /// </summary>
        public static int StableSeed(string key)
        {
            unchecked
            {
                int hash = 17;
                foreach (char c in key ?? string.Empty) hash = hash * 31 + c;
                return 1 + (hash & 0x7FFFFFFE);   // 落在 1..int.MaxValue，避开 0 与负数
            }
        }

        // ────────────────────────── 连通性 ──────────────────────────

        /// <summary>探测 ComfyUI 是否在线。返回一句人话——失败文案里带最可能的错因，省一轮排查。</summary>
        public async Task<string> ProbeAsync(CancellationToken ct)
        {
            // 本类的 HttpClient 超时是无限（生成任务动辄几分钟，不能有超时），
            // 所以探测必须自带 5 秒上限，否则服务不在时按钮会一直转。
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                using var resp = await Http.GetAsync($"{_baseUrl}/system_stats", timeout.Token).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    return $"有响应但状态异常：HTTP {(int)resp.StatusCode} @ {_baseUrl}"
                         + PortCollisionHint((int)resp.StatusCode, body);

                JToken json;
                try { json = JToken.Parse(body); }
                catch
                {
                    return $"应答不是 JSON，这个地址上大概不是 ComfyUI @ {_baseUrl}"
                         + PortCollisionHint(200, body);
                }
                if (json["system"]?["comfyui_version"] == null)
                    return $"应答不像 ComfyUI 的 /system_stats @ {_baseUrl}"
                         + PortCollisionHint(200, body);

                string ver = (string)json["system"]["comfyui_version"];
                return $"在线：ComfyUI {ver} @ {_baseUrl}";
            }
            catch (Exception e)
            {
                return $"连不上 {_baseUrl}（{e.Message}）——" +
                       "提示：ComfyUI Desktop 默认监听 8000，通用教程里的 8188 在本机是空的。";
            }
        }

        /// <summary>
        /// 「这个端口上应答的不是 ComfyUI」的排查提示。
        ///
        /// 依据不是猜：同一端口上**更具体的绑定优先**——某个只绑 127.0.0.1 的本地服务
        /// 与绑 0.0.0.0 的 ComfyUI 撞在同一端口时，Windows 会把本机发往 127.0.0.1 的连接
        /// 交给前者。于是插件写 127.0.0.1 打到的是那个服务（它多半没有 /upload/image 路由，
        /// 或者只有前端静态页的兜底 GET，于是回 405），而写 localhost / 本机局域网 IP 才打到 ComfyUI。
        /// 本项目实锤过：插件收到 `上传视图失败（HTTP 405）：{"detail":"Method Not Allowed"}`
        /// ——`{"detail":…}` 是 FastAPI/Starlette 的形状，aiohttp 的 ComfyUI 不会这么回。
        /// </summary>
        private string PortCollisionHint(int status, string body)
        {
            bool suspicious = status == 405 || status == 404
                              || (body != null && body.Contains("\"detail\""));
            if (!suspicious) return "";

            string seen = string.IsNullOrWhiteSpace(body) ? "" : $"，应答 {Trunc(body)}";
            return $"　← 这个地址上应答的很可能不是 ComfyUI（HTTP {status}{seen}）。" +
                   "同一端口上更具体的绑定优先：写 127.0.0.1 会打到占用该端口的本地服务，" +
                   "写 localhost 或本机局域网 IP 才打到 ComfyUI。" +
                   $"临时绕过：地址改 {SuggestAlternativeHost()}；根治：给其中一个服务换端口。";
        }

        /// <summary>把当前地址换成同端口的 localhost 写法（换绑定的最小改动）。</summary>
        private string SuggestAlternativeHost()
        {
            try
            {
                var u = new Uri(_baseUrl);
                return u.IsDefaultPort ? "http://localhost" : $"http://localhost:{u.Port}";
            }
            catch { return "http://localhost:8000"; }
        }

        // ────────────────────────── 第 1 步：角色立绘 ──────────────────────────

        /// <summary>
        /// 文生图出角色立绘。提示词由 <see cref="PromptForgeService"/> 从角色描述锻造，
        /// 也可由用户手改——本方法只把**最终提示词**送进 ComfyUI，不做任何改写。
        /// 这样「界面里看到的那条」与「真正生效的那条」永远是同一串字符。
        ///
        /// 画风由提示词里的画风前缀决定（<see cref="PromptForgeService.StylePrefix"/>），
        /// 这里不做任何风格干预——立绘与第 2 步成片的画风是解耦的，见该常量的说明。
        ///
        /// 尺寸不绑：节点 4（ResolutionSelector，1:1 / 1 MP → 1024×1024）自己算。
        /// 绑 width/height 会把节点 5 的链接换成字面量，等于逼 Qwen 用 512 出图
        /// （512×512 仅 26 万像素，低于它建议的 1 MP 下限）。
        /// </summary>
        /// <returns>图 + 本次种子 + 实际提示词。种子回交调用方用于留痕与复现。</returns>
        public async Task<PortraitResult> GeneratePortraitAsync(string prompt, Action<string> log, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(prompt))
                throw new InvalidOperationException("提示词为空——先点「锻造提示词」，或自己写一条。");

            string file = CharForgeSettings.PortraitWorkflowFile;
            var wf = await LoadWorkflowAsync(file, ct).ConfigureAwait(false);
            var bind = LoadBindings(file);
            // 立绘只出一张，不需要跨图共用种子——每次给一颗新的，不满意就再点一次。
            // 但仍然**记进日志并回交调用方**：出图不满意时，可复现是排查的第一前提。
            int seed = rng.Next(1, int.MaxValue);
            string final = prompt.Trim();
            SetNode(wf, bind.Positive, bind.PositiveInput, final);
            SetNode(wf, bind.Seed, "seed", seed);

            log?.Invoke($"生成立绘（seed {seed}，约 20-40 秒）…");
            byte[] png = await RunWorkflowAsync(wf, 600, log, ct).ConfigureAwait(false);
            return new PortraitResult { Png = png, Seed = seed, Prompt = final };
        }

        // ────────────────────────── 第 2 步：帧动画（keep_pose 动作模仿） ──────────────────────────

        private const string PoseRefRel = "comfyui/pose_refs";

        /// <summary>
        /// 枚举姿势参考库。两级查找（<see cref="CharForgePaths"/> 惯例）：用户覆盖目录优先，
        /// 包内默认补缺，同相对路径先命中者胜。目录结构：<c>pose_refs/&lt;动作&gt;/&lt;朝向&gt;/*.png</c>，
        /// 少一层不收（防止把库根目录整层误当一组）。文件名排序即帧序——先按文件名长度再按字典序，
        /// 对「k1…k9 &lt; k10」这类无前导零命名恰好等于自然数字序。
        /// </summary>
        public static List<PoseRefSet> ListPoseRefs()
        {
            var roots = new List<string>();
            string user = Path.Combine(CharForgePaths.UserRoot, PoseRefRel.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(user)) roots.Add(user);
            string pkgRoot = CharForgePaths.PackageConfigRoot;
            if (!string.IsNullOrEmpty(pkgRoot))
            {
                string pkg = Path.Combine(pkgRoot, PoseRefRel.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(pkg)) roots.Add(pkg);
            }

            var result = new List<PoseRefSet>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots)
            {
                string absRoot = Path.GetFullPath(root);
                if (!Directory.Exists(absRoot)) continue;
                foreach (string dir in Directory.EnumerateDirectories(absRoot, "*", SearchOption.AllDirectories))
                {
                    string rel = dir.Substring(absRoot.Length + 1).Replace('\\', '/');
                    string[] segs = rel.Split('/');
                    if (segs.Length != 2) continue;

                    var files = Directory.EnumerateFiles(dir, "*.png")
                        .OrderBy(f => f.Length)
                        .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (files.Count == 0) continue;
                    if (!seen.Add(rel)) continue;

                    result.Add(new PoseRefSet { Action = segs[0], Direction = segs[1], FramePaths = files });
                }
            }
            return result;
        }

        /// <summary>
        /// keep_pose 逐帧动作模仿：立绘（节点 470）+ 每帧一张姿势参考（节点 507）→ 一帧成图。
        /// 提示词由 VNCCS_PoseStudio 前端按当帧姿势**实时生成**（bindings 的 auto_prompt=true），
        /// 插件**绝不覆写**——节点 484 的 prompt 是前端链接，写成字面量会静默顶掉它（姿势全废且不报错）。
        ///
        /// 两个运行前提（缺一即失败）：
        /// ① ComfyUI 端装有 vnccs-utils 套件与 VNCCS_QI2_PoseStudioV1.1 LoRA（姿势服从度的来源）；
        /// ② **ComfyUI Desktop 前端在线**——姿势来自 SAM 3D Body 对当帧姿势图的实时估计，
        ///    估计结果要经前端同步到 3D 人偶再渲染捕获，无头后端跑不动这一步。
        ///
        /// 帧间纪律与旧路线同源：**种子由调用方传入**，一组帧共用一颗（<see cref="StableSeed"/>），
        /// 帧间差异只来自姿势参考图——方法内绝不掷骰子。
        /// </summary>
        public async Task<byte[][]> GenerateKeepPoseFramesAsync(
            PoseRefSet set, byte[] characterPng, int seed, Action<string> log, CancellationToken ct)
        {
            if (set?.FramePaths == null || set.FramePaths.Count == 0)
                throw new InvalidOperationException(
                    $"姿势参考集为空：{set?.Action}/{set?.Direction}——pose_refs 目录里至少要有一张 PNG。");

            string file = CharForgeSettings.KeepPoseWorkflowFile;
            var wf = await LoadWorkflowAsync(file, ct).ConfigureAwait(false);
            var bind = LoadBindings(file);
            if (!bind.AutoPrompt)
                throw new InvalidOperationException(
                    $"{file} 的 bindings 缺少 auto_prompt=true——keep_pose 的提示词由前端按帧生成，" +
                    "插件无处可写（写字面量只会静默顶掉前端链接）。");

            // 背面/正面朝向的步伐在 2D 投影里是前后向位移，SAM 3D 估出的肢体摆幅天然偏小
            // （背面 thigh 只有侧面的 1/3~1/4），弱信号到扩散端会被站姿先验吞掉
            // ——2026-10-14 实锤：人偶确实在跨步而成图静止。
            // 按方向放大肢体摆动角（vnccs 服务端 pose_amplify，默认 1.0 = 原行为）。
            // 注意注入点必须是 joint_rotations（旋转增量），不能是 joint_coords：
            // 前端 applyWorldKeypointImport 带 normalizeLegs，位置只有方向有效，
            // 「径向等比放大」方向不变 → 与没放大等价（v0.5.12 的教训）。
            double amplify = bind.PoseAmplifyFor(set.Direction);
            if (!string.IsNullOrEmpty(bind.PoseDataNode))
            {
                var pdToken = wf[bind.PoseDataNode]?["inputs"]?["pose_data"];
                if (pdToken != null && pdToken.Type == JTokenType.String)
                {
                    var pd = JObject.Parse((string)pdToken);
                    var export = pd["export"] as JObject ?? new JObject();
                    export["pose_amplify"] = amplify;
                    pd["export"] = export;
                    SetNode(wf, bind.PoseDataNode, "pose_data",
                        pd.ToString(Newtonsoft.Json.Formatting.None));
                    log?.Invoke($"  姿势摆动放大 {set.Direction} ×{amplify:0.##}（投影信号弱补偿）");
                }
                else
                {
                    log?.Invoke($"  ⚠ 节点 {bind.PoseDataNode} 没有 pose_data 字符串输入——摆动放大未生效");
                }
            }

            // v0.5.15：姿势复制提示词。文案与目标节点**都在 bindings 里**，插件只负责搬进去——
            // 不改第三方插件代码、不需重启 ComfyUI（工作流每次提交都重读）。
            // 覆写会顶掉 484.prompt 原本接自节点 488 的 lighting_prompt 链接，这是预期行为：
            // Qwen-Image-Edit 2.1 光靠 image_1 服从不了姿势（背面朝向尤甚），必须在提示词里
            // 显式要求复制——取证节点实锤，见 bindings 的 _note_pose_copy_prompt。
            if (!string.IsNullOrEmpty(bind.PoseCopyPrompt) && !string.IsNullOrEmpty(bind.PoseCopyPromptNode))
            {
                SetNode(wf, bind.PoseCopyPromptNode, bind.PositiveInput, bind.PoseCopyPrompt);
                log?.Invoke(
                    $"  姿势复制提示词 → 节点 {bind.PoseCopyPromptNode}.{bind.PositiveInput}" +
                    "（顶掉 PoseStudio 的 lighting_prompt 链接）");
            }

            // 立绘整组只上传一次，全部帧共用同一个 input 文件名（原样直传，不做白底预处理——
            // keep_pose 的身份槽没有 Spritely Sheet 那套版式约束）。
            string charName = await UploadAsync(characterPng, "cf_keepose_character.png", log, ct)
                .ConfigureAwait(false);
            SetNode(wf, bind.ImageCharacter, "image", charName);
            SetNode(wf, bind.Seed, "seed", seed);

            var frames = new byte[set.FramePaths.Count][];
            byte[] prevRefBytes = null;
            for (int i = 0; i < set.FramePaths.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                log?.Invoke($"  帧 {i + 1}/{set.FramePaths.Count} ← {Path.GetFileName(set.FramePaths[i])}");
                byte[] posePng = await File.ReadAllBytesAsync(set.FramePaths[i], ct).ConfigureAwait(false);
                string poseName = await UploadAsync(posePng, $"cf_keepose_pose_{i + 1:D2}.png", log, ct)
                    .ConfigureAwait(false);
                SetNode(wf, bind.ImagePose, "image", poseName);

                byte[] png = await RunKeepPoseFrameAsync(wf, log, ct).ConfigureAwait(false);
                byte[] flat = await EditorMainThread
                    .RunAsync(() => FlattenAlphaNoise(png, log))
                    .ConfigureAwait(false);

                // 竞态防线（2026-10-14 二次修正：原样重提是无效的——ComfyUI 对输入完全
                // 相同的提交直接返回缓存结果，实测 0.02s「完成」且输出文件名不变，等于
                // 没重跑。重试必须换 seed 才真正重新执行；种子共用约定只约束正常帧，
                // 疑似废帧换 seed 换来的是「真的重跑」）。
                // 对策：参考图本身差异明显而成图几乎没变 → 判定抓到旧帧，换 seed 重提（最多 2 次）。
                if (i > 0 && prevRefBytes != null)
                {
                    double refDiff = await EditorMainThread
                        .RunAsync(() => MeanFrameDiff(prevRefBytes, posePng))
                        .ConfigureAwait(false);
                    for (int attempt = 1; attempt <= 2; attempt++)
                    {
                        double frameDiff = await EditorMainThread
                            .RunAsync(() => MeanFrameDiff(frames[i - 1], flat))
                            .ConfigureAwait(false);
                        // 尺寸对不上（NaN）或参考图本身就静止（≤4）时不重试——那是姿势参考的问题，重试无解
                        if (double.IsNaN(frameDiff) || double.IsNaN(refDiff) ||
                            refDiff <= 4.0 || frameDiff >= 5.0)
                            break;
                        log?.Invoke(
                            $"  ⚠ 帧 {i + 1} 与前帧几乎无差异（{frameDiff:F1}/255，参考差 {refDiff:F1}）" +
                            $"——疑似姿势捕获竞态，第 {attempt}/2 次换 seed 自动重提");
                        SetNode(wf, bind.Seed, "seed", seed + attempt);
                        png = await RunKeepPoseFrameAsync(wf, log, ct).ConfigureAwait(false);
                        flat = await EditorMainThread
                            .RunAsync(() => FlattenAlphaNoise(png, log))
                            .ConfigureAwait(false);
                    }
                }
                frames[i] = flat;
                prevRefBytes = posePng;

                // 帧间留出前端渲染收尾的间隔，降低竞态概率（治标；上面的重试是治本兜底）。
                if (i < set.FramePaths.Count - 1)
                    await Task.Delay(2000, ct).ConfigureAwait(false);
            }

            // 出组自检：相邻帧差异过低 = 姿势没传过去（实测对照——正常走路约 16/255，
            // 站姿残骸约 3/255）。这类失败肉眼在窗口里看不出来，必须当场喊出来。
            await EditorMainThread
                .RunAsync(() => WarnIfStaticFrames(frames, set, log))
                .ConfigureAwait(false);

            log?.Invoke($"keep_pose 逐帧完成：{set.Action}/{set.Direction} 共 {frames.Length} 帧（seed {seed} 共用）");
            return frames;
        }

        /// <summary>
        /// 单帧提交，带前端同步超时的自动重试（2026-10-14 实测：walk/down 第 4 帧偶发
        /// `frontend sync timed out` 直接把整组跑崩——超时是随机的（前端 ws 短暂失联），
        /// 重试即可，不值得让用户重来一整组）。
        /// </summary>
        private async Task<byte[]> RunKeepPoseFrameAsync(JObject wf, Action<string> log, CancellationToken ct)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return await RunWorkflowAsync(wf, 900, log, ct).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("frontend sync timed out") && attempt < 3)
                {
                    log?.Invoke($"  ⚠ 前端同步超时（第 {attempt}/2 次重试前）——10 秒后自动重提该帧……");
                    await Task.Delay(10000, ct).ConfigureAwait(false);
                }
            }
        }

        /// <summary>相邻帧平均像素差（RGBA 四通道，0-255 尺度）。主线程调（Texture2D 解码）。</summary>
        private static double MeanFrameDiff(byte[] a, byte[] b)
        {
            var ta = Decode(a);
            var tb = Decode(b);
            if (ta.width != tb.width || ta.height != tb.height)
            {
                UnityEngine.Object.DestroyImmediate(ta);
                UnityEngine.Object.DestroyImmediate(tb);
                return double.NaN;
            }
            var pa = ta.GetPixels32();
            var pb = tb.GetPixels32();
            double sum = 0;
            for (int i = 0; i < pa.Length; i++)
                sum += (Mathf.Abs(pa[i].r - pb[i].r) + Mathf.Abs(pa[i].g - pb[i].g) +
                        Mathf.Abs(pa[i].b - pb[i].b) + Mathf.Abs(pa[i].a - pb[i].a)) / 4.0;
            UnityEngine.Object.DestroyImmediate(ta);
            UnityEngine.Object.DestroyImmediate(tb);
            return sum / pa.Length;
        }

        /// <summary>
        /// 批量静态帧体检：任一相邻帧差 &lt; 5/255（实测站姿残骸 ≤4、正常走路 ≥11）即告警。
        /// 只告警不抛——个别相位（如双脚触地）本来就动得少，全组静态才真正致命，交给人工判断重跑。
        /// </summary>
        private static void WarnIfStaticFrames(byte[][] frames, PoseRefSet set, Action<string> log)
        {
            if (frames == null || frames.Length < 2) return;
            var diffs = new List<double>();
            for (int i = 0; i < frames.Length - 1; i++)
                diffs.Add(MeanFrameDiff(frames[i], frames[i + 1]));

            double min = double.MaxValue, sum = 0;
            foreach (var d in diffs)
            {
                if (double.IsNaN(d)) continue;   // 尺寸不一致时跳过该对，别误报
                if (d < min) min = d;
                sum += d;
            }
            if (min == double.MaxValue) return;
            double mean = sum / diffs.Count;
            if (min < 5.0 || mean < 8.0)
                log?.Invoke(
                    $"  ⚠ 静态帧体检：{set.Action}/{set.Direction} 相邻帧平均差仅 {mean:F1}/255" +
                    $"（最低 {min:F1}；正常走路应 ≥11）——动作可能没传导到成片。按序排查：" +
                    "① 本窗口上方应有两条注入日志：`姿势摆动放大 …×2.2` 与 " +
                    "`姿势复制提示词 → 节点 484.prompt`；缺后者 → bindings 的 " +
                    "pose_copy_prompt / pose_copy_prompt_node 没配好（文案在配置里，不重启即可改）；" +
                    "② ComfyUI 日志里应有 `CharForge pose_amplify x…: N limb joints re-solved (thigh_r …deg)`；" +
                    "没有 → 姿势放大补丁没加载（重启 ComfyUI Desktop）；" +
                    "打印 `NO limb rotations matched` → 载荷结构变了，把那行发我；" +
                    "③ 确认 Desktop 前端窗口在前台（失焦/被遮挡会抓到旧帧）；" +
                    "④ 以上都正常仍静态 → 换『跨步大、双脚左右明显分开』的该朝向参考图" +
                    "（pose_refs/<动作>/<朝向>/；背面/正面步伐是前后向位移，投影信号天然弱于侧面）。");
        }

        /// <summary>
        /// 压平背景半透明噪点：alpha&lt;64 一律置 0。keep_pose 出图的背景残留实测约 44% 像素
        /// 落在 1-63 区间——肉眼是紫雾、程序上是 alpha 包围盒污染（不报错的失败家族），
        /// 必须在入装配线前清干净。顺手做一份 alpha 体检：空帧与「背景没抠开」当场现形。
        /// **只准在主线程调**（Texture2D），调用点已封送。
        /// </summary>
        private static byte[] FlattenAlphaNoise(byte[] png, Action<string> log)
        {
            var tex = Decode(png);
            int w = tex.width, h = tex.height;
            var px = tex.GetPixels32();
            int kept = 0;
            for (int i = 0; i < px.Length; i++)
            {
                if (px[i].a < 64) px[i].a = 0;
                else kept++;
            }
            tex.SetPixels32(px);
            var outPng = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);

            double ratio = 100.0 * kept / ((double)w * h);
            if (ratio < 0.5)
                log?.Invoke($"  ⚠ alpha 体检：非透明仅 {ratio:F1}%——近乎空帧，姿势参考可能没被吃进去，建议重跑");
            else if (ratio > 92)
                log?.Invoke($"  ⚠ alpha 体检：非透明 {ratio:F1}%——背景疑似没抠开，检查 ComfyUI 端输出");
            else
                log?.Invoke($"  ✓ alpha 体检：非透明 {ratio:F1}%");
            return outPng;
        }

        // ────────────────────────── ComfyUI 通信 ──────────────────────────

        private static string ResolveWorkflow(string fileName)
        {
            string path = CharForgePaths.ResolveFile("comfyui/" + fileName);
            if (path == null)
                throw new FileNotFoundException(
                    $"找不到工作流 {fileName}（期望位于 <插件包>/Configs/comfyui/ 或 <工程>/CharForge/comfyui/）");
            return path;
        }

        /// <summary>
        /// 工作流节点绑定。节点 id 与字段名**不写死在代码里**，读同目录的
        /// <c>&lt;工作流名&gt;.bindings.json</c>。好处：换底座（fp8 ↔ Nunchaku、SD1.5 ↔ Qwen）
        /// 只改 JSON，不必重编译插件——配置就该待在配置里。
        /// </summary>
        private class WorkflowBook
        {
            public string Positive = "6";
            public string PositiveInput = "prompt";
            public string Seed = "8";
            /// <summary>姿势参考图槽。</summary>
            public string ImagePose;
            /// <summary>角色参考图槽（双槽位工作流才有）。</summary>
            public string ImageCharacter;
            /// <summary>
            /// true = 提示词由工作流内部生成（VNCCS_PoseStudio 前端按帧实时产出），
            /// 插件**不写**提示词，positive 节点允许缺失。默认 false = 必须有 positive 可写。
            /// 这是防静默失效的一道闸：auto_prompt 的工作流里 positive 字段多半是**链接**，
            /// 写字面量进去会顶掉链接——不报错，只是功能整个哑掉。
            /// </summary>
            public bool AutoPrompt;
            /// <summary>pose_data 字符串所在的节点 id（keep_pose 专用；用于按方向注入 pose_amplify）。</summary>
            public string PoseDataNode;
            /// <summary>
            /// 姿势复制提示词的写入目标节点 id（keep_pose 专用）。配置了就覆写它的
            /// <see cref="PositiveInput"/> 字段，<b>顶掉原本接自 PoseStudio 的链接</b>——
            /// 这是预期行为，原因见 bindings 的 _note_pose_copy_prompt。
            /// </summary>
            public string PoseCopyPromptNode;
            /// <summary>姿势复制提示词正文（写在 bindings 里，不散落到第三方插件代码中）。</summary>
            public string PoseCopyPrompt;
            /// <summary>按方向放大肢体摆动角（背面/正面步伐投影信号弱，见 bindings 注释）。缺省方向 = 1.0。</summary>
            public Dictionary<string, double> PoseAmplifyByDirection = new Dictionary<string, double>();

            public double PoseAmplifyFor(string direction)
            {
                if (string.IsNullOrEmpty(direction)) return 1.0;
                return PoseAmplifyByDirection.TryGetValue(direction.Trim().ToLowerInvariant(), out var f)
                    ? f : 1.0;
            }
        }

        private static async Task<JObject> LoadWorkflowAsync(string fileName, CancellationToken ct)
        {
            string path = ResolveWorkflow(fileName);
            string text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException($"工作流文件是空的：{path}");
            return JObject.Parse(text);
        }

        /// <summary>
        /// 读绑定文件。**找不到就报错而不是猜节点 id**：猜错会让提示词写进一个没人读的键，
        /// 不报错、日志照常，只是出图不听话——本项目最忌讳的那种失败。
        /// </summary>
        private static WorkflowBook LoadBindings(string fileName)
        {
            string bindName = fileName.Contains(".workflow.json")
                ? fileName.Replace(".workflow.json", ".bindings.json")
                : Path.GetFileNameWithoutExtension(fileName) + ".bindings.json";

            string path = CharForgePaths.ResolveFile("comfyui/" + bindName);
            if (path == null)
                throw new FileNotFoundException(
                    $"工作流缺少绑定文件 {bindName}（与工作流同目录）。它记录提示词/种子/图像槽的节点 id；" +
                    "没有它就无法安全写入，插件不会去猜。");

            var json = JObject.Parse(File.ReadAllText(path));
            var book = new WorkflowBook
            {
                Positive = (string)json["positive"],
                PositiveInput = (string)json["positive_input"] ?? "prompt",
                Seed = (string)json["seed"],
                // 双槽位：优先新键，兼容旧的单槽位键。
                ImagePose = (string)json["load_image_pose"] ?? (string)json["load_image"],
                ImageCharacter = (string)json["load_image_character"],
                AutoPrompt = (bool?)json["auto_prompt"] == true,
                PoseDataNode = (string)json["pose_data_node"],
                PoseCopyPromptNode = (string)json["pose_copy_prompt_node"],
                PoseCopyPrompt = (string)json["pose_copy_prompt"],
            };
            if (json["pose_amplify_by_direction"] is JObject amp)
                foreach (var kv in amp.Properties())
                    book.PoseAmplifyByDirection[kv.Name] = (double?)kv.Value ?? 1.0;
            if (!book.AutoPrompt && string.IsNullOrEmpty(book.Positive))
                throw new InvalidOperationException(
                    $"{bindName} 里缺少 positive 节点 id（且未声明 auto_prompt=true）——提示词无处可写，拒绝运行。");
            if (string.IsNullOrEmpty(book.Seed))
                throw new InvalidOperationException($"{bindName} 里缺少 seed 节点 id——种子无处可写，拒绝运行。");
            return book;
        }

        /// <summary>写字符串值（提示词、图片名等）。<b>字段必须已存在</b>，否则抛错。</summary>
        private static void SetNode(JObject wf, string nodeId, string field, string value)
            => SetNode(wf, nodeId, field, new JValue(value));

        /// <summary>
        /// 写整数值（seed / steps 这类）。
        /// **走重载而不是 <c>.ToString()</c> 是刻意的**：<c>KSampler.seed</c> 在工作流 JSON 里
        /// 是数字（`"seed": 12345`），写成 `"12345"` 就变成了字符串。ComfyUI 现版本会隐式
        /// `int()` 兜住，所以照样能跑——但这属于依赖未文档化的宽容：一旦上游校验收紧，
        /// 症状是一个难查的 400，而不是一句人话。类型在写入时就落对，不留给下游猜。
        /// </summary>
        private static void SetNode(JObject wf, string nodeId, string field, int value)
            => SetNode(wf, nodeId, field, new JValue(value));

        private static void SetNode(JObject wf, string nodeId, string field, JToken value)
        {
            if (string.IsNullOrEmpty(nodeId))
                throw new InvalidOperationException(
                    "bindings 里缺少所需节点 id（双槽位工作流需要 load_image_character 与 load_image_pose）——" +
                    "检查 Configs/comfyui/*.bindings.json");

            var node = wf[nodeId] as JObject;
            if (node == null)
                throw new InvalidOperationException(
                    $"工作流里没有节点 {nodeId}——bindings 与工作流不匹配（常见于换了工作流却没换 bindings）");

            var inputs = node["inputs"] as JObject;
            if (inputs == null)
                throw new InvalidOperationException($"节点 {nodeId} 没有 inputs 段——工作流不是 API 扁平格式？");

            // 关键防线：字段必须**已存在**。往不存在的字段写值只会新增一个没人读的键，
            // 不报错、日志照常，提示词静默失效（本项目 v0.3.1 实锤过一次）。
            var old = inputs[field];
            if (old == null)
            {
                var names = new List<string>();
                foreach (var prop in inputs.Properties()) names.Add(prop.Name);
                throw new InvalidOperationException(
                    $"节点 {nodeId} 的 inputs 里没有字段「{field}」，写进去等于静默无效——已拦下。" +
                    $"该节点现有字段：{string.Join(", ", names)}");
            }

            // 第二道防线：**别把数字写成字符串**。目标字段原本是数值、却收到字符串时直接抛错。
            // 这正是上面那个 int 重载要解决的问题——在运行时再兜一层，免得以后有人
            // 图省事写 .ToString() 又把它悄悄改回 `"12345"`。
            // 只拦这一个方向（数值 ← 字符串）：反方向不拦，因为「seed 在 JSON 里就是字符串」
            // 的工作流确实存在且能正常跑，拦了会误伤。
            bool wasNumeric = old.Type == JTokenType.Integer || old.Type == JTokenType.Float;
            if (wasNumeric && value.Type != JTokenType.Integer && value.Type != JTokenType.Float)
                throw new InvalidOperationException(
                    $"节点 {nodeId} 的字段「{field}」原本是数值（{old}），却要写入字符串（{value}）——" +
                    "多半是把数字字段当成文本字段传了（seed / steps 这类必须写数字）。已拦下。");

            inputs[field] = value;
        }

        private async Task<byte[]> RunWorkflowAsync(JObject wf, int timeoutSec, Action<string> log, CancellationToken ct)
        {
            // /prompt 的 body 必须是 {"prompt": <工作流图>, "client_id": ...} 这层封装。
            // 直接把工作流图当 body 发出去，ComfyUI 在顶层找不到 prompt 键，
            // 会回 HTTP 400 no_prompt / "No prompt provided"（v0.3.6 实跑踩到）。
            // 封装格式与 ComfyUiBackend.SubmitAsync 保持一致。
            var payload = new JObject
            {
                ["prompt"] = wf,
                ["client_id"] = Guid.NewGuid().ToString("N")
            };
            var content = new StringContent(payload.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync($"{_baseUrl}/prompt", content, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"ComfyUI 拒绝工作流（HTTP {(int)resp.StatusCode}）：{Trunc(body)}");

            string promptId = (string)JObject.Parse(body)["prompt_id"];
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(1000, ct).ConfigureAwait(false);

                using var hist = await Http.GetAsync($"{_baseUrl}/history/{promptId}", ct).ConfigureAwait(false);
                var hJson = JObject.Parse(await hist.Content.ReadAsStringAsync().ConfigureAwait(false));
                var entry = hJson[promptId] as JObject;
                if (entry == null) continue;

                var status = entry["status"] as JObject;
                if (status != null && (string)status["status_str"] == "error")
                {
                    var raw = status.ToString(Newtonsoft.Json.Formatting.None);
                    // keep_pose 的姿势同步报错最常见且原文最难懂：用简洁人话替代整段 JSON。
                    // 异常文案里必须保留 "frontend sync timed out" 原文——RunKeepPoseFrameAsync
                    // 靠它在文案里识别这条错误做自动重试（之前把 JSON 截断后关键词丢失，
                    // 重试永远匹配不上，实机踩过）。
                    if (raw.Contains("frontend sync timed out"))
                        throw new InvalidOperationException(
                            "ComfyUI 执行出错：Pose image frontend sync timed out（keep_pose 姿势同步超时）——" +
                            "SAM 3D 估出的姿势要推给 ComfyUI 前端页面驱动 3D 人偶，页面被遮挡/失焦时可能无响应。" +
                            "插件将自动重试该帧；若连续失败，请让任一打开本 ComfyUI 的前端页面保持可见" +
                            "（本机 Desktop 窗口或局域网内其他电脑的浏览器都行）。");
                    throw new InvalidOperationException("ComfyUI 执行出错：" + Trunc(raw));
                }

                var outputs = entry["outputs"] as JObject;
                if (outputs == null) continue;

                foreach (var nodeOut in outputs.Properties())
                {
                    var imgs = nodeOut.Value["images"] as JArray;
                    if (imgs == null || imgs.Count == 0) continue;
                    var first = (JObject)imgs[0];
                    string file = (string)first["filename"];
                    string sub = (string)first["subfolder"] ?? "";
                    string type = (string)first["type"] ?? "output";
                    string url = $"{_baseUrl}/view?filename={Uri.EscapeDataString(file)}" +
                                 $"&subfolder={Uri.EscapeDataString(sub)}&type={type}";
                    // Unity 的 .NET Standard 2.1 下 GetByteArrayAsync 只有单参数重载，
                    // 这里用 GetAsync(ct) + ReadAsByteArrayAsync() 保留取消能力。
                    using var imgResp = await Http.GetAsync(url, ct).ConfigureAwait(false);
                    if (!imgResp.IsSuccessStatusCode)
                        throw new InvalidOperationException(
                            $"取图失败（HTTP {(int)imgResp.StatusCode}）：{file}");
                    var bytes = await imgResp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    log?.Invoke($"  完成 ← {file}");
                    return bytes;
                }
            }
            throw new TimeoutException($"ComfyUI 生成超时（{timeoutSec}s）——若首次运行还在加载模型，重试一次即可。");
        }

        private async Task<string> UploadAsync(byte[] png, string fileName, Action<string> log, CancellationToken ct)
        {
            using var form = new MultipartFormDataContent();
            var imgContent = new ByteArrayContent(png);
            imgContent.Headers.TryAddWithoutValidation("Content-Type", "image/png");
            form.Add(imgContent, "image", fileName);
            form.Add(new StringContent("true"), "overwrite");

            using var resp = await Http.PostAsync($"{_baseUrl}/upload/image", form, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"上传视图失败（HTTP {(int)resp.StatusCode}）：{Trunc(body)}"
                    + PortCollisionHint((int)resp.StatusCode, body));
            return (string)JObject.Parse(body)["name"];
        }

        private static string Trunc(string s) =>
            string.IsNullOrEmpty(s) || s.Length <= 400 ? s : s.Substring(0, 400) + "…";

        // ────────────────────────── 图像工具 ──────────────────────────

        internal static Texture2D Decode(byte[] png)
        {
            // 一处守卫罩住本类全部纹理入口：越界时抛的是真话，
            // 而不是 Unity 那句让人以为「构造器 / 字段初始化写错了」的样板文案。
            EditorMainThread.EnsureMainThread("CharacterAssetService.Decode（Texture2D 解码）");
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(png))
                throw new InvalidOperationException("PNG 解码失败");
            return tex;
        }
    }
}
