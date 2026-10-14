using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CharForge.EditorTools.Services;
using CharForge.EditorTools.Util;
using UnityEditor;
using UnityEngine;

namespace CharForge.EditorTools.Cli
{
    /// <summary>
    /// 无头命令行入口，把「勘察 / 装配 / 自检 / 挂脚本」交给外部程序驱动。
    ///
    /// 存在的理由：窗口只能人来点，而这三件事都是**确定性**的——帧 PNG 在磁盘上，
    /// 装配就是照着它算 pivot、写片段、出 prefab。确定性步骤不该由人守着。
    /// 有了这个入口，外部程序（Agent / CI / 批处理脚本）就能把
    /// 「人设 → 立绘 → 帧 → 装配 → 自检」整条链跑完，只在真正需要判断的地方才找人。
    ///
    /// 调用形态：
    /// <code>
    /// Unity.exe -batchmode -projectPath &lt;工程&gt; ^
    ///   -executeMethod CharForge.EditorTools.Cli.CharForgeCli.Run -cmd assemble -character yunshu
    /// </code>
    ///
    /// **不要加 <c>-quit</c>。** 加了之后 Unity 会在本方法返回后立刻退出，
    /// 并把进程退出码固定成 0——于是失败会被读成成功，这是最坏的一类错。
    /// 退出码统一由 <see cref="Finish"/> 显式给出：0 成功 / 1 失败 / 2 用法错。
    ///
    /// 另一条设计约束：**只读磁盘上的产物，不读界面内存**。
    /// 所以 Unity 刚重启、窗口从没打开过，装配照样能跑——`Frames/` 里有 PNG 就够。
    /// </summary>
    public static class CharForgeCli
    {
        private const int ExitOk = 0;
        private const int ExitFail = 1;
        private const int ExitUsage = 2;

        /// <summary>Unity 的 <c>-executeMethod</c> 目标。别名见 <see cref="Main"/>。</summary>
        public static void Run()
        {
            var args = ParseArgs(Environment.GetCommandLineArgs());
            string cmd = (Arg(args, "cmd") ?? "").Trim().ToLowerInvariant();

            int code;
            try
            {
                switch (cmd)
                {
                    case "env": code = CmdEnv(); break;
                    case "scan": code = CmdScan(args); break;
                    case "import": code = CmdImport(args); break;
                    case "assemble": code = CmdAssemble(args); break;
                    case "verify": code = CmdVerify(args); break;
                    case "attach": code = CmdAttach(args); break;
                    default:
                        PrintUsage(cmd);
                        code = string.IsNullOrEmpty(cmd) ? ExitUsage : ExitFail;
                        break;
                }
            }
            catch (Exception e)
            {
                // 异常必须转成非零退出码：外部程序只看得到退出码和日志。
                Debug.LogError($"[CharForge CLI] 命令 '{cmd}' 异常终止：{e.GetType().Name}: {e.Message}");
                Debug.LogError(e.StackTrace);
                code = ExitFail;
            }

            Finish(code);
        }

        /// <summary>与 <see cref="Run"/> 同一个入口，保留给「用方法名表达命令」的调用方。</summary>
        public static void Main() => Run();

        // ────────────────────────── 命令：env ──────────────────────────

