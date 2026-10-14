using System;
using System.IO;
using System.Text;
using CharForge.EditorTools.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CharForge.EditorTools.Services
{
    /// <summary>
    /// 第 1 步的人工检查留痕：把「角色描述 → 锻造提示词 → 立绘 PNG」整条链落盘到
    /// <c>&lt;Unity 工程&gt;/CharForge/forge_log/</c>。
    ///
    /// **为什么要留**：最终提示词是三段拼出来的——用户写的描述、模型扩写的角色本体、
    /// 插件注入的画风前缀与版式后缀。混成一段之后事后无从分辨哪句是谁写的，
    /// 出图不对时只能靠猜。留一组原始记录，问题就能直接定位到具体一环：
    /// 是描述没说清、是模型写偏了、还是插件的画风件在打架。
    ///
    /// **为什么不放 Assets/**：它是排查用的中间物，不是游戏资产。放 Assets 会被 Unity 导入
    /// （1024×1024 的 PNG 还要生成 .meta），而它永远不该进 prefab。放工程根则是纯文件系统访问，
    /// 与配置目录（<see cref="CharForgePaths.UserRoot"/>，即 <c>&lt;工程&gt;/CharForge</c>）同一棵树。
    /// 日志里会打印绝对路径，复制到资源管理器即可打开。
    ///
    /// **写失败一律不抛**：留痕是辅助功能，磁盘满 / 目录只读不该把已经出好的立绘弄丢。
    /// 但必须回报 ⚠ 告警——静默写不进去等于让人以为「已经留了」，那比不留更坏。
    /// </summary>
    public static class ForgeArchive
    {
        /// <summary>留痕目录：<c>&lt;Unity 工程&gt;/CharForge/forge_log</c>（工程外，不会被导入）。</summary>
        public static string Dir => Path.Combine(CharForgePaths.UserRoot, "forge_log");

        private const string NL = "\r\n";

        /// <summary>
        /// 同一次第 1 步共用一个 stem：<c>&lt;yyyyMMdd-HHmmss&gt;_&lt;角色名&gt;</c>。
        /// txt / json / png 同名不同扩展 —— 一组文件挨在一起，配对查看不用翻目录。
        /// </summary>
        public static string NewStem(string title) => $"{DateTime.Now:yyyyMMdd-HHmmss}_{Slug(title)}";

        /// <summary>
        /// 锻造留痕：写 <c>&lt;stem&gt;.txt</c>（人读，分段标注哪段是谁写的）与
        /// <c>&lt;stem&gt;.json</c>（机器读，便于以后做提示词回归对比）。
        /// 返回 txt 的绝对路径；失败返回 null（并已 log 告警）。
        /// </summary>
        public static string WriteForge(string stem, string title, string description,
            PromptForgeService.ForgeResult result, Action<string> log)
        {
            if (string.IsNullOrEmpty(stem)) stem = NewStem(title);
            if (result == null) return null;

            string txtPath = null;
            try
            {
                Directory.CreateDirectory(Dir);
                string name = string.IsNullOrWhiteSpace(title) ? "（未命名）" : title.Trim();

                var sb = new StringBuilder();
                sb.Append("CharForge · 第 1 步留痕").Append(NL);
                sb.Append("生成时间：").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append(NL);
                sb.Append("角色名：").Append(name).Append(NL);
                sb.Append("提示词来源：").Append(result.Source ?? "未知").Append(NL);
                sb.Append("警告：").Append(string.IsNullOrEmpty(result.Warning) ? "无" : result.Warning).Append(NL);
                sb.Append(NL);

                sb.Append("── 1. 角色描述（你写的） ──────────────────────").Append(NL);
                sb.Append(string.IsNullOrWhiteSpace(description) ? "（空）" : description.Trim()).Append(NL).Append(NL);

                sb.Append("── 2. 画风前缀（插件注入，模型无权改） ─────────").Append(NL);
                sb.Append(PromptForgeService.StylePrefix).Append(NL).Append(NL);

                sb.Append("── 3. 角色本体（模型/模板扩写的部分） ──────────").Append(NL);
                sb.Append(string.IsNullOrWhiteSpace(result.Body) ? "（空——若为空说明正文被剥干了，见「警告」）" : result.Body)
                  .Append(NL).Append(NL);

                sb.Append("── 4. 版式后缀（插件注入） ────────────────────").Append(NL);
                sb.Append(PromptForgeService.LayoutSuffix).Append(NL).Append(NL);

                sb.Append("── 5. 最终提示词（送进 ComfyUI 的就是这段） ────").Append(NL);
                sb.Append(result.Prompt).Append(NL).Append(NL);

                sb.Append("── 6. 立绘 ────────────────────────────────────").Append(NL);
                sb.Append("点「生成角色立绘」后，同目录出现 ").Append(stem).Append("_portrait.png").Append(NL);

                txtPath = Path.Combine(Dir, stem + ".txt");
                // txt 带 BOM：Windows 记事本对无 BOM 的中文 UTF-8 仍可能猜错编码。
                File.WriteAllText(txtPath, sb.ToString(), new UTF8Encoding(true));

                // 机器可读的那份。字段名用下划线式，方便以后用脚本批量比对不同版本的提示词。
                var json = new JObject
                {
                    ["schema"] = "charforge.forge_log/1",
                    ["time"] = DateTime.Now.ToString("o"),
                    ["character_name"] = name,
                    ["description"] = description?.Trim() ?? "",
                    ["style_prefix"] = PromptForgeService.StylePrefix,
                    ["layout_suffix"] = PromptForgeService.LayoutSuffix,
                    ["model_body"] = result.Body ?? "",
                    ["final_prompt"] = result.Prompt ?? "",
                    ["source"] = result.Source ?? "",
                    ["warning"] = string.IsNullOrEmpty(result.Warning) ? JValue.CreateNull() : new JValue(result.Warning),
                    ["portrait_file"] = JValue.CreateNull()
                };
                // 无 BOM：解析器（Newtonsoft / python json）对带 BOM 的 JSON 处理不一，不给自己找麻烦。
                File.WriteAllText(Path.Combine(Dir, stem + ".json"),
                    json.ToString(Formatting.Indented), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                log?.Invoke($"⚠ 提示词留痕写盘失败（不影响出图）：{e.Message} → 目标目录 {Dir}");
                return null;
            }

            log?.Invoke($"提示词已留痕：{txtPath}");
            return txtPath;
        }

        /// <summary>
        /// 立绘留痕：写 <c>&lt;stem&gt;_portrait.png</c>。返回绝对路径；失败返回 null（并已 log 告警）。
        ///
        /// 与产物目录里的帧（<c>Assets/CharForge/…/Frames/</c>）刻意分开：立绘是**中间物**，
        /// 第 2 步只把它当身份参考图上传，它本身不进 prefab、也不该被 Unity 当 Sprite 导入。
        /// 但要落盘——不落盘就只能在窗口那 200×200 的预览框里看，看不出画风到底对不对。
        /// </summary>
        public static string WritePortrait(string stem, byte[] png, Action<string> log)
        {
            if (png == null || png.Length == 0) return null;
            if (string.IsNullOrEmpty(stem)) stem = NewStem(null);

            try
            {
                Directory.CreateDirectory(Dir);
                string path = Path.Combine(Dir, stem + "_portrait.png");
                File.WriteAllBytes(path, png);
                log?.Invoke($"立绘已留痕：{path}");
                return path;
            }
            catch (Exception e)
            {
                log?.Invoke($"⚠ 立绘留痕写盘失败（图仍在窗口预览里，可重跑）：{e.Message} → 目标目录 {Dir}");
                return null;
            }
        }

        /// <summary>
        /// 文件名安全片段。**刻意保留中文**：这是本地排查用的留痕目录，可读性优先
        /// ——时间戳排序里一眼认出是哪次。只剔除路径非法字符与控制字符，并限长。
        /// （与 prefab / 产物目录名的取舍不同，那边要跨平台，所以一律 ASCII。）
        /// </summary>
        private static string Slug(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return "unnamed";

            var sb = new StringBuilder();
            foreach (char c in title.Trim())
            {
                if (char.IsControl(c)) continue;
                sb.Append("\\/:*?\"<>|".IndexOf(c) >= 0 ? '_' : c);
            }

            string s = sb.ToString().Trim().Trim('.', ' ');
            if (s.Length == 0) return "unnamed";
            return s.Length <= 24 ? s : s.Substring(0, 24);
        }
    }
}
