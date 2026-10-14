using System;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace CharForge.EditorTools.Services
{
    /// <summary>
    /// 把「角色描述」锻造成「文生图提示词」。
    ///
    /// 两条路，且**永远明说是哪条**（<see cref="ForgeResult.Source"/>）：
    ///   1. 外部 LLM（OpenAI 兼容 <c>/chat/completions</c>）—— 填了 BaseURL + Key + 模型名就走这条；
    ///   2. 本地模板拼接 —— 未配置、或 LLM 调用失败时的兜底，离线可用。
    ///
    /// 为什么必须回报 Source：本项目反复栽在「看起来做了、其实没做」上。
    /// 若静默降级，用户会以为提示词是模型写的，实则模板拼的——这类**不报错的失败**
    /// 比报错难查得多。所以降级一律带 <see cref="ForgeResult.Warning"/>，由界面显式展示。
    ///
    /// 协议只做 OpenAI 兼容一种：DeepSeek / 通义 / 智谱 / OpenAI / 本地 vLLM 都提供
    /// 同一形状的 <c>/chat/completions</c>，多一种协议就多一份维护成本。
    /// </summary>
    public static class PromptForgeService
    {
        /// <summary>
        /// 外部接口用**系统代理**（与本地 ComfyUI 相反）。
        /// 本地服务走代理会被截成 502，所以那个客户端显式 UseProxy=false；
        /// 而 api.openai.com / dashscope 这类境外端点，国内往往正是靠代理才通。
        /// 两个客户端各自成立，不要合并。
        /// </summary>
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        // ── 固定件：画风前缀 / 版式后缀 ──────────────────────────
        //
        // 这两段由**插件**持有并原样包在模型输出之外，模型只负责中间那截「角色本体」。
        // 好处是画风与版式不会随模型措辞漂移；代价是**模型无权修改它们**，
        // 所以它们必须只包含「画风」与「版式」这类不因角色而变的东西。

        /// <summary>
        /// 画风前缀：由**插件**持有，模型无权改——所以换画风只改这一处。
        ///
        /// v0.4.3 起由「扁平赛璐璐」改为「华丽半写实厚涂」。v0.4.8 起理由更硬了：
        /// 第 2 步切换为 keep_pose 动作模仿后，**立绘就是成片的画风来源**（image_2 身份槽直接决定输出风格），
        /// 立绘画风糊，帧动画跟着糊——半写实厚涂在姿势模仿下依然稳定出效果。
        ///
        /// 「材质与轮廓可辨」这条底线因此从「参考可读」升级为「直接决定成片质量」：
        /// 发色、服饰形制、主配色必须一眼能读出来——华丽到把结构糊掉，第 2 步必然走形。
        /// </summary>
        public const string StylePrefix =
            "华丽精致的半写实厚涂画风的2D游戏角色立绘：光影层次细腻，体积感与材质感明确" +
            "（布料、皮革、金属、木料各自成色），服饰纹样与随身饰件细节丰富，轮廓带边缘光，" +
            "整体华丽而有层次；不要扁平色块，不要写实照片质感。";

        /// <summary>
        /// 版式后缀：只管**版式**——全身、正面、无背景。
        ///
        /// 这里**刻意不写手势与神情**。0.4.1 及之前写的是「双手自然垂放，神情平静」，
        /// 结果与角色描述直接打架：描述说「手持讲解册」「眼神凶」，后缀说「垂放」「平静」，
        /// 拼出的提示词自相矛盾，模型只能猜。**手势与神态属于角色内容，归描述，不归版式。**
        /// </summary>
        public const string LayoutSuffix =
            "全身立绘，正面站立。无背景，无地面，无投影，脚下是看不见的地面。";

        /// <summary>t2i 工作流的编码器是多语言 Qwen3-VL，中文直入即可，无需英译。</summary>
        public const string QwenAlphaHint =
            "This is an RGBA image with transparency. The image has alpha channel and the background is transparent.";

        private const string SystemPrompt =
            "你是 2D 游戏角色美术的提示词工程师。把用户的一句话角色描述扩写成一条中文文生图提示词，" +
            "用于生成 2D 游戏 NPC 的角色立绘。\n" +
            "硬性要求：\n" +
            "1. 只输出提示词本身。不要解释、不要自我检查、不要分点、不要标题、不要引号、不要换行。\n" +
            "2. 不要输出思考过程。若你习惯先推理，请只输出最终答案——" +
            "输出里出现 <think> 之类的推理段会被判为不合格。\n" +
            "3. 画风与版式由插件固定注入，你**只写角色本体**：不要自己写画风词" +
            "（如「赛璐璐」「厚涂」「二次元」「插画」「像素」「清晰勾线」「平涂上色」），" +
            "也不要描述绘画技法。写了会与注入的画风打架，且长度会超。\n" +
            "4. 必须写到：身份与年龄感、服饰形制与主配色、随身道具、神态。\n" +
            "5. 版式固定：全身立绘、正面站立、无背景、无地面、无投影。" +
            "**不要另写手势或神情**——描述里说了就照写，没说就留白，不要替他决定。\n" +
            "6. 不要写年代、朝代、藏品档案、考古考据、馆藏编号、出土信息——" +
            "本流水线只出游戏角色立绘，不做出土器物档案。\n" +
            "7. 长度 60–120 字，一段话，绝对不超过 200 字。";

        // ── 思考链清洗 ────────────────────────────────────────
        //
        // 为什么要这一步：不少模型（Qwen3 / DeepSeek-R1 一类）会把思维链**混在
        // choices[0].message.content 里**返回，常见形式是 <think>…</think> 包一大段。
        // 若原样拼接，提示词里就插进整段自我推演——实测过一次：画风前缀后面跟着
        // 五百字的「让我分析一下需求…让我检查是否符合要求…」，正文夹在中间。
        // 出图自然一塌糊涂，而日志只显示「完成」。这正是本项目最忌讳的那类失败：
        // **看起来成功、其实没做成**，而且没有任何人报错。
        private static readonly Regex ThinkBlock = new Regex(
            @"<\s*(?:think|thinking|reasoning|reflection|analysis)\s*>[\s\S]*?<\s*/\s*(?:think|thinking|reasoning|reflection|analysis)\s*>",
            RegexOptions.IgnoreCase);

        /// <summary>未闭合的思考块——被 max_tokens 截断时最常见：从开标签一路吃到结尾。</summary>
        private static readonly Regex ThinkUnclosed = new Regex(
            @"<\s*(?:think|thinking|reasoning|reflection|analysis)\s*>[\s\S]*$",
            RegexOptions.IgnoreCase);

        /// <summary>单剩的标签本身（摘录思考链内容时用来剥壳）。</summary>
        private static readonly Regex ThinkTagOnly = new Regex(
            @"<\s*/?\s*(?:think|thinking|reasoning|reflection|analysis)\s*>",
            RegexOptions.IgnoreCase);

        private static readonly Regex CodeFence = new Regex("```[a-zA-Z0-9_+-]*");

        /// <summary>正文开头常见的自贴标签：「提示词：」「最终提示词如下：」「Prompt:」。</summary>
        private static readonly Regex LeadingLabel = new Regex(
            @"^\s*(最终)?\s*(提示词|prompt|答案)\s*(如下|正文)?\s*[:：]\s*",
            RegexOptions.IgnoreCase);

        /// <summary>
        /// 画风前缀的「前导短语」（「：」之前那截），用来识别模型抄回来的画风开头。
        ///
        /// **从 <see cref="StylePrefix"/> 自动派生，不写死文案**：写死过一次正则，换画风时忘了同步，
        /// 症状是提示词里悄悄留下两段画风描述——不报错，只是画面更糊。本项目已经在
        /// 「改了文案忘了改配套规则」上栽过（`SetNode` 的字段名、bindings 的节点 id 同理），
        /// 所以这类派生关系一律让代码自己去算。
        /// </summary>
        private static readonly string StyleLeadinText =
            StylePrefix.Split('：', ':', '。')[0].Trim();

        /// <summary>
        /// 推演文本的「指纹词」。从思考块里摘录候选正文时，命中任一即判为**不是提示词**。
        ///
        /// 为什么要这道闸：光靠「最长的一行」不够。实测过一种思考块——
        /// 整段都是「用户要一个角色。让我想想怎么组织语言…还要控制字数在一百二十字以内」，
        /// 既长又不以小标题格式结尾，只按长度挑就会把这段自述当成提示词发去出图。
        /// 提示词正文是**描述**（发色、服饰、道具、神态），推演文本是**元叙述**（用户、需求、字数），
        /// 两者用词几乎不重叠，所以这张黑名单够用且误伤可控。
        /// 宁可摘不出来而降级模板，也不能把推演当提示词——那是最难查的一类失败。
        /// </summary>
        private static readonly string[] ReasoningMarkers =
        {
            "用户", "需求", "要点", "要求", "必须", "需要", "应该", "让我", "我们要",
            "检查", "符合", "是否", "字数", "分析", "元素", "格式", "标题", "任务",
            "组织语言", "构建", "提示词", "prompt"
        };

        public class ForgeResult
        {
            /// <summary>最终提示词（已按画风前缀/版式后缀包好，可直接送进 t2i）。</summary>
            public string Prompt = "";
            /// <summary>
            /// 模型（或模板）写的**角色本体**那一段，不含插件注入的画风前缀与版式后缀。
            /// 留痕时用来分辨「哪句是模型写的、哪句是插件套的」——出图不对时这是第一件要分清的事。
            /// </summary>
            public string Body = "";
            /// <summary>
            /// 来源：<c>llm:&lt;模型名&gt;</c>、<c>llm:&lt;模型名&gt;·思考块摘录</c> 或 <c>template</c>。
            /// 界面据此如实显示。
            /// </summary>
            public string Source = "template";
            /// <summary>降级或异常摘录的原因。为空表示一切正常。</summary>
            public string Warning;

            public bool FromLlm => Source != null && Source.StartsWith("llm:", StringComparison.Ordinal);
        }

        /// <summary>三个字段齐了才算配置好；缺任一项都退回模板。</summary>
        public static bool LlmConfigured =>
            !string.IsNullOrWhiteSpace(CharForgeSettings.LlmBaseUrl) &&
            !string.IsNullOrWhiteSpace(CharForgeSettings.LlmApiKey) &&
            !string.IsNullOrWhiteSpace(CharForgeSettings.LlmModel);

        /// <summary>本地模板拼接：画风前缀 + 描述 + 版式后缀。零依赖、零成本、离线可用。</summary>
        public static string FromTemplate(string description) => Compose(TemplateBody(description));

        /// <summary>模板路线下的「角色本体」段：就是用户那句描述，去掉尾部标点（由 <see cref="Compose"/> 统一补）。</summary>
        public static string TemplateBody(string description)
        {
            string body = string.IsNullOrWhiteSpace(description)
                ? CharForgeSettings.DefaultPrompt
                : description.Trim();
            return body.TrimEnd('。', '.', ' ');
        }

        /// <summary>
        /// 把「角色本体」段包成最终提示词。**所有出口都走这里**——
        /// 界面里看到的那串、送进 ComfyUI 的那串、留痕文件里的那串，
        /// 必须是同一段字符。曾经各处各拼一遍，改画风时差点漏掉一处。
        /// </summary>
        public static string Compose(string body) =>
            $"{QwenAlphaHint} {StylePrefix}{body}。{LayoutSuffix}";

        /// <summary>
        /// 锻造提示词。配置了 LLM 就调用它；未配置或调用失败则退回模板，
        /// 两种情况都在 <see cref="ForgeResult"/> 里如实标注，绝不假装。
        /// </summary>
        public static async Task<ForgeResult> ForgeAsync(
            string description, Action<string> log, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(description))
                throw new InvalidOperationException("角色描述为空——先写一句要什么角色。");

            if (!LlmConfigured)
            {
                log?.Invoke("提示词来源：本地模板（未配置外部 LLM——填 BaseURL / API Key / 模型名即可改走模型）");
                return new ForgeResult
                {
                    Prompt = FromTemplate(description),
                    Body = TemplateBody(description),
                    Source = "template"
                };
            }

            string baseUrl = CharForgeSettings.LlmBaseUrl.TrimEnd('/');
            string model = CharForgeSettings.LlmModel;

            try
            {
                var body = new JObject
                {
                    ["model"] = model,
                    ["temperature"] = 0.7,
                    // 512 是给「纯提示词」留的额度。思考型模型会把思维链也算进这个上限，
                    // 512 常常在推理途中就被截断，正文一个字都轮不到——留够余量。
                    ["max_tokens"] = 1024,
                    ["messages"] = new JArray
                    {
                        new JObject { ["role"] = "system", ["content"] = SystemPrompt },
                        new JObject
                        {
                            ["role"] = "user",
                            ["content"] = $"角色描述：{description.Trim()}\n" +
                                          $"画风前缀（固定，不要重复改写）：{StylePrefix}\n" +
                                          $"版式后缀（固定，不要重复改写）：{LayoutSuffix}\n" +
                                          "只输出提示词正文（角色本体那一段），不要带上面前缀后缀。"
                        }
                    }
                };

                // 部分网关（vLLM / SGLang / DashScope 兼容模式）用这个开关关掉思考模式；
                // 不支持的网关一般直接忽略这个额外字段。默认不开，避免严格网关因未知字段报 400。
                if (CharForgeSettings.LlmNoThink)
                {
                    body["chat_template_kwargs"] = new JObject { ["enable_thinking"] = false };
                    log?.Invoke("已请求关闭思考模式（chat_template_kwargs.enable_thinking=false）");
                }

                using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions");
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + CharForgeSettings.LlmApiKey.Trim());
                req.Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");

                log?.Invoke($"锻造提示词中（{model} @ {baseUrl}）…");
                using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
                string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                    return Degrade(description, log,
                        $"LLM 返回 HTTP {(int)resp.StatusCode}：{Trunc(text)}");

                var msg = JObject.Parse(text)?["choices"]?[0]?["message"];
                string raw = (string)msg?["content"];
                if (string.IsNullOrWhiteSpace(raw))
                    return Degrade(description, log, "LLM 回包里没有 choices[0].message.content（响应格式与 OpenAI 兼容协议不符）");

                // 有的网关（含 OpenAI 官方新协议）把思考链单独放在 reasoning_content，
                // 那样 content 本来就是干净的——不动正文，只记一笔。
                if (!string.IsNullOrWhiteSpace((string)msg?["reasoning_content"]))
                    log?.Invoke("（模型另附了 reasoning_content 思考链，已丢弃，不进提示词）");

                string core = CleanBody(raw, out string reasoning);

                if (core.Length < 8)
                {
                    // 正文剥空了。两种可能：模型把答案整段写进了思考块（可摘），
                    // 或者压根没给答案（只能降级）。摘录必须报出来——摘的不是模型的最终答案。
                    string salvaged = SalvageFromReasoning(reasoning);
                    if (salvaged == null)
                        return Degrade(description, log,
                            "模型返回的正文里只有思考过程、没有提示词——在网关侧关闭思考模式，" +
                            "或换一个不输出思考链的模型（如 deepseek-chat / qwen-plus）");

                    log?.Invoke($"⚠ 模型把提示词写在了思考块里，已从中摘出 {salvaged.Length} 字。" +
                                "摘录不等于模型正常作答，建议勾选「关闭模型思考模式」后重锻一次。");
                    return new ForgeResult
                    {
                        Prompt = Compose(salvaged),
                        Body = salvaged,
                        Source = $"llm:{model}·思考块摘录",
                        Warning = "提示词是从模型的思考块里摘出来的，不保证与模型本意一致。" +
                                  "建议在高级设置勾选「关闭模型思考模式」，或换一个不输出思考链的模型。"
                    };
                }

                if (core.Length > 400)
                    return Degrade(description, log,
                        $"模型正文 {core.Length} 字，远超 60–120 字要求——疑似把说明文字也写进来了，" +
                        $"不做截断（截哪儿都可能截断语义）。前 80 字：{Trunc(core, 80)}");

                log?.Invoke($"提示词来源：{model}（{core.Length} 字）");
                return new ForgeResult
                {
                    Prompt = Compose(core),
                    Body = core,
                    Source = "llm:" + model
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                return Degrade(description, log, e.Message);
            }
        }

        /// <summary>
        /// 把模型回包的正文洗净成「纯提示词」：剥思考链、去代码围栏、去自贴标签、去重复的固定件。
        /// 剥下来的思考链经 <paramref name="reasoning"/> 交回，供正文为空时兜底摘录。
        /// </summary>
        private static string CleanBody(string raw, out string reasoning)
        {
            reasoning = "";
            if (string.IsNullOrEmpty(raw)) return "";

            var captured = new StringBuilder();
            string rest = ThinkBlock.Replace(raw, m =>
            {
                captured.Append(m.Value).Append('\n');
                return "\n";
            });

            // 配对的都摘走之后，若还剩一个开标签，说明它没闭合（多半被 max_tokens 截断）。
            var open = ThinkUnclosed.Match(rest);
            if (open.Success)
            {
                captured.Append(open.Value).Append('\n');
                rest = rest.Substring(0, open.Index);
            }

            reasoning = ThinkTagOnly.Replace(captured.ToString(), " ");

            string body = CodeFence.Replace(rest, " ");
            body = Collapse(body);
            body = LeadingLabel.Replace(body, "");

            // 模型有时会把固定前缀/后缀也抄一遍，去掉以免提示词里出现两份画风描述。
            // 带句号与不带句号两种写法都要处理。
            foreach (string fixedPart in new[] { QwenAlphaHint, StylePrefix, LayoutSuffix })
            {
                body = body.Replace(fixedPart, " ");
                string bare = fixedPart.TrimEnd('。', '.');
                if (bare != fixedPart) body = body.Replace(bare, " ");
            }

            body = Collapse(body);
            body = StripStyleLeadin(body);   // 模型抄回来的画风开头——前缀里已有，去重
            body = Collapse(body).Trim('"', '\'', '“', '”', '‘', '’', '「', '」', '『', '』', '`', ' ');
            return body.TrimEnd('。', '.', '；', ';', '，', ',', '：', ':', ' ');
        }

        /// <summary>
        /// 兜底：模型把提示词整段写在思考块里时，从思考块挑出最像正文的一行。
        ///
        /// 规则刻意保守——只接受「最长、不是小标题、长度在合理区间、且不含推演指纹词」的那一行。
        /// 不追求准确，只求**不把一段推演当成提示词**：挑不出来就返回 null，让调用方降级模板。
        /// 摘录命中时调用方必须报出警告，不能让人以为这是模型正常作答。
        /// </summary>
        private static string SalvageFromReasoning(string reasoning)
        {
            if (string.IsNullOrWhiteSpace(reasoning)) return null;

            string best = null;
            foreach (string rawLine in reasoning.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = Collapse(rawLine)
                    .Trim('"', '“', '”', '「', '」', '『', '』', '`', '*', '#', '-', ' ');
                if (line.Length < 20 || line.Length > 300) continue;      // 自检项 / 又一段推演
                if (line.EndsWith("：") || line.EndsWith(":")) continue;  // 小标题
                if (LooksLikeReasoning(line)) continue;                   // 元叙述，不是描述
                if (best == null || line.Length > best.Length) best = line;
            }

            // 末尾句号要去掉：拼装时我们自己会补一个，否则会出现「敌意。。全身立绘」。
            return best?.TrimEnd('。', '.', '；', ';', '，', ',', '：', ':', ' ');
        }

        /// <summary>
        /// 剥掉模型抄回来的画风开头。处理两种实测形态：
        /// 整段照抄（「华丽精致的半写实厚涂画风的2D游戏角色立绘：…」）与
        /// 只抄到「…画风的」就接正文。都不命中就原样返回——**宁可留着，不做模糊匹配**，
        /// 免得把角色本体里以「华丽」开头的正常描述误切。
        /// </summary>
        private static string StripStyleLeadin(string body)
        {
            if (string.IsNullOrEmpty(body)) return body;

            if (body.StartsWith(StyleLeadinText, StringComparison.Ordinal))
                return body.Substring(StyleLeadinText.Length).TrimStart('：', ':', '。', ' ', '，', ',');

            int cut = StyleLeadinText.IndexOf("画风的", StringComparison.Ordinal);
            if (cut > 0)
            {
                string shortForm = StyleLeadinText.Substring(0, cut + 3);   // 含「画风的」
                if (body.StartsWith(shortForm, StringComparison.Ordinal))
                    return body.Substring(shortForm.Length).TrimStart('：', ':', '。', ' ', '，', ',');
            }
            return body;
        }

        private static bool LooksLikeReasoning(string line)
        {
            foreach (string marker in ReasoningMarkers)
                if (line.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>降级到模板，但把原因写进结果与日志——降级可以，静默不行。</summary>
        private static ForgeResult Degrade(string description, Action<string> log, string reason)
        {
            log?.Invoke($"⚠ 外部 LLM 结果不可用（{reason}），已退回本地模板。提示词仍可用，但不是模型写的。");
            return new ForgeResult
            {
                Prompt = FromTemplate(description),
                Body = TemplateBody(description),
                Source = "template",
                Warning = $"LLM 结果不可用，已退回本地模板：{reason}"
            };
        }

        /// <summary>把任意空白（含换行）压成单个空格——提示词必须是**一行**，换行会破坏版式。</summary>
        private static string Collapse(string s) =>
            string.IsNullOrEmpty(s) ? s : Regex.Replace(s, @"\s+", " ").Trim();

        private static string Trunc(string s, int max = 300) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
