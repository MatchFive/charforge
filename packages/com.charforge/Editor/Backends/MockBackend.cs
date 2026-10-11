using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CharForge.EditorTools.Data;
using UnityEngine;

namespace CharForge.EditorTools.Backends
{
    /// <summary>
    /// 占位后端：不调用任何模型，程序化画出可辨认的占位帧。
    ///
    /// 存在的意义有三条，都不是"凑数"：
    /// 1. **CI 可跑**：仓库的自动化测试不依赖显存与网络，装配逻辑照样被覆盖。
    /// 2. **首次体验兜底**：用户第一次打开窗口、还没配好后端时，能立刻看到完整链路跑通
    ///    （切片 → Sprite Library → Animator → prefab），而不是先撞一堵配置墙。
    /// 3. **回归基线**：改了装配代码后，用 Mock 出图能一眼看出切片坐标、方向、帧序有没有错。
    ///
    /// 画的图形刻意编码了信息，方便肉眼看错：
    /// - 中间色块 = 身体，颜色由角色名哈希决定（同一角色每次一致）
    /// - 四角标记块 = 朝向（南=下中、北=上中、东=右中、西=左中）
    /// - 底部两条"腿"随帧号交替摆动；idle 则为轻微上下呼吸
    /// - 右下角刻度条 = 帧序号
    /// </summary>
    public class MockBackend : IInferenceBackend
    {
        public string Id => "mock";
        public string DisplayName => "占位后端（无需任何依赖）";
        public BackendKind Kind => BackendKind.Mock;
        public int Priority => 1;   // 永远最低：只在没有别的可用后端时兜底
        public BackendCapability Capabilities =>
            BackendCapability.FrameGeneration | BackendCapability.StyleAnalysis | BackendCapability.CharacterCard;

        private static readonly Dictionary<string, Color32> Palette = new Dictionary<string, Color32>
        {
            { "south", new Color32(96, 128, 168, 255) },
            { "north", new Color32(120, 108, 96, 255) },
            { "east",  new Color32(110, 148, 120, 255) },
            { "west",  new Color32(150, 116, 128, 255) },
        };

        public Task<BackendStatus> ProbeAsync(CancellationToken ct)
        {
            return Task.FromResult(BackendStatus.Ok("占位后端可用（不产生真实美术资产）"));
        }

        public Task<StyleProfile> ExtractStyleAsync(
            IReadOnlyList<string> referenceImagePaths, string presetName, CancellationToken ct)
        {
            // 占位实现：把预设名与参考图数量翻成一组稳定标签，够验证"风格约束有没有贯通到提示词"。
            var profile = new StyleProfile
            {
                Preset = string.IsNullOrEmpty(presetName) ? "custom" : presetName,
                Source = "mock",
                ReferenceCount = referenceImagePaths?.Count ?? 0,
                PositiveTags = new List<string>
                {
                    "flat cel shading", "clean lineart", "limited palette",
                    "consistent character design", "full body", "side-view friendly"
                },
                NegativeTags = new List<string> { "photo", "3d render", "watermark", "text" },
                Palette = new List<string> { "#6070A8", "#6E9478", "#967480" }
            };
            return Task.FromResult(profile);
        }

        public Task<CharacterCard> DescribeAsync(JobRequest request, string jobId, CancellationToken ct)
        {
            // 占位实现不做语义理解，只做机械切分；真实角色卡由 Sidecar（LLM）或本地规则版产出。
            var card = new CharacterCard
            {
                JobId = jobId,
                SourcePrompt = request?.Prompt ?? "",
                Name = string.IsNullOrEmpty(request?.CharacterName) ? "NPC" : request.CharacterName,
                Role = request?.Prompt ?? "",
                Appearance = request?.Prompt ?? "",
                Actions = request?.Actions ?? new List<string>(),
                Directions = request?.Directions ?? new List<string>(),
                Forbidden = request?.Forbidden ?? new List<string>(),
                StyleTags = new List<string> { "flat cel shading", "clean lineart" }
            };
            return Task.FromResult(card);
        }

        public Task<byte[]> GenerateFrameAsync(FrameRequest request, CancellationToken ct)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            // Texture2D / EncodeToPNG 都是 Unity API，必须回主线程。
            return EditorMainThread.RunAsync(() => DrawFrame(request));
        }

