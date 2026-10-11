using System;
using System.Collections.Generic;
using UnityEngine;

namespace CharForge.EditorTools.Services
{
    /// <summary>
    /// 本地抠图兜底：背景色键控 + 从四边出发的连通域判定。纯 CPU、零依赖、不需要 GPU。
    ///
    /// **为什么必须有它**：扩散模型产出的 PNG 是**全不透明**的，「transparent background」
    /// 只是一句提示词，不会真的产生 alpha 通道。而下游的 <see cref="FrameNormalizer"/>
    /// 是靠 alpha 来裁切并反算角色包围盒的——拿到全不透明图时，包围盒就等于整张画布，
    /// 于是碰撞体会盖住整个屏幕，而且这个问题**不会报任何错**，只会「看起来不对」。
    ///
    /// 为什么不是简单的距离键控：若只按「像素与背景色的距离」判定，角色身上任何
    /// 接近背景色的部位（青灰长衫对灰底、青碧釉色对绿底）都会被击穿成空洞，
    /// 这种损坏在成品上极难察觉。所以这里先用**四边连通域**确定「哪些背景是连到画外的」，
    /// 只把连到画外的部分判为背景——衣服上的相近色因为被不透明像素包住，不会被误伤。
    ///
    /// 质量上限：明显低于 rembg（U²-Net 语义分割）。真实资产请优先在 ComfyUI 里接
    /// rembg 节点，或把本类当作「没装 rembg 时的确定性兜底」。
    /// </summary>
    public static class LocalMatting
    {
        /// <summary>与背景色的欧氏距离小于此值即视为「可能是背景」。</summary>
        private const float KeyTolerance = 60f;

        /// <summary>边缘软过渡带宽度：用于消除抗锯齿留下的毛边。</summary>
        private const float SoftBand = 48f;

        public struct Outcome
        {
            public byte[] Png;
            /// <summary>是否真的改动了像素。false 表示原图已带 alpha，或键控不适用。</summary>
            public bool Changed;
            /// <summary>非空的说明文字，调用方应把它转成告警展示出来。</summary>
            public string Note;
        }

        /// <summary>
        /// 保证返回的 PNG 带可用的 alpha。
        /// 已经是透明图的原样返回；全不透明的图做色键抠图；无法处理时也原样返回并给出说明。
        /// </summary>
        public static Outcome EnsureTransparent(byte[] pngBytes)
        {
            var unchanged = new Outcome { Png = pngBytes, Changed = false, Note = null };
            if (pngBytes == null || pngBytes.Length == 0) return unchanged;

            var tex = Decode(pngBytes);
            if (tex == null) return unchanged;

            try
            {
                int w = tex.width, h = tex.height;
                var px = tex.GetPixels32();
                if (px.Length < w * h || w < 4 || h < 4) return unchanged;

                // 已经有 alpha 了就别动它——后端（rembg / 占位帧）给的透明图是更好的结果。
                for (int i = 0; i < px.Length; i++)
                {
                    if (px[i].a < 250) return unchanged;
                }

                Color bg = SampleBackground(px, w, h);

                // 四边连通域：只有「与画外相连且颜色接近背景色」的像素才算背景。
                var isBackground = new bool[w * h];
                var queue = new Queue<int>();
                for (int x = 0; x < w; x++)
                {
                    TrySeed(px, w, h, x, 0, bg, isBackground, queue);
                    TrySeed(px, w, h, x, h - 1, bg, isBackground, queue);
                }
                for (int y = 0; y < h; y++)
                {
                    TrySeed(px, w, h, 0, y, bg, isBackground, queue);
                    TrySeed(px, w, h, w - 1, y, bg, isBackground, queue);
                }

                while (queue.Count > 0)
                {
                    int idx = queue.Dequeue();
                    int cx = idx % w, cy = idx / w;
                    if (cx > 0) TrySeed(px, w, h, cx - 1, cy, bg, isBackground, queue);
                    if (cx < w - 1) TrySeed(px, w, h, cx + 1, cy, bg, isBackground, queue);
                    if (cy > 0) TrySeed(px, w, h, cx, cy - 1, bg, isBackground, queue);
                    if (cy < h - 1) TrySeed(px, w, h, cx, cy + 1, bg, isBackground, queue);
                }

                int backgroundCount = 0;
                for (int i = 0; i < isBackground.Length; i++) if (isBackground[i]) backgroundCount++;

                float ratio = backgroundCount / (float)(w * h);
                if (ratio < 0.02f)
                {
                    // 背景几乎没连到画外：多半是满幅场景图，或无纯色背景。
                    // 此时强行键控只会毁掉画面，原样返回并说明原因。
                    return new Outcome
                    {
                        Png = pngBytes,
                        Changed = false,
                        Note = $"本地抠图跳过：仅 {ratio:P0} 的像素被判为背景，说明这张图没有可用的纯色背景，" +
                               "包围盒会等于整幅画布。请在提示词里要求纯色背景（如 solid magenta background），" +
                               "或改用 ComfyUI 的 rembg 节点。"
                    };
                }

                // 上色：背景全透明；紧邻背景的角色像素按颜色距离做软过渡，压掉毛边。
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int idx = y * w + x;
                        if (isBackground[idx])
                        {
                            px[idx] = new Color32(px[idx].r, px[idx].g, px[idx].b, 0);
                            continue;
                        }
                        if (!TouchesBackground(isBackground, w, h, x, y)) continue;

                        float d = Distance(px[idx], bg);
                        float t = Mathf.Clamp01((d - KeyTolerance) / SoftBand);
                        var a = (byte)Mathf.Clamp(Mathf.RoundToInt(t * 255f), 1, 255);
                        px[idx] = new Color32(px[idx].r, px[idx].g, px[idx].b, a);
                    }
                }

