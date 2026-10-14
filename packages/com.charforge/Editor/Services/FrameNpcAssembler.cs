using System;
using System.Collections.Generic;
using System.IO;
using CharForge.EditorTools.Util;
using CharForge.Runtime;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace CharForge.EditorTools.Services
{
    /// <summary>一组「动作 × 朝向」的帧序列（第 2 步产物）。</summary>
    public class FrameSet
    {
        public string Action = "";
        public string Direction = "";
        public byte[][] Frames = new byte[0][];

        /// <summary>循环动作：走 / 跑。其余（attack / jump / death）按一次性播放处理。</summary>
        public bool IsLoop => Action == "walk" || Action == "run";

        /// <summary>播完停在末帧、不回默认状态（death 该躺着，不该自动站起来）。</summary>
        public bool IsHold => Action == "death";

        public string StateName => $"{Action}_{Direction}";
    }

    /// <summary>
    /// 第 2、3 步的**资产侧实现**（必须主线程：AssetDatabase / Texture2D 都只能在主线程碰）。
    ///
    /// 分成两个入口，对应界面的两步：
    ///   * <see cref="WriteFrames"/> —— 第 2 步：落帧 PNG、算脚底线、写导入设置。产物在磁盘上可见；
    ///   * <see cref="Assemble"/>    —— 第 3 步：片段 → 状态机 → GameObject 层级 → prefab。
    /// 两步都做同一件测量（并集 alpha 包围盒），所以第 3 步接受第 2 步的结果复用，避免重复解码。
    ///
    /// 与旧的骨骼装配器的根本差别：那份要先把立绘拆成 6 个部件再挂骨骼层级，
    /// 于是「拆件糊不糊」直接决定成品；这份只要帧动画——一张 SpriteRenderer + 状态机。
    /// 少一个中间环节，就少一处会糊掉的地方（宽袍袖、长下摆尤其明显）。
    ///
    /// **精度上唯一要紧的事：脚底对齐。** 六格图里角色居中排布，按格子底边做 pivot 会悬空，
    /// 按格子中心做 pivot 会陷地。所以从**所有帧的并集** alpha 包围盒反算脚底线，一次算出、
    /// 全部帧共用——逐帧各算各的 pivot，走路时人物会在竖直方向抖，而且不报错。
    /// </summary>
    public static class FrameNpcAssembler
    {
        public const int PixelsPerUnit = 100;

        /// <summary>
        /// 精灵曲线绑定：**单一来源在 <see cref="AnimatorWriter"/>**（真正写片段的地方），此处只做转发。
        /// 两处各写一份迟早漂移，那时校验的就不是同一条曲线了。
        /// </summary>
        internal static EditorCurveBinding SpriteBinding => AnimatorWriter.SpriteCurveBinding;

        /// <summary>装配参数（对应界面第 3 步的开关与高级设置）。</summary>
        public class AssembleOptions
        {
            public string OutputFolder = "Assets/CharForge";
            public int FrameRate = 8;
            public bool AddCollider = true;
            /// <summary>none | patrol | manual（键盘试走，挂 NpcKeyboardDriver）。</summary>
            public string MovementMode = "none";
            public float MoveSpeed = 1.5f;
            public bool DialogueEnabled = true;
            /// <summary>写进 AigcDisclosure 的提示词来源，供合规留痕。</summary>
            public string SourcePrompt = "";
        }

        /// <summary>第 2 步的成果：落盘位置 + 像素测量。</summary>
        public class FrameWriteReport
        {
            public bool Ok;
            public string AssetDir = "";
            public int CellW, CellH;
            public int MinX, MaxX, MinY, MaxY;
            public int FrameCount;
            public readonly List<string> Messages = new List<string>();

            /// <summary>脚底线（归一化到 0–1），Sprite 的 pivot Y 直接用这个值。</summary>
            public float PivotY => CellH > 0 ? Mathf.Clamp01((float)MinY / CellH) : 0.5f;
        }

        public class AssembleResult
        {
            public bool Ok;
            public string PrefabPath = "";
            public string AssetDir = "";
            public readonly List<string> Messages = new List<string>();
        }

        // ────────────────────────── 第 2 步：落帧 ──────────────────────────

        /// <summary>
        /// 把帧序列写进 Assets 并设好导入参数。**这一步之后磁盘上就有帧了**——
        /// 第 2 步的交付物就是这些 PNG，不该等到装配才看得见。
        /// </summary>
        public static FrameWriteReport WriteFrames(string characterName, List<FrameSet> sets, AssembleOptions opt, Action<string> log)
        {
            var rep = new FrameWriteReport();
            opt = opt ?? new AssembleOptions();
            void Say(string m) { rep.Messages.Add(m); log?.Invoke(m); }

            if (string.IsNullOrWhiteSpace(characterName))
                throw new InvalidOperationException("角色名为空——产物目录靠它命名。");

            string name = characterName.Trim();
            string relDir = $"{opt.OutputFolder.TrimEnd('/')}/{name}";
            string absDir = Path.Combine(CharForgePaths.ProjectRoot, relDir.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(absDir);
            rep.AssetDir = relDir;

            var written = new List<(FrameSet set, string[] paths)>();
            foreach (var set in sets ?? new List<FrameSet>())
            {
                if (set?.Frames == null || set.Frames.Length == 0) continue;
                string sub = $"{set.Action}_{set.Direction}";
                Directory.CreateDirectory(Path.Combine(absDir, "Frames", sub));
                var paths = new string[set.Frames.Length];
                for (int i = 0; i < set.Frames.Length; i++)
                {
                    string rel = $"{relDir}/Frames/{sub}/frame-{i + 1}.png";
                    File.WriteAllBytes(
                        Path.Combine(CharForgePaths.ProjectRoot, rel.Replace('/', Path.DirectorySeparatorChar)),
                        set.Frames[i]);
                    paths[i] = rel;
                    rep.FrameCount++;
                }
                written.Add((set, paths));
            }
            if (written.Count == 0)
                throw new InvalidOperationException("所有帧序列都是空的——先跑第 2 步的生成。");
            AssetDatabase.Refresh();

            MeasureUnion(written, out int cellW, out int cellH, out var geo);
            rep.CellW = cellW; rep.CellH = cellH;
            rep.MinX = geo.MinX; rep.MaxX = geo.MaxX; rep.MinY = geo.MinY; rep.MaxY = geo.MaxY;

            Say($"帧已落盘：{rep.FrameCount} 张 → {relDir}/Frames/（{written.Count} 组）");
            Say($"像素包围盒：横 {geo.MinX}–{geo.MaxX}，纵 {geo.MinY}–{geo.MaxY}（格 {cellW}×{cellH}）" +
                $"→ 脚底 pivot Y = {rep.PivotY:F3}（{geo.MinY}/{cellH} 像素）");

            foreach (var (set, paths) in written)
                foreach (var rel in paths)
                    ApplyImportSettings(rel, rep.PivotY);
            AssetDatabase.Refresh();

            rep.Ok = true;
            return rep;
        }

        // ────────────────────────── 从磁盘帧重建（修复入口） ──────────────────────────

        /// <summary>
        /// 照着产物目录里已有的帧 PNG 反推帧序列，用来**修复**已有产物。
        ///
        /// 为什么需要：第 2 步的帧序列只活在界面内存里，Unity 一重启就没了，而磁盘上的
        /// <c>Frames/&lt;动作&gt;_&lt;朝向&gt;/frame-N.png</c> 还在。有了这条路，
        /// 「片段坏了 / prefab 要重装」就只是一次装配的事，不必重跑几分钟的生成——
        /// 「帧在磁盘上」本身就该是足够的信息。
        ///
        /// 帧号按**数值**排序（frame-10 排在 frame-9 之后，不是字典序）；
        /// 组按目录名排序（同一动作的朝向相邻，便于核对）。名字不成对/目录为空的组跳过。
        /// </summary>
        public static List<FrameSet> LoadFrameSetsFromDisk(string assetDir, Action<string> log)
        {
            var sets = new List<FrameSet>();
            if (string.IsNullOrWhiteSpace(assetDir)) return sets;

            string absFrames = Path.Combine(
                CharForgePaths.ProjectRoot,
                assetDir.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar),
                "Frames");
            if (!Directory.Exists(absFrames)) return sets;

            var dirs = Directory.GetDirectories(absFrames);
            Array.Sort(dirs, StringComparer.Ordinal);   // 顺序稳定，日志可核对

            foreach (var dir in dirs)
            {
                string sub = Path.GetFileName(dir);              // 动作_朝向
                int cut = sub.LastIndexOf('_');
                if (cut <= 0 || cut >= sub.Length - 1) continue;  // 名字对不上就不猜

                var files = Directory.GetFiles(dir, "frame-*.png");
                Array.Sort(files, CompareFrameNumbers);
                if (files.Length == 0) continue;

                var frames = new byte[files.Length][];
                for (int i = 0; i < files.Length; i++)
                    frames[i] = File.ReadAllBytes(files[i]);

                sets.Add(new FrameSet
                {
                    Action = sub.Substring(0, cut),
                    Direction = sub.Substring(cut + 1),
                    Frames = frames,
                });
            }

            if (sets.Count > 0)
                log?.Invoke($"从磁盘重建帧序列：{sets.Count} 组 ← {assetDir.TrimEnd('/')}/Frames/" +
                            "（无需重跑生成）");
            return sets;
        }

        /// <summary>frame-10 要排在 frame-9 之后——字典序会把 10 排到 2 前面。</summary>
        private static int CompareFrameNumbers(string a, string b)
            => FrameNumberOf(a).CompareTo(FrameNumberOf(b));

        private static int FrameNumberOf(string path)
        {
            string n = Path.GetFileNameWithoutExtension(path);   // frame-7
            int cut = n.LastIndexOf('-');
            return cut >= 0 && int.TryParse(n.Substring(cut + 1), out int v) ? v : 0;
        }

        // ────────────────────────── 第 3 步：装配 ──────────────────────────

        /// <summary>
        /// 帧序列 → NPC prefab。传入 <paramref name="prepared"/>（第 2 步的结果）可跳过重复落盘。
        ///
        /// 产物层级（NPC_&lt;名&gt;）：
        ///   SpriteRenderer（曲线路径为空字符串，必须挂在根物体上）
        ///   Animator（walk_left / walk_right / attack_left …）
        ///   NpcCharacter（身份 + 对话开关）
        ///   AigcDisclosure（AIGC 显式标识，GB 45438-2025 界面侧）
        ///   CapsuleCollider2D（尺寸来自实测像素包围盒）
        ///   Rigidbody2D + SimplePatrol2D（移动方式 ≠ none 时）
        /// </summary>
        public static AssembleResult Assemble(
            string characterName, List<FrameSet> sets, AssembleOptions opt, FrameWriteReport prepared, Action<string> log)
        {
            var result = new AssembleResult();
            opt = opt ?? new AssembleOptions();
            void Say(string m) { result.Messages.Add(m); log?.Invoke(m); }

            string name = (characterName ?? "").Trim();
            if (string.IsNullOrEmpty(name))
                throw new InvalidOperationException("角色名为空——prefab 靠它命名。");
            if (sets == null || sets.Count == 0)
                throw new InvalidOperationException("没有任何帧序列——先跑第 2 步生成帧动画。");

            var rep = prepared ?? WriteFrames(name, sets, opt, log);
            if (prepared != null)
                Say($"复用第 2 步的帧产物：{rep.FrameCount} 张，脚底 pivot Y = {rep.PivotY:F3}");

            string relDir = rep.AssetDir;
            string absDir = Path.Combine(CharForgePaths.ProjectRoot, relDir.Replace('/', Path.DirectorySeparatorChar));
            result.AssetDir = relDir;

            // ── 1. 取回 Sprite ────────────────────────────────────
            var sprites = new Dictionary<string, Sprite>();
            foreach (var set in sets)
            {
                for (int i = 0; i < set.Frames.Length; i++)
                {
                    string rel = $"{relDir}/Frames/{set.Action}_{set.Direction}/frame-{i + 1}.png";
                    var sp = AssetDatabase.LoadAssetAtPath<Sprite>(rel);
                    if (sp == null)
                        Say($"  ⚠ {rel} 未能导入为 Sprite，该帧将被跳过");
                    else
                        sprites[rel] = sp;
                }
            }

            // ── 2. 控制器 + 片段 ─────────────────────────────────
            string animDir = $"{relDir}/Animations";
            Directory.CreateDirectory(Path.Combine(absDir, "Animations"));
            AssetDatabase.Refresh();

            string controllerPath = $"{animDir}/{name}.controller";
            var controller = EnsureController(controllerPath, sets, out string defaultState, Say);
            if (controller == null)
                throw new InvalidOperationException("AnimatorController 创建失败，装配中止。");
            // 控制器这回也可能是新建的：参数/状态都在内存里，趁现在立刻落盘，
            // 别等到片段循环里那几次 AssetDatabase 操作把新建资产重新导入、把改动冲掉。
            AssetDatabase.SaveAssets();

            var states = new List<(FrameSet set, AnimatorState state)>();
            var firstFrame = new Dictionary<string, Sprite>();
            foreach (var set in sets)
            {
                var frames = new List<Sprite>();
                for (int i = 0; i < set.Frames.Length; i++)
                {
                    string rel = $"{relDir}/Frames/{set.Action}_{set.Direction}/frame-{i + 1}.png";
                    if (sprites.TryGetValue(rel, out var sp)) frames.Add(sp);
                }
                if (frames.Count == 0)
                {
                    Say($"  ⚠ 片段 {set.StateName} 无有效帧（帧导入为 Sprite 失败？），跳过");
                    // 同名旧片段是上一轮的残骸：留着它，控制器下一次装配会继续引用空动画——
                    // 不报错、只是不动。宁可删掉让「缺片段」变成看得见的状态。
                    string stalePath = $"{animDir}/{set.StateName}.anim";
                    if (AssetDatabase.LoadAssetAtPath<AnimationClip>(stalePath) != null)
                    {
                        AssetDatabase.DeleteAsset(stalePath);
                        Say($"  已删除残留片段（下一轮装配会用完整帧重建）：{stalePath}");
                    }
                    continue;
                }

                if (frames.Count < set.Frames.Length)
                    // 少帧不报错、只是动作缺几拍——所以必须说出来（"图里少了什么，先看数"）。
                    Say($"  ⚠ 片段 {set.StateName} 只凑到 {frames.Count}/{set.Frames.Length} 帧" +
                        "（有 PNG 没导入成 Sprite），先按现有帧生成");

                string clipPath = $"{animDir}/{set.StateName}.anim";
                var clip = AnimatorWriter.EnsureSpriteClip(
                    set.StateName, frames.ToArray(), opt.FrameRate, set.IsLoop, clipPath);

                // 硬校验：**读磁盘**，不读内存。
                //
                // 空片段是最典型的「不报错的失败」——用户只看到「动画播不出来」，
                // 倒查不到「片段里其实一个精灵都没有」。
                //
                // v0.5.6 这道守卫查的是内存对象，而实测坏法是「内存里 8 条键、磁盘上 0 条键」
                // （见 AnimatorWriter.EnsureSpriteClip 的落盘说明）：守卫照样放行，
                // 于是交付了一份空片段（阿茹 walk_down / walk_left 实锤）。真相只在磁盘上。
                // 先按原参数重写一次（落盘窗口已经压到零，正常情况下这步不会触发）。
                int keyCount = AnimatorWriter.CountSpriteKeysOnDisk(clipPath);
                if (keyCount != frames.Count)
                {
                    Say($"  ⚠ 片段 {set.StateName} 落盘后关键帧 {keyCount} 条 ≠ 帧数 {frames.Count}，重写一次…");
                    clip = AnimatorWriter.EnsureSpriteClip(
                        set.StateName, frames.ToArray(), opt.FrameRate, set.IsLoop, clipPath);
                    keyCount = AnimatorWriter.CountSpriteKeysOnDisk(clipPath);
                }
                if (keyCount != frames.Count)
                    throw new InvalidOperationException(
                        $"片段 {set.StateName} 落盘后关键帧 {keyCount} 条，与帧数 {frames.Count} 不符——" +
                        "帧 PNG 导入异常，装配中止（先修帧的导入再重试）。");

                var state = controller.layers[0].stateMachine.AddState(set.StateName);
                state.motion = clip;
                states.Add((set, state));
                firstFrame[set.StateName] = frames[0];
                Say($"片段 {set.StateName}：{keyCount} 关键帧 @ {opt.FrameRate} fps，{(set.IsLoop ? "循环" : "一次性")}");
            }
            if (states.Count == 0)
                throw new InvalidOperationException("没有任何有效片段，装配中止。");

            WireStateMachine(controller, states, defaultState, Say);
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            // ── 3. 层级 → prefab ─────────────────────────────────
            var go = BuildHierarchy(name, controller, defaultState, firstFrame,
                                    rep.MinX, rep.MaxX, rep.MinY, rep.MaxY, rep.CellW, opt, rep.PivotY, Say);
            string prefabPath = $"{relDir}/NPC_{name}.prefab";
            // 不删档：SaveAsPrefabAsset 就地覆盖。删了再建会换 GUID，
            // 把场景里已拖好的实例引用全部打断——本项目为此吃过一次亏。
            PrefabUtility.SaveAsPrefabAsset(go, prefabPath, out bool saved);
            UnityEngine.Object.DestroyImmediate(go);
            AssetDatabase.SaveAssets();

            if (!saved)
            {
                Say($"prefab 保存失败：{prefabPath}");
                return result;
            }

            result.Ok = true;
            result.PrefabPath = prefabPath;
            Say($"prefab 已生成：{prefabPath}（{states.Count} 个状态，默认 {defaultState}）");
            return result;
        }

        // ────────────────────────── 状态机 ──────────────────────────

        /// <summary>
        /// 状态机契约（运行期脚本按这套驱动，**不要改名**）：
        ///   facing   int     朝向索引，按生成顺序编号（sidescroller 里 left=0 / right=1）
        ///   isMoving bool    是否在移动 → 走 / 跑 循环动作的开关
        ///   Play_xxx trigger 一次性动作（attack / jump / death）；death 播完停在末帧
        ///
        /// 用 AnyState 而不是两两互联：动作 × 朝向轻易到十几个状态，两两连边是几十条
        /// transition，加一个动作就要重连一遍；AnyState 每个状态只需一条。
        /// </summary>
        private static void WireStateMachine(
            AnimatorController controller,
            List<(FrameSet set, AnimatorState state)> states,
            string defaultState,
            Action<string> say)
        {
            var sm = controller.layers[0].stateMachine;
            var facingIndex = BuildFacingIndex(states);

            // 默认状态也要钉死（先钉再连线）。循环动作的默认状态**同样要连 AnyState 回转边**：
            // 玩家从其他朝向走回来时（facing 回到默认值），没有这条边就永远停在别的朝向里
            // ——v0.4.8 及之前默认状态被整条跳过，就是「大部分帧用不上」的另一半原因。
            // 自转移由 canTransitionToSelf=false 拦住（条件真且已在该状态时不重进），不会失控。
            var defaultSmState = FindState(sm, defaultState);
            if (defaultSmState == null)
                throw new InvalidOperationException($"状态机里找不到默认状态 {defaultState}——生成与连线不同步。");
            sm.defaultState = defaultSmState;

            for (int i = states.Count - 1; i >= 0; i--)
            {
                var (set, state) = states[i];
                if (state == defaultSmState && !set.IsLoop) continue;

                var t = sm.AddAnyStateTransition(state);
                t.hasExitTime = false;
                t.duration = 0f;
                t.canTransitionToSelf = false;
                t.hasFixedDuration = true;

                if (set.IsLoop)
                {
                    t.AddCondition(AnimatorConditionMode.Equals, facingIndex[set.Direction], "facing");
                    t.AddCondition(AnimatorConditionMode.If, 0f, "isMoving");
                    continue;
                }

                t.AddCondition(AnimatorConditionMode.If, 0f, "Play_" + set.Action);
                // 同一动作有多个朝向时才用 facing 区分；只有单朝向还要加条件，它会永远播不出来。
                int sameActionDirs = 0;
                foreach (var (s2, _) in states) if (s2.Action == set.Action) sameActionDirs++;
                if (sameActionDirs > 1)
                    t.AddCondition(AnimatorConditionMode.Equals, facingIndex[set.Direction], "facing");
            }

            // 一次性动作播完的去向。death 不在此列——它该停在末帧。
            foreach (var (set, state) in states)
            {
                if (set.IsLoop || set.IsHold || state == defaultSmState) continue;
                var back = state.AddTransition(defaultSmState);
                back.hasExitTime = true;
                back.exitTime = 1f;
                back.duration = 0f;
                back.hasFixedDuration = true;
            }

            var triggers = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var (set, _) in states)
                if (!set.IsLoop) triggers.Add("Play_" + set.Action);

            say($"状态机：{states.Count} 个状态，默认 {defaultState}；朝向索引 " +
                string.Join(" / ", facingIndex) +
                (triggers.Count > 0 ? $"；触发器 {string.Join(" / ", triggers)}" : ""));
        }

        /// <summary>
        /// 朝向 → facing 索引。**必须与 <see cref="NpcCharacter.FacingDirection"/> 的枚举值一致**
        /// （South=0 / North=1 / East=2 / West=3）：运行期 SetFacing 写的就是这个枚举整数，
        /// 两处一旦漂移，切朝向的条件永远对不上——v0.4.8 及之前按勾选顺序编号，踩过这个坑
        /// （同一套参数，装配时编号随勾选顺序漂移，外部脚本没法预知，大部分帧永远播不到）。
        /// 未收录的朝向接着 4 往后排（保持向后兼容）。
        /// </summary>
        private static Dictionary<string, int> BuildFacingIndex(List<(FrameSet set, AnimatorState state)> states)
        {
            var map = new Dictionary<string, int>
            {
                ["down"] = (int)FacingDirection.South,
                ["up"] = (int)FacingDirection.North,
                ["right"] = (int)FacingDirection.East,
                ["left"] = (int)FacingDirection.West,
            };
            foreach (var (set, _) in states)
                if (!string.IsNullOrEmpty(set.Direction) && !map.ContainsKey(set.Direction))
                    map[set.Direction] = map.Count;
            return map;
        }

        private static AnimatorState FindState(AnimatorStateMachine sm, string name)
        {
            foreach (var child in sm.states)
                if (child.state != null && child.state.name == name)
                    return child.state;
            return null;
        }

        private static AnimatorController EnsureController(
            string controllerPath, List<FrameSet> sets, out string defaultState, Action<string> say)
        {
            // 默认状态优先给循环动作（走路是 NPC 的常态），否则给第一组。
            defaultState = "";
            foreach (var set in sets)
                if (set != null && set.IsLoop) { defaultState = set.StateName; break; }
            if (string.IsNullOrEmpty(defaultState) && sets.Count > 0)
                defaultState = sets[0].StateName;

            // 复用而非删档重建：删了再建会让正显示该控制器的 Animator 窗口持有失效图节点
            // （UnityEditor.Graphs NRE），并且换 GUID 会打断 prefab 对控制器的引用。
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
            if (!IsHealthy(controller))
            {
                AssetDatabase.DeleteAsset(controllerPath);
                controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
            }
            if (controller == null) return null;

            for (int i = controller.parameters.Length - 1; i >= 0; i--)
                controller.RemoveParameter(i);
            controller.AddParameter("facing", AnimatorControllerParameterType.Int);
            controller.AddParameter("isMoving", AnimatorControllerParameterType.Bool);
            // 去重：同一动作有多个朝向会来两条 set，AddParameter 撞名会直接抛异常。
            var triggerKeys = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var set in sets)
                if (set != null && !set.IsLoop) triggerKeys.Add("Play_" + set.Action);
            foreach (var key in triggerKeys)
                controller.AddParameter(key, AnimatorControllerParameterType.Trigger);

            var sm = controller.layers[0].stateMachine;
            foreach (var child in sm.states)
                sm.RemoveState(child.state);

            say($"控制器：{controllerPath}（默认状态 {defaultState}）");
            return controller;
        }

        private static bool IsHealthy(AnimatorController c)
        {
            if (c == null) return false;
            try
            {
                if (c.layers == null || c.layers.Length == 0) return false;
                return c.layers[0].stateMachine != null;
            }
            catch { return false; }
        }

        // ────────────────────────── 层级 ──────────────────────────

        private static GameObject BuildHierarchy(
            string name, AnimatorController controller, string defaultState,
            Dictionary<string, Sprite> firstFrame,
            int minX, int maxX, int minY, int maxY, int cellW,
            AssembleOptions opt, float pivotY, Action<string> say)
        {
            var go = new GameObject($"NPC_{name}");
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sortingOrder = 0;
            sr.sprite = firstFrame.TryGetValue(defaultState, out var defSprite) ? defSprite : null;

            var animator = go.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;

            var npc = go.AddComponent<NpcCharacter>();
            var so = new SerializedObject(npc);
            SetString(so, "characterId", name);
            SetString(so, "displayName", name);
            SetBool(so, "dialogueEnabled", opt.DialogueEnabled);
            so.ApplyModifiedProperties();

            var label = go.AddComponent<AigcDisclosure>();
            label.Configure("本内容由人工智能生成", "corner", true, opt.SourcePrompt ?? "");

            if (opt.AddCollider)
            {
                // 尺寸来自实测包围盒。pivot 已在脚底线，所以内容在局部坐标里是 y ∈ [0, 高度]，
                // 胶囊的 offset.y 取高的一半即可，不用再猜。
                float w = Mathf.Max(0.05f, (maxX - minX + 1) / (float)PixelsPerUnit);
                float h = Mathf.Max(0.05f, (maxY - minY + 1) / (float)PixelsPerUnit);
                float offX = ((minX + maxX) * 0.5f - cellW * 0.5f) / PixelsPerUnit;
                var capsule = go.AddComponent<CapsuleCollider2D>();
                capsule.direction = CapsuleDirection2D.Vertical;
                capsule.size = new Vector2(w, h);
                capsule.offset = new Vector2(offX, h * 0.5f);
                say($"碰撞体：胶囊 {w:0.##}×{h:0.##} 单位，偏移 ({offX:0.##}, {h * 0.5f:0.##})，PPU {PixelsPerUnit}");
            }

            if (!string.IsNullOrEmpty(opt.MovementMode) && opt.MovementMode != "none")
            {
                var rb = go.AddComponent<Rigidbody2D>();
                rb.gravityScale = 0f;
                rb.freezeRotation = true;
                rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
                rb.interpolation = RigidbodyInterpolation2D.Interpolate;

                if (opt.MovementMode == "manual")
                {
                    var drv = go.AddComponent<NpcKeyboardDriver>();
                    var dso = new SerializedObject(drv);
                    SetFloat(dso, "speed", opt.MoveSpeed);
                    dso.ApplyModifiedProperties();
                    say($"移动：键盘试走（WASD / 方向键），速度 {opt.MoveSpeed:0.##}");
                }
                else
                {
                    var patrol = go.AddComponent<SimplePatrol2D>();
                    var pso = new SerializedObject(patrol);
                    SetFloat(pso, "speed", opt.MoveSpeed);
                    pso.ApplyModifiedProperties();
                    say($"移动：{opt.MovementMode}，速度 {opt.MoveSpeed:0.##}");
                }
            }

            say($"层级：NPC_{name}（SpriteRenderer / Animator / NpcCharacter / AigcDisclosure" +
                $"{(opt.AddCollider ? " / CapsuleCollider2D" : "")}），pivot Y {pivotY:F3}");
            return go;
        }

        // ────────────────────────── 像素测量 ──────────────────────────

        /// <summary>
        /// 全部帧的并集 alpha 包围盒。**并集是刻意的**：逐帧各算各的会造成
        /// 「同一角色在不同帧里脚底高度不同」，播放时人物上下抖——不报错，只是难看，很难倒查。
        /// </summary>
        private static void MeasureUnion(
            List<(FrameSet set, string[] paths)> written, out int cellW, out int cellH, out Geometry geo)
        {
            geo = new Geometry { MinX = int.MaxValue, MaxX = -1, MinY = int.MaxValue, MaxY = -1 };
            cellW = 0; cellH = 0;
            const byte alphaCut = 8;

            foreach (var (set, paths) in written)
            {
                for (int i = 0; i < set.Frames.Length; i++)
                {
                    var tex = Decode(set.Frames[i]);
                    cellW = Mathf.Max(cellW, tex.width);
                    cellH = Mathf.Max(cellH, tex.height);
                    int w = tex.width, h = tex.height;
                    var px = tex.GetPixels32();
                    UnityEngine.Object.DestroyImmediate(tex);

                    for (int y = 0; y < h; y++)
                    {
                        int row = y * w;
                        for (int x = 0; x < w; x++)
                        {
                            if (px[row + x].a <= alphaCut) continue;
                            if (x < geo.MinX) geo.MinX = x;
                            if (x > geo.MaxX) geo.MaxX = x;
                            if (y < geo.MinY) geo.MinY = y;
                            if (y > geo.MaxY) geo.MaxY = y;
                        }
                    }
                }
            }

            if (geo.MaxX < geo.MinX)
                throw new InvalidOperationException(
                    "所有帧都是全透明的——去白底把角色也一起清掉了？检查出图背景是否为纯白。");
        }

        private struct Geometry
        {
            public int MinX, MaxX, MinY, MaxY;
        }

        private static Texture2D Decode(byte[] png)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(png))
                throw new InvalidOperationException("帧 PNG 解码失败");
            return tex;
        }

        private static void ApplyImportSettings(string relPath, float pivotY)
        {
            var imp = AssetImporter.GetAtPath(relPath) as TextureImporter;
            if (imp == null) return;

            var settings = new TextureImporterSettings();
            imp.ReadTextureSettings(settings);
            settings.textureType = TextureImporterType.Sprite;
            // 注意：TextureImporterSettings 上的成员叫 spriteMode（不是 spriteImportMode）——
            // 后者是 TextureImporter 自己的属性，写在 settings 上会 CS1061（本项目实锤过一次）。
            settings.spriteMode = (int)SpriteImportMode.Single;
            settings.spriteAlignment = (int)SpriteAlignment.Custom;
            settings.spritePivot = new Vector2(0.5f, pivotY);
            settings.spriteMeshType = SpriteMeshType.FullRect;   // 不用紧贴网格：透明区被裁会让 pivot 漂移
            settings.spritePixelsPerUnit = PixelsPerUnit;
            settings.spriteExtrude = 0;
            settings.alphaIsTransparency = true;
            settings.mipmapEnabled = false;                      // 像素风：关 mipmap，避免缩放糊边
            settings.filterMode = FilterMode.Point;
            imp.SetTextureSettings(settings);
            imp.textureCompression = TextureImporterCompression.Uncompressed;
            imp.SaveAndReimport();
        }

        private static void SetString(SerializedObject so, string prop, string value)
        {
            var p = so.FindProperty(prop);
            if (p != null) p.stringValue = value ?? "";
        }

        private static void SetBool(SerializedObject so, string prop, bool value)
        {
            var p = so.FindProperty(prop);
            if (p != null) p.boolValue = value;
        }

        private static void SetFloat(SerializedObject so, string prop, float value)
        {
            var p = so.FindProperty(prop);
            if (p != null) p.floatValue = value;
        }
    }
}
