using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CharForge.EditorTools.Backends;
using CharForge.EditorTools.Data;
using CharForge.EditorTools.Services;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
// ObjectField 属编辑器侧控件（UnityEditor.UIElements），运行时的 UnityEngine.UIElements
// 里没有它。这里用**别名**而不是再 using 一个命名空间：
// 不同 Unity 版本对"运行时是否也有 ObjectField"处理不一致，写别名可彻底避免 CS0104 二义性。
using ObjectField = UnityEditor.UIElements.ObjectField;

namespace CharForge.EditorTools.UI
{
    /// <summary>
    /// CharForge 主窗口（UI Toolkit）。
    ///
    /// 用 UI Toolkit 而非 IMGUI 的理由很实际：这个窗口有列表、进度、日志流和一堆
    /// 随后端切换而显隐的控件，IMGUI 的"每帧全量重绘 + 手写布局"在长任务轮询时会
    /// 明显增加编辑器 CPU 占用（Unity 6 官方也把 UI Toolkit 列为复杂编辑器工具的首选项）。
    ///
    /// 交互上的三个刻意取舍：
    /// - **全程不离开编辑器**：探测、启动、出图、装配、打开产物都在这个窗口里完成。
    ///   这是"插件"与"独立工具"的用户可见分界线。
    /// - **失败必须给出下一步**：后端连不上时不是丢一句红字，而是直接给出可执行动作
    ///   （"点启动后端" / "换成占位后端先跑通流程"）。
    /// - **长任务可取消**：生成到一半发现描述写错了，不该只能等它跑完。
    /// </summary>
    [EditorWindowTitle(title = "CharForge", icon = "d_SpriteRenderer Icon")]
    public class CharacterForgeWindow : EditorWindow
    {
        // 界面资产路径**不写死**。包有三种安装形态（Add package from disk / 嵌入 Packages/ /
        // 注册表安装），UPM 暴露的虚拟路径并不总等于仓库里的目录名；一旦写死，加载失败是
        // **静默的**——窗口照常打开，只是样式丢失、回落到兜底界面，排查成本很高。
        // 所以这里按「程序集反查包路径 → 全库按类型搜索」两级定位。
        private const string UxmlFileName = "CharacterForgeWindow.uxml";
        private const string UssFileName = "CharacterForgeWindow.uss";

        private static readonly string[] BackendIds = { "auto", "mock", "comfyui", "sidecar", "sentis" };
        private static readonly string[] BackendLabels =
        {
            "自动选择（推荐）", "占位后端（零依赖·立即可用）", "本地 ComfyUI（质量最高）",
            "自带 Python 服务（server/）", "Unity 引擎内推理（实验性）"
        };

        private static readonly string[] PresetIds = { "custom", "museum_guide", "hand_drawn", "pixel_art" };
        private static readonly string[] PresetLabels = { "自定义（沿用参考图）", "文博展陈", "手绘厚涂", "像素风" };

        private static readonly string[] MovementIds = { "none", "rigidbody", "astar", "navmesh" };
        private static readonly string[] MovementLabels = { "不移动", "Rigidbody2D 自由移动", "A* 寻路", "NavMesh" };

        private static readonly string[] DirectionKeys = { "south", "north", "east", "west" };
        private static readonly string[] DirectionLabels = { "南（正面）", "北（背面）", "东（右）", "西（左）" };

        // ── 控件引用（构建时填充）──
        private DropdownField _backend;
        private Label _backendStatus;
        private Toggle _autoStart;

        private TextField _prompt;
        private TextField _charName;
        private TextField _era;
        private TextField _refNotes;
        private readonly ObjectField[] _refImages = new ObjectField[3];
        private DropdownField _preset;

        private Toggle _actIdle;
        private Toggle _actWalk;
        private readonly Toggle[] _dirs = new Toggle[4];
        private SliderInt _framesPerClip;
        private TextField _outputFolder;
        private DropdownField _movement;
        private Toggle _dialogue;

