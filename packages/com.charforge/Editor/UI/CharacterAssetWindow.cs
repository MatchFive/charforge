using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CharForge.EditorTools.Services;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace CharForge.EditorTools.UI
{
    /// <summary>
    /// 角色资产流水线（唯一窗口）。三步串一条线：
    ///   ① 角色立绘 —— 上传已有立绘，或「角色描述 → 外部 LLM 锻造提示词 → 文生图」；
    ///   ② 帧动画   —— 从姿势参考库勾「动作 × 朝向」，keep_pose 逐帧动作模仿（一次一帧）；
    ///   ③ 组装     —— 帧序列 → Animator 状态机 → NPC prefab（精灵 + 碰撞体 + 人设 + AIGC 标识）。
    ///
    /// 设计取向：**一条主链，两个折叠区**。主链只留三步各自的最小输入；
    /// ComfyUI 地址、工作流、LLM、输出目录这类「设一次就不动」的东西收进「高级设置」，
    /// 避免把配置摆在流程中间，让人误以为每次都要填。
    ///
    /// 线程模型（本项目实锤过两次）：服务层全程 ConfigureAwait(false)，于是它回调进来的
    /// log 几乎必然落在**线程池线程**上；而 UIElements 只能在主线程碰。所以窗口侧
    /// 「谁拥有 UI 谁负责封送」——日志进并发队列，由 EditorApplication.update 泵回主线程。
    /// 服务层管不到也不该管这件事。
    /// </summary>
    public class CharacterAssetWindow : EditorWindow
    {
        [MenuItem("CharForge/角色资产流水线")]
        public static void Open() => GetWindow<CharacterAssetWindow>("CharForge 角色资产");

        // ── 服务与任务 ──
        private CharacterAssetService _svc;
        private CancellationTokenSource _cts;
        private bool _busy;

        // ── 线程封送 ──
        private int _mainThreadId;
        private readonly ConcurrentQueue<string> _pendingLogs = new ConcurrentQueue<string>();
        private bool _pumpHooked;

        // ── 第 1 步 ──
        private TextField _charName;
        private TextField _description;
        private TextField _prompt;
        private TextField _portraitPath;
        private Button _btnForge, _btnPortrait, _btnLoadPortrait;
        private Label _status1;
        private VisualElement _portraitPreview;
        private Texture2D _portraitTex;
        private byte[] _portraitPng;
        /// <summary>
        /// 本次第 1 步的留痕名（&lt;时间戳&gt;_&lt;角色名&gt;）。锻造时生成、立绘时复用，
        /// 让 .txt / .json / _portrait.png 三份落在同一个前缀下，配对查看不用翻目录。
        /// </summary>
        private string _forgeStem;

        // ── 第 2 步 ──
        private List<PoseRefSet> _poseRefs;
        private VisualElement _matrixHost;
        private readonly List<CellBinding> _cells = new List<CellBinding>();
        private Button _btnFrames;
        private Label _status2;
        private VisualElement _framesPreview;
        private readonly List<FrameSet> _frameSets = new List<FrameSet>();
        private FrameNpcAssembler.FrameWriteReport _frameReport;
        private readonly List<Texture2D> _frameTextures = new List<Texture2D>();

        // ── 第 3 步 ──
        private Toggle _addCollider;
        private Toggle _dialogue;
        private DropdownField _movement;
        private Slider _speed;
        private Button _btnAssemble;
        private Label _status3;

        // ── 高级设置 ──
        private TextField _comfyUrl, _t2iWorkflow, _keeposeWorkflow, _outputFolder;
        private TextField _llmBase, _llmKey, _llmModel;
        private SliderInt _frameRate;
        private Label _comfyStatus;

        // ── 日志 ──
        private readonly List<string> _logLines = new List<string>();
        private Label _logLabel;

        private const string MovementNone = "无（静止）";
        private const string MovementPatrol = "巡逻";
        private const string MovementManual = "键盘试走（WASD）";

        /// <summary>动作 × 朝向 勾选格里的一格：勾选框 ↔ 一组逐帧姿势参考（一个目录）。</summary>
        private struct CellBinding
        {
            public Toggle Toggle;
            public PoseRefSet Set;
        }

        private static readonly string[] SprActionOrder = { "walk", "run", "attack", "jump", "death" };
        private static readonly string[] SprDirectionOrder = { "left", "right", "down", "down-left", "down-right", "up" };

        public void CreateGUI()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            _svc = new CharacterAssetService(CharForgeSettings.ComfyUiUrl);

            var root = rootVisualElement;
            root.style.paddingLeft = 8;
            root.style.paddingRight = 8;
            root.style.paddingTop = 6;

            var scroll = new ScrollView();
            root.Add(scroll);

            scroll.Add(MakeTitle("角色资产流水线：立绘 → 帧动画 → NPC prefab"));

            BuildStep1(scroll);
            BuildStep2(scroll);
            BuildStep3(scroll);
            BuildAdvanced(scroll);

            _logLabel = new Label { style = { whiteSpace = WhiteSpace.Normal, marginTop = 8, marginBottom = 8 } };
            _logLabel.style.color = new Color(0.55f, 0.75f, 0.55f);
            scroll.Add(_logLabel);

            Log("就绪。第 1 步：写一句角色描述 → 锻造提示词 → 生成立绘（也可直接载入已有立绘）。");

            // 布局稳定后量一次多行框的内层宽度，用于验证换行修复（见 EnableWordWrap 注释）。
            root.schedule.Execute(() =>
                Log("[wrap] 角色描述: " + WrapInfo(_description) + " ｜ 提示词: " + WrapInfo(_prompt)))
                .StartingIn(1000);
        }

        // ────────────────────────── 第 1 步 ──────────────────────────

        private void BuildStep1(VisualElement root)
        {
            var box = MakeBox("第 1 步 · 角色立绘");

            _charName = new TextField("角色名") { value = CharForgeSettings.CharacterName };
            _charName.tooltip = "产物目录、prefab、状态机名字都用它。建议填。";
            _charName.RegisterValueChangedCallback(e => CharForgeSettings.CharacterName = e.newValue);
            box.Add(_charName);

            _description = new TextField("角色描述")
            {
                multiline = true,
                value = CharForgeSettings.Prompt
            };
            _description.style.minHeight = 56;
            _description.tooltip = "一句话说清身份、服饰、道具、神态。例：博物馆导览员，素雅青灰长衫，手持讲解册。";
            _description.RegisterValueChangedCallback(e => CharForgeSettings.Prompt = e.newValue);
            box.Add(_description);
            EnableWordWrap(_description);

            var row1 = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap } };
            _btnForge = new Button(() => _ = RunForgeAsync()) { text = "锻造提示词（外部 LLM）" };
            _btnPortrait = new Button(() => _ = RunPortraitAsync()) { text = "生成角色立绘" };
            _btnLoadPortrait = new Button(LoadPortraitFromDisk) { text = "载入已有立绘…" };
            row1.Add(_btnForge);
            row1.Add(_btnPortrait);
            row1.Add(_btnLoadPortrait);
            box.Add(row1);

            _prompt = new TextField("文生图提示词")
            {
                multiline = true,
                value = CharForgeSettings.PortraitPrompt
            };
            _prompt.style.minHeight = 88;
            _prompt.tooltip = "锻造结果会写在这里，可任意修改——送进 ComfyUI 的就是这段文字本身。";
            _prompt.RegisterValueChangedCallback(e => CharForgeSettings.PortraitPrompt = e.newValue);
            box.Add(_prompt);
            EnableWordWrap(_prompt);

            _portraitPath = new TextField("已有立绘路径（可选）");
            _portraitPath.tooltip = "填了且存在就优先用它，跳过生成——第 2 步需要的是这类「透明底全身立绘」。";
            box.Add(_portraitPath);

            _portraitPreview = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 4 } };
            box.Add(_portraitPreview);

            _status1 = MakeStatus();
            box.Add(_status1);
            root.Add(box);
        }

        // ────────────────────────── 第 2 步 ──────────────────────────

        private void BuildStep2(VisualElement root)
        {
            var box = MakeBox("第 2 步 · 帧动画（keep_pose 动作模仿）");

            _poseRefs = CharacterAssetService.ListPoseRefs();
            if (_poseRefs == null || _poseRefs.Count == 0)
            {
                var miss = new Label(
                    "未找到姿势参考库。期望位于 <工程>/CharForge/comfyui/pose_refs/<动作>/<朝向>/*.png（用户覆盖）" +
                    "或包内 Configs/comfyui/pose_refs/（随包默认）。每帧一张「照片级姿势参考」，" +
                    "文件名排序即帧序（如视频抽帧 k1…k8；骨架 stick figure 未经 SAM 验证，不建议）。");
                miss.style.color = new Color(0.9f, 0.5f, 0.4f);
                miss.style.whiteSpace = WhiteSpace.Normal;
                box.Add(miss);
                _status2 = MakeStatus();
                box.Add(_status2);
                root.Add(box);
                return;
            }

            _matrixHost = new VisualElement { style = { marginTop = 2 } };
            box.Add(_matrixHost);

            var hint = new Label(
                "勾选要生成的「动作 × 朝向」。每格对应一个姿势参考目录，逐帧动作模仿：立绘 + 每帧一张姿势图 → 一帧成图，" +
                "提示词由 Pose Studio 前端按当帧姿势实时生成（无需填写）。");
            hint.style.color = new Color(0.6f, 0.6f, 0.6f);
            hint.style.whiteSpace = WhiteSpace.Normal;
            box.Add(hint);

            var need = new Label(
                "⚠ 运行前提：ComfyUI 端装有 vnccs-utils 与 VNCCS_QI2_PoseStudioV1.1 LoRA，" +
                "且 ComfyUI Desktop 窗口在线（姿势经 SAM 3D 估计后要回前端同步渲染，无头后端跑不了）。");
            need.style.color = new Color(0.85f, 0.72f, 0.4f);
            need.style.whiteSpace = WhiteSpace.Normal;
            box.Add(need);

            RebuildMatrix();

            _btnFrames = new Button(() => _ = RunFramesAsync()) { text = "生成勾选的帧动画（每帧约 30-60 秒）" };
            box.Add(_btnFrames);

            _framesPreview = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, marginTop = 4 } };
            box.Add(_framesPreview);

            _status2 = MakeStatus();
            box.Add(_status2);
            root.Add(box);
        }

        private void RebuildMatrix()
        {
            if (_matrixHost == null) return;
            _matrixHost.Clear();
            _cells.Clear();

            var actions = _poseRefs.Select(c => c.Action).Distinct()
                                   .OrderBy(a => SprRank(a, SprActionOrder)).ToList();
            var dirs = _poseRefs.Select(c => c.Direction).Distinct()
                                .OrderBy(d => SprRank(d, SprDirectionOrder)).ToList();

            var header = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            header.Add(MatrixLabel("", 118, true));
            foreach (var d in dirs) header.Add(MatrixLabel(DirLabel(d), 118, true));
            _matrixHost.Add(header);

            foreach (var action in actions)
            {
                var row = new VisualElement { style = { flexDirection = FlexDirection.Row } };
                row.Add(MatrixLabel(ActionLabel(action), 118, false));
                foreach (var dir in dirs)
                {
                    var set = _poseRefs.FirstOrDefault(c => c.Action == action && c.Direction == dir);
                    if (set == null)
                    {
                        row.Add(MatrixLabel("—", 118, false));
                        continue;
                    }
                    var cell = new Toggle
                    {
                        // 默认勾 walk：那是 NPC 的常态动作，也让「进来就能跑」成立。
                        value = action == "walk",
                        // 帧数直接标在格子上：keep_pose 的帧数由目录内容决定，不再恒为六。
                        tooltip = $"{ActionLabel(action)} × {DirLabel(dir)} · {set.FramePaths.Count} 帧"
                    };
                    cell.style.width = 118;
                    _cells.Add(new CellBinding { Toggle = cell, Set = set });
                    row.Add(cell);
                }
                _matrixHost.Add(row);
            }
        }

        private static string ActionLabel(string a) => a switch
        {
            "walk" => "行走",
            "run" => "奔跑",
            "attack" => "攻击",
            "jump" => "跳跃",
            "death" => "倒地",
            _ => a,
        };

        private static string DirLabel(string d) => d switch
        {
            "left" => "朝左",
            "right" => "朝右",
            "down" => "朝下",
            "down-left" => "左下",
            "down-right" => "右下",
            "up" => "朝上",
            _ => d,
        };

        private static int SprRank(string v, string[] order)
        {
            int i = Array.IndexOf(order, v ?? "");
            return i >= 0 ? i : order.Length;
        }

        // ────────────────────────── 第 3 步 ──────────────────────────

        private void BuildStep3(VisualElement root)
        {
            var box = MakeBox("第 3 步 · 组装 NPC prefab");

            _addCollider = new Toggle("添加贴合碰撞体") { value = true };
            _addCollider.tooltip = "胶囊体尺寸由帧的实测像素包围盒算出，不用手填。";
            box.Add(_addCollider);

            _movement = new DropdownField("移动方式", new List<string> { MovementNone, MovementPatrol, MovementManual }, 0);
            _movement.tooltip = "巡逻：Rigidbody2D + SimplePatrol2D，巡逻点拖进场景后再设。\n" +
                                "键盘试走：Rigidbody2D + NpcKeyboardDriver，Play 模式用 WASD/方向键验证各朝向切换。";
            box.Add(_movement);

            _speed = new Slider("巡逻速度", 0.2f, 5f) { value = 1.5f, showInputField = true };
            box.Add(_speed);

            _dialogue = new Toggle("启用对话人设") { value = true };
            box.Add(_dialogue);

            _btnAssemble = new Button(RunAssemble) { text = "组装 prefab" };
            box.Add(_btnAssemble);

            _status3 = MakeStatus();
            box.Add(_status3);
            root.Add(box);
        }

        // ────────────────────────── 高级设置 ──────────────────────────

        /// <summary>让多行输入框真正换行。multiline 的文本住在内层 ScrollView 里，而
        /// 官方文档（UI elements reference · ScrollView）明说：ScrollView 的内容容器
        /// **没有有限宽度**（infinite in size），文本拿不到"可用宽度"就不折——
        /// 所以主题里现成的 white-space:normal 没用、TextElement 的 maxWidth:100%
        /// 对着无限宽的父容器解析也等于没约束。前三版全败在这，缺的是这一块：
        /// 给内容视口 + 内容容器钉上有限宽度，四面一起围：
        /// ① 内层 ScrollView 钉死纵向模式、藏横向滚动条；
        /// ② 内容视口 + 内容容器 width:100%（**有限宽度，wrap 的前提**）；
        /// ③ 输入容器 whiteSpace=Normal + align-self:Stretch（UUM-122632）；
        /// ④ 子树内所有 TextElement：whiteSpace=Normal + maxWidth=100%（有界才折）。
        /// </summary>
        private static void EnableWordWrap(TextField field)
        {
            field.style.whiteSpace = WhiteSpace.Normal;

            var input = field.Q(className: "unity-base-text-field__input");
            if (input != null)
            {
                input.style.whiteSpace = WhiteSpace.Normal;
                input.style.alignSelf = Align.Stretch;
            }
            field.Query<ScrollView>().ForEach(sv =>
            {
                sv.mode = ScrollViewMode.Vertical;
                sv.horizontalScrollerVisibility = ScrollerVisibility.Hidden;

                var viewport = sv.Q(className: "unity-scroll-view__content-viewport");
                if (viewport != null)
                    viewport.style.width = Length.Percent(100);
                var container = sv.contentContainer;
                if (container != null)
                    container.style.width = Length.Percent(100);
            });
            field.Query<TextElement>().ForEach(e =>
            {
                e.style.whiteSpace = WhiteSpace.Normal;
                e.style.maxWidth = Length.Percent(100);
            });
        }

        /// <summary>换行诊断：布局稳定后量一遍内层各元素的实际宽度，
        /// 若换行仍未生效，日志行能直接指出是哪一层没被约束住。</summary>
        private static string WrapInfo(TextField field)
        {
            var input = field.Q(className: "unity-base-text-field__input");
            var sv = field.Q<ScrollView>();
            var te = field.Q<TextElement>();
            var cc = sv?.contentContainer;
            return "input=" + (input != null ? input.resolvedStyle.width.ToString("0") : "∅") +
                   " container=" + (cc != null ? cc.resolvedStyle.width.ToString("0") : "∅") +
                   " text=" + (te != null ? te.resolvedStyle.width.ToString("0") : "∅") +
                   " whiteSpace=" + (te != null ? te.resolvedStyle.whiteSpace.ToString() : "?") +
                   " mode=" + (sv != null ? sv.mode.ToString() : "?");
        }

        private void BuildAdvanced(VisualElement root)
        {
            var fold = new Foldout { text = "高级设置（ComfyUI / 工作流 / 外部 LLM / 输出）", value = false };
            fold.style.marginTop = 8;
            root.Add(fold);

            _comfyUrl = new TextField("ComfyUI 地址") { value = CharForgeSettings.ComfyUiUrl };
            _comfyUrl.tooltip = "ComfyUI Desktop 默认 8000；通用教程里的 8188 在 Desktop 版上是空的。" +
                                "若同一端口上还跑着别的本地服务（写 127.0.0.1 会被它抢答），" +
                                "改写成 localhost 或本机局域网 IP 才打到 ComfyUI——或给其中一个换端口。";
            _comfyUrl.RegisterValueChangedCallback(e =>
            {
                CharForgeSettings.ComfyUiUrl = e.newValue;
                _svc = new CharacterAssetService(e.newValue);
            });
            fold.Add(_comfyUrl);

            var probeRow = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            var btnProbe = new Button(() => _ = ProbeAsync()) { text = "探测连通性" };
            probeRow.Add(btnProbe);
            _comfyStatus = new Label("未探测");
            _comfyStatus.style.whiteSpace = WhiteSpace.Normal;
            probeRow.Add(_comfyStatus);
            fold.Add(probeRow);

            _t2iWorkflow = new TextField("立绘工作流文件") { value = CharForgeSettings.PortraitWorkflowFile };
            _t2iWorkflow.tooltip = "文件名即可（自动在 Configs/comfyui/ 下查找），同名 .bindings.json 记录提示词/种子节点。";
            _t2iWorkflow.RegisterValueChangedCallback(e => CharForgeSettings.PortraitWorkflowFile = e.newValue);
            fold.Add(_t2iWorkflow);

            _keeposeWorkflow = new TextField("帧动画工作流文件") { value = CharForgeSettings.KeepPoseWorkflowFile };
            _keeposeWorkflow.RegisterValueChangedCallback(e => CharForgeSettings.KeepPoseWorkflowFile = e.newValue);
            fold.Add(_keeposeWorkflow);

            _outputFolder = new TextField("输出目录") { value = CharForgeSettings.OutputAssetFolder };
            _outputFolder.RegisterValueChangedCallback(e => CharForgeSettings.OutputAssetFolder = e.newValue);
            fold.Add(_outputFolder);

            _frameRate = new SliderInt("动画帧率", 1, 30)
            {
                value = CharForgeSettings.SpriteFrameRate,
                showInputField = true
            };
            _frameRate.RegisterValueChangedCallback(e => CharForgeSettings.SpriteFrameRate = e.newValue);
            fold.Add(_frameRate);
            AddHint(fold, "帧率用 8 fps（原 Spritely 官方 GIF 同款）；keep_pose 逐帧路线同样适用。");

            AddHint(fold, "外部 LLM（OpenAI 兼容 /chat/completions）——三项都填才走模型，否则自动用本地模板。");
            _llmBase = new TextField("LLM BaseURL") { value = CharForgeSettings.LlmBaseUrl };
            _llmBase.tooltip = "例：https://api.deepseek.com/v1（不要带 /chat/completions）。";
            _llmBase.RegisterValueChangedCallback(e => CharForgeSettings.LlmBaseUrl = e.newValue);
            fold.Add(_llmBase);

            _llmKey = new TextField("LLM API Key") { value = CharForgeSettings.LlmApiKey, isPasswordField = true };
            _llmKey.RegisterValueChangedCallback(e => CharForgeSettings.LlmApiKey = e.newValue);
            fold.Add(_llmKey);

            _llmModel = new TextField("LLM 模型名") { value = CharForgeSettings.LlmModel };
            _llmModel.tooltip = "例：deepseek-chat / qwen-plus / gpt-4o-mini。";
            _llmModel.RegisterValueChangedCallback(e => CharForgeSettings.LlmModel = e.newValue);
            fold.Add(_llmModel);

            var noThink = new Toggle("关闭模型思考模式") { value = CharForgeSettings.LlmNoThink };
            noThink.tooltip =
                "向网关发 chat_template_kwargs.enable_thinking=false。" +
                "只有当你用的模型（Qwen3 思考版 / DeepSeek-R1 一类）把思维链混在正文里返回、把提示词污染了，才需要勾。";
            noThink.RegisterValueChangedCallback(e => CharForgeSettings.LlmNoThink = e.newValue);
            fold.Add(noThink);
            AddHint(fold,
                "插件无论如何都会先剥一遍 <think> 思考块；这个开关是从源头省掉那一步。" +
                "不认这个字段的网关会忽略它，严格些的可能因未知参数报 400 —— 遇到就把它关掉。");
        }

        private static void AddHint(VisualElement host, string text)
        {
            var l = new Label(text);
            l.style.color = new Color(0.6f, 0.6f, 0.6f);
            l.style.whiteSpace = WhiteSpace.Normal;
            l.style.marginTop = 4;
            host.Add(l);
        }

        // ────────────────────────── 执行：第 1 步 ──────────────────────────

        private async Task RunForgeAsync()
        {
            if (_busy) return;
            var ct = Begin();
            try
            {
                SetStatus(_status1, "锻造提示词中…");
                var res = await PromptForgeService.ForgeAsync(_description.value, Log, ct);
                _prompt.value = res.Prompt;
                CharForgeSettings.PortraitPrompt = res.Prompt;

                // 留痕：描述 / 模型正文 / 插件注入的画风与版式 / 最终提示词，分段存一份。
                // 这四段混在一起之后事后无法分辨谁写的谁——出图不对时只能靠猜。
                // stem 在这里新建、立绘时复用，三份文件同前缀，检查时一眼配对。
                _forgeStem = ForgeArchive.NewStem(_charName.value);
                ForgeArchive.WriteForge(_forgeStem, _charName.value, _description.value, res, Log);

                if (res.Warning != null)
                    SetStatus(_status1, "⚠ " + res.Warning, warn: true);
                else if (res.FromLlm)
                    SetStatus(_status1, $"完成 ✓ 提示词来自 {res.Source}（可手工修改）");
                else
                    SetStatus(_status1, "完成 ✓ 提示词由本地模板拼出（未配置外部 LLM）");
            }
            catch (OperationCanceledException) { SetStatus(_status1, "已取消。"); }
            catch (Exception e) { SetStatus(_status1, "失败：" + e.Message, warn: true); Log(e.ToString()); }
            finally { End(); }
        }

        private async Task RunPortraitAsync()
        {
            if (_busy) return;
            var ct = Begin();
            try
            {
                string prompt = _prompt.value?.Trim();
                if (string.IsNullOrEmpty(prompt))
                    throw new InvalidOperationException("提示词为空——先点「锻造提示词」，或自己写一条。");

                SetStatus(_status1, "生成立绘中…");
                var pr = await _svc.GeneratePortraitAsync(prompt, Log, ct);
                _portraitPng = pr.Png;
                ShowPortrait(pr.Png);

                // 立绘落到工程根的留痕目录里（不进 Assets）。**必须落盘**：
                // 只挂在窗口那 200×200 的预览框里，是看不出画风到底对不对的。
                // 手写提示词直接出图时没有锻造记录，这里补一个 stem——本次生成不留成孤儿。
                if (string.IsNullOrEmpty(_forgeStem)) _forgeStem = ForgeArchive.NewStem(_charName.value);
                ForgeArchive.WritePortrait(_forgeStem, pr.Png, Log);

                SetStatus(_status1, $"完成 ✓ 立绘已就绪（seed {pr.Seed}），可以进第 2 步。");
            }
            catch (OperationCanceledException) { SetStatus(_status1, "已取消。"); }
            catch (Exception e) { SetStatus(_status1, "失败：" + e.Message, warn: true); Log(e.ToString()); }
            finally { End(); }
        }

        private void LoadPortraitFromDisk()
        {
            if (_busy) return;
            // 先走路径框（可粘贴），没有就开文件面板。
            string path = _portraitPath.value?.Trim();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                path = EditorUtility.OpenFilePanel("选择角色立绘（透明底 PNG 最佳）", "", "png");
                if (string.IsNullOrEmpty(path)) return;
                _portraitPath.value = path;
            }

            try
            {
                var png = File.ReadAllBytes(path);
                _portraitPng = png;
                ShowPortrait(png);
                SetStatus(_status1, $"完成 ✓ 已载入 {Path.GetFileName(path)}");
            }
            catch (Exception e)
            {
                SetStatus(_status1, "载入失败：" + e.Message, warn: true);
            }
        }

        private void ShowPortrait(byte[] png)
        {
            if (_portraitTex != null)
            {
                UnityEngine.Object.DestroyImmediate(_portraitTex);
                _portraitTex = null;
            }
            _portraitPreview.Clear();

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.LoadImage(png);
            _portraitTex = tex;

            var img = new Image { image = tex, scaleMode = ScaleMode.ScaleToFit };
            img.style.width = 200;
            img.style.height = 200;
            _portraitPreview.Add(img);
        }

        // ────────────────────────── 执行：第 2 步 ──────────────────────────

        private async Task RunFramesAsync()
        {
            if (_busy) return;
            var picks = _cells.Where(c => c.Toggle.value).Select(c => c.Set).ToList();
            if (picks.Count == 0)
            {
                SetStatus(_status2, "没有勾选任何「动作 × 朝向」——至少勾一格。", warn: true);
                return;
            }

            var ct = Begin();
            try
            {
                if (_portraitPng == null)
                    throw new InvalidOperationException(
                        "还没有角色立绘——先在第 1 步生成或载入（第 2 步的角色参考图就是它）。");

                // 名字必须在开跑前校验：否则白烧 N 次 15-25 秒的生成，最后才因为没名字失败。
                string name = _charName.value?.Trim();
                if (string.IsNullOrEmpty(name))
                    throw new InvalidOperationException("角色名为空——产物目录、prefab 与状态机都靠它命名，先填上。");

                _frameSets.Clear();
                _frameReport = null;
                ClearFramePreview();

                // 一次点击 = 一颗种子，同角色的**全部动作/朝向共用**。
                // 这是角色同一性的总开关：逐格各掷一次随机种子 = 每个动作长一张脸，
                // 而症状看起来像「模型不行」。钥匙带上本次时间戳 → 同角色重跑会换新的一版。
                int seed = CharacterAssetService.StableSeed($"{name}@{DateTime.Now.Ticks}");
                Log($"[CharForge] 本次任务种子 {seed}——{name} 的所有动作/朝向共用（重跑会换新的一版）");

                int done = 0;
                foreach (var set in picks)
                {
                    done++;
                    SetStatus(_status2, $"生成中 [{done}/{picks.Count}]：{ActionLabel(set.Action)} · {DirLabel(set.Direction)}（{set.FramePaths.Count} 帧）…");
                    var frames = await _svc.GenerateKeepPoseFramesAsync(set, _portraitPng, seed, Log, ct);

                    var fs = new FrameSet { Action = set.Action, Direction = set.Direction, Frames = frames };
                    _frameSets.Add(fs);
                    ShowFrameThumb(fs);
                }

                // 只重跑了部分格：没勾的组**沿用磁盘上已有的帧**（帧 PNG 就在产物目录里，
                // 直接读回来）。不这样做的后果：_frameSets 里只剩这次勾的组，
                // 装配出的控制器只有这几个状态——「只想补一个 left，down/up 从 prefab 里消失了」，
                // 而且不报错。沿用哪些组会写进日志，装配前一眼可核对。
                var picked = new HashSet<string>(
                    picks.Select(p => $"{p.Action}_{p.Direction}"), StringComparer.Ordinal);
                string outDir = $"{BuildOptions().OutputFolder.TrimEnd('/')}/{name}";
                var carried = FrameNpcAssembler.LoadFrameSetsFromDisk(outDir, Log)
                    .Where(fs => !picked.Contains(fs.StateName))
                    .ToList();
                if (carried.Count > 0)
                {
                    Log($"[CharForge] 未勾选的 {carried.Count} 组沿用磁盘已有帧（不重新生成）：" +
                        string.Join(" / ", carried.Select(fs => fs.StateName)));
                    _frameSets.AddRange(carried);
                }

                // 落盘必须在主线程（AssetDatabase）。此刻续体已回到主线程。
                _frameReport = FrameNpcAssembler.WriteFrames(name, _frameSets, BuildOptions(), Log);
                SetStatus(_status2,
                    $"完成 ✓ {_frameSets.Count} 组帧已落盘 {_frameReport.AssetDir}/Frames/，" +
                    $"脚底 pivot Y = {_frameReport.PivotY:F3}（逐帧 alpha 体检见日志，全 ✓ 才算过）");
            }
            catch (OperationCanceledException) { SetStatus(_status2, "已取消。"); }
            catch (Exception e) { SetStatus(_status2, "失败：" + e.Message, warn: true); Log(e.ToString()); }
            finally { End(); }
        }

        private void ShowFrameThumb(FrameSet set)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.LoadImage(set.Frames[0]);
            _frameTextures.Add(tex);

            var col = new VisualElement { style = { alignItems = Align.Center, marginRight = 6 } };
            var img = new Image { image = tex, scaleMode = ScaleMode.ScaleToFit };
            img.style.width = 96;
            img.style.height = 96;
            col.Add(img);
            col.Add(new Label($"{ActionLabel(set.Action)} {DirLabel(set.Direction)}"));
            _framesPreview.Add(col);
        }

        private void ClearFramePreview()
        {
            if (_framesPreview != null) _framesPreview.Clear();
            foreach (var t in _frameTextures)
                if (t != null) UnityEngine.Object.DestroyImmediate(t);
            _frameTextures.Clear();
        }

        // ────────────────────────── 执行：第 3 步 ──────────────────────────

        private void RunAssemble()
        {
            if (_busy) return;
            try
            {
                if (_frameSets.Count == 0)
                {
                    // 内存里没有帧序列（多半是 Unity 重启过了）——但磁盘上的帧还在。
                    // 直接照着产物目录重建，别逼用户重跑一遍几分钟的生成：
                    // 「帧在磁盘上」本身就该是足够的信息。
                    string nm = _charName.value?.Trim();
                    var rebuilt = string.IsNullOrEmpty(nm)
                        ? new List<FrameSet>()
                        : FrameNpcAssembler.LoadFrameSetsFromDisk(
                            $"{BuildOptions().OutputFolder.TrimEnd('/')}/{nm}", Log);

                    if (rebuilt.Count == 0)
                    {
                        SetStatus(_status3, "还没有帧序列——先跑第 2 步。", warn: true);
                        return;
                    }
                    _frameSets.AddRange(rebuilt);
                }

                SetStatus(_status3, "组装中…");
                string name = _charName.value?.Trim();

                // 第 2 步之后若改过角色名，旧产物目录就与当前名字对不上了；
                // 这种情况不能直接复用——否则 prefab 会被写进上一次的目录，名字与内容不符。
                var reuse = _frameReport;
                if (reuse != null && !string.Equals(
                        reuse.AssetDir, $"{BuildOptions().OutputFolder.TrimEnd('/')}/{name}", StringComparison.Ordinal))
                {
                    Log($"角色名变了（{reuse.AssetDir} → 新名 {name}），重新落一遍帧。");
                    reuse = null;
                }

                var result = FrameNpcAssembler.Assemble(
                    name, _frameSets, BuildOptions(), reuse, Log);

                if (!result.Ok)
                {
                    SetStatus(_status3, "装配未完成，详见日志。", warn: true);
                    return;
                }

                SetStatus(_status3, $"完成 ✓ {result.PrefabPath}");
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(result.PrefabPath);
                if (asset != null) EditorGUIUtility.PingObject(asset);
            }
            catch (Exception e)
            {
                SetStatus(_status3, "失败：" + e.Message, warn: true);
                Log(e.ToString());
            }
        }

        private FrameNpcAssembler.AssembleOptions BuildOptions()
        {
            string folder = _outputFolder.value?.Trim();
            if (string.IsNullOrEmpty(folder)) folder = "Assets/CharForge";

            var opt = new FrameNpcAssembler.AssembleOptions
            {
                OutputFolder = folder,
                FrameRate = _frameRate != null ? _frameRate.value : CharForgeSettings.SpriteFrameRate,
                AddCollider = _addCollider.value,
                MovementMode = _movement.index == 1 ? "patrol"
                             : _movement.index == 2 ? "manual"
                             : "none",
                MoveSpeed = _speed.value,
                DialogueEnabled = _dialogue.value,
                SourcePrompt = _prompt.value ?? "",
            };
            return opt;
        }

        private async Task ProbeAsync()
        {
            try
            {
                _comfyStatus.text = "探测中…";
                _comfyStatus.style.color = new Color(0.8f, 0.8f, 0.8f);
                string msg = await _svc.ProbeAsync(CancellationToken.None);
                _comfyStatus.text = msg;
                _comfyStatus.style.color = msg.StartsWith("在线")
                    ? new Color(0.5f, 0.85f, 0.5f)
                    : new Color(0.95f, 0.6f, 0.4f);
            }
            catch (Exception e)
            {
                _comfyStatus.text = e.Message;
            }
        }

        // ────────────────────────── UI 辅助与线程 ──────────────────────────

        private static Label MakeTitle(string text)
        {
            var l = new Label(text);
            l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.marginTop = 4;
            l.style.marginBottom = 4;
            return l;
        }

        private static VisualElement MakeBox(string title)
        {
            var box = new VisualElement();
            box.style.marginTop = 6;
            box.style.marginBottom = 6;
            box.style.paddingLeft = 8;
            box.style.paddingRight = 8;
            box.style.paddingTop = 6;
            box.style.paddingBottom = 6;
            box.style.borderLeftWidth = 2;
            box.style.borderLeftColor = new Color(0.35f, 0.35f, 0.35f);

            var head = new Label(title);
            head.style.unityFontStyleAndWeight = FontStyle.Bold;
            box.Add(head);
            return box;
        }

        private static Label MakeStatus()
        {
            var l = new Label { style = { whiteSpace = WhiteSpace.Normal, marginTop = 4 } };
            l.style.color = new Color(0.85f, 0.75f, 0.4f);
            return l;
        }

        private static void SetStatus(Label label, string text, bool warn = false)
        {
            if (label == null) return;
            label.text = text;
            label.style.color = warn
                ? new Color(0.95f, 0.5f, 0.4f)
                : new Color(0.85f, 0.75f, 0.4f);
        }

        /// <summary>矩阵表头/行头的定宽标签。定宽是关键：勾选框与列头严格对齐。</summary>
        private static Label MatrixLabel(string text, float width, bool bold)
        {
            var label = new Label(text)
            {
                style =
                {
                    width = width,
                    unityFontStyleAndWeight = bold ? FontStyle.Bold : FontStyle.Normal,
                    unityTextAlign = TextAnchor.MiddleLeft
                }
            };
            return label;
        }

        /// <summary>开新作业：置忙、换取消源。**返回 Token 而不是 CTS**——
        /// 调用方只该关心"取消没取消"，不该碰生命周期（Dispose 是 <see cref="End"/> 的事）。
        private CancellationToken Begin()
        {
            _busy = true;
            SetButtonsEnabled(false);
            _cts = new CancellationTokenSource();
            return _cts.Token;
        }

        private void End()
        {
            _busy = false;
            _cts?.Dispose();
            _cts = null;
            SetButtonsEnabled(true);
        }

        private void SetButtonsEnabled(bool on)
        {
            if (_btnForge != null) _btnForge.SetEnabled(on);
            if (_btnPortrait != null) _btnPortrait.SetEnabled(on);
            if (_btnLoadPortrait != null) _btnLoadPortrait.SetEnabled(on);
            if (_btnFrames != null) _btnFrames.SetEnabled(on && _poseRefs != null && _poseRefs.Count > 0);
            if (_btnAssemble != null) _btnAssemble.SetEnabled(on);
        }

        /// <summary>
        /// 日志。Debug.Log 本身线程安全，任何线程立即输出；只有**写 UI** 需要主线程，
        /// 所以非主线程时先进队列，由 EditorApplication.update 泵回来。
        /// </summary>
        private void Log(string line)
        {
            UnityEngine.Debug.Log($"[CharForge] {line}");

            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            {
                _pendingLogs.Enqueue(line);
                HookLogPump();
                return;
            }
            AppendLogUi(line);
        }

        private void HookLogPump()
        {
            if (_pumpHooked) return;
            _pumpHooked = true;
            EditorApplication.update += PumpLogs;
        }

        private void PumpLogs()
        {
            if (_pendingLogs.IsEmpty) return;
            if (_logLabel == null) { _pendingLogs.Clear(); return; }   // 窗口已重建/关闭的兜底
            while (_pendingLogs.TryDequeue(out var line))
                AppendLogUi(line);
        }

        /// <summary>仅限主线程：写一行进日志区（最多保留 80 行）。</summary>
        private void AppendLogUi(string line)
        {
            _logLines.Add(line);
            if (_logLines.Count > 80) _logLines.RemoveRange(0, _logLines.Count - 80);
            _logLabel.text = string.Join("\n", _logLines);
        }

        private void OnDisable()
        {
            _cts?.Cancel();
            if (_pumpHooked)
            {
                EditorApplication.update -= PumpLogs;
                _pumpHooked = false;
            }
            ClearFramePreview();
            if (_portraitTex != null) UnityEngine.Object.DestroyImmediate(_portraitTex);
            _portraitTex = null;
        }
    }
}
