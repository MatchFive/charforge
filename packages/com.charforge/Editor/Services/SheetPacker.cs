using System;
using System.Collections.Generic;
using System.Linq;
using CharForge.EditorTools.Data;
using UnityEngine;

namespace CharForge.EditorTools.Services
{
    /// <summary>待打包的一帧。</summary>
    public class PlacedFrame
    {
        public string Direction = "south";
        public string Action = "idle";
        public int Index;
        public byte[] Png;

        public string SliceName => $"{Direction}_{Action}_{Index:00}";
    }

    /// <summary>
    /// [6] Pack 的 C# 实现：把归一化后的帧拼成一张 sprite sheet，并算出切片坐标与清单。
    ///
    /// **布局是有讲究的**：一行一个 clip（如 south_idle 一行、south_walk 一行），
    /// 而不是紧凑排布。紧凑排布能省一点贴图空间，但切片名与位置会变得不可读，
    /// 人排查问题时得拿计算器算坐标——为了省几个像素不值得。
    ///
    /// 坐标约定（与 Python 侧完全一致，改一处要同步改另一处）：
    ///   - 清单里的 y 是 Unity 的**左下原点**值，`SpriteImportSetup` 直接填，不要再翻转
    ///   - 第 0 个 clip 位于 sheet 的**最上一行**
    /// </summary>
    public static class SheetPacker
    {
        public class PackResult
        {
            public byte[] SheetPng;
            public SpriteManifest Manifest;
            public readonly List<string> Messages = new List<string>();
            public bool Ok => SheetPng != null && SheetPng.Length > 0;
        }

        private static readonly string[] DirectionOrder = { "south", "north", "east", "west" };

        public static PackResult Pack(
            string jobId,
            IReadOnlyList<PlacedFrame> frames,
            int cellWidth,
            int cellHeight,
            int columns,
            int pixelsPerUnit,
            bool pixelArt,
            CharacterBounds bounds)
        {
            var result = new PackResult();
            if (frames == null || frames.Count == 0)
            {
                result.Messages.Add("没有可打包的帧。");
                return result;
            }

            // 按 clip 分组，顺序稳定（方向 → 动作），避免每次任务顺序抖动导致 .meta 无谓变更
            var groups = frames
                .GroupBy(f => $"{f.Direction}_{f.Action}")
                .Select(g => new
                {
                    Key = g.Key,
                    Items = g.OrderBy(f => f.Index).ToList()
                })
                .OrderBy(g => DirectionRank(g.Key))
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .ToList();

            int rows = groups.Count;
            int sheetW = cellWidth * columns;
            int sheetH = cellHeight * rows;

            var sheet = NewTransparentTexture(sheetW, sheetH);
            var rects = new List<SliceInfo>();

            try
            {
                for (int r = 0; r < rows; r++)
                {
                    var items = groups[r].Items;
                    for (int c = 0; c < items.Count && c < columns; c++)
                    {
                        var frame = items[c];
                        var cell = DecodeToCell(frame.Png, cellWidth, cellHeight);
                        if (cell == null)
                        {
                            result.Messages.Add($"帧 {frame.SliceName} 解码失败，已跳过。");
                            continue;
                        }

                        // Texture2D 像素坐标是左下原点；第 0 行 clip 放在最上面一行，
                        // 所以 y = (rows - r - 1) * cellHeight。
                        int x = c * cellWidth;
                        int y = (rows - r - 1) * cellHeight;
                        sheet.SetPixels32(x, y, cellWidth, cellHeight, cell.GetPixels32());
                        UnityEngine.Object.DestroyImmediate(cell);

                        rects.Add(new SliceInfo
                        {
                            Name = frame.SliceName,
                            X = x,
                            Y = y,
                            W = cellWidth,
                            H = cellHeight,
                            PivotX = 0.5f,
                            PivotY = 0f          // 脚底锚点：角色不悬空的关键
                        });
                    }

                    if (items.Count > columns)
                    {
                        result.Messages.Add(
                            $"clip {groups[r].Key} 有 {items.Count} 帧，超过列数 {columns}，" +
                            "多余帧未入 sheet。请调大 columns 或减少每片段帧数。");
                    }
                }

                sheet.Apply(false, false);
                result.SheetPng = sheet.EncodeToPNG();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(sheet);
            }

            result.Manifest = new SpriteManifest
            {
                JobId = jobId,
                Sheet = new SheetInfo
                {
                    Path = "sheet.png",
                    CellWidth = cellWidth,
                    CellHeight = cellHeight,
                    Columns = columns,
                    Rows = rows,
                    FrameCount = rects.Count
                },
                Sprites = rects,
                PixelsPerUnit = pixelsPerUnit,
                FilterMode = pixelArt ? "Point" : "Bilinear",
                AlphaIsTransparency = true,
                TextureType = "Sprite",
                CharacterBounds = bounds
            };

            result.Messages.Add($"打包完成：{rows} 行 × {columns} 列，{rects.Count} 个切片。");
            return result;
        }

        /// <summary>
        /// 由归一化阶段测得的像素包围盒换算成世界单位的角色范围。
        /// 这一步是「碰撞体贴合角色」的数据来源，缺了它装配出来的 prefab 一进场景就穿模。
        /// </summary>
        public static CharacterBounds BoundsFromPixels(
            Rect boundsPixels, int cellWidth, int pixelsPerUnit)
        {
            float ppu = Mathf.Max(1, pixelsPerUnit);
            return new CharacterBounds
            {
                Width = boundsPixels.width / ppu,
                Height = boundsPixels.height / ppu,
                // 锚点在画布底部中线：水平偏移相对画布中心，垂直偏移就是距底边的高度
                CenterX = (boundsPixels.center.x - cellWidth / 2f) / ppu,
                CenterY = boundsPixels.center.y / ppu
            };
        }

        private static int DirectionRank(string clipKey)
        {
            string dir = clipKey.Split('_')[0];
            int idx = Array.IndexOf(DirectionOrder, dir);
            return idx < 0 ? 99 : idx;
        }

        private static Texture2D NewTransparentTexture(int width, int height)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var clear = new Color32[width * height];
            for (int i = 0; i < clear.Length; i++) clear[i] = new Color32(0, 0, 0, 0);
            tex.SetPixels32(clear);
            return tex;
        }

        private static Texture2D DecodeToCell(byte[] png, int cellWidth, int cellHeight)
        {
            if (png == null || png.Length == 0) return null;

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(png))
            {
                UnityEngine.Object.DestroyImmediate(tex);
                return null;
            }

            if (tex.width == cellWidth && tex.height == cellHeight) return tex;

            // 尺寸不符时先硬化到画布尺寸，保证切片矩形与内容严格对齐
            var rt = RenderTexture.GetTemporary(cellWidth, cellHeight, 0, RenderTextureFormat.ARGB32);
            var previous = RenderTexture.active;
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;

            var cell = new Texture2D(cellWidth, cellHeight, TextureFormat.RGBA32, false);
            cell.ReadPixels(new Rect(0, 0, cellWidth, cellHeight), 0, 0);
            cell.Apply(false, false);

            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            UnityEngine.Object.DestroyImmediate(tex);
            return cell;
        }
    }
}
