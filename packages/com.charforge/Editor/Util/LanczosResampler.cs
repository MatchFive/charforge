using System;
using UnityEngine;

namespace CharForge.EditorTools.Util
{
    /// <summary>
    /// 纯 C# 的 Lanczos3 重采样（Editor 侧，无外部依赖）。
    ///
    /// 为什么不用 <c>Texture2D</c> 的双线性/最近邻：<see cref="FrameNormalizer"/> 交出来的
    /// 立绘是**带透明通道**的资产，双线性在缩小透明边缘时会把 RGB 采样进「alpha=0 的像素」，
    /// 那些像素的 RGB 是无意义的残留值（Qwen 透明区甚至是品红噪点）——结果就是轮廓上一圈
    /// 暗色/品色的脏边。正确做法是**预乘 alpha 后再插值**（what every image library does），
    /// 缩完再反预乘，透明边的颜色永远干净。
    ///
    /// 可分离两趟（水平 → 垂直）：复杂度 O(srcW·dstH·k + dstW·srcH·k)，k 为核宽。
    /// 缩小时核按 srcW/dstW 拉宽（抗锯齿的正确行为），放大时核宽不变。
    ///
    /// 纯托管数组进出，不碰 Unity API——**可在任意线程调用**；PNG 的解码/编码由调用方
    /// （主线程）负责，见 <see cref="CharacterAssetService"/>。
    /// </summary>
    internal static class LanczosResampler
    {
        private const float KernelA = 3f;   // Lanczos3：a=3，锐度与振铃的经典折中

        /// <summary>把 RGBA 像素数组从 srcW×srcH 重采样到 dstW×dstH（等比与否由调用方保证）。</summary>
        public static Color32[] Resample(Color32[] src, int srcW, int srcH, int dstW, int dstH)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (src.Length != srcW * srcH)
                throw new ArgumentException($"像素数组长度 {src.Length} 与 {srcW}×{srcH} 不符");
            if (dstW <= 0 || dstH <= 0)
                throw new ArgumentException($"目标尺寸非法：{dstW}×{dstH}");

            // ① 预乘 alpha → float 平面（0..1），透明区的 RGB 归零后不再污染插值。
            var plane = new float[srcW * srcH * 4];
            for (int i = 0; i < src.Length; i++)
            {
                float a = src[i].a / 255f;
                plane[i * 4 + 0] = src[i].r / 255f * a;
                plane[i * 4 + 1] = src[i].g / 255f * a;
                plane[i * 4 + 2] = src[i].b / 255f * a;
                plane[i * 4 + 3] = a;
            }

            // ② 水平趟：srcW → dstW（每行独立）。
            var contribX = BuildContributions(srcW, dstW);
            var mid = new float[dstW * srcH * 4];
            for (int y = 0; y < srcH; y++)
            {
                int rowOff = y * srcW * 4;
                for (int x = 0; x < dstW; x++)
                {
                    var c = contribX[x];
                    float r = 0, g = 0, b = 0, a = 0;
                    for (int k = 0; k < c.Weights.Length; k++)
                    {
                        int off = rowOff + c.Start[k] * 4;
                        float w = c.Weights[k];
                        r += plane[off] * w;
                        g += plane[off + 1] * w;
                        b += plane[off + 2] * w;
                        a += plane[off + 3] * w;
                    }
                    int o = (y * dstW + x) * 4;
                    mid[o] = r; mid[o + 1] = g; mid[o + 2] = b; mid[o + 3] = a;
                }
            }

            // ③ 垂直趟：srcH → dstH。
            var contribY = BuildContributions(srcH, dstH);
            var dst = new Color32[dstW * dstH];
            for (int y = 0; y < dstH; y++)
            {
                var c = contribY[y];
                for (int x = 0; x < dstW; x++)
                {
                    float r = 0, g = 0, b = 0, a = 0;
                    for (int k = 0; k < c.Weights.Length; k++)
                    {
                        int off = (c.Start[k] * dstW + x) * 4;
                        float w = c.Weights[k];
                        r += mid[off] * w;
                        g += mid[off + 1] * w;
                        b += mid[off + 2] * w;
                        a += mid[off + 3] * w;
                    }

                    // ④ 反预乘 + 落回 byte（钳位防振铃过冲出 0..1）。
                    a = Mathf.Clamp01(a);
                    byte ar = (byte)Mathf.RoundToInt(a * 255f);
                    byte rr, gr, br;
                    if (a <= 1e-6f)
                    {
                        rr = gr = br = 0;   // 全透明像素的 RGB 置零，产物纯净
                    }
                    else
                    {
                        rr = ToByte(r / a);
                        gr = ToByte(g / a);
                        br = ToByte(b / a);
                    }
                    dst[y * dstW + x] = new Color32(rr, gr, br, ar);
                }
            }
            return dst;
        }

        /// <summary>为 dst 侧每个位置预计算源侧贡献（起点 + 权重），两趟共用同一套数学。</summary>
        private static Contribution[] BuildContributions(int srcLen, int dstLen)
        {
            // 缩小时核在源坐标系里展宽 scale 倍（低通，抗混叠）；放大时保持原核。
            double scale = (double)dstLen / srcLen;
            double filterScale = scale < 1.0 ? 1.0 / scale : 1.0;
            double radius = KernelA * filterScale;

            var result = new Contribution[dstLen];
            for (int i = 0; i < dstLen; i++)
            {
                double center = (i + 0.5) / scale - 0.5;   // 源坐标系中心（半像素对齐）
                int lo = (int)Math.Ceiling(center - radius);
                int hi = (int)Math.Floor(center + radius);
                lo = Math.Max(lo, 0);
                hi = Math.Min(hi, srcLen - 1);
                if (hi < lo) { lo = hi = Math.Clamp((int)center, 0, srcLen - 1); }

                int count = hi - lo + 1;
                var starts = new int[count];
                var weights = new float[count];
                double sum = 0;
                for (int k = 0; k < count; k++)
                {
                    double d = (lo + k - center) / filterScale;
                    double w = Lanczos(d);
                    weights[k] = (float)w;
                    sum += w;
                    starts[k] = lo + k;
                }
                if (sum > 1e-9)
                    for (int k = 0; k < count; k++)
                        weights[k] /= (float)sum;          // 归一化，保证能量守恒

                result[i] = new Contribution { Start = starts, Weights = weights };
            }
            return result;
        }

        private static double Lanczos(double x)
        {
            if (x == 0.0) return 1.0;
            x = Math.Abs(x);
            if (x >= KernelA) return 0.0;
            double px = Math.PI * x;
            return KernelA * Math.Sin(px) * Math.Sin(px / KernelA) / (px * px);
        }

        private static byte ToByte(float v) => (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);

        private struct Contribution
        {
            public int[] Start;
            public float[] Weights;
        }
    }
}