        /// <summary>
        /// 打印本次运行的环境事实。**先跑它再跑别的**——
        /// 代理地址、工作流文件名、产物目录、姿势参考库这些都不该靠猜，
        /// 让机器把事实说出来，比在任何一端假设都便宜。
        /// </summary>
        private static int CmdEnv()
        {
            var sb = new StringBuilder();
            sb.AppendLine("CHARFORGE_ENV_BEGIN");
            sb.AppendLine($"project_root={CharForgePaths.ProjectRoot}");
            sb.AppendLine($"user_root={CharForgePaths.UserRoot}");
            sb.AppendLine($"package_config_root={CharForgePaths.PackageConfigRoot ?? "(未解析)"}");
            sb.AppendLine($"comfyui_url={CharForgeSettings.ComfyUiUrl}");
            sb.AppendLine($"portrait_workflow={CharForgeSettings.PortraitWorkflowFile}");
            sb.AppendLine($"keepose_workflow={CharForgeSettings.KeepPoseWorkflowFile}");
            sb.AppendLine($"output_folder={CharForgeSettings.OutputAssetFolder}");
            sb.AppendLine($"sprite_frame_rate={CharForgeSettings.SpriteFrameRate}");
            sb.AppendLine($"pose_refs_lookup={CharForgePaths.DescribeLookup("comfyui/pose_refs")}");

            var refs = CharacterAssetService.ListPoseRefs();
            sb.AppendLine($"pose_refs_total={refs.Count}");
            foreach (var set in refs)
                sb.AppendLine($"pose_ref={set.Action}/{set.Direction} frames={set.FramePaths.Count}");

            sb.AppendLine("CHARFORGE_ENV_END");
            Debug.Log(sb.ToString());
            return ExitOk;
        }

        // ────────────────────────── 命令：scan ──────────────────────────

        /// <summary>列出产物目录下每个角色的就绪状态（帧组、片段、prefab、人设）。</summary>
        private static int CmdScan(Dictionary<string, List<string>> args)
        {
            string folder = Arg(args, "folder", CharForgeSettings.OutputAssetFolder);
            string absRoot = AbsPath(folder);
            if (!Directory.Exists(absRoot))
            {
                Debug.Log($"[CharForge CLI] 产物目录不存在（还没有任何角色）：{absRoot}");
                Debug.Log("CHARFORGE_SCAN_BEGIN\nCHARFORGE_SCAN_END");
                return ExitOk;
            }

            var sb = new StringBuilder();
            sb.AppendLine("CHARFORGE_SCAN_BEGIN");
            int chars = 0;
            foreach (var dir in Directory.GetDirectories(absRoot))
            {
                string name = Path.GetFileName(dir);
                if (name.StartsWith(".", StringComparison.Ordinal)) continue;
                chars++;

                string rel = $"{folder.TrimEnd('/')}/{name}";
                var groups = ReadFrameGroups(dir, out int totalFrames);
                bool prefab = File.Exists(Path.Combine(dir, $"NPC_{name}.prefab"));
                bool controller = File.Exists(Path.Combine(dir, "Animations", name + ".controller"));
                bool persona = File.Exists(Path.Combine(dir, "persona.json"));

                sb.AppendLine($"character={name} dir={rel} groups={groups.Count} frames={totalFrames} " +
                              $"prefab={(prefab ? "yes" : "no")} controller={(controller ? "yes" : "no")} " +
                              $"persona={(persona ? "yes" : "no")}");
                foreach (var g in groups)
                    sb.AppendLine($"  group={g.Name} frames={g.Frames} anim_keys={g.AnimKeys} " +
                                  $"ok={(g.Ok ? "yes" : "no")}");
            }
            sb.AppendLine($"characters={chars}");
            sb.AppendLine("CHARFORGE_SCAN_END");
            Debug.Log(sb.ToString());
            return ExitOk;
        }

        // ────────────────────────── 命令：import ──────────────────────────

        /// <summary>
        /// 把**外部程序生成好的帧 PNG**导入成插件标准帧（落进 `Frames/&lt;动作&gt;_&lt;朝向&gt;/`）。
        ///
        /// **为什么这一步必须由插件做，不能让外部程序自己缩放了事**：
        /// 帧不是"缩到 512 就行"，它要经过 `FrameNormalizer` ——
        /// 按 alpha 裁切 → **全批共用一个 scale** → 放进统一画布 → 脚底居中锚定。
        /// "全批共用一个 scale"是其中的要害：逐帧自适应缩放会让角色**走路时忽大忽小**，
        /// 而这看起来像"模型不稳定"，很难倒查。外部程序复刻这套规则 = 造第二份真相，
        /// 迟早和这里漂移。所以外部只管出图，标准化归口在 Unity 侧。
        ///
        /// 这里**不设导入参数、不算 pivot**：那些依赖"全部组的并集包围盒"，
        /// 必须等所有组都到位，由 `assemble` 统一算。分批算会让各组的脚底对不齐。
        /// </summary>
        private static int CmdImport(Dictionary<string, List<string>> args)
        {
            string name = Arg(args, "character");
            string group = Arg(args, "group");
            string src = Arg(args, "src");

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(group) || string.IsNullOrWhiteSpace(src))
            {
                Debug.LogError("[CharForge CLI] import 需要 -character <名> -group <动作_朝向> -src <PNG 目录>。");
                return ExitUsage;
            }
            if (!group.Contains("_"))
            {
                Debug.LogError($"[CharForge CLI] -group 必须形如 <动作>_<朝向>（收到 '{group}'）——" +
                               "这个名字就是 Frames/ 下的目录名，装配靠它反推动作与朝向。");
                return ExitUsage;
            }

