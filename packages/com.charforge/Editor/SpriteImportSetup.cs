using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace CharForge.EditorTools
{
    /// <summary>
    /// 自动写贴图的导入设置（本项目三处空白点之一）。
    ///
    /// 为什么需要它：手工在 Sprite Editor 里点几十个切片、调锚点、改 PPU、改过滤模式，
    /// 是 2D 项目里最枯燥也最容易出错的环节。这里读 manifest.json 一次性写对。
    ///
    /// 坐标系说明（这里最容易踩坑）：
    /// Python 侧产出的 SliceInfo.y 已经是 Unity 的**左下原点**坐标，
    /// 与 TextureImporter.spritesheet[].rect 的坐标系一致，直接填即可，不要再翻转。
    /// </summary>
    public static class SpriteImportSetup
    {
        public class SliceInfo
        {
            public string name;
            public float x;
            public float y;
            public float w;
            public float h;
            public float pivotX = 0.5f;
            public float pivotY = 0f;
        }

        public static List<SliceInfo> ParseManifest(string manifestPath)
        {
            var list = new List<SliceInfo>();
            if (!File.Exists(manifestPath))
            {
                Debug.LogError($"[CharForge] 找不到 manifest：{manifestPath}");
                return list;
            }

            var root = JObject.Parse(File.ReadAllText(manifestPath));
            var sprites = root["sprites"] as JArray;
            if (sprites == null) return list;

            foreach (var s in sprites)
            {
                list.Add(new SliceInfo
                {
                    name = (string)s["name"],
                    x = (float)s["x"],
                    y = (float)s["y"],
                    w = (float)s["w"],
                    h = (float)s["h"],
                    pivotX = s["pivot_x"] != null ? (float)s["pivot_x"] : 0.5f,
                    pivotY = s["pivot_y"] != null ? (float)s["pivot_y"] : 0f
                });
            }
            return list;
        }

        /// <summary>按 manifest 配置贴图导入设置。返回切片名 -> Sprite 的映射。</summary>
        public static Dictionary<string, Sprite> Apply(string textureAssetPath, string manifestPath)
        {
            var result = new Dictionary<string, Sprite>();
            var slices = ParseManifest(manifestPath);
            if (slices.Count == 0) return result;

            var root = JObject.Parse(File.ReadAllText(manifestPath));
            int ppu = root["pixels_per_unit"] != null ? (int)root["pixels_per_unit"] : 100;
            string filterMode = root["filter_mode"] != null ? (string)root["filter_mode"] : "Bilinear";
            string textureType = root["texture_type"] != null ? (string)root["texture_type"] : "Sprite";
            bool alphaIsTransparency = root["alpha_is_transparency"] == null || (bool)root["alpha_is_transparency"];

            var importer = AssetImporter.GetAtPath(textureAssetPath) as TextureImporter;
            if (importer == null)
            {
                Debug.LogError($"[CharForge] 无法读取 TextureImporter：{textureAssetPath}");
                return result;
            }

            importer.textureType = textureType == "Default"
                ? TextureImporterType.Default
                : TextureImporterType.Sprite;

            importer.spriteImportMode = SpriteImportMode.Multiple;
            importer.spritePixelsPerUnit = ppu;
            importer.alphaIsTransparency = alphaIsTransparency;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;

            // 像素风用 Point 避免插值糊边；手绘风用 Bilinear 更平滑
            switch (filterMode)
            {
                case "Point":
                    importer.filterMode = FilterMode.Point;
                    importer.textureCompression = TextureImporterCompression.Uncompressed;
                    break;
                case "Trilinear":
                    importer.filterMode = FilterMode.Trilinear;
                    importer.textureCompression = TextureImporterCompression.CompressedHQ;
                    break;
                default:
                    importer.filterMode = FilterMode.Bilinear;
                    importer.textureCompression = TextureImporterCompression.CompressedHQ;
                    break;
            }

            var metas = new SpriteMetaData[slices.Count];
            for (int i = 0; i < slices.Count; i++)
            {
                var s = slices[i];
                metas[i] = new SpriteMetaData
                {
                    name = s.name,
                    rect = new Rect(s.x, s.y, s.w, s.h),
                    alignment = (int)SpriteAlignment.Custom,
                    pivot = new Vector2(s.pivotX, s.pivotY)
                };
            }
            importer.spritesheet = metas;
            importer.SaveAndReimport();

            foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(textureAssetPath))
            {
                if (obj is Sprite sp) result[sp.name] = sp;
            }

            Debug.Log($"[CharForge] 导入设置完成：{slices.Count} 个切片，PPU={ppu}，Filter={filterMode}");
            return result;
        }
    }
}