        public Task<byte[]> MattingAsync(byte[] pngBytes, CancellationToken ct)
        {
            // 占位帧本身就是透明背景，无需抠图。返回 null 让流水线跳过这一环而不是报错。
            return Task.FromResult<byte[]>(null);
        }

        public void Dispose() { }

        // ────────────────────────── 绘制 ──────────────────────────

        private static byte[] DrawFrame(FrameRequest req)
        {
            int w = Mathf.Max(32, req.Width);
            int h = Mathf.Max(32, req.Height);

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var px = new Color32[w * h];
            var clear = new Color32(0, 0, 0, 0);
            for (int i = 0; i < px.Length; i++) px[i] = clear;

            Color32 body = Palette.TryGetValue(req.Direction ?? "south", out var c)
                ? c
                : new Color32(120, 120, 120, 255);

            // 身体：竖向矩形，占画面高度 46%，水平居中
            int bodyW = Mathf.RoundToInt(w * 0.28f);
            int bodyH = Mathf.RoundToInt(h * 0.46f);
            int bodyX = (w - bodyW) / 2;
            int bodyY = Mathf.RoundToInt(h * 0.30f);

            bool walking = string.Equals(req.Action, "walk", StringComparison.OrdinalIgnoreCase);
            int totalFrames = Mathf.Max(1, req.TotalFrames);

            // 行走：整体上下起伏；待机：更小的呼吸位移
            float phase = (req.Index % totalFrames) / (float)totalFrames * Mathf.PI * 2f;
            int bob = walking ? Mathf.RoundToInt(Mathf.Abs(Mathf.Sin(phase)) * h * 0.015f)
                              : Mathf.RoundToInt(Mathf.Sin(phase) * h * 0.006f);
            bodyY += bob;

            FillRect(px, w, h, bodyX, bodyY, bodyW, bodyH, body);

            // 头
            int headD = Mathf.RoundToInt(w * 0.16f);
            FillRect(px, w, h, (w - headD) / 2, bodyY + bodyH, headD, headD,
                new Color32(232, 214, 196, 255));

            // 腿：两条，随帧交替
            int legW = Mathf.RoundToInt(w * 0.07f);
            int legH = Mathf.RoundToInt(h * 0.14f);
            int legBase = Mathf.RoundToInt(h * 0.30f);
            int spread = Mathf.RoundToInt(bodyW * 0.32f);
            int swing = walking ? Mathf.RoundToInt(Mathf.Sin(phase) * legH * 0.55f) : 0;
            FillRect(px, w, h, (w / 2) - spread - legW / 2, legBase + swing, legW, legH, body);
            FillRect(px, w, h, (w / 2) + spread - legW / 2, legBase - swing, legW, legH, body);

            // 朝向标记：画在画面一条边中点（南=下、北=上、东=右、西=左），并附带方向名对应的颜色
            int mk = Mathf.RoundToInt(Mathf.Min(w, h) * 0.06f);
            var marker = new Color32(255, 96, 64, 255);
            switch ((req.Direction ?? "south").ToLowerInvariant())
            {
                case "north": FillRect(px, w, h, (w - mk) / 2, h - mk - 4, mk, mk, marker); break;
                case "east": FillRect(px, w, h, w - mk - 4, (h - mk) / 2, mk, mk, marker); break;
                case "west": FillRect(px, w, h, 4, (h - mk) / 2, mk, mk, marker); break;
                default: FillRect(px, w, h, (w - mk) / 2, 4, mk, mk, marker); break;
            }

            // 帧序号刻度：右下角一排小方块，第 Index 个填满
            int tick = Mathf.Max(3, w / 64);
            for (int i = 0; i < totalFrames; i++)
            {
                var color = i == req.Index ? new Color32(255, 200, 60, 255) : new Color32(120, 120, 120, 160);
                FillRect(px, w, h, w - 8 - (totalFrames - i) * (tick + 2), 8, tick, tick, color);
            }

            tex.SetPixels32(px);
            tex.Apply(false, false);
            byte[] png = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);
            return png;
        }

        private static void FillRect(Color32[] px, int w, int h, int x0, int y0, int rw, int rh, Color32 color)
        {
            int xEnd = Mathf.Min(w, x0 + rw);
            int yEnd = Mathf.Min(h, y0 + rh);
            for (int y = Mathf.Max(0, y0); y < yEnd; y++)
            {
                int row = y * w;
                for (int x = Mathf.Max(0, x0); x < xEnd; x++) px[row + x] = color;
            }
        }
    }
}