            string absSrc = AbsPath(src);
            if (!Directory.Exists(absSrc))
            {
                Debug.LogError($"[CharForge CLI] 源目录不存在：{absSrc}");
                return ExitFail;
            }

            string[] files = Directory.GetFiles(absSrc, "*.png");
            if (files.Length == 0)
            {
                Debug.LogError($"[CharForge CLI] 源目录里没有 PNG：{absSrc}");
                return ExitFail;
            }
            Array.Sort(files, NaturalCompare);

            var raw = new List<byte[]>(files.Length);
            for (int i = 0; i < files.Length; i++)
                raw.Add(File.ReadAllBytes(files[i]));

            int canvas = ArgInt(args, "canvas", 512);
            var normalizer = new FrameNormalizer
            {
                CanvasWidth = canvas,
                CanvasHeight = ArgInt(args, "canvasHeight", canvas),
                MarginRatio = ArgFloat(args, "margin", 0.08f),
                PixelGrid = ArgInt(args, "grid", 1),
            };

            FrameNormalizer.Result normalized = normalizer.NormalizeAllSync(raw);
            if (!normalized.HasContent)
            {
                Debug.LogError($"[CharForge CLI] {group}：归一化后没有任何非透明像素——" +
                               "源图全是空的，或 alpha 被整片清掉了。");
                return ExitFail;
            }

            string folder = Arg(args, "folder", CharForgeSettings.OutputAssetFolder);
            string relDir = $"{folder.TrimEnd('/')}/{name.Trim()}/Frames/{group}";
            string absDir = AbsPath(relDir);
            Directory.CreateDirectory(absDir);

            for (int i = 0; i < normalized.Frames.Count; i++)
                File.WriteAllBytes(Path.Combine(absDir, $"frame-{i + 1}.png"), normalized.Frames[i]);

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            Say($"导入 {group}：{normalized.Frames.Count} 帧 → {relDir}" +
                $"（画布 {canvas}，批内统一缩放）");
            Debug.Log($"CHARFORGE_RESULT ok=1 group={group} frames={normalized.Frames.Count} dir={relDir}");
            return ExitOk;
        }