        private TextField _comfyUrl;
        private TextField _comfyWorkflow;
        private TextField _comfyLaunch;
        private TextField _sidecarUrl;
        private TextField _sidecarPython;
        private TextField _sidecarWorkDir;
        private TextField _sentisModel;
        private Toggle _sentisExperimental;

        private Button _btnGenerate;
        private Button _btnReassemble;
        private ProgressBar _progress;
        private Label _nextAction;
        private ScrollView _log;

        private readonly List<string> _logLines = new List<string>();
        private CancellationTokenSource _cts;
        private string _lastOutputDir;
        private bool _busy;

        [MenuItem("Window/CharForge 角色资产生成")]
        public static void Open()
        {
            var window = GetWindow<CharacterForgeWindow>("CharForge");
            window.minSize = new Vector2(460, 620);
            window.Show();
        }

        public void CreateGUI()
        {
            rootVisualElement.Clear();

            string ussPath = ResolvePackageAssetPath(UssFileName);
            var styleSheet = string.IsNullOrEmpty(ussPath)
                ? null
                : AssetDatabase.LoadAssetAtPath<StyleSheet>(ussPath);
            if (styleSheet != null) rootVisualElement.styleSheets.Add(styleSheet);

            string uxmlPath = ResolvePackageAssetPath(UxmlFileName);
            var uxml = string.IsNullOrEmpty(uxmlPath)
                ? null
                : AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            if (uxml != null)
                rootVisualElement.Add(uxml.Instantiate());
            else
                BuildFallbackTree(uxmlPath);     // 包被移动/裁剪时仍要能用，不能白屏

            CacheAndBuildControls();
            LoadSettingsIntoUi();
            ApplyBackendVisibility();

            Log("就绪。先点「探测后端」看看有哪些后端可用；没有后端时切到「占位后端」也能立刻跑通全流程。");
        }

        private void OnDisable()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        // ────────────────────────── 界面构建 ──────────────────────────

        private VisualElement Section(string name) => rootVisualElement.Q<VisualElement>(name);

        private void CacheAndBuildControls()
        {
            _progress = rootVisualElement.Q<ProgressBar>("progress");
            _nextAction = rootVisualElement.Q<Label>("next-action");
            _log = rootVisualElement.Q<ScrollView>("log");

            BuildBackendSection(Section("section-backend"));
            BuildJobSection(Section("section-job"));
            BuildOutputSection(Section("section-output"));
            BuildOptionsSection(Section("section-options"));

            _btnGenerate = rootVisualElement.Q<Button>("btn-generate");
            _btnReassemble = rootVisualElement.Q<Button>("btn-reassemble");
            var btnOpen = rootVisualElement.Q<Button>("btn-open-output");

            if (_btnGenerate != null) _btnGenerate.clicked += () => _ = RunGenerateAsync();
            if (_btnReassemble != null) _btnReassemble.clicked += () => _ = RunReassembleAsync();
            if (btnOpen != null) btnOpen.clicked += OpenOutputFolder;
        }

        private void BuildBackendSection(VisualElement host)
        {
            if (host == null) return;

            _backend = new DropdownField("推理后端", BackendLabels.ToList(), 0);
            _backend.RegisterValueChangedCallback(_ =>
            {
                CharForgeSettings.BackendId = BackendIds[Mathf.Clamp(_backend.index, 0, BackendIds.Length - 1)];
                ApplyBackendVisibility();
            });
            host.Add(_backend);

            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap } };
            var btnProbe = new Button(() => _ = ProbeAsync()) { text = "探测后端" };
            var btnStart = new Button(() => _ = StartBackendAsync()) { text = "启动后端" };
            var btnStop = new Button(StopBackend) { text = "停止后端" };
            row.Add(btnProbe);
            row.Add(btnStart);
            row.Add(btnStop);
            host.Add(row);

