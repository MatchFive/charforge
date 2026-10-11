using System.Collections.Generic;
using System.Linq;
using CharForge.EditorTools;
using CharForge.EditorTools.Data;
using UnityEditor;
using UnityEngine;
using UnityEngine.U2D.Animation;

namespace CharForge.EditorTools.Integrations
{
    /// <summary>
    /// Sprite Library 集成：把四向 idle/walk 组织成**分类 / 标签**，供运行期 Sprite Swap 使用。
    ///
    /// **为什么这件事只有插件能做**：SpriteLibraryAsset 是 Unity 侧的 ScriptableObject 资产，
    /// 独立工具拿不到资产管线，只能给你一堆 PNG。而有了 Sprite Library + SpriteResolver，
    /// 运行期换装、换表情、切朝向**不需要重新导入、也不会丢蒙皮绑定**——
    /// 这是 2D 角色从"帧动画"走向"可组合角色"的关键一步。
    ///
    /// 组织结构（刻意选可预期的映射，不玩花活）：
    ///   分类 = 动作（idle / walk / …）
    ///   标签 = 朝向（south / north / east / west）
    ///   精灵 = 该片段的**首帧**
    ///
    /// **可选依赖**：本文件所在程序集由 versionDefine + defineConstraints 控制——
    /// 装了 com.unity.2d.animation 就自动编译并生效，没装则整个程序集不参与编译，
    /// 核心功能一行不少、也不会冒出"程序集引用缺失"的报错。
    /// </summary>
    [InitializeOnLoad]
    public static class SpriteLibraryWriter
    {
        private const string Suffix = "_SpriteLibrary";

        static SpriteLibraryWriter()
        {
            // 程序集加载时注册钩子。Editor 重载后静态构造会重新执行，无需手工反注册。
            PostAssembleHooks.AfterAssembleRoot += Attach;
        }

        private static void Attach(AssembleContext context)
        {
            if (context?.Root == null || context.Manifest == null || context.Spec == null) return;

            var asset = BuildLibraryAsset(context);
            if (asset == null) return;

            // 资产落盘
            string assetPath = $"{context.AssetDir}/{context.PrefabName}{Suffix}.asset";
            AssetDatabase.DeleteAsset(assetPath);
            AssetDatabase.CreateAsset(asset, assetPath);

            // SetDirty 不能省。SpriteLibraryAsset 的分类/标签存在内部序列化结构里，
            // 只 CreateAsset + SaveAssets 会出现"当时看得见、重启 Unity 后标签消失"的假成功
            // （Unity 官方论坛有该问题的实例记录）。标记脏对象才会真正写进 .asset。
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();

            // 挂到 prefab 根物体：SpriteLibrary 提供查表能力，SpriteResolver 让根上的
            // SpriteRenderer 能按 (分类, 标签) 取图，而不再依赖 Animator 硬切帧。
            var library = context.Root.AddComponent<SpriteLibrary>();
            library.spriteLibraryAsset = asset;

            var resolver = context.Root.AddComponent<SpriteResolver>();
            var defaultAction = ResolveDefaultAction(context.Spec);

            EditorApplication.delayCall += () =>
            {
                if (resolver == null) return;
                resolver.SetCategoryAndLabel(defaultAction, "south");
            };

            context.Messages.Add(
                $"Sprite Library 已生成：{assetPath}（分类={string.Join("/", Categories(context))}，" +
                $"标签=朝向）。运行期可用 SpriteResolver.SetCategoryAndLabel(\"walk\", \"east\") 直接换套。");
        }

        private static List<string> Categories(AssembleContext context)
        {
            return context.Spec.Animations
                .Select(a => a.Action)
                .Distinct()
                .ToList();
        }

        private static string ResolveDefaultAction(AssemblySpec spec)
        {
            var idle = spec.Animations.FirstOrDefault(a => a.Action == "idle");
            return idle?.Action ?? spec.Animations.FirstOrDefault()?.Action ?? "idle";
        }

        private static SpriteLibraryAsset BuildLibraryAsset(AssembleContext context)
        {
            var asset = ScriptableObject.CreateInstance<SpriteLibraryAsset>();
            asset.name = context.PrefabName + Suffix;

            int added = 0;
            foreach (var anim in context.Spec.Animations)
            {
                // 首帧：切片名形如 south_idle_00
                string firstName = anim.Sprites != null && anim.Sprites.Count > 0
                    ? anim.Sprites[0]
                    : $"{anim.Direction}_{anim.Action}_00";

                if (!context.Sprites.TryGetValue(firstName, out var sprite) || sprite == null) continue;

                asset.AddCategoryLabel(sprite, anim.Action, anim.Direction);
                added++;
            }

            if (added == 0)
            {
                Object.DestroyImmediate(asset);
                context.Messages.Add("Sprite Library 未生成：没有可用的首帧切片。");
                return null;
            }

            return asset;
        }
    }
}
