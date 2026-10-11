using System;
using System.IO;
using UnityEngine;

namespace CharForge.EditorTools.Util
{
    /// <summary>
    /// 配置文件的定位规则。**这是本插件最容易搞错的一处路径逻辑。**
    ///
    /// 坑在哪：Unity 里 <c>Application.dataPath</c> 指向的是 **Unity 工程**的 Assets
    /// （例如 <c>F:\unity_project\test_plugin\Assets</c>），而插件包是另外一棵树
    /// （<c>&lt;仓库&gt;/packages/com.charforge</c>，或 PackageCache 里的展开副本）。
    /// 两者没有共同的祖先目录。所以「在仓库里放一份 configs/」这个直觉做法，
    /// 在编辑器里**永远解析不到**——而且不会报错，只会表现为「配置静默不生效」，
    /// 排查起来极费时间（本项目已经在这类"静默失效"上栽过好几次）。
    ///
    /// 因此统一按两级查找，先命中者胜：
    /// <list type="number">
    ///   <item><c>&lt;Unity 工程&gt;/CharForge/&lt;相对路径&gt;</c> —— 用户覆盖，放自己的姿势模板、
    ///     改过的术语表。放在工程里，容易找到、容易进你自己的 git。</item>
    ///   <item><c>&lt;插件包&gt;/Configs/&lt;相对路径&gt;</c> —— 随包发布的默认值。</item>
    /// </list>
    /// 包内路径用 <c>PackageInfo.resolvedPath</c> 反查，因此 Add from disk / 嵌入 / 注册表
    /// 三种安装形态都能定位到——写死 <c>Packages/com.charforge</c> 会在后两种形态下失效。
    /// </summary>
    public static class CharForgePaths
    {
        /// <summary>Unity 工程根目录（Assets 的父目录）。</summary>
        public static string ProjectRoot =>
            Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;

        /// <summary>用户覆盖目录：<c>&lt;Unity 工程&gt;/CharForge</c>。</summary>
        public static string UserRoot => Path.Combine(ProjectRoot, "CharForge");

        /// <summary>随包默认配置目录：<c>&lt;插件包&gt;/Configs</c>；解析不到时返回 null。</summary>
        public static string PackageConfigRoot
        {
            get
            {
                try
                {
                    var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                        typeof(CharForgePaths).Assembly);
                    if (info == null) return null;

                    string root = !string.IsNullOrEmpty(info.resolvedPath)
                        ? info.resolvedPath
                        : Path.Combine(ProjectRoot, info.assetPath ?? "");

                    return string.IsNullOrEmpty(root) ? null : Path.Combine(root, "Configs");
                }
                catch (Exception)
                {
                    // 极少数环境下反查失败；调用方按"找不到配置"处理，会给出明确告警。
                    return null;
                }
            }
        }

        /// <summary>
        /// 按两级顺序查找一个配置文件，返回绝对路径；都不存在时返回 null。
        /// 返回 null 而不是抛异常：配置文件缺失是**可诊断的正常情况**，
        /// 应该由调用方转成一句能读懂的中文告警，而不是一个栈回溯。
        /// </summary>
        public static string ResolveFile(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return null;

            string user = Path.Combine(UserRoot, relativePath);
            if (File.Exists(user)) return user;

            string pkgRoot = PackageConfigRoot;
            if (!string.IsNullOrEmpty(pkgRoot))
            {
                string shipped = Path.Combine(pkgRoot, relativePath);
                if (File.Exists(shipped)) return shipped;
            }

            return null;
        }

        /// <summary>用于告警文案：把两级查找的实际位置说清楚，便于用户自己放文件。</summary>
        public static string DescribeLookup(string relativePath) =>
            $"{Path.Combine(UserRoot, relativePath)}（用户覆盖）或 " +
            $"{Path.Combine(PackageConfigRoot ?? "<插件包>/Configs", relativePath)}（随包默认）";
    }
}
