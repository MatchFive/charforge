using System;
using System.Collections.Generic;
using CharForge.EditorTools.Data;
using UnityEngine;

namespace CharForge.EditorTools
{
    /// <summary>装配过程中的上下文，传给装配后的扩展钩子。</summary>
    public class AssembleContext
    {
        public string JobId = "";
        public string AssetDir = "";
        public string PrefabName = "";
        public AssemblySpec Spec;
        public SpriteManifest Manifest;

        /// <summary>切片名 → Sprite。</summary>
        public Dictionary<string, Sprite> Sprites = new Dictionary<string, Sprite>();

        /// <summary>正在装配的 prefab 根物体（尚未存盘，可直接挂组件/改属性）。</summary>
        public GameObject Root;

        /// <summary>把信息带回装配报告。</summary>
        public readonly List<string> Messages = new List<string>();
    }

    /// <summary>
    /// 装配完成后的扩展点。
    ///
    /// **这是"可选依赖不污染核心"的做法**：Sprite Library 的生成需要
    /// com.unity.2d.animation 包（版本随 Unity 版本变化，不能硬依赖）。于是把它放进
    /// 一个由 versionDefine 自动开关的独立程序集，通过 <c>[InitializeOnLoad]</c> 把钩子注册进来。
    ///
    /// 效果：装了 2D Animation 的项目自动多出 Sprite Library 产物；没装的项目核心功能
    /// 一行代码都不少，也不会出现"程序集引用缺失"的编译错误。这比把所有可选能力
    /// 都塞进主程序集、再让用户去解依赖冲突要干净得多。
    /// </summary>
    public static class PostAssembleHooks
    {
        public static event Action<AssembleContext> AfterAssembleRoot;

        public static void Raise(AssembleContext context)
        {
            if (context == null) return;
            var handlers = AfterAssembleRoot;
            if (handlers == null) return;

            foreach (var handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<AssembleContext>)handler)(context);
                }
                catch (Exception e)
                {
                    // 扩展点失败不能毁掉整个装配——核心产物已经生成，用户应该拿到它。
                    context.Messages.Add($"装配扩展点执行失败（已忽略）：{e.Message}");
                    Debug.LogWarning($"[CharForge] 装配扩展点失败：{e}");
                }
            }
        }
    }
}
