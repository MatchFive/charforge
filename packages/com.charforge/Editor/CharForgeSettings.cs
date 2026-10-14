using UnityEditor;
using UnityEngine;

namespace CharForge.EditorTools
{
    /// <summary>
    /// 插件设置（EditorPrefs 持久化）。
    ///
    /// 刻意不用 ScriptableObject：插件配置属于"这台机器上的这个编辑器"，
    /// 不该被提交进版本库，也不该因为切分支而漂移。
    /// 需要团队共享的配置（风格预设、姿势模板）放在 <c>configs/</c> 里走版本控制，两者职责分开。
    /// </summary>
    public static class CharForgeSettings
    {
        private const string Prefix = "CharForge.";

        private static string Get(string key, string fallback) =>
            EditorPrefs.GetString(Prefix + key, fallback);

        private static void Set(string key, string value) => EditorPrefs.SetString(Prefix + key, value);

        private static bool GetBool(string key, bool fallback) =>
            EditorPrefs.GetBool(Prefix + key, fallback);

        private static void SetBool(string key, bool value) => EditorPrefs.SetBool(Prefix + key, value);

        private static int GetInt(string key, int fallback) => EditorPrefs.GetInt(Prefix + key, fallback);
        private static void SetInt(string key, int value) => EditorPrefs.SetInt(Prefix + key, value);

        // ── 后端选择 ──────────────────────────────────────────
        /// <summary>auto | mock | sentis | comfyui | sidecar | http</summary>
        public static string BackendId
        {
            get => Get("BackendId", "auto");
            set => Set("BackendId", value);
        }

        /// <summary>允许插件代为启动本地后端进程。</summary>
        public static bool AutoStartBackend
        {
            get => GetBool("AutoStartBackend", true);
            set => SetBool("AutoStartBackend", value);
        }

        // ── ComfyUI ──────────────────────────────────────────
        public static string ComfyUiUrl
        {
            get => Get("ComfyUiUrl", "http://127.0.0.1:8188");
            set => Set("ComfyUiUrl", value);
        }

        public static string ComfyUiWorkflowPath
        {
            get => Get("ComfyUiWorkflowPath", "");
            set => Set("ComfyUiWorkflowPath", value);
        }

        /// <summary>ComfyUI 启动命令（留空则不代管，只做被动探测）。</summary>
        public static string ComfyUiLaunchCommand
        {
            get => Get("ComfyUiLaunchCommand", "");
            set => Set("ComfyUiLaunchCommand", value);
        }

        // ── 自带 Python 服务 ──────────────────────────────────
        public static string SidecarUrl
        {
            get => Get("SidecarUrl", "http://127.0.0.1:8000");
            set => Set("SidecarUrl", value);
        }

        public static string SidecarPythonExe
        {
            get => Get("SidecarPythonExe", "python");
            set => Set("SidecarPythonExe", value);
        }

        public static string SidecarWorkingDir
        {
            get => Get("SidecarWorkingDir", "");
            set => Set("SidecarWorkingDir", value);
        }

        // ── Sentis ───────────────────────────────────────────
        public static string SentisModelAssetPath
        {
            get => Get("SentisModelAssetPath", "");
            set => Set("SentisModelAssetPath", value);
        }

        public static bool SentisExperimental
        {
            get => GetBool("SentisExperimental", false);
            set => SetBool("SentisExperimental", value);
        }

        // ── 产物 ─────────────────────────────────────────────
        /// <summary>生成资产写入的工程目录。默认 Assets/CharForge。</summary>
        public static string OutputAssetFolder
        {
            get => Get("OutputAssetFolder", "Assets/CharForge");
            set => Set("OutputAssetFolder", value);
        }

        public static int FramesPerClip
        {
            get => GetInt("FramesPerClip", 4);
            set => SetInt("FramesPerClip", Mathf.Clamp(value, 2, 8));
        }

