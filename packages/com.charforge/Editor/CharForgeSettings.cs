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

        // ── 表单输入（② 角色描述区）────────────────────────────
        //
        // 为什么要持久化：这些字段此前只活在窗口实例里，重开窗口就被重置回默认值，
        // 而「年代 / 形制约束」「藏品档案 / 考据要点」正是 persona.json 里【知识边界】的
        // 唯一来源。静默清空会直接导致产出的人设丢掉文博考据——画面看不出来，
        // 只有翻开 persona.json 才发现知识边界是空的。
        public const string DefaultPrompt = "博物馆导览员，身着素雅青灰色长衫，手持讲解册，神态温和";

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

        /// <summary>年代 / 形制约束，例如「南宋」。进提示词，也进人设知识边界。</summary>
        public static string EraOrPeriod
        {
            get => Get("EraOrPeriod", "");
            set => Set("EraOrPeriod", value ?? "");
        }

        /// <summary>藏品档案 / 考据要点。进 persona.json 的【知识边界】。</summary>
        public static string ReferenceNotes
        {
            get => Get("ReferenceNotes", "");
            set => Set("ReferenceNotes", value ?? "");
        }
    }
}