        /// <summary>
        /// 自然序比较：`frame-2.png` 要排在 `frame-10.png` 前面。
        /// 纯字典序会把 10 排到 2 前面——帧序一错，动画就倒着播。
        /// </summary>
        private static int NaturalCompare(string a, string b)
        {
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;

                    string na = a.Substring(si, i - si).TrimStart('0');
                    string nb = b.Substring(sj, j - sj).TrimStart('0');
                    if (na.Length != nb.Length) return na.Length - nb.Length;

                    int cmp = string.CompareOrdinal(na, nb);
                    if (cmp != 0) return cmp;
                }
                else
                {
                    char ca = char.ToLowerInvariant(a[i]);
                    char cb = char.ToLowerInvariant(b[j]);
                    if (ca != cb) return ca.CompareTo(cb);
                    i++;
                    j++;
                }
            }
            return (a.Length - i) - (b.Length - j);
        }

        // ────────────────────────── 命令：assemble ──────────────────────────

        /// <summary>
        /// 从磁盘上的帧 PNG 重建帧序列并装配 prefab。
        ///
        /// 这一步不调任何模型——同一批帧跑两次必须得到同一个 prefab，
        /// 所以它是「能跑但结果不对」这类静默失败唯一拦得住的地方。
        /// 装配完成后**自动跑一次自检**，空片段不会被当成功交付。
        /// </summary>
        private static int CmdAssemble(Dictionary<string, List<string>> args)
        {
            string name = Arg(args, "character");
            if (string.IsNullOrWhiteSpace(name))
            {
                Debug.LogError("[CharForge CLI] assemble 缺少 -character <角色名>。");
                return ExitUsage;
            }
            name = name.Trim();

            string folder = Arg(args, "folder", CharForgeSettings.OutputAssetFolder);
            string relDir = $"{folder.TrimEnd('/')}/{name}";

            var opt = new FrameNpcAssembler.AssembleOptions
            {
                OutputFolder = folder,
                FrameRate = ArgInt(args, "fps", CharForgeSettings.SpriteFrameRate),
                AddCollider = ArgBool(args, "collider", true),
                MovementMode = NormalizeMovement(Arg(args, "movement", "none")),
                MoveSpeed = ArgFloat(args, "speed", 1.5f),
                DialogueEnabled = ArgBool(args, "dialogue", true),
                SourcePrompt = Arg(args, "prompt", ""),
            };

            Say($"装配 {name}：movement={opt.MovementMode} collider={opt.AddCollider} " +
                $"fps={opt.FrameRate} dialogue={opt.DialogueEnabled}");

            // 帧可能是外部程序刚写进来的，先让 AssetDatabase 追上磁盘。
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            var sets = FrameNpcAssembler.LoadFrameSetsFromDisk(relDir, Say);
            if (sets.Count == 0)
            {
                Debug.LogError($"[CharForge CLI] {relDir}/Frames/ 下没有可用的帧组" +
                               "（目录名必须是 <动作>_<朝向>，里面放 frame-N.png）。");
                return ExitFail;
            }

            var result = FrameNpcAssembler.Assemble(name, sets, opt, null, Say);
            AssetDatabase.SaveAssets();

            if (!result.Ok)
            {
                Debug.LogError($"[CharForge CLI] 装配未产出 prefab：{relDir}");
                return ExitFail;
            }

            Debug.Log($"CHARFORGE_RESULT ok=1 prefab={result.PrefabPath} dir={result.AssetDir}");

            // 「不报错的失败优先于报错的失败」：装配完立刻自己验一遍，
            // 空片段绝不作为成功交付（v0.5.6 血证：内存 8 条键、磁盘 0 条键）。
            return VerifyOne(name, folder, out _) ? ExitOk : ExitFail;
        }

        // ────────────────────────── 命令：verify ──────────────────────────

        /// <summary>数片段文件里**真实**的精灵关键帧，与帧 PNG 张数比对。空片段退出码 1。</summary>
        private static int CmdVerify(Dictionary<string, List<string>> args)
        {
            string name = Arg(args, "character");
            if (string.IsNullOrWhiteSpace(name))
            {
                Debug.LogError("[CharForge CLI] verify 缺少 -character <角色名>（或改用 -cmd scan 看全部）。");
                return ExitUsage;
            }

            string folder = Arg(args, "folder", CharForgeSettings.OutputAssetFolder);
            return VerifyOne(name.Trim(), folder, out _) ? ExitOk : ExitFail;
        }

        /// <summary>单个角色的产物自检。返回 false = 有片段里帧数对不上（含空片段）。</summary>
        private static bool VerifyOne(string name, string folder, out int badGroups)
        {
            badGroups = 0;
            string absDir = AbsPath($"{folder.TrimEnd('/')}/{name}");
            if (!Directory.Exists(absDir))
            {
                Debug.LogError($"[CharForge CLI] 产物目录不存在：{absDir}");
                return false;
            }

            var groups = ReadFrameGroups(absDir, out int totalFrames);
            if (groups.Count == 0)
            {
                Debug.LogError($"[CharForge CLI] {name}：Frames/ 下一个帧组都没有，无从自检。");
                return false;
            }

            var sb = new StringBuilder();
            sb.AppendLine("CHARFORGE_VERIFY_BEGIN");
            sb.AppendLine($"character={name} groups={groups.Count} frames={totalFrames}");
            foreach (var g in groups)
            {
                if (!g.Ok) badGroups++;
                sb.AppendLine($"  group={g.Name} frames={g.Frames} anim_keys={g.AnimKeys} ok={(g.Ok ? "yes" : "no")}");
            }
            sb.AppendLine($"bad_groups={badGroups}");
            sb.AppendLine("CHARFORGE_VERIFY_END");
            Debug.Log(sb.ToString());

            if (badGroups > 0)
            {
                Debug.LogError($"[CharForge CLI] {name}：{badGroups} 个片段的真实关键帧数与帧 PNG 张数不符" +
                               "——这就是「动画播不出来且不报错」的形态，先按上面的列表定位到具体片段。");
                return false;
            }

            Debug.Log($"[CharForge CLI] {name}：{groups.Count} 个片段全部有帧（共 {totalFrames} 张），自检通过。");
            return true;
        }

        // ────────────────────────── 命令：attach ──────────────────────────

        /// <summary>
        /// 把已经编译进工程的自定义组件挂到角色 prefab 上。
        ///
        /// 只认**类型全名**（如 <c>NpcBehaviours.YunshuGreeter</c>），不认脚本路径——
        /// 类型找不到就一定是没编译通过，这个区分比路径更有诊断价值。
        /// 已经挂过的不重复挂；重复执行同一命令是安全的。
        /// </summary>
        private static int CmdAttach(Dictionary<string, List<string>> args)
        {
            string name = Arg(args, "character");
            if (string.IsNullOrWhiteSpace(name))
            {
                Debug.LogError("[CharForge CLI] attach 缺少 -character <角色名>。");
                return ExitUsage;
            }
            name = name.Trim();

            if (!args.TryGetValue("script", out var scripts) || scripts.Count == 0)
            {
                Debug.LogError("[CharForge CLI] attach 缺少 -script <类型全名>（可重复传多次）。");
                return ExitUsage;
            }

            string folder = Arg(args, "folder", CharForgeSettings.OutputAssetFolder);
            string prefabPath = $"{folder.TrimEnd('/')}/{name}/NPC_{name}.prefab";
            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
            {
                Debug.LogError($"[CharForge CLI] 找不到 prefab：{prefabPath}——先跑 -cmd assemble。");
                return ExitFail;
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            if (root == null)
            {
                Debug.LogError($"[CharForge CLI] prefab 内容加载失败：{prefabPath}");
                return ExitFail;
            }

            int added = 0;
            try
            {
                foreach (string raw in scripts)
                {
                    string typeName = (raw ?? "").Trim();
                    if (typeName.Length == 0) continue;

                    var type = FindType(typeName);
                    if (type == null)
                    {
                        Debug.LogError($"[CharForge CLI] 找不到类型 {typeName}——脚本大概率还没编译进工程" +
                                       "（先让它编译通过，再重跑本命令；本命令不做「编辑后自动重编译」）。");
                        return ExitFail;
                    }
                    if (!typeof(Component).IsAssignableFrom(type))
                    {
                        Debug.LogError($"[CharForge CLI] {typeName} 不是 MonoBehaviour/Component，挂不上去。");
                        return ExitFail;
                    }
                    if (root.GetComponent(type) != null)
                    {
                        Say($"{typeName} 已挂载，跳过");
                        continue;
                    }

                    root.AddComponent(type);
                    added++;
                    Say($"已挂载 {typeName}");
                }

                PrefabUtility.SaveAsPrefabAsset(root, prefabPath, out bool saved);
                if (!saved)
                {
                    Debug.LogError($"[CharForge CLI] prefab 保存失败：{prefabPath}");
                    return ExitFail;
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"CHARFORGE_RESULT ok=1 prefab={prefabPath} attached={added}");
            return ExitOk;
        }

        // ────────────────────────── 产物读取 ──────────────────────────

        /// <summary>一个帧组在磁盘上的真实状态。帧数与片段关键帧数分别来自 PNG 与 .anim。</summary>
        private class GroupState
        {
            public string Name = "";
            public int Frames;
            public int AnimKeys;
            public bool Ok;
        }

        /// <summary>
        /// 扫 `Frames/&lt;动作&gt;_&lt;朝向&gt;/` 与对应的 `.anim`。
        ///
        /// 关键在于**两边分别从磁盘数**：帧数数 PNG 张数，关键帧数数片段文件里的曲线点。
        /// 只要有一边来自内存，这个检查就废了——本项目栽过一次「内存 8 条、磁盘 0 条」。
        /// </summary>
        private static List<GroupState> ReadFrameGroups(string absCharDir, out int totalFrames)
        {
            var list = new List<GroupState>();
            totalFrames = 0;

            string framesDir = Path.Combine(absCharDir, "Frames");
            if (!Directory.Exists(framesDir)) return list;

            foreach (var dir in Directory.GetDirectories(framesDir))
            {
                string sub = Path.GetFileName(dir);
                int png = Directory.GetFiles(dir, "frame-*.png").Length;
                if (png == 0) continue;

                string animPath = Path.Combine(absCharDir, "Animations", sub + ".anim");
                int keys = File.Exists(animPath) ? CountSpriteKeys(animPath) : 0;

                list.Add(new GroupState
                {
                    Name = sub,
                    Frames = png,
                    AnimKeys = keys,
                    Ok = keys == png,
                });
                totalFrames += png;
            }

            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return list;
        }

        /// <summary>读片段文件数精灵关键帧；读不出来按 0 算（0 就是失败，不会误判成通过）。</summary>
        private static int CountSpriteKeys(string relOrAbsAnimPath)
        {
            try
            {
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(relOrAbsAnimPath);
                if (clip == null) return 0;

                int max = 0;
                var bindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
                for (int i = 0; i < bindings.Length; i++)
                {
                    var curve = AnimationUtility.GetObjectReferenceCurve(clip, bindings[i]);
                    if (curve != null && curve.Length > max) max = curve.Length;
                }
                return max;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CharForge CLI] 读片段失败（按 0 计）：{relOrAbsAnimPath} — {e.Message}");
                return 0;
            }
        }

        // ────────────────────────── 参数与工具 ──────────────────────────

        /// <summary>
        /// 把 `-key value -key2 value2` 解析成字典。重复的 key 收成一个列表
        /// （<c>-script A -script B</c> 要能同时拿到两个）。
        /// </summary>
        private static Dictionary<string, List<string>> ParseArgs(string[] argv)
        {
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            string current = null;

            for (int i = 1; i < argv.Length; i++)
            {
                string token = argv[i] ?? "";
                if (token.StartsWith("-", StringComparison.Ordinal))
                {
                    current = token.TrimStart('-');
                    if (current.Length == 0) current = null;
                    else if (!map.ContainsKey(current)) map[current] = new List<string>();
                    continue;
                }
                if (current != null) map[current].Add(token);
            }
            return map;
        }

        private static string Arg(Dictionary<string, List<string>> args, string key, string fallback = null)
        {
            return args.TryGetValue(key, out var v) && v.Count > 0 ? v[0] : fallback;
        }

        private static bool ArgBool(Dictionary<string, List<string>> args, string key, bool fallback)
        {
            string s = Arg(args, key);
            if (string.IsNullOrEmpty(s)) return fallback;
            return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase)
                 || s.Equals("yes", StringComparison.OrdinalIgnoreCase)
                 || s.Equals("on", StringComparison.OrdinalIgnoreCase);
        }

        private static int ArgInt(Dictionary<string, List<string>> args, string key, int fallback)
        {
            string s = Arg(args, key);
            return int.TryParse(s, out int v) && v > 0 ? v : fallback;
        }

        private static float ArgFloat(Dictionary<string, List<string>> args, string key, float fallback)
        {
            string s = Arg(args, key);
            return float.TryParse(s, out float v) && v > 0f ? v : fallback;
        }

        /// <summary>移动方式只认这三个值，其余一律退回 none（quietly——不猜）。</summary>
        private static string NormalizeMovement(string raw)
        {
            string s = (raw ?? "").Trim().ToLowerInvariant();
            if (s == "patrol" || s == "manual" || s == "none") return s;
            if (s.Length > 0) Debug.LogWarning($"[CharForge CLI] 未知 -movement '{raw}'，按 none 处理" +
                                               "（可选：patrol / manual / none）。");
            return "none";
        }

        /// <summary>把 Assets 相对路径转成绝对路径（自动补工程根）。</summary>
        private static string AbsPath(string relOrAbs)
        {
            string p = (relOrAbs ?? "").Replace('/', Path.DirectorySeparatorChar);
            return Path.IsPathRooted(p) ? p : Path.Combine(CharForgePaths.ProjectRoot, p);
        }

        private static Type FindType(string fullName)
        {
            // 不用 AppDomain.CurrentDomain.GetAssemblies()：Unity 可能返回**已卸载**的程序集，
            // 反射到一半抛错或泄漏（UAC0005）。UnitySearcher API 才是编辑器里的正路。
            foreach (var assembly in UnityEditor.TypeCache.GetTypesDerivedFrom<object>()
                         .Select(t => t.Assembly).Distinct())
            {
                try
                {
                    var t = assembly.GetType(fullName, false);
                    if (t != null) return t;
                }
                catch (Exception)
                {
                    // 少数动态/反射程序集会抛，跳过即可——找不到就是找不到，不必中断整个命令。
                }
            }
            return null;
        }

        private static void Say(string message) => Debug.Log("[CharForge CLI] " + message);

        private static bool IsBatchMode()
        {
            var argv = Environment.GetCommandLineArgs();
            for (int i = 0; i < argv.Length; i++)
                if (string.Equals(argv[i], "-batchmode", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// 结束命令。**批处理模式下显式给退出码**；在打开着的编辑器里手动执行时
        /// 只打日志、绝不去关编辑器（没人希望点一下菜单就丢未保存的场景）。
        /// </summary>
        private static void Finish(int code)
        {
            Debug.Log($"[CharForge CLI] 结束，退出码 {code}" +
                      (IsBatchMode() ? "。" : "（非批处理模式，不关闭编辑器）。"));

            if (IsBatchMode()) EditorApplication.Exit(code);
        }

        private static void PrintUsage(string badCmd)
        {
            if (!string.IsNullOrEmpty(badCmd))
                Debug.LogError($"[CharForge CLI] 未知命令 '{badCmd}'。");

            Debug.Log(
                "[CharForge CLI] 用法：Unity.exe -batchmode -projectPath <工程> " +
                "-executeMethod CharForge.EditorTools.Cli.CharForgeCli.Run -cmd <命令> [参数]\n" +
                "  （不要加 -quit：它会把退出码冲成 0，失败会被读成成功）\n" +
                "  env                          —— 打印环境事实（工程根 / ComfyUI 地址 / 工作流 / 姿势参考库）\n" +
                "  scan                         —— 列出每个角色的就绪状态\n" +
                "  import   -character <名> -group <动作_朝向> -src <PNG 目录> " +
                "[-canvas 512] [-margin 0.08] [-grid 1]\n" +
                "                               —— 把外部生成的帧 PNG 归一化（alpha 裁切 / 统一缩放 / 脚底锚定）\n" +
                "                                  后导入 Frames/<动作>_<朝向>/；pivot 与导入设置留给 assemble 统一算\n" +
                "  assemble -character <名> [-movement patrol|manual|none] [-collider true|false] " +
                "[-fps 8] [-dialogue true|false] [-speed 1.5] [-folder Assets/CharForge]\n" +
                "  verify   -character <名>      —— 数片段真实关键帧 vs 帧 PNG 张数，空片段退出码 1\n" +
                "  attach   -character <名> -script <类型全名> [-script <类型全名> …]");
        }
    }
}
