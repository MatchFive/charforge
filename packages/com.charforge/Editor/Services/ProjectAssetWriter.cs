using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace CharForge.EditorTools
{
    /// <summary>
    /// 工程内资产落盘工具。
    ///
    /// 只有一件事必须记牢：**Unity 只认 Assets/ 下的文件**。
    /// 独立工具生成的产物在磁盘任意位置，用户得自己拷进来；插件则可以直接写进工程并立即刷新，
    /// 这就是"产物可拖进场景"与"产物是一个文件夹"的差别。
    /// </summary>
    public static class ProjectAssetWriter
    {
        /// <summary>把 "Assets/CharForge/xxx" 之类的工程路径转成系统绝对路径。</summary>
        public static string ToAbsolute(string assetPath)
        {
            string p = assetPath.Replace('\\', '/');
            if (p.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase))
                p = p.Substring("Assets/".Length);
            else if (p == "Assets")
                p = "";
            return Path.Combine(Application.dataPath, p);
        }

        /// <summary>逐级创建工程文件夹，并返回规范化后的 Assets/ 路径。</summary>
        public static string EnsureAssetFolder(string assetPath)
        {
            string normalized = assetPath.Replace('\\', '/').TrimEnd('/');
            if (!normalized.StartsWith("Assets", System.StringComparison.OrdinalIgnoreCase))
                normalized = "Assets/" + normalized.TrimStart('/');

            string absolute = ToAbsolute(normalized);
            if (!Directory.Exists(absolute)) Directory.CreateDirectory(absolute);

            // AssetDatabase 需要知道新目录的存在才能正确生成 .meta
            AssetDatabase.Refresh();
            return normalized;
        }

        public static void WriteBytes(string assetPath, byte[] bytes)
        {
            string absolute = ToAbsolute(assetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute) ?? ".");
            File.WriteAllBytes(absolute, bytes);
        }

        public static void WriteText(string assetPath, string text)
        {
            string absolute = ToAbsolute(assetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute) ?? ".");
            File.WriteAllText(absolute, text, new System.Text.UTF8Encoding(false));
        }

        /// <summary>
        /// 把后端产物目录里的四份关键文件同步进工程。
        /// 只同步"装配必需的"，中间帧不回拷——避免把工程撑大，也因为装配只需要 sheet + 三份 JSON。
        /// </summary>
        public static readonly string[] EssentialArtifacts =
        {
            "sheet.png", "manifest.json", "assembly_spec.json", "persona.json"
        };

        public static List<string> SyncJobArtifacts(string jobDir, string assetDir)
        {
            var messages = new List<string>();
            string jobAbsolute = Path.GetFullPath(jobDir);

            string[] optional = { "style_profile.json", "character_card.json" };
            var names = new List<string>(EssentialArtifacts);
            names.AddRange(optional);

            foreach (var name in names)
            {
                string src = Path.Combine(jobAbsolute, name);
                if (!File.Exists(src)) continue;
                File.Copy(src, ToAbsolute($"{assetDir}/{name}"), overwrite: true);
            }

            AssetDatabase.Refresh();
            messages.Add($"产物已同步到 {assetDir}");
            return messages;
        }
    }
}