                var outTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                try
                {
                    outTex.SetPixels32(px);
                    outTex.Apply(false, false);
                    return new Outcome
                    {
                        Png = outTex.EncodeToPNG(),
                        Changed = true,
                        // 色键抠图对纯色背景有效，对渐变/纹理背景会留下残边——说清楚，
                        // 免得用户把它当成品，然后奇怪为什么边缘「脏」。
                        Note = $"已用本地色键抠图（背景占比 {ratio:P0}）。这是无 rembg 时的兜底，" +
                               "边缘质量明显低于 rembg；要更好的效果请在 ComfyUI 工作流里加 rembg 节点。"
                    };
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(outTex);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        // ────────────────────────── 内部工具 ──────────────────────────

        private static void TrySeed(
            Color32[] px, int w, int h, int x, int y, Color bg, bool[] isBackground, Queue<int> queue)
        {
            int idx = y * w + x;
            if (isBackground[idx]) return;
            if (Distance(px[idx], bg) > KeyTolerance) return;
            isBackground[idx] = true;
            queue.Enqueue(idx);
        }

        private static bool TouchesBackground(bool[] isBackground, int w, int h, int x, int y)
        {
            if (x > 0 && isBackground[y * w + x - 1]) return true;
            if (x < w - 1 && isBackground[y * w + x + 1]) return true;
            if (y > 0 && isBackground[(y - 1) * w + x]) return true;
            if (y < h - 1 && isBackground[(y + 1) * w + x]) return true;
            return false;
        }

        /// <summary>取四角像素的中位数当背景色——比取单个角更抗噪点与水印。</summary>
        private static Color SampleBackground(Color32[] px, int w, int h)
        {
            var corners = new[]
            {
                px[0], px[w - 1], px[(h - 1) * w], px[h * w - 1]
            };

            return new Color(
                Median(corners[0].r, corners[1].r, corners[2].r, corners[3].r),
                Median(corners[0].g, corners[1].g, corners[2].g, corners[3].g),
                Median(corners[0].b, corners[1].b, corners[2].b, corners[3].b));
        }

        private static float Median(byte a, byte b, byte c, byte d)
        {
            float[] v = { a, b, c, d };
            Array.Sort(v);
            return (v[1] + v[2]) * 0.5f;
        }

        private static float Distance(Color32 p, Color bg)
        {
            float dr = p.r - bg.r, dg = p.g - bg.g, db = p.b - bg.b;
            return Mathf.Sqrt(dr * dr + dg * dg + db * db);
        }

        private static Texture2D Decode(byte[] png)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(png))
            {
                UnityEngine.Object.DestroyImmediate(tex);
                return null;
            }
            return tex;
        }
    }
}