            _autoStart = new Toggle("生成前自动启动后端") { value = CharForgeSettings.AutoStartBackend };
            _autoStart.RegisterValueChangedCallback(e => CharForgeSettings.AutoStartBackend = e.newValue);
            host.Add(_autoStart);

            _backendStatus = new Label("未探测") { name = "backend-status" };
            _backendStatus.AddToClassList("cf-badge");
            host.Add(_backendStatus);
        }

        private void BuildJobSection(VisualElement host)
        {
            if (host == null) return;

            _prompt = new TextField("角色描述")
            {
                multiline = true,
                value = "博物馆导览员，身着素雅青灰色长衫，手持讲解册，神态温和"
            };
            _prompt.style.minHeight = 64;
            _prompt.AddToClassList("cf-field");
            host.Add(_prompt);

            _charName = new TextField("角色名（可选）") { value = "" };
            _charName.tooltip = "留空则由后端或本地规则推断。想得到「云叔」这种名字建议直接填。";
            host.Add(_charName);

            _era = new TextField("年代 / 形制约束（文博场景）") { value = "" };
            _era.tooltip = "例如「南宋」。会影响提示词与知识边界，防止把宋代器型画成明代样式。";
            host.Add(_era);

            _refNotes = new TextField("藏品档案 / 考据要点（可选）")
            {
                multiline = true,
                value = ""
            };
            _refNotes.style.minHeight = 48;
            _refNotes.tooltip = "会进入人设的「知识边界」，让 NPC 只依据资料回答，避免史实硬伤。";
            host.Add(_refNotes);

            for (int i = 0; i < _refImages.Length; i++)
            {
                _refImages[i] = new ObjectField($"画风参考图 {i + 1}")
                {
                    objectType = typeof(Texture2D),
                    allowSceneObjects = false
                };
                host.Add(_refImages[i]);
            }

            var hint = new Label("建议 3-5 张，人审挑选。参考图是画风锁定的输入——不提供就无法验收四向一致性。");
            hint.AddToClassList("cf-badge");
            host.Add(hint);

            _preset = new DropdownField("风格预设", PresetLabels.ToList(), 0);
            host.Add(_preset);
        }

        private void BuildOutputSection(VisualElement host)
        {
            if (host == null) return;

            var actionRow = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap } };
            _actIdle = new Toggle("待机 idle") { value = true };
            _actWalk = new Toggle("行走 walk") { value = true };
            actionRow.Add(_actIdle);
            actionRow.Add(_actWalk);
            host.Add(actionRow);

            var dirRow = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap } };
            for (int i = 0; i < 4; i++)
            {
                _dirs[i] = new Toggle(DirectionLabels[i]) { value = i < 2 };
                dirRow.Add(_dirs[i]);
            }
            host.Add(dirRow);

            _framesPerClip = new SliderInt("每片段帧数", 2, 8)
            {
                value = CharForgeSettings.FramesPerClip,
                showInputField = true
            };
            host.Add(_framesPerClip);

            _movement = new DropdownField("移动方式", MovementLabels.ToList(), 0);
            host.Add(_movement);

            _dialogue = new Toggle("启用对话人设") { value = true };
            host.Add(_dialogue);

            _outputFolder = new TextField("生成目录") { value = CharForgeSettings.OutputAssetFolder };
            _outputFolder.RegisterValueChangedCallback(e => CharForgeSettings.OutputAssetFolder = e.newValue);
            host.Add(_outputFolder);
        }

        private void BuildOptionsSection(VisualElement host)
        {
            if (host == null) return;

            _comfyUrl = new TextField("ComfyUI 地址") { value = CharForgeSettings.ComfyUiUrl };
            _comfyUrl.RegisterValueChangedCallback(e => CharForgeSettings.ComfyUiUrl = e.newValue);
            host.Add(_comfyUrl);

            var wfRow = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            _comfyWorkflow = new TextField("工作流 JSON") { value = CharForgeSettings.ComfyUiWorkflowPath };
            _comfyWorkflow.style.flexGrow = 1;
            _comfyWorkflow.RegisterValueChangedCallback(e => CharForgeSettings.ComfyUiWorkflowPath = e.newValue);
            var btnPickWorkflow = new Button(() =>
            {
                string path = EditorUtility.OpenFilePanel("选择 ComfyUI API 格式工作流", "", "json");
                if (!string.IsNullOrEmpty(path)) _comfyWorkflow.value = path;
            })
            { text = "选择…" };
            wfRow.Add(_comfyWorkflow);
            wfRow.Add(btnPickWorkflow);
            host.Add(wfRow);

            _comfyLaunch = new TextField("ComfyUI 启动命令（可留空）")
            {
                value = CharForgeSettings.ComfyUiLaunchCommand
            };
            _comfyLaunch.tooltip = "填了则由插件代管启动与关闭；留空则只做被动探测。";
            _comfyLaunch.RegisterValueChangedCallback(e => CharForgeSettings.ComfyUiLaunchCommand = e.newValue);
            host.Add(_comfyLaunch);

            _sidecarUrl = new TextField("Python 服务地址") { value = CharForgeSettings.SidecarUrl };
            _sidecarUrl.RegisterValueChangedCallback(e => CharForgeSettings.SidecarUrl = e.newValue);
            host.Add(_sidecarUrl);

            _sidecarPython = new TextField("Python 可执行文件") { value = CharForgeSettings.SidecarPythonExe };
            _sidecarPython.RegisterValueChangedCallback(e => CharForgeSettings.SidecarPythonExe = e.newValue);
            host.Add(_sidecarPython);

            var dirRow = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            _sidecarWorkDir = new TextField("服务工作目录") { value = CharForgeSettings.SidecarWorkingDir };
            _sidecarWorkDir.style.flexGrow = 1;
            _sidecarWorkDir.RegisterValueChangedCallback(e => CharForgeSettings.SidecarWorkingDir = e.newValue);
            var btnPickDir = new Button(() =>
            {
                string path = EditorUtility.OpenFolderPanel("选择仓库根目录（含 server/ 的那一层）", "", "");
                if (!string.IsNullOrEmpty(path)) _sidecarWorkDir.value = path;
            })
            { text = "选择…" };
            dirRow.Add(_sidecarWorkDir);
            dirRow.Add(btnPickDir);
            host.Add(dirRow);

            _sentisModel = new TextField("Sentis 模型（工程内路径）")
            {
                value = CharForgeSettings.SentisModelAssetPath
            };
            _sentisModel.RegisterValueChangedCallback(e => CharForgeSettings.SentisModelAssetPath = e.newValue);
            host.Add(_sentisModel);

            _sentisExperimental = new Toggle("启用引擎内推理（实验性）")
            {
                value = CharForgeSettings.SentisExperimental
            };
            _sentisExperimental.RegisterValueChangedCallback(e => CharForgeSettings.SentisExperimental = e.newValue);
            host.Add(_sentisExperimental);
        }

        /// <summary>UXML 缺失时的兜底骨架：宁可样式朴素，也不能白屏。</summary>
        private void BuildFallbackTree(string attemptedPath)
        {
            var root = new VisualElement { name = "root" };
            root.AddToClassList("cf-root");

            var header = new VisualElement();
            header.AddToClassList("cf-header");
            var title = new Label("CharForge · 2D 角色资产流水线");
            title.AddToClassList("cf-title");
            header.Add(title);
            root.Add(header);

            var body = new ScrollView { name = "body" };
            body.AddToClassList("cf-body");
            foreach (var (name, text) in new[]
            {
                ("section-backend", "① 推理后端（可替换）"),
                ("section-job", "② 角色描述"),
                ("section-output", "③ 方向 / 动作 / 移动"),
                ("section-options", "④ 后端设置")
            })
            {
                body.Add(new Foldout { name = name, text = text, value = true });
            }

            var actions = new VisualElement();
            actions.AddToClassList("cf-actions");
            actions.Add(new Button { name = "btn-generate", text = "生成并装配" });
            actions.Add(new Button { name = "btn-reassemble", text = "仅重新装配" });
            actions.Add(new Button { name = "btn-open-output", text = "打开产物目录" });
            body.Add(actions);
            root.Add(body);

            var footer = new VisualElement();
            root.Add(footer);

            var progress = new ProgressBar { name = "progress", title = "就绪" };
            footer.Add(progress);
            footer.Add(new Label { name = "next-action" });

            var log = new ScrollView { name = "log" };
            log.AddToClassList("cf-log");
            root.Add(log);

            rootVisualElement.Add(root);
            Debug.LogWarning(
                $"[CharForge] 未找到界面定义文件（{attemptedPath ?? UxmlFileName}），已使用兜底界面。" +
                "功能不受影响；若希望恢复完整版式，请确认包内 Editor/UI/ 下的 .uxml 与 .uss 未被删除或裁剪。");
        }

        /// <summary>
        /// 定位包内界面资产。两级策略：
        /// 1. 用本程序集反查所属包（对三种安装形态都成立）；
        /// 2. 按资产类型全库搜索（包被改名 / 被嵌入到非标准路径时的兜底）。
        /// 找不到返回 null，由调用方决定是回落还是提示——不抛异常。
        /// </summary>
        private static string ResolvePackageAssetPath(string fileName)
        {
            try
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                    typeof(CharacterForgeWindow).Assembly);
                if (info != null && !string.IsNullOrEmpty(info.assetPath))
                {
                    string candidate = $"{info.assetPath}/Editor/UI/{fileName}";

                    // 显式写 UnityEngine.Object：本文件 using 了 System，裸写 Object 会在
                    // UnityEngine.Object 与 System.Object 之间产生 CS0104 二义。
                    if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(candidate) != null) return candidate;
                }
            }
            catch (Exception)
            {
                // 极少数环境下 PackageInfo 反查失败；直接走下面的全库搜索。
            }

            string typeFilter = fileName.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase)
                ? "t:VisualTreeAsset"
                : "t:StyleSheet";
            string stem = Path.GetFileNameWithoutExtension(fileName);

            foreach (var guid in AssetDatabase.FindAssets($"{stem} {typeFilter}"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path) &&
                    path.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return path;
                }
            }
            return null;
        }

        private void LoadSettingsIntoUi()
        {
            int backendIdx = Array.IndexOf(BackendIds, CharForgeSettings.BackendId);
            _backend.index = backendIdx < 0 ? 0 : backendIdx;

            _framesPerClip.value = CharForgeSettings.FramesPerClip;
            _preset.index = 0;      // 默认「自定义（沿用参考图）」
            _movement.index = 0;    // 默认不移动——先拿到能站着的角色，再谈走路
        }

        /// <summary>只显示当前后端相关的设置项——一屏塞满所有后端的参数没人愿意读。</summary>
        private void ApplyBackendVisibility()
        {
            string id = BackendIds[Mathf.Clamp(_backend?.index ?? 0, 0, BackendIds.Length - 1)];
            bool comfy = id == "comfyui" || id == "auto";
            bool sidecar = id == "sidecar" || id == "auto";
            bool sentis = id == "sentis" || id == "auto";

            Show(_comfyUrl, comfy);
            Show(_comfyWorkflow?.parent, comfy);
            Show(_comfyLaunch, comfy);
            Show(_sidecarUrl, sidecar);
            Show(_sidecarPython, sidecar);
            Show(_sidecarWorkDir?.parent, sidecar);
            Show(_sentisModel, sentis);
            Show(_sentisExperimental, sentis);
        }

        /// <summary>
        /// 显隐切换走 <c>display</c> 而非 <c>visible</c>：后者保留布局占位，
        /// 切换后端时会在选项区留下一片空白，看起来像界面坏了。
        /// </summary>
        private static void Show(VisualElement element, bool visible)
        {
            if (element == null) return;
            element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // ────────────────────────── 后端操作 ──────────────────────────

        private async Task ProbeAsync()
        {
            SetBusy(true, "探测后端…");
            // 徽标此前只在初始化时写一次「未探测」，探测完也不更新——用户会一直以为没测过。
            if (_backendStatus != null) _backendStatus.text = "探测中…";
            try
            {
                var ready = new List<string>();
                foreach (var backend in InferenceBackendRegistry.All())
                {
                    ApplyBackendSettings(backend);
                    BackendStatus status;
                    try
                    {
                        status = await backend.ProbeAsync(CancellationToken.None);
                    }
                    catch (Exception e)
                    {
                        status = BackendStatus.Fail($"探测异常：{e.Message}", "");
                    }

                    Log($"[{(status != null && status.Ready ? "可用" : "不可用")}] {backend.DisplayName} — {status?.Message}");
                    if (status != null && status.Ready) ready.Add(backend.DisplayName);
                }

                if (_backendStatus != null)
                    _backendStatus.text = ready.Count > 0 ? $"可用 {ready.Count} 个" : "全部不可用";

                SetStatus(ready.Count > 0
                    ? $"可用后端 {ready.Count} 个：{string.Join("、", ready)}"
                    : "没有可用后端",
                    ready.Count > 0 ? "" : "切到「占位后端（零依赖）」即可立刻跑通全流程，再逐步接入真实模型。");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task StartBackendAsync()
        {
            string id = BackendIds[Mathf.Clamp(_backend.index, 0, BackendIds.Length - 1)];
            SetBusy(true, "启动后端…");
            try
            {
                if (id == "comfyui" || id == "auto")
                {
                    var result = await BackendProcessManager.EnsureRunningAsync(
                        BackendProcessManager.BuildComfyProfile(), Log, CancellationToken.None);
                    if (result.Running)
                    {
                        SetStatus(result.Message + (result.AlreadyWasRunning ? "（复用已有进程）" : ""), "");
                        return;
                    }
                    Log($"ComfyUI：{result.Message}");
                }

                if (id == "sidecar" || id == "auto")
                {
                    var result = await BackendProcessManager.EnsureRunningAsync(
                        BackendProcessManager.BuildSidecarProfile(), Log, CancellationToken.None);
                    SetStatus(result.Message, result.NextAction);
                    return;
                }

                SetStatus("该后端不需要外部进程。", "点「探测后端」确认状态即可。");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void StopBackend()
        {
            foreach (var name in BackendProcessManager.ManagedNames().ToList())
                Log($"停止托管进程：{name}");

            BackendProcessManager.Stop(BackendProcessManager.PortFromUrl(CharForgeSettings.ComfyUiUrl));
            BackendProcessManager.Stop(BackendProcessManager.PortFromUrl(CharForgeSettings.SidecarUrl));
            SetStatus("已停止由插件托管的进程。", "若你手动启动过服务，需要自行关闭。");
        }

        private static void ApplyBackendSettings(IInferenceBackend backend)
        {
            switch (backend)
            {
                case ComfyUiBackend comfy:
                    comfy.BaseUrl = CharForgeSettings.ComfyUiUrl;
                    comfy.WorkflowApiJsonPath = CharForgeSettings.ComfyUiWorkflowPath;
                    break;
                case SidecarBackend sidecar:
                    sidecar.BaseUrl = CharForgeSettings.SidecarUrl;
                    break;
                case SentisBackend sentis:
                    sentis.ModelAssetPath = CharForgeSettings.SentisModelAssetPath;
                    sentis.EnableExperimental = CharForgeSettings.SentisExperimental;
                    break;
            }
        }

        // ────────────────────────── 生成 ──────────────────────────

        private async Task RunGenerateAsync()
        {
            if (_busy) return;

            // BuildRequest 在 try 之外时，一旦抛异常会被 `() => _ = RunGenerateAsync()`
            // 整个吞掉——按钮点了没反应、日志里一个字都没有，看起来就像「按钮坏了」。
            // 表单读取属于可预期失败，必须就地接住并把原因写进窗口日志。
            JobRequest request;
            try
            {
                request = BuildRequest();
            }
            catch (Exception e)
            {
                Log($"表单读取失败：{e.GetType().Name}：{e.Message}");
                SetStatus($"表单读取失败：{e.Message}", "把 Console 里的完整堆栈贴给维护者。");
                Debug.LogException(e);
                return;
            }

            if (string.IsNullOrWhiteSpace(request.Prompt))
            {
                Log("请先填写角色描述。");
                SetStatus("请先填写角色描述。", "一句话即可，例如「博物馆导览员，身着青灰色长衫」。");
                return;
            }

            CharForgeSettings.BackendId = BackendIds[Mathf.Clamp(_backend.index, 0, BackendIds.Length - 1)];

            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            SetBusy(true, "开始生成…");
            Log($"===== 新任务 {DateTime.Now:HH:mm:ss} =====");

            try
            {
                if (CharForgeSettings.AutoStartBackend)
                {
                    var profile = CharForgeSettings.BackendId == "comfyui"
                        ? BackendProcessManager.BuildComfyProfile()
                        : CharForgeSettings.BackendId == "sidecar"
                            ? BackendProcessManager.BuildSidecarProfile()
                            : null;

                    if (profile != null)
                    {
                        var ensure = await BackendProcessManager.EnsureRunningAsync(profile, Log, _cts.Token);
                        if (!ensure.Running) Log($"后端未就绪：{ensure.Message}（{ensure.NextAction}）");
                    }
                }

                var result = await CharacterForgePipeline.RunAsync(
                    request,
                    CharForgeSettings.BackendId,
                    OnPipelineProgress,
                    _cts.Token);

                if (result.Ok && result.Assemble != null && result.Assemble.ok)
                {
                    _lastOutputDir = result.OutputDir;
                    SetStatus($"完成 → {result.Assemble.prefabAssetPath}", "");
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(result.Assemble.prefabAssetPath);
                    if (prefab != null)
                    {
                        EditorGUIUtility.PingObject(prefab);
                        Selection.activeObject = prefab;
                    }
                }
                else
                {
                    SetStatus(result.Message, result.NextAction);
                }
            }
            catch (OperationCanceledException)
            {
                SetStatus("已取消。", "");
            }
            catch (Exception e)
            {
                SetStatus($"意外错误：{e.Message}", "查看 Console 堆栈。");
                Debug.LogException(e);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task RunReassembleAsync()
        {
            if (_busy) return;
            if (string.IsNullOrEmpty(_lastOutputDir) || !Directory.Exists(_lastOutputDir))
            {
                SetStatus("还没有可复用的产物。", "先跑一次「生成并装配」。");
                return;
            }

            SetBusy(true, "重新装配…");
            try
            {
                var report = await EditorMainThread.RunAsync(() => PrefabAssembler.Assemble(_lastOutputDir));
                foreach (var m in report.messages) Log(m);
                SetStatus(report.ok ? $"重新装配成功 → {report.prefabAssetPath}" : "重新装配失败",
                    report.ok ? "" : "看日志：多半是产物目录被移动过。");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void OnPipelineProgress(PipelineProgress progress)
        {
            if (progress == null) return;
            EditorMainThread.Post(() =>
            {
                if (_progress != null)
                {
                    _progress.title = $"{progress.Stage} · {progress.Message}";
                    if (progress.Indeterminate)
                    {
                        // 委托给外部服务时无法得知真实进度——显示"忙"比编一个假百分比诚实。
                        _progress.value = 50f;
                        _progress.MarkDirtyRepaint();
                    }
                    else
                    {
                        _progress.value = Mathf.Clamp(progress.Percent, 0f, 100f);
                    }
                }
            });
            if (!string.IsNullOrEmpty(progress.Message)) Log(progress.Message);
        }

        private JobRequest BuildRequest()
        {
            var actions = new List<string>();
            if (_actIdle.value) actions.Add("idle");
            if (_actWalk.value) actions.Add("walk");
            if (actions.Count == 0) actions.Add("idle");

            var directions = new List<string>();
            for (int i = 0; i < 4; i++)
                if (_dirs[i].value) directions.Add(DirectionKeys[i]);
            if (directions.Count == 0) directions.Add("south");

            var refs = _refImages
                .Where(f => f != null && f.value != null)
                .Select(f => AssetDatabase.GetAssetPath(f.value))
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(Path.GetFullPath)
                .ToList();

            CharForgeSettings.FramesPerClip = _framesPerClip.value;

            return new JobRequest
            {
                Prompt = _prompt.value,
                CharacterName = string.IsNullOrWhiteSpace(_charName.value) ? null : _charName.value.Trim(),
                EraOrPeriod = string.IsNullOrWhiteSpace(_era.value) ? null : _era.value.Trim(),
                ReferenceNotes = string.IsNullOrWhiteSpace(_refNotes.value) ? null : _refNotes.value.Trim(),
                ReferenceImages = refs,
                StylePreset = PresetIds[Mathf.Clamp(_preset.index, 0, PresetIds.Length - 1)],
                Actions = actions,
                Directions = directions,
                FramesPerClip = _framesPerClip.value,
                DialogueEnabled = _dialogue.value,
                Forbidden = new List<string>(),
                Movement = new MovementSpec
                {
                    Mode = MovementIds[Mathf.Clamp(_movement.index, 0, MovementIds.Length - 1)],
                    Speed = 1.5f,
                    PatrolPoints = new List<float[]>()
                }
            };
        }

        private void OpenOutputFolder()
        {
            string path = string.IsNullOrEmpty(_lastOutputDir) ? null : _lastOutputDir;
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                var folder = ProjectAssetWriter.ToAbsolute(CharForgeSettings.OutputAssetFolder);
                path = Directory.Exists(folder) ? folder : null;
            }

            if (string.IsNullOrEmpty(path))
            {
                SetStatus("还没有产物目录。", "先跑一次生成。");
                return;
            }
            EditorUtility.RevealInFinder(path);
        }

        // ────────────────────────── 状态与日志 ──────────────────────────

        private void SetBusy(bool busy, string status = null)
        {
            _busy = busy;
            if (_btnGenerate != null)
            {
                _btnGenerate.SetEnabled(!busy);
                _btnGenerate.text = busy ? "生成中…" : "生成并装配";
            }
            _btnReassemble?.SetEnabled(!busy);
            if (!string.IsNullOrEmpty(status)) SetStatus(status, "");
        }

        private void SetStatus(string message, string nextAction)
        {
            if (_progress != null) _progress.title = message;
            if (_nextAction != null) _nextAction.text = string.IsNullOrEmpty(nextAction) ? "" : $"下一步：{nextAction}";
            if (!string.IsNullOrEmpty(message)) Debug.Log($"[CharForge] {message}");
        }

        private void Log(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            EditorMainThread.Post(() =>
            {
                string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
                _logLines.Add(line);
                if (_logLines.Count > 500) _logLines.RemoveAt(0);

                if (_log == null) return;

                var label = new Label(line);
                label.AddToClassList("cf-log-label");
                _log.Add(label);

                // 只保留最近 300 条，避免长任务把编辑器拖慢
                while (_log.childCount > 300) _log.RemoveAt(0);
                _log.ScrollTo(label);
            });
        }
    }
}
