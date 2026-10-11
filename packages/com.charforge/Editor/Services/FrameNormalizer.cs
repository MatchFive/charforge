using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace CharForge.EditorTools.Services
{
    /// <summary>
    /// [5] Normalize 的 C# 实现（Unity 引擎内完成，不需要 Python）。
    ///
    /// 为什么"归一化"这一步值得在引擎内重做一遍：它直接决定生成的角色**能不能用**。
    /// 不归一化会发生什么：
    ///   - 角色在画布里位置飘忽 → 播放动画时抽搐
    ///   - 每帧角色大小不一致 → 走路时忽大忽小
    ///   - 锚点不在脚底 → 角色悬空或陷进地面
    ///   - 像素风没对齐像素网格 → 移动时边缘抖动
    ///
    /// 四个动作：按 alpha 裁切 → **统一缩放**（关键：所有帧共用一个 scale，
    /// 绝不能逐帧自适应，否则"忽大忽小"就是自己造出来的）→ 放进统一画布、脚底居中锚定
    /// → 可选像素网格量化。
    ///
    /// 全部使用 Texture2D / RenderTexture：缩放交给 GPU（Graphics.Blit），
    /// 只有裁切与拼贴是 CPU 循环，32 帧 512px 在普通机器上是秒级。
    /// </summary>
    public class FrameNormalizer
    {
        public int CanvasWidth = 512;
        public int CanvasHeight = 512;

        /// <summary>四边留白比例。留 8% 是为了防"贴边"——相邻角色并排时不会互相切到。</summary>
        public float MarginRatio = 0.08f;

        /// <summary>像素网格量化倍数。像素风填 canvasWidth/targetResolution（如 8），手绘填 1。</summary>
        public int PixelGrid = 1;

        private const int AlphaThreshold = 8;

        public class Result
        {
            public List<byte[]> Frames = new List<byte[]>();

            /// <summary>所有帧的可视包围盒并集（像素），用于反算角色在世界单位的尺寸。</summary>
            public Rect BoundsPixels;

            /// <summary>是否至少有一帧含非透明像素。</summary>
            public bool HasContent;
        }

        /// <summary>批量归一化。<b>必须整批一起处理</b>，因为统一 scale 依赖全批的最大尺寸。</summary>
        public Task<Result> NormalizeAllAsync(IReadOnlyList<byte[]> frames)
        {
            return EditorMainThread.RunAsync(() => NormalizeAll(frames));
        }

        private Result NormalizeAll(IReadOnlyList<byte[]> frames)
        {
            var result = new Result();
            if (frames == null || frames.Count == 0) return result;

            // ── 1. 解码 + 裁切，记录每帧裁切后的纹理与包围盒 ──
            var cropped = new List<Texture2D>();
            int maxW = 1, maxH = 1;
            int minLeft = int.MaxValue, minTop = int.MaxValue, maxRight = 0, maxBottom = 0;

            try
            {
                foreach (var png in frames)
                {
                    var decoded = Decode(png);
                    if (decoded == null) { cropped.Add(null); continue; }

                    var (crop, box) = TrimAlpha(decoded);
                    UnityEngine.Object.DestroyImmediate(decoded);

                    cropped.Add(crop);
                    if (crop == null) continue;

                    maxW = Mathf.Max(maxW, crop.width);
                    maxH = Mathf.Max(maxH, crop.height);
                    minLeft = Mathf.Min(minLeft, box.xMin);
                    minTop = Mathf.Min(minTop, box.yMin);
                    maxRight = Mathf.Max(maxRight, box.xMax);
                    maxBottom = Mathf.Max(maxBottom, box.yMax);
                }

                if (maxRight <= 0)
                {
                    result.HasContent = false;
                    return result;   // 全透明，上游出了问题
                }

                result.HasContent = true;
                result.BoundsPixels = new Rect(minLeft, minTop, maxRight - minLeft, maxBottom - minTop);

                // ── 2. 统一 scale：以最大的那一帧为基准，保证谁都不会超出可用区 ──
                float usableW = CanvasWidth * (1f - MarginRatio * 2f);
                float usableH = CanvasHeight * (1f - MarginRatio * 2f);
                float scale = Mathf.Min(usableW / maxW, usableH / maxH);

                // ── 3. 逐帧缩放 + 贴到统一画布（水平居中、脚底对齐）──
                foreach (var crop in cropped)
                {
                    result.Frames.Add(PasteToCanvas(crop, scale, maxH));
                }

                // ── 4. 像素网格量化（整批一起做，保持网格相位一致）──
                if (PixelGrid > 1)
                {
                    for (int i = 0; i < result.Frames.Count; i++)
                        result.Frames[i] = QuantizeToGrid(result.Frames[i], PixelGrid);
                }
            }
            finally
            {
                foreach (var t in cropped)
                    if (t != null) UnityEngine.Object.DestroyImmediate(t);
            }

            return result;
        }

        // ────────────────────────── 单帧步骤 ──────────────────────────

        private byte[] PasteToCanvas(Texture2D crop, float scale, int maxHeight)
        {
            var canvas = new Texture2D(CanvasWidth, CanvasHeight, TextureFormat.RGBA32, false);
            var clear = new Color32(CanvasWidth * CanvasHeight);
            for (int i = 0; i < clear.Length; i++) clear[i] = new Color32(0, 0, 0, 0);
            canvas.SetPixels32(clear);

            if (crop != null)
            {
                int newW = Mathf.Max(1, Mathf.RoundToInt(crop.width * scale));
                int newH = Mathf.Max(1, Mathf.RoundToInt(crop.height * scale));
                var scaled = BlitScale(crop, newW, newH);

                int x = (CanvasWidth - newW) / 2;
                // 脚底对齐：底部留出下边距。maxHeight 参与是为了让不同高矮的角色
                // 共享同一条"地面线"，而不是各自贴底导致视觉上高度不一。
                int groundY = Mathf.RoundToInt(CanvasHeight * MarginRatio);
                int y = groundY + Mathf.RoundToInt((maxHeight - crop.height) * scale);

                canvas.SetPixels32(x, y, newW, newH, scaled.GetPixels32());
                UnityEngine.Object.DestroyImmediate(scaled);
            }

            canvas.Apply(false, false);
            byte[] png = canvas.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(canvas);
            return png;
        }

        private byte[] QuantizeToGrid(byte[] png, int grid)
        {
            var tex = Decode(png);
            if (tex == null) return png;

            int smallW = Mathf.Max(1, CanvasWidth / grid);
            int smallH = Mathf.Max(1, CanvasHeight / grid);

            var down = BlitScale(tex, smallW, smallH, FilterMode.Box);
            var up = BlitScale(down, CanvasWidth, CanvasHeight, FilterMode.Point);

            byte[] outPng = up.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);
            UnityEngine.Object.DestroyImmediate(down);
            UnityEngine.Object.DestroyImmediate(up);
            return outPng;
        }

        // ────────────────────────── 工具 ──────────────────────────

        private static Texture2D Decode(byte[] png)
        {
            if (png == null || png.Length == 0) return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(png))
            {
                UnityEngine.Object.DestroyImmediate(tex);
                return null;
            }
            return tex;
        }

        /// <summary>按 alpha 裁掉四周全透明区域。返回裁切后的纹理与它在原图中的包围盒。</summary>
        private static (Texture2D crop, Rect box) TrimAlpha(Texture2D src)
        {
            var px = src.GetPixels32();
            int w = src.width, h = src.height;

            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    if (px[row + x].a <= AlphaThreshold) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }

            if (maxX < 0) return (null, new Rect(0, 0, 0, 0));   // 整帧透明

            int cw = maxX - minX + 1;
            int ch = maxY - minY + 1;
            var crop = new Texture2D(cw, ch, TextureFormat.RGBA32, false);
            crop.SetPixels32(src.GetPixels(minX, minY, cw, ch));
            crop.Apply(false, false);
            return (crop, new Rect(minX, minY, cw, ch));
        }

        /// <summary>用 GPU 缩放纹理（Graphics.Blit），比逐像素双线性采样快一个数量级。</summary>
        private static Texture2D BlitScale(Texture2D src, int width, int height, FilterMode filter = FilterMode.Bilinear)
        {
            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            rt.filterMode = filter;
            var previous = RenderTexture.active;

            Graphics.Blit(src, rt);
            RenderTexture.active = rt;

            var outTex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            outTex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            outTex.Apply(false, false);

            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            return outTex;
        }

        /// <summary>推荐像素网格：512 画布、目标 64px 像素风 → grid = 8。</summary>
        public static int SuggestPixelGrid(int canvasWidth, int targetResolution = 64)
        {
            return Mathf.Max(1, Mathf.RoundToInt(canvasWidth / (float)Mathf.Max(1, targetResolution)));
        }
    }
}
