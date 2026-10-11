using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CharForge.EditorTools.Backends;
using CharForge.EditorTools.Data;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace CharForge.EditorTools.Services
{
    public class PipelineProgress
    {
        public string Stage = "";
        public float Percent;
        public string Message = "";

        /// <summary>负值表示"进度不可知"（例如委托给外部服务时只能等），窗口据此显示不确定进度条。</summary>
        public bool Indeterminate => Percent < 0f;
    }

    public class PipelineResult
    {
        public bool Ok;
        public string Message = "";
        public string NextAction = "";
        public string OutputDir = "";
        public PrefabAssembler.AssembleReport Assemble;

        /// <summary>
        /// 非致命但用户必须知道的提示（用了规则版角色卡、参考图不生效、某帧被跳过等）。
        /// 这条链此前是断的：warnings 累积到最后没有任何消费点，等于没写。
        /// </summary>
        public List<string> Warnings = new List<string>();

        public static PipelineResult Fail(string message, string nextAction = "") =>
            new PipelineResult { Ok = false, Message = message, NextAction = nextAction };

        /// <summary>把流水线途中累积的提示挂到结果上，交给界面显示。</summary>
        private static PipelineResult WithWarnings(PipelineResult result, List<string> warnings)
        {
            if (warnings != null && warnings.Count > 0)
            {
                result.Warnings = warnings;
                if (string.IsNullOrEmpty(result.NextAction)) result.NextAction = warnings[0];
            }
            return result;
        }
    }

    /// <summary>
    /// 流水线编排（C# 侧）——**取代 Python orchestrator 成为主控**。
    ///
    /// 这是 v0.2「宿主倒置」的落点：Unity 决定跑哪几段、怎么跑、产物落在哪；
    /// 后端只被调用它不可替代的那部分（扩散推理 / 风格分析）。
    ///
    /// 两条路径，对装配层完全等价：
    ///   ① 逐帧路径（ComfyUI / Sentis / Mock）：Unity 编排每一段，第 5、6 段用
    ///      <see cref="FrameNormalizer"/> 与 <see cref="SheetPacker"/> 在引擎内完成——
    ///      **这条路径不需要 Python**。
    ///   ② 整单路径（Sidecar / 远端服务）：把整个请求交给下游跑完，Unity 只做装配——
    ///      复用已有流水线，改造成本最低。
    ///
    /// 两条路径都会落下一份**标准的产物目录**（sheet.png + manifest.json +
    /// assembly_spec.json + persona.json）。好处有三：装配指令可重放（改人设不用重新出图）、
    /// 产物可归档比对、装配代码只有一份。
    /// </summary>
    public static class CharacterForgePipeline
    {
        private static readonly string[] DirectionKeys = { "south", "north", "east", "west" };

        /// <summary>切片与项目其它素材保持一致，否则角色视觉尺寸会不对。</summary>
        private const int PixelsPerUnit = 100;

        private static readonly Dictionary<string, string> DirectionPromptHint = new Dictionary<string, string>
        {
            { "south", "front view, facing the viewer" },
            { "north", "back view, facing away" },
            { "east", "side view, facing right" },
            { "west", "side view, facing left" }
        };

        public static async Task<PipelineResult> RunAsync(
            JobRequest request,
            string backendId,
            Action<PipelineProgress> onProgress,
            CancellationToken ct)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            void Report(string stage, float percent, string message) =>
                onProgress?.Invoke(new PipelineProgress { Stage = stage, Percent = percent, Message = message });

            // ── 0. 选后端 ────────────────────────────────────────────
            Report("backend", 1f, "选择推理后端…");
            var backend = await ResolveBackendAsync(backendId, ct).ConfigureAwait(false);
            if (backend == null)
            {
                return PipelineResult.Fail(
                    "没有可用的推理后端。",
                    "在「后端设置」里选一个：占位后端（零依赖，立即可用）/ 本地 ComfyUI / 自带 Python 服务 / 引擎内推理。");
            }

            string jobId = NewJobId();
            Report("backend", 3f, $"使用后端：{backend.DisplayName}");

            try
            {
                return backend is IWholeJobBackend wholeJob
                    ? await RunDelegatedAsync(wholeJob, request, jobId, Report, ct).ConfigureAwait(false)
                    : await RunGranularAsync(backend, request, jobId, Report, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return PipelineResult.Fail("任务已取消。");
            }
            catch (Exception e)
            {
                Debug.LogError($"[CharForge] 流水线异常：{e}");
                return PipelineResult.Fail($"流水线异常：{e.Message}", "查看 Console 完整堆栈；步骤级原因通常在最后一条日志里。");
            }
        }

        // ────────────────────────── 后端选择 ──────────────────────────

        private static async Task<IInferenceBackend> ResolveBackendAsync(string backendId, CancellationToken ct)
        {
            InferenceBackendRegistry.EnsureInitialized();

            if (!string.IsNullOrEmpty(backendId) && backendId != "auto")
            {
                var chosen = InferenceBackendRegistry.Find(backendId);
                if (chosen == null) return null;
                ApplySettings(chosen);
                var status = await chosen.ProbeAsync(ct).ConfigureAwait(false);
                if (status != null && status.Ready) return chosen;

                Debug.LogWarning($"[CharForge] 指定后端不可用：{status?.Message}");
                return null;
            }

            foreach (var b in InferenceBackendRegistry.All()) ApplySettings(b);
            return await InferenceBackendRegistry.PickReadyAsync(ct).ConfigureAwait(false);
        }

        /// <summary>把插件设置灌进后端实例。后端不知道 EditorPrefs 的存在，职责更干净。</summary>
        private static void ApplySettings(IInferenceBackend backend)
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

        // ────────────────────────── 路径②：整单委托 ──────────────────────────

        private static async Task<PipelineResult> RunDelegatedAsync(
            IWholeJobBackend backend,
            JobRequest request,
            string jobId,
            Action<string, float, string> report,
            CancellationToken ct)
        {
            report("generate", -1f, $"{backend.DisplayName} 处理中…");

            var outcome = await backend.RunWholeJobAsync(
                request, jobId,
                (stage, percent) => report("generate", percent, stage),
                ct).ConfigureAwait(false);

            if (!outcome.Ok)
                return PipelineResult.Fail(outcome.Error, "看后端日志定位；也可先用占位后端验证装配链路本身是通的。");

            report("assemble", 94f, "装配 prefab…");
            var assembly = await AssembleOnMainThreadAsync(outcome.OutputDir).ConfigureAwait(false);

            return new PipelineResult
            {
                Ok = assembly.ok,
                Message = assembly.ok ? "完成" : "装配失败",
                NextAction = assembly.ok ? "" : "看 Console 里的装配日志，常见原因是切片坐标与贴图不匹配。",
                OutputDir = outcome.OutputDir,
                Assemble = assembly
            };
        }

        // ────────────────────────── 路径①：逐帧编排（无需 Python） ──────────────────────────

        private static async Task<PipelineResult> RunGranularAsync(
            IInferenceBackend backend,
            JobRequest request,
            string jobId,
            Action<string, float, string> report,
            CancellationToken ct)
        {
            var warnings = new List<string>();

            // ── [1] 角色卡 ──
            report("prompt_forge", 5f, "解析角色描述…");
            var card = await backend.DescribeAsync(request, jobId, ct).ConfigureAwait(false);
            if (card == null)
            {
                card = BuildCardLocal(request, jobId);
                warnings.Add("后端不支持角色卡提炼，已使用本地规则版（角色名/年代靠启发式识别）。");
            }

            // ── [2] 风格约束 ──
            report("style_lock", 10f, "锁定画风约束…");
            var style = await backend.ExtractStyleAsync(request.ReferenceImages, request.StylePreset, ct).ConfigureAwait(false);
            if (style == null)
            {
                style = BuildStyleLocal(request);
                if (request.ReferenceImages != null && request.ReferenceImages.Count > 0)
                {
                    warnings.Add(
                        $"已提供 {request.ReferenceImages.Count} 张参考图，但当前后端不支持风格剖析，" +
                        "产出不会体现项目画风。要真正锁住画风请用 ComfyUI + IP-Adapter / LoRA。");
                }
                else
                {
                    warnings.Add("未提供画风参考图：人类在环检查点，四向一致性无法验收。");
                }
            }

            bool pixelArt = style.PositiveTags.Any(t =>
                t.IndexOf("pixel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf("8-bit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.IndexOf("retro", StringComparison.OrdinalIgnoreCase) >= 0);

            int cellW = 512, cellH = 512, columns = 4;
            int framesPerClip = Mathf.Clamp(request.FramesPerClip, 2, 8);
            var directions = request.Directions != null && request.Directions.Count > 0
                ? request.Directions
                : new List<string> { "south" };
            var actions = request.Actions != null && request.Actions.Count > 0
                ? request.Actions
                : new List<string> { "idle" };

            // 默认只勾「南/北」时，产出不会有东/西片段——不说清楚，用户会以为四向坏了。
            if (directions.Count < 4)
            {
                warnings.Add(
                    $"本次只生成 {directions.Count} 个方向（{string.Join("、", directions)}），" +
                    "其余朝向不会有动画片段，facing 切到那些方向时会没有片段可播；" +
                    "要验收四向一致性请把东南西北都勾上。");
            }

            // ── [3] 逐帧生成 ──
            var placed = new List<PlacedFrame>();
            int total = directions.Count * actions.Count * framesPerClip;
            int done = 0;

            foreach (var direction in directions)
            {
                foreach (var action in actions)
                {
                    for (int i = 0; i < framesPerClip; i++)
                    {
                        ct.ThrowIfCancellationRequested();

                        var frameRequest = new FrameRequest
                        {
                            JobId = jobId,
                            CharacterName = card.Name,
                            Direction = direction,
                            Action = action,
                            Index = i,
                            TotalFrames = framesPerClip,
                            Width = cellW,
                            Height = cellH,
                            PositivePrompt = BuildPositivePrompt(card, style, direction, action, i, framesPerClip),
                            NegativePrompt = BuildNegativePrompt(card, style),
                            ReferenceImages = request.ReferenceImages ?? new List<string>(),
                            StyleTags = style.PositiveTags
                        };

                        float percent = 12f + 58f * (done / (float)Mathf.Max(1, total));
                        report("generate", percent,
                            $"生成 {direction}/{action} 第 {i + 1}/{framesPerClip} 帧（{done + 1}/{total}）");

                        var png = await backend.GenerateFrameAsync(frameRequest, ct).ConfigureAwait(false);
                        done++;

                        if (png == null || png.Length == 0)
                        {
                            warnings.Add($"帧 {frameRequest.SliceName} 未返回图像，已跳过。");
                            continue;
                        }

                        // 抠图：后端能做就用后端的，不能就让后面的归一化按 alpha 自行裁切
                        var matted = await backend.MattingAsync(png, ct).ConfigureAwait(false);
                        placed.Add(new PlacedFrame
                        {
                            Direction = direction,
                            Action = action,
                            Index = i,
                            Png = matted ?? png
                        });
                    }
                }
            }

            if (placed.Count == 0)
                return WithWarnings(
                    PipelineResult.Fail("一帧都没生成出来。", "先用占位后端验证链路，再排查具体后端的出图问题。"),
                    warnings);

            // ── [5] 归一化（引擎内）──
            report("normalize", 72f, "归一化：统一缩放 / 脚底锚点 / 像素网格…");
            int pixelGrid = pixelArt ? FrameNormalizer.SuggestPixelGrid(cellW) : 1;
            var normalizer = new FrameNormalizer
            {
                CanvasWidth = cellW,
                CanvasHeight = cellH,
                MarginRatio = 0.08f,
                PixelGrid = pixelGrid
            };

            var normalized = await normalizer.NormalizeAllAsync(placed.Select(p => p.Png).ToList()).ConfigureAwait(false);
            if (!normalized.HasContent)
                return PipelineResult.Fail(
                    "归一化后没有任何非透明像素。",
                    "多半是生成结果全透明或抠图抠没了——先关掉抠图（或用占位后端）复现一次。");

            for (int i = 0; i < placed.Count && i < normalized.Frames.Count; i++)
                placed[i].Png = normalized.Frames[i];

            // ── [6] 打包（引擎内）──
            report("pack", 82f, "打包 sprite sheet…");
            var bounds = SheetPacker.BoundsFromPixels(normalized.BoundsPixels, cellW, PixelsPerUnit);

            var packResult = await EditorMainThread.RunAsync(() => SheetPacker.Pack(
                jobId, placed, cellW, cellH, columns,
                pixelsPerUnit: PixelsPerUnit, pixelArt: pixelArt, bounds: bounds)).ConfigureAwait(false);

            if (!packResult.Ok)
                return PipelineResult.Fail("打包失败。", string.Join("；", packResult.Messages));

            // ── [7] 落产物目录（与整单路径统一，装配代码只有一份）──
            report("assemble", 90f, "写装配指令…");
            var spec = BuildAssemblySpec(card, packResult.Manifest, request, jobId);
            var persona = BuildPersona(card);

            string outDir = await EditorMainThread.RunAsync(() =>
            {
                string dir = Path.Combine(ProjectRoot, "CharForgeCache", jobId);
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, "sheet.png"), packResult.SheetPng);
                File.WriteAllText(Path.Combine(dir, "manifest.json"),
                    JsonConvert.SerializeObject(packResult.Manifest, Formatting.Indented), Utf8NoBom);
                File.WriteAllText(Path.Combine(dir, "assembly_spec.json"),
                    JsonConvert.SerializeObject(spec, Formatting.Indented), Utf8NoBom);
                File.WriteAllText(Path.Combine(dir, "persona.json"),
                    JsonConvert.SerializeObject(persona, Formatting.Indented), Utf8NoBom);
                return dir;
            }).ConfigureAwait(false);

            // ── 装配 ──
            report("assemble", 94f, "装配 prefab…");
            var assembly = await AssembleOnMainThreadAsync(outDir).ConfigureAwait(false);

            return WithWarnings(new PipelineResult
            {
                Ok = assembly.ok,
                Message = assembly.ok ? "完成" : "装配失败",
                NextAction = assembly.ok ? "" : "看 Console 里的装配日志。",
                OutputDir = outDir,
                Assemble = assembly
            }, warnings);
        }

        private static Task<PrefabAssembler.AssembleReport> AssembleOnMainThreadAsync(string jobDir) =>
            EditorMainThread.RunAsync(() => PrefabAssembler.Assemble(jobDir));

        // ────────────────────────── 组装指令（镜像 server/pipeline/assemble.py）──────────────────────────

        private static AssemblySpec BuildAssemblySpec(
            CharacterCard card, SpriteManifest manifest, JobRequest request, string jobId)
        {
            var bounds = manifest.CharacterBounds ?? new CharacterBounds { Width = 1f, Height = 2f, CenterY = 1f };
            const float shrink = 0.9f;   // 略小于轮廓：贴墙时不至于看起来悬空

            return new AssemblySpec
            {
                JobId = jobId,
                PrefabName = MakePrefabName(card, jobId),
                CharacterCard = card,
                Animations = BuildAnimations(manifest),
                Collider = new ColliderSpec
                {
                    Shape = "capsule",
                    Radius = Mathf.Max(0.05f, bounds.Width * shrink / 2f),
                    Size = new List<float>
                    {
                        Mathf.Max(0.05f, bounds.Width * shrink),
                        Mathf.Max(0.05f, bounds.Height * shrink)
                    },
                    Offset = new List<float> { bounds.CenterX, bounds.CenterY }
                },
                Movement = request.Movement ?? new MovementSpec(),
                Dialogue = new DialogueSpec
                {
                    Enabled = request.DialogueEnabled,
                    Provider = request.DialogueEnabled ? "llm-unity" : "none",
                    PersonaPath = request.DialogueEnabled ? "persona.json" : null,
                    MaxTokens = 160
                },
                AesLabel = new AesLabelSpec
                {
                    Enabled = true,
                    Text = "本内容由人工智能生成",
                    Placement = "corner"
                }
            };
        }

        private static List<AnimationSpec> BuildAnimations(SpriteManifest manifest)
        {
            var grouped = new Dictionary<string, List<(int index, string name)>>();
            foreach (var slice in manifest.Sprites)
            {
                int last = slice.Name.LastIndexOf('_');
                if (last <= 0) continue;
                string clipKey = slice.Name.Substring(0, last);
                string indexPart = slice.Name.Substring(last + 1);
                if (!int.TryParse(indexPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx)) continue;

                if (!grouped.TryGetValue(clipKey, out var list))
                {
                    list = new List<(int, string)>();
                    grouped[clipKey] = list;
                }
                list.Add((idx, slice.Name));
            }

            var result = new List<AnimationSpec>();
            foreach (var kv in grouped)
            {
                int underscore = kv.Key.IndexOf('_');
                if (underscore <= 0) continue;

                kv.Value.Sort((a, b) => a.index.CompareTo(b.index));
                result.Add(new AnimationSpec
                {
                    Name = kv.Key,
                    Direction = kv.Key.Substring(0, underscore),
                    Action = kv.Key.Substring(underscore + 1),
                    Sprites = kv.Value.Select(v => v.name).ToList(),
                    FrameRate = 8,
                    Loop = true
                });
            }

            // 顺序：方向（南→北→东→西）→ 动作，保证生成结果稳定可比对
            return result
                .OrderBy(a => DirectionRank(a.Direction))
                .ThenBy(a => a.Action, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>未知方向排到最后，而不是被当成"南"排到最前。</summary>
        private static int DirectionRank(string direction)
        {
            int idx = Array.IndexOf(DirectionKeys, direction);
            return idx < 0 ? 99 : idx;
        }

        private static Dictionary<string, object> BuildPersona(CharacterCard card)
        {
            string personality = card.Personality != null && card.Personality.Count > 0
                ? string.Join("、", card.Personality)
                : "沉稳、亲切";

            var knowledge = string.IsNullOrWhiteSpace(card.ReferenceNotes)
                ? ""
                : $"\n【知识边界】\n- 只依据以下资料回答，资料没有的内容不要推测：\n{card.ReferenceNotes}\n";

            var forbidden = card.Forbidden != null && card.Forbidden.Count > 0
                ? $"\n【禁止】\n- 不要提及：{string.Join("、", card.Forbidden)}\n"
                : "";

            string greeting = $"你好，我是{card.Name}，{card.Role}。";
            string era = string.IsNullOrWhiteSpace(card.EraOrPeriod) ? "" : $"（{card.EraOrPeriod}）";

            string prompt =
                $"你是{card.Role}，名字叫{card.Name}。\n\n" +
                $"【身份】{card.Role}{era}\n" +
                $"【性格】{personality}\n" +
                "【说话要求】\n" +
                "- 用词符合身份与年代，不要出现现代词汇或网络用语。\n" +
                "- 每次回答不超过 3 句，避免长篇大论。\n" +
                "- 不知道的事情就说不知道，不要编造。\n" +
                knowledge + forbidden +
                $"【开场白】{greeting}";

            return new Dictionary<string, object>
            {
                ["id"] = card.Name,
                ["display_name"] = card.Name,
                ["role"] = card.Role,
                ["personality"] = card.Personality,
                ["era_or_period"] = card.EraOrPeriod,
                ["knowledge_domains"] = string.IsNullOrWhiteSpace(card.ReferenceNotes)
                    ? new List<string>()
                    : new List<string> { card.ReferenceNotes },
                ["forbidden"] = card.Forbidden,
                ["persona_prompt"] = prompt,
                ["greeting"] = greeting,
                ["max_tokens"] = 160,
                ["source_prompt"] = card.SourcePrompt
            };
        }

        // ────────────────────────── 本地兜底：规则版角色卡 / 风格预设 ──────────────────────────

        private static readonly string[] EraKeywords =
        {
            "南宋", "北宋", "唐代", "宋代", "明代", "清代", "元代", "汉代", "秦代", "先秦",
            "民国", "现代", "未来", "赛博朋克", "蒸汽朋克", "中世纪", "文艺复兴"
        };

        private static CharacterCard BuildCardLocal(JobRequest request, string jobId)
        {
            string prompt = request.Prompt ?? "";
            string role = prompt.Split(new[] { '，', ',', '。', '；', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? prompt;

            string era = EraKeywords.FirstOrDefault(k => prompt.Contains(k));

            return new CharacterCard
            {
                JobId = jobId,
                SourcePrompt = prompt,
                Name = string.IsNullOrWhiteSpace(request.CharacterName) ? ShortName(role) : request.CharacterName,
                Role = role,
                Appearance = prompt,
                SilhouetteNotes = "轮廓清晰、剪影可辨；头身比与项目既有角色保持一致",
                Personality = new List<string>(),
                Palette = new List<string>(),
                Actions = request.Actions ?? new List<string>(),
                Directions = request.Directions ?? new List<string>(),
                Forbidden = request.Forbidden != null && request.Forbidden.Count > 0
                    ? request.Forbidden
                    : new List<string>
                    {
                        "modern glasses", "watch", "plastic material",
                        "neon color", "brand logo", "modern footwear"
                    },
                StyleTags = new List<string>
                {
                    "flat cel shading", "clean lineart", "limited palette",
                    "consistent character design", "full body", "soft even lighting"
                },
                EraOrPeriod = string.IsNullOrWhiteSpace(request.EraOrPeriod) ? era : request.EraOrPeriod,
                ReferenceNotes = request.ReferenceNotes
            };
        }

        /// <summary>
        /// 从整句身份里取一个短名。规则版没有语义理解能力，
        /// 所以只做"取前若干字"这种可预期的事，别指望它起出"云叔"这种名字——
        /// 想要好名字就填 <c>character_name</c>，或让支持 LLM 的后端来做。
        /// </summary>
        private static string ShortName(string role)
        {
            if (string.IsNullOrWhiteSpace(role)) return "NPC";

            var cleaned = Regex.Replace(role, @"^(一位|一个|这名|那个|年迈的|年轻的|中年的)", "");
            cleaned = cleaned.Trim();
            if (cleaned.Length <= 6) return cleaned;

            foreach (var marker in new[] { "师", "匠", "员", "长", "官", "兵", "商", "医", "客", "翁", "婆", "婆" })
            {
                int idx = cleaned.IndexOf(marker, StringComparison.Ordinal);
                if (idx >= 0 && idx < 6) return cleaned.Substring(0, idx + 1);
            }
            return cleaned.Substring(0, 6);
        }

        private static StyleProfile BuildStyleLocal(JobRequest request)
        {
            return new StyleProfile
            {
                Preset = string.IsNullOrWhiteSpace(request.StylePreset) ? "custom" : request.StylePreset,
                Source = "rule-based",
                ReferenceCount = request.ReferenceImages?.Count ?? 0,
                PositiveTags = new List<string>
                {
                    "flat cel shading", "clean confident lineart", "limited harmonious palette",
                    "consistent character proportions", "full body", "soft even lighting"
                },
                NegativeTags = new List<string>
                {
                    "photo", "3d render", "extra limbs", "blurry", "watermark", "text", "signature"
                },
                Palette = new List<string> { "#6070A8", "#6E9478", "#967480" }
            };
        }

        private static string BuildPositivePrompt(
            CharacterCard card, StyleProfile style, string direction, string action, int index, int total)
        {
            var parts = new List<string> { card.Appearance ?? card.Role };

            if (DirectionPromptHint.TryGetValue(direction, out var dirHint)) parts.Add(dirHint);
            parts.Add(action == "walk"
                ? $"walking cycle, keyframe {index + 1} of {total}, arms swinging"
                : "idle standing pose, subtle breathing motion");

            parts.Add(card.SilhouetteNotes);
            if (card.EraOrPeriod != null) parts.Add($"historically accurate costume, {card.EraOrPeriod} era");
            parts.AddRange(style.PositiveTags);
            parts.Add("transparent background, single character, centered");

            return string.Join(", ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        private static string BuildNegativePrompt(CharacterCard card, StyleProfile style)
        {
            var parts = new List<string>();
            if (card.Forbidden != null) parts.AddRange(card.Forbidden);
            if (style.NegativeTags != null) parts.AddRange(style.NegativeTags);
            return string.Join(", ", parts);
        }

        // ────────────────────────── 杂项 ──────────────────────────

        private static readonly System.Text.UTF8Encoding Utf8NoBom = new System.Text.UTF8Encoding(false);

        private static string ProjectRoot =>
            Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;

        private static string NewJobId() => Guid.NewGuid().ToString("N").Substring(0, 12);

        /// <summary>
        /// prefab 名必须 ASCII 安全：中文角色名（如「云叔」）进文件名会在跨平台 git、
        /// CI、不同文件系统编码下出问题。显示名保留中文，文件名用 ASCII + 短后缀防重名。
        /// </summary>
        private static string MakePrefabName(CharacterCard card, string jobId)
        {
            string suffix = jobId.Length >= 6 ? jobId.Substring(0, 6) : jobId;
            string slug = Regex.Replace(card?.Name ?? "", "[^A-Za-z0-9]+", "_").Trim('_').ToLowerInvariant();
            if (slug.Length > 20) slug = slug.Substring(0, 20);
            return string.IsNullOrEmpty(slug) ? $"NPC_{suffix}" : $"NPC_{slug}_{suffix}";
        }
    }
}
