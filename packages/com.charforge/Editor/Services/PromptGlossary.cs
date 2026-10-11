using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CharForge.EditorTools.Data;
using CharForge.EditorTools.Util;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CharForge.EditorTools.Services
{
    /// <summary>
    /// 把提示词里的中文术语翻成英文。
    ///
    /// **为什么必须做这一步**：Stable Diffusion / SDXL 的文本编码器用的是英文 CLIP 词表，
    /// **汉字会被整段丢弃**（不是翻错，是压根不进模型）。而本工具的目标场景恰恰是文博——
    /// 用户在「角色描述」里写「博物馆导览员，身着素雅青灰色长衫」，
    /// 在「年代 / 形制约束」里写「南宋」，这些才是决定画面内容的东西。
    /// 不翻译的话，真正进模型的只剩画风预设里那几句英文标签，
    /// 产出会变成一个和文博毫不相干的随机角色——而且看起来「能跑」，很容易被误判成提示词不够细。
    ///
    /// 术语表是**数据**不是代码：<c>Configs/prompt_glossary.json</c>，随便加词条，不需要改 C#。
    /// 匹配按词条长度倒序（长的先替换），否则「南宋」会被「宋」抢先切开。
    /// </summary>
    public static class PromptGlossary
    {
        /// <summary>读取术语表；文件缺失或格式不对时返回空表（此时调用方应告警）。</summary>
        public static List<KeyValuePair<string, string>> Load()
        {
            var pairs = new List<KeyValuePair<string, string>>();

            // 两级查找：Unity 工程内 → 插件包内。见 CharForgePaths 的说明。
            string path = CharForgePaths.ResolveFile("prompt_glossary.json");
            if (string.IsNullOrEmpty(path)) return pairs;

            try
            {
                var arr = JObject.Parse(File.ReadAllText(path))["replacements"] as JArray;
                if (arr == null) return pairs;

                foreach (var item in arr)
                {
                    string zh = (string)item?["zh"];
                    string en = (string)item?["en"];
                    if (!string.IsNullOrWhiteSpace(zh) && !string.IsNullOrWhiteSpace(en))
                        pairs.Add(new KeyValuePair<string, string>(zh, en));
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CharForge] 读取术语表失败（{path}）：{e.Message}");
                return pairs;
            }

            // 长的先替换，避免短词抢先命中。
            pairs.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
            return pairs;
        }

        /// <summary>把角色卡里所有会进提示词的文本字段翻成英文，并回报残余中文。</summary>
        public static void TranslateCard(
            CharacterCard card,
            List<KeyValuePair<string, string>> glossary,
            List<string> warnings)
        {
            if (card == null || glossary == null || glossary.Count == 0) return;

            card.Appearance = Translate(card.Appearance, glossary);
            card.SilhouetteNotes = Translate(card.SilhouetteNotes, glossary);
            card.Role = Translate(card.Role, glossary);
            card.EraOrPeriod = Translate(card.EraOrPeriod, glossary);

            TranslateList(card.StyleTags, glossary);
            TranslateList(card.Forbidden, glossary);
            TranslateList(card.Personality, glossary);

            // 残余中文 = 术语表没覆盖到的词。这些词对模型等于不存在，
            // 不说出来的话，用户只会看到「画面和描述不符」而找不到原因。
            var leftovers = new HashSet<string>();
            CollectLeftovers(card.Appearance, leftovers);
            CollectLeftovers(card.EraOrPeriod, leftovers);

            if (leftovers.Count > 0)
            {
                var sample = new List<string>(leftovers);
                if (sample.Count > 6) sample.RemoveRange(6, sample.Count - 6);

                warnings.Add(
                    $"提示词里仍有 {leftovers.Count} 个中文词条没有对应英文，这些词不会进入模型" +
                    $"（文本编码器只认英文）：{string.Join("、", sample.ToArray())}。" +
                    $"请在 {CharForgePaths.DescribeLookup("prompt_glossary.json")} 里补上对应词条，" +
                    "或直接把角色描述写成英文。");
            }
        }

        public static string Translate(string text, List<KeyValuePair<string, string>> glossary)
        {
            if (string.IsNullOrEmpty(text) || glossary == null || glossary.Count == 0) return text;

            var sb = new StringBuilder(text);
            foreach (var pair in glossary) sb.Replace(pair.Key, pair.Value);
            return sb.ToString();
        }

        private static void TranslateList(List<string> values, List<KeyValuePair<string, string>> glossary)
        {
            if (values == null) return;
            for (int i = 0; i < values.Count; i++) values[i] = Translate(values[i], glossary);
        }

        /// <summary>收集翻译后仍残留的中文片段（连续的汉字算作一条）。</summary>
        private static void CollectLeftovers(string text, HashSet<string> into)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (Match m in Regex.Matches(text, @"[\u4e00-\u9fff]+"))
                into.Add(m.Value);
        }
    }
}
