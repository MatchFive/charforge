using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CharForge.EditorTools.Data;
using CharForge.EditorTools.Util;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CharForge.EditorTools.Backends
{
    /// <summary>
    /// 本地 ComfyUI 后端（默认 http://127.0.0.1:8188）。**生产环境的首选**：
    /// 只有它同时具备 SDXL/Flux + LoRA + ControlNet + IP-Adapter，也就是"画风锁定"这件事的完整解法。
    ///
    /// 工作方式：读一份 **API 格式** 的 workflow JSON（ComfyUI 里 Settings → Enable Dev mode Options
    /// → "Save (API Format)"），把提示词/种子/尺寸补进去，走 /prompt → /history → /view 三接口取图。
    ///
    /// 节点绑定（哪个节点是正向提示词、哪个是种子）写在 workflow 同目录的 <c>*.bindings.json</c>：
    /// <code>
    /// { "positive": "6", "negative": "7", "seed": "3", "width": "5", "height": "5", "load_image": "10" }
    /// </code>
    /// 绑定文件缺失时会退化为启发式：找出 class_type 含 <c>TextEncode</c> 的节点，按 id 排序取前两个。
    ///
    /// **提示词字段名不能写死。** SD1.5/SDXL 的 <c>CLIPTextEncode</c> 用 <c>text</c>，且正负是两个节点；
    /// 而 Qwen-Image 2.1 的 <c>TextEncodeQwenImage21</c> 用 <c>prompt</c> / <c>negative_prompt</c>，
    /// 正负在**同一个节点**上。字段名对不上时写进去的键不存在，提示词等于没写而且不报错，
    /// 表现成"出图跟角色描述无关"。因此：
    /// <list type="bullet">
    ///   <item>绑定文件可用 <c>positive_input</c> / <c>negative_input</c> 显式指定字段名；</item>
    ///   <item>不指定时按 <c>text → prompt → prompt_text</c>（负向：<c>text → negative_prompt → prompt</c>）
    ///     逐个探测，命中即写。</item>
    /// </list>
    /// 启发式只保证"能跑"，要精确请写绑定文件——这比在 C# 里猜节点语义可靠得多。
    /// </summary>
    public class ComfyUiBackend : IInferenceBackend
    {
        public string BaseUrl = "http://127.0.0.1:8188";

        /// <summary>API 格式 workflow 的绝对路径。为空时视为未配置。</summary>
        public string WorkflowApiJsonPath = "";

        /// <summary>单帧超时（秒）。ComfyUI 冷启动 + SDXL 20 步 / 1024px 通常 20-60 秒。</summary>
        public int FrameTimeoutSeconds = 240;

        // 关键：显式禁用系统代理。Unity 的 HttpClient 默认沿用 Windows 系统代理，
        // 而本插件只与本地 ComfyUI（127.0.0.1:8188）通信——走代理的实测后果是：
        // 普通 JSON 请求（/prompt、/history）能被代理正常转发，multipart 文件上传
        // （/upload/image）却被代理重置连接，姿势骨架图永远传不上去，
        // 随后 /prompt 校验 LoadImage 时报 "Invalid image file" 的 400。
        private static readonly HttpClient Http = new HttpClient(new HttpClientHandler
        {
            UseProxy = false
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        public string Id => "comfyui";
        public string DisplayName => "本地 ComfyUI（质量最高）";
        public BackendKind Kind => BackendKind.ComfyUI;
        public int Priority => 100;
        public BackendCapability Capabilities => BackendCapability.FrameGeneration;

        // ────────────────────────── 告警收集 ──────────────────────────
        // 「姿势模板缺失」这类问题本后端知道、流水线不知道，却直接决定产出能不能采信。
        // 同一句话只报一次——16 帧重复 16 遍会把窗口日志冲掉。
        private readonly List<string> _warnings = new List<string>();
        private readonly HashSet<string> _warnedOnce = new HashSet<string>();
        private bool _workflowPathLogged;

        private void Warn(string message)
        {
            if (_warnedOnce.Add(message))
            {
                _warnings.Add(message);
                // 告警必须同时在控制台可见：流水线中途抛异常时 DrainWarnings 不会被调到，
                // 这里曾经只进内部列表——"上传姿势图失败"这种关键线索在控制台上毫无痕迹，
                // 用户只看到一个 400 Invalid image file，无从排查。
                UnityEngine.Debug.LogWarning($"[CharForge][告警] {message}");
            }
        }

        public IReadOnlyList<string> DrainWarnings()
        {
            var copy = _warnings.ToArray();
            _warnings.Clear();
            _warnedOnce.Clear();
            return copy;
        }

        // ────────────────────────── 探测 ──────────────────────────

        public async Task<BackendStatus> ProbeAsync(CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(WorkflowApiJsonPath) || !File.Exists(WorkflowApiJsonPath))
            {
                return BackendStatus.Fail(
                    "未配置 ComfyUI 工作流",
                    "在窗口「后端设置」里指定一份 API 格式的 workflow.json，" +
                    "或在 ComfyUI 中 Settings → Enable Dev mode → Save (API Format) 导出后拖进来。" +
                    "也可以直接切换到无需配置的占位后端先跑通流程。");
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                using var resp = await Http.GetAsync($"{BaseUrl.TrimEnd('/')}/system_stats", cts.Token)
                    .ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    return BackendStatus.Fail(
                        $"ComfyUI 响应异常（HTTP {(int)resp.StatusCode}）",
                        "确认 ComfyUI 已在运行；若改过端口，请在「后端设置」里同步更新地址。");
                }
                return BackendStatus.Ok($"ComfyUI 就绪：{BaseUrl}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return BackendStatus.Fail(
                    $"3 秒内连不上 {BaseUrl}",
                    "先在终端启动 ComfyUI（python main.py），或点窗口里的「启动后端」由插件代管。");
            }
            catch (Exception e)
            {
                return BackendStatus.Fail(
                    $"连接 ComfyUI 失败：{e.Message}",
                    "确认地址与端口正确（默认 127.0.0.1:8188），必要时检查防火墙。");
            }
        }

        public Task<StyleProfile> ExtractStyleAsync(
            IReadOnlyList<string> referenceImagePaths, string presetName, CancellationToken ct)
        {
            // 风格剖析交给 Sidecar（CLIP）或本地规则预设；ComfyUI 只负责按约束出图。
            return Task.FromResult<StyleProfile>(null);
        }

        public Task<CharacterCard> DescribeAsync(JobRequest request, string jobId, CancellationToken ct)
        {
            return Task.FromResult<CharacterCard>(null);
        }

        public Task<byte[]> MattingAsync(byte[] pngBytes, CancellationToken ct)
        {
            // ComfyUI 侧当然可以加 rembg 节点，但那要求用户在 workflow 里连好；
            // 这里返回 null 让流水线走本地抠图兜底，避免"必须改 workflow 才能跑"。
            return Task.FromResult<byte[]>(null);
        }

        // ────────────────────────── 出图 ──────────────────────────

        public async Task<byte[]> GenerateFrameAsync(FrameRequest request, CancellationToken ct)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (!File.Exists(WorkflowApiJsonPath))
                throw new FileNotFoundException($"找不到 ComfyUI 工作流：{WorkflowApiJsonPath}");

            var bindings = WorkflowBindings.Load(WorkflowApiJsonPath);

            // 每次任务打一次实际加载的工作流路径：bindings 失效的常见原因就是
            // 这里指向了一份旁边没有 .bindings.json 的拷贝，不写出来无从核对。
            if (!_workflowPathLogged)
            {
                _workflowPathLogged = true;
                UnityEngine.Debug.Log($"[CharForge] ComfyUI 工作流：{WorkflowApiJsonPath}");
            }
            var workflow = JObject.Parse(await ReadAllTextAsync(WorkflowApiJsonPath, ct).ConfigureAwait(false));

            // 绑定解析（含"找不到提示词节点"）与提示词注入都可能出问题，两者都必须说出来。
            // 这里刻意不写成 if (ok) ——"提示词没进去"的症状是出图与描述无关，
            // 看起来像模型不行，只有把原因报出来才不会被误判。
            foreach (var w in bindings.Apply(workflow, request)) Warn(w);

            await ApplyPoseTemplateAsync(workflow, bindings, request, ct).ConfigureAwait(false);

            string promptId = await SubmitAsync(workflow, ct).ConfigureAwait(false);

            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < FrameTimeoutSeconds)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(600, ct).ConfigureAwait(false);

                var image = await TryFetchOutputAsync(promptId, ct).ConfigureAwait(false);
                if (image != null) return image;
            }

            throw new TimeoutException(
                $"ComfyUI 在 {FrameTimeoutSeconds} 秒内未返回结果（prompt_id={promptId}）。" +
                "常见原因：模型过大导致显存换页、workflow 里存在未安装的自定义节点。请先在 ComfyUI 界面手动跑一次该 workflow。");
        }

        /// <summary>
        /// 把本帧的姿势骨架图送进 workflow 的 LoadImage 节点（即 ControlNet 的图像输入）。
        ///
        /// ComfyUI 的 LoadImage 读的是「它自己 input 目录下的文件名」，不是绝对路径，
        /// 所以本地文件必须先经 /upload/image 传过去再改节点上的文件名。
        ///
        /// 这条链路此前是**断的**：bindings 里解析了 load_image 却没有任何消费点，
        /// 于是无论用户准备了多少骨架图，ControlNet 拿到的永远是工作流里写死的那一张。
        /// 表面症状是「四向姿势没锁住」，实际是根本没接上——所以这里不但要接，
        /// 缺图时还必须说出来，否则同样的症状会再被误判成「模型不行」。
        /// </summary>
        private async Task ApplyPoseTemplateAsync(
            JObject workflow, WorkflowBindings bindings, FrameRequest request, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(bindings.LoadImage))
            {
                // bindings 没给 load_image 时不再静默返回：如果工作流里其实有 LoadImage 节点，
                // 说明"姿势注入链路断了"而不是"本来就不需要"——必须说出来，
                // 否则症状（Invalid image file / 姿势不生效）会被误判成模型或工作流的问题。
                foreach (var prop in workflow.Properties())
                {
                    if ((string)prop.Value?["class_type"] != "LoadImage") continue;
                    Warn(
                        "bindings 未提供 load_image（bindings 文件缺失或没写该键），" +
                        $"但工作流节点 {prop.Name} 是 LoadImage——姿势骨架图不会被注入，" +
                        "该节点将使用工作流里写死的图片（而那个文件多半不在 ComfyUI 的 input 目录，" +
                        "会直接报 Invalid image file）。请检查工作流路径旁的同名 .bindings.json。");
                    break;
                }
                return;
            }

            var inputs = workflow[bindings.LoadImage]?["inputs"];
            if (inputs == null)
            {
                Warn($"工作流里找不到 load_image 绑定的节点 {bindings.LoadImage}，姿势注入被跳过。");
                return;
            }

            if (string.IsNullOrEmpty(request.PoseTemplatePath) || !File.Exists(request.PoseTemplatePath))
            {
                // 文案只带「方向_动作」不带帧号：同系列多帧只报一次（Warn 有去重），
                // 但换方向/换动作时必须重新报——曾因文案固定成示例文件名，
                // 用户换了方向后告警被去重吞掉，看起来像"文件放了却还是缺"。
                string stem = request.SliceName ?? "frame";
                int last = stem.LastIndexOf('_');
                if (last > 0 && int.TryParse(stem.Substring(last + 1), out _))
                    stem = stem.Substring(0, last);
                string expected = Path.Combine("pose_templates", stem + "_00.png");
                Warn(
                    $"缺少姿势骨架图（本帧 {request.SliceName}，期望 {expected.Replace('\\', '/')} 这一系列）。" +
                    "本次各帧都沿用工作流里写死的那张图片——四向姿势不会被锁住。放置位置：" +
                    CharForgePaths.DescribeLookup(expected) +
                    "；命名约定与制作方法见该目录下的 README。");
                return;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(request.PoseTemplatePath);
                string uploaded = await UploadImageAsync(bytes, request.SliceName + ".png", ct)
                    .ConfigureAwait(false);
                inputs["image"] = uploaded;
                UnityEngine.Debug.Log($"[CharForge] 姿势注入 {request.SliceName} <- {Path.GetFileName(request.PoseTemplatePath)}");
            }
            catch (Exception e)
            {
                Warn($"上传姿势图失败（{e.Message}），本帧沿用工作流里写死的图片。");
            }
        }

        private async Task<string> SubmitAsync(JObject workflow, CancellationToken ct)
        {
            var payload = new JObject
            {
                ["prompt"] = workflow,
                ["client_id"] = Guid.NewGuid().ToString("N")
            };

            using var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync($"{BaseUrl.TrimEnd('/')}/prompt", content, ct)
                .ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"ComfyUI 拒绝该工作流（HTTP {(int)resp.StatusCode}）：{Truncate(body, 600)}");
            }

            var json = JObject.Parse(body);
            var id = (string)json["prompt_id"];
            if (string.IsNullOrEmpty(id))
                throw new InvalidOperationException($"ComfyUI 未返回 prompt_id：{Truncate(body, 600)}");
            return id;
        }

        private async Task<byte[]> TryFetchOutputAsync(string promptId, CancellationToken ct)
        {
            using var resp = await Http.GetAsync($"{BaseUrl.TrimEnd('/')}/history/{promptId}", ct)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var json = JObject.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            var entry = json[promptId];
            var outputs = entry?["outputs"] as JObject;
            if (outputs == null) return null;

            foreach (var node in outputs.Properties())
            {
                var images = node.Value?["images"] as JArray;
                if (images == null || images.Count == 0) continue;

                var img = images[0];
                string filename = (string)img["filename"];
                string subfolder = (string)img["subfolder"] ?? "";
                string type = (string)img["type"] ?? "output";
                if (string.IsNullOrEmpty(filename)) continue;

                string url = $"{BaseUrl.TrimEnd('/')}/view?filename={Uri.EscapeDataString(filename)}" +
                             $"&subfolder={Uri.EscapeDataString(subfolder)}&type={Uri.EscapeDataString(type)}";
                using var imgResp = await Http.GetAsync(url, ct).ConfigureAwait(false);
                if (!imgResp.IsSuccessStatusCode) continue;
                return await imgResp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }
            return null;
        }

        /// <summary>上传一张图到 ComfyUI 的 input 目录（ControlNet 姿势模板 / 参考图用）。</summary>
        public async Task<string> UploadImageAsync(byte[] pngBytes, string fileName, CancellationToken ct)
        {
            using var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(pngBytes);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            content.Add(fileContent, "image", fileName);
            content.Add(new StringContent("true"), "overwrite");

            using var resp = await Http.PostAsync($"{BaseUrl.TrimEnd('/')}/upload/image", content, ct)
                .ConfigureAwait(false);
            // 不用 EnsureSuccessStatusCode：它抛出的异常只有一句状态码，
            // 把响应体（ComfyUI 会写具体原因）一起带上，否则失败原因不可见。
            if (!resp.IsSuccessStatusCode)
            {
                string errBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                throw new HttpRequestException(
                    $"ComfyUI /upload/image 返回 HTTP {(int)resp.StatusCode}：{Truncate(errBody, 300)}");
            }
            var json = JObject.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            return (string)json["name"] ?? fileName;
        }

        private static async Task<string> ReadAllTextAsync(string path, CancellationToken ct)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";

        public void Dispose() { }

        // ────────────────────────── 节点绑定 ──────────────────────────

        private class WorkflowBindings
        {
            public string Positive;
            public string Negative;
            public string Seed;
            public string Width;
            public string Height;
            public string LoadImage;

            /// <summary>
            /// 提示词字段名。**不能写死成 text**：CLIPTextEncode（SD1.5/SDXL）用 <c>text</c>，
            /// 而 Qwen-Image 2.1 的 TextEncodeQwenImage21 用 <c>prompt</c> / <c>negative_prompt</c>，
            /// 且正负提示词挂在**同一个节点**上。写死 text 的后果是：换成 Qwen 工作流后
            /// 提示词一个字都进不去，而且不报错——本项目反复踩的就是这类"静默失效"。
            /// </summary>
            public string PositiveInput;
            public string NegativeInput;

            /// <summary>
            /// 可选：追加到正向提示词末尾的固定指令。用于多参考图工作流——插件覆写
            /// prompt 后不知道工作流里有几个参考槽位，「保持 image_1 形象、按 image_2
            /// 姿势作画」这类槽位指令只能由 bindings 提供。
            /// </summary>
            public string PromptSuffix;

            /// <summary>Load 阶段产生的告警（启发式解析不出提示词节点时）。</summary>
            private readonly List<string> _loadWarnings = new List<string>();

            private static readonly string[] PositiveKeyCandidates = { "text", "prompt", "prompt_text" };
            private static readonly string[] NegativeKeyCandidates = { "text", "negative_prompt", "prompt" };

            public static WorkflowBindings Load(string workflowPath)
            {
                string dir = Path.GetDirectoryName(workflowPath) ?? ".";
                string file = Path.GetFileName(workflowPath);

                // bindings 文件名要覆盖两种命名约定：
                //   xxx.workflow.json → xxx.bindings.json（本项目的包内配置）
                //   xxx.json          → xxx.bindings.json（ComfyUI 原生导出件）
                // 这里曾有个静默坑：只算一次 GetFileNameWithoutExtension，
                // 对 "xxx.workflow.json" 得到 "xxx.workflow"，拼出 "xxx.workflow.bindings.json"
                // ——与实际文件名差一个词，File.Exists 永远 false，bindings 静默失效，
                // load_image / prompt_suffix 整条链路断掉（2026-10-12 姿势注入 400 实测根因）。
                string plain = Path.GetFileNameWithoutExtension(file);   // xxx.workflow 或 xxx
                var candidates = new List<string> { Path.Combine(dir, plain + ".bindings.json") };
                if (plain.EndsWith(".workflow", StringComparison.OrdinalIgnoreCase))
                    candidates.Add(Path.Combine(
                        dir, plain.Substring(0, plain.Length - ".workflow".Length) + ".bindings.json"));

                foreach (string bindPath in candidates)
                {
                    if (!File.Exists(bindPath)) continue;

                    var j = JObject.Parse(File.ReadAllText(bindPath));
                    return new WorkflowBindings
                    {
                        Positive = (string)j["positive"],
                        PositiveInput = (string)j["positive_input"],
                        Negative = (string)j["negative"],
                        NegativeInput = (string)j["negative_input"],
                        Seed = (string)j["seed"],
                        Width = (string)j["width"],
                        Height = (string)j["height"],
                        LoadImage = (string)j["load_image"],
                        PromptSuffix = (string)j["prompt_suffix"]
                    };
                }

                var inferred = Infer(workflowPath);
                // bindings 文件缺失 = load_image / prompt_suffix 等高级绑定全部失效，
                // 这是「姿势注入静默失败」的根因，绝不能无声退化。
                inferred._loadWarnings.Insert(0,
                    $"未找到 bindings 文件（依次尝试：{string.Join("；", candidates)}）。" +
                    "已退化为启发式绑定：load_image（姿势骨架注入）与 prompt_suffix（槽位指令）都不会生效。" +
                    "请检查工作流旁的 .bindings.json 文件名（两种约定：xxx.bindings.json 或 xxx.workflow.bindings.json）。");
                return inferred;
            }

            /// <summary>
            /// 没有 bindings 文件时的启发式。**只保证"能跑"**，所以宁可猜得宽一点。
            ///
            /// 关键点是两类节点的形态根本不同，不能只认 CLIPTextEncode：
            /// <list type="bullet">
            ///   <item><c>CLIPTextEncode</c> —— 正向、负向各一个节点，字段都叫 <c>text</c>；</item>
            ///   <item><c>TextEncodeQwen*</c> —— 一个节点同时持有 <c>prompt</c> 与 <c>negative_prompt</c>。</item>
            /// </list>
            /// 因此按 id 排序取前两个节点，并额外识别"单节点双提示词"这种形态。
            /// 解析不出来时**要留一条告警**——否则表现就是"出图与角色描述无关"，
            /// 会被错判成模型或提示词的问题。
            /// </summary>
            private static WorkflowBindings Infer(string workflowPath)
            {
                var inferred = new WorkflowBindings();
                var textNodes = new List<(int order, string id, bool dual)>();

                try
                {
                    var wf = JObject.Parse(File.ReadAllText(workflowPath));
                    foreach (var p in wf.Properties())
                    {
                        string cls = (string)p.Value?["class_type"];
                        if (string.IsNullOrEmpty(cls) || !cls.Contains("TextEncode")) continue;

                        bool dual = p.Value?["inputs"]?["negative_prompt"] != null;
                        textNodes.Add((int.TryParse(p.Name, out var n) ? n : int.MaxValue, p.Name, dual));
                    }
                }
                catch { /* 解析失败留给调用方报出明确错误 */ }

                textNodes.Sort((a, b) => a.order.CompareTo(b.order));

                if (textNodes.Count == 0)
                {
                    inferred._loadWarnings.Add(
                        "这份 workflow 里找不到文本编码节点（class_type 含 TextEncode 的一个都没有）。" +
                        "提示词不会被写进去，出图将与角色描述无关。请确认导出的是 API 格式，" +
                        "或在同名 .bindings.json 里用 positive / negative 显式指定节点 id。");
                    return inferred;
                }

                var first = textNodes[0];
                inferred.Positive = first.id;
                inferred.PositiveInput = first.dual ? "prompt" : null;

                if (textNodes.Count > 1)
                {
                    inferred.Negative = textNodes[1].id;
                }
                else if (first.dual)
                {
                    // Qwen：正负同节点，但负向字段名与正向不同。
                    inferred.Negative = first.id;
                    inferred.NegativeInput = "negative_prompt";
                }

                return inferred;
            }

            /// <summary>把提示词/种子/尺寸写进工作流；返回需要上报的告警（可能为空）。</summary>
            public List<string> Apply(JObject workflow, FrameRequest req)
            {
                var warnings = new List<string>(_loadWarnings);

                string positiveText = string.IsNullOrEmpty(PromptSuffix)
                    ? req.PositivePrompt
                    : req.PositivePrompt + ", " + PromptSuffix;
                if (!SetText(workflow, Positive, positiveText, PositiveInput, PositiveKeyCandidates))
                {
                    warnings.Add(
                        $"没能把提示词写进节点 {Positive ?? "(未指定)"}：" +
                        "该节点既没有 text 也没有 prompt / prompt_text 字段。" +
                        "请确认 workflow 是 API 格式，或在 .bindings.json 里加 positive_input 指明字段名。");
                }

                if (!string.IsNullOrEmpty(Negative))
                {
                    SetText(workflow, Negative, req.NegativePrompt, NegativeInput, NegativeKeyCandidates);
                }

                SetValue(workflow, Seed, req.Seed >= 0 ? req.Seed : new System.Random().Next(1, int.MaxValue));
                SetValue(workflow, Width, req.Width);
                SetValue(workflow, Height, req.Height);
                return warnings;
            }

            /// <summary>
            /// 按「显式字段名 → 候选名」的顺序把文本写进节点，成功返回 true。
            ///
            /// 返回 bool 而不是无声返回，是为了让调用方能区分「写进去了」和「这个节点压根没有
            /// 提示词字段」——后者正是 Qwen 工作流配 CLIPTextEncode 假设时的实际情形。
            /// </summary>
            private static bool SetText(
                JObject wf, string nodeId, string value, string explicitKey, string[] candidates)
            {
                if (string.IsNullOrEmpty(nodeId) || value == null) return false;

                var inputs = wf[nodeId]?["inputs"];
                if (inputs == null) return false;

                if (!string.IsNullOrEmpty(explicitKey))
                {
                    if (inputs[explicitKey] == null) return false;
                    inputs[explicitKey] = value;
                    return true;
                }

                foreach (var key in candidates)
                {
                    if (inputs[key] == null) continue;
                    inputs[key] = value;
                    return true;
                }

                return false;
            }

            private static void SetValue(JObject wf, string nodeId, int value)
            {
                if (string.IsNullOrEmpty(nodeId)) return;
                var inputs = wf[nodeId]?["inputs"];
                if (inputs == null) return;

                // KSampler 的 seed 与尺寸可能在不同节点；能设哪个设哪个，设不到就保留 workflow 原值。
                if (inputs["seed"] != null) inputs["seed"] = value;
                else if (inputs["noise_seed"] != null) inputs["noise_seed"] = value;
                if (inputs["width"] != null) inputs["width"] = value;
                if (inputs["height"] != null) inputs["height"] = value;
                if (inputs["batch_size"] != null) inputs["batch_size"] = 1;
            }
        }
    }
}