        // ── 表单输入（第 1 步 · 立绘）──────────────────────────
        //
        // 为什么要持久化：这些字段此前只活在窗口实例里，重开窗口就被重置回默认值——
        // 静默清空会让人白填一遍描述，还以为是编辑器抽风。
        public const string DefaultPrompt = "博物馆导览员，身着素雅青灰色长衫，手持讲解册，神态温和";

        /// <summary>角色描述（自然语言，供 LLM 锻造成提示词）。</summary>
        public static string Prompt
        {
            get => Get("Prompt", DefaultPrompt);
            set => Set("Prompt", value ?? "");
        }

        public static string CharacterName
        {
            get => Get("CharacterName", "");
            set => Set("CharacterName", value ?? "");
        }

        /// <summary>
        /// 文生图提示词（LLM 产物，用户可改）。留空时第 1 步按本地模板现拼一条。
        /// 单独持久化是因为它是**可复用资产**：同一条提示词反复出立绘、换 seed 试稿，
        /// 不该每次重跑一遍 LLM。
        /// </summary>
        public static string PortraitPrompt
        {
            get => Get("PortraitPrompt", "");
            set => Set("PortraitPrompt", value ?? "");
        }

        // ── 外部 LLM（提示词锻造）──────────────────────────────
        //
        // 刻意只做「OpenAI 兼容」这一种协议：DeepSeek、通义、智谱、OpenAI、本地 vLLM
        // 都提供 /chat/completions，填三个字段就能接。多一种协议就多一份维护成本，
        // 而这一步的需求仅仅是「把一句话写开」。
        // 三个字段任一为空 → 自动退回本地模板拼接，并在日志里明说走了哪条路（不许静默）。
        public static string LlmBaseUrl
        {
            get => Get("LlmBaseUrl", "");
            set => Set("LlmBaseUrl", value ?? "");
        }

        public static string LlmApiKey
        {
            get => Get("LlmApiKey", "");
            set => Set("LlmApiKey", value ?? "");
        }

        public static string LlmModel
        {
            get => Get("LlmModel", "");
            set => Set("LlmModel", value ?? "");
        }

        /// <summary>
        /// 请求关闭模型的思考模式（连同一个 <c>chat_template_kwargs.enable_thinking=false</c>）。
        ///
        /// **默认关**：不是所有网关都认这个字段，严格的网关会因未知参数直接回 400，
        /// 所以不能默认发。只有当你用的模型（Qwen3 思考版、DeepSeek-R1 一类）把思维链
        /// 混在 content 里返回、导致提示词被污染时，才打开它。
        /// 插件侧无论如何都会剥一遍思考块，这个开关是**从源头省掉**那一步。
        /// </summary>
        public static bool LlmNoThink
        {
            get => GetBool("LlmNoThink", false);
            set => SetBool("LlmNoThink", value);
        }

        /// <summary>第 1 步立绘用的 t2i 工作流文件名（相对 Configs/comfyui/）。</summary>
        public static string PortraitWorkflowFile
        {
            get => Get("PortraitWorkflowFile", "charforge_qwen21_txt2img.workflow.json");
            set => Set("PortraitWorkflowFile", value ?? "");
        }

        /// <summary>
        /// 帧动画用的 keep_pose 工作流文件名（相对 Configs/comfyui/）。
        /// v0.4.8 起帧动画整体切换为 keep_pose 动作模仿路线，原 2509 整表路线已删除
        /// （旧设置键 SpritelyWorkflowFile 不再读取，残留值无害）。
        /// </summary>
        public static string KeepPoseWorkflowFile
        {
            get => Get("KeepPoseWorkflowFile", "charforge_keepose_pose.workflow.json");
            set => Set("KeepPoseWorkflowFile", value ?? "");
        }

        /// <summary>动画帧率（Spritely 官方 GIF 同款 8 fps）。</summary>
        public static int SpriteFrameRate
        {
            get => GetInt("SpriteFrameRate", 8);
            set => SetInt("SpriteFrameRate", Mathf.Clamp(value, 1, 30));
        }

    }
}
