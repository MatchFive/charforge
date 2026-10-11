using System.Collections.Generic;
using System.IO;
using CharForge.EditorTools.Data;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.U2D.Sprites;
using UnityEngine;

namespace CharForge.EditorTools
{
    /// <summary>
    /// 按 manifest 一次性写对贴图的导入设置。**本项目三处「插件才做得到」的空白点之一。**
    ///
    /// 独立工具为什么做不到：它只能把 PNG 丢给你，切片数量、锚点、PPU、过滤模式、
    /// 压缩开关得你在 Sprite Editor 里手工点几十下——这正是 2D 项目里最枯燥
    /// 也最容易点错的一环。而 .meta 是 Unity 侧资产，只有插件能在工程内直接写对。
    ///
    /// 采用 Unity 6 推荐的 <c>SpriteDataProviderFactories</c>（多切片的现代 API），
    /// 而不是旧的 <c>TextureImporter.spritesheet</c>：后者在批量切片 + spriteID 稳定性上不可靠，
    /// 且已不属于官方推荐路径。
    ///
    /// 坐标系说明（这里最容易踩坑）：manifest 里的 <c>y</c> 已经由服务端换算为 Unity 的
    /// **左下原点**值，与 <c>Rect</c> / <c>SpriteRect</c> 的坐标系一致，**不要再翻转**。
    /// </summary>
    public static class SpriteImportSetup
    {
        /// <summary>按 manifest 配置导入设置，返回「切片名 → Sprite」映射。</summary>
        public static Dictionary<string, Sprite> Apply(string textureAssetPath, SpriteManifest manifest)
        {
            var result = new Dictionary<string, Sprite>();
            if (manifest == null || manifest.Sprites == null || manifest.Sprites.Count == 0)
            {
                Debug.LogWarning("[CharForge] manifest 中没有切片，跳过导入设置。");
                return result;
            }

            var importer = AssetImporter.GetAtPath(textureAssetPath) as TextureImporter;
            if (importer == null)
            {
                Debug.LogError($"[CharForge] 无法读取 TextureImporter：{textureAssetPath}");
                return result;
            }

            // ── 1. 先写基础导入设置并重新导入，让 provider 能拿到最新的贴图状态 ──
            importer.textureType = manifest.TextureType == "Default"
                ? TextureImporterType.Default
                : TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Multiple;
            importer.spritePixelsPerUnit = Mathf.Max(1, manifest.PixelsPerUnit);
            importer.alphaIsTransparency = manifest.AlphaIsTransparency;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.maxTextureSize = 4096;

            // 像素风必须 Point + 不压缩，否则放大后糊边、颜色也会被压出噪点；
            // 手绘/厚涂用 Bilinear + HQ 压缩，观感更平滑。
            switch (manifest.FilterMode)
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

            importer.SaveAndReimport();

            // ── 2. 用现代 Sprite Data Provider 写入切片 ──
            var factories = new SpriteDataProviderFactories();
            factories.Init();
            var provider = factories.GetSpriteEditorDataProviderFromObject(importer);
            provider.InitSpriteEditorDataProvider();

            var rects = new SpriteRect[manifest.Sprites.Count];
            for (int i = 0; i < manifest.Sprites.Count; i++)
            {
                var s = manifest.Sprites[i];
                rects[i] = new SpriteRect
                {
                    name = s.Name,
                    rect = new Rect(s.X, s.Y, s.W, s.H),
                    alignment = SpriteAlignment.Custom,
                    pivot = new Vector2(Mathf.Clamp01(s.PivotX), Mathf.Clamp01(s.PivotY)),
                    // spriteID 必须稳定生成并在多次导入间保持，否则动画曲线里的 Sprite 引用会断。
                    spriteID = GUID.Generate()
                };
            }

            provider.SetSpriteRects(rects);
            provider.Apply();
            AssetDatabase.ImportAsset(textureAssetPath, ImportAssetOptions.ForceUpdate);

            // ── 3. 回读切片 ──
            foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(textureAssetPath))
            {
                if (obj is Sprite sp) result[sp.name] = sp;
            }

            if (result.Count != manifest.Sprites.Count)
            {
                Debug.LogWarning(
                    $"[CharForge] 切片数不一致：manifest 声明 {manifest.Sprites.Count} 个，实际导入 {result.Count} 个。" +
                    "请检查 manifest 的坐标是否越出贴图边界。");
            }

            Debug.Log($"[CharForge] 导入设置完成：{result.Count} 个切片，" +
                      $"PPU={manifest.PixelsPerUnit}，Filter={manifest.FilterMode}");
            return result;
        }

        /// <summary>从 JSON 文件读 manifest 后配置导入设置。</summary>
        public static Dictionary<string, Sprite> Apply(string textureAssetPath, string manifestJsonPath)
        {
            if (!File.Exists(manifestJsonPath))
            {
                Debug.LogError($"[CharForge] 找不到 manifest：{manifestJsonPath}");
                return new Dictionary<string, Sprite>();
            }
            var manifest = JsonConvert.DeserializeObject<SpriteManifest>(File.ReadAllText(manifestJsonPath));
            return Apply(textureAssetPath, manifest);
        }
    }
}
