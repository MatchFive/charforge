using System;
using System.IO;
using System.Runtime.CompilerServices;
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
                    if (info != null)
                    {
                        string root = !string.IsNullOrEmpty(info.resolvedPath)
                            ? info.resolvedPath
                            : Path.Combine(ProjectRoot, info.assetPath ?? "");

                        if (!string.IsNullOrEmpty(root) && Directory.Exists(Path.Combine(root, "Configs")))
                            return Path.Combine(root, "Configs");
                    }
                }
                catch (Exception)
                {
                    // 反查失败不抛：继续走下面的编译期兜底。
                }

                // 兜底：CallerFilePath 是**编译期**嵌进程序集的本文件源码绝对路径。
                // 本包以 file:/嵌入形态编译时就发生在真实位置，往上三级
                // （Util → Editor → 包根）即包根目录。不依赖 PackageInfo，
                // 覆盖反查失效的环境（实测出现过：术语表静默找不到 + 定位文案崩溃）。
                string src = SourceFilePath();
                if (!string.IsNullOrEmpty(src))
                {
                    string editorDir = Path.GetDirectoryName(src);           // <包>/Editor/Util
                    string pkgRoot = Path.GetDirectoryName(editorDir);       // <包>/Editor
                    pkgRoot = Path.GetDirectoryName(pkgRoot);                // <包>
                    if (!string.IsNullOrEmpty(pkgRoot) &&
                        Directory.Exists(Path.Combine(pkgRoot, "Configs")))
                        return Path.Combine(pkgRoot, "Configs");
                }

                return null;
            }
        }

        /// <summary>编译期源码路径兜底。见 <see cref="PackageConfigRoot"/>。</summary>
        private static string SourceFilePath([CallerFilePath] string path = "") => path;

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

        /// <summary>
        /// 用于告警文案：把两级查找的实际位置说清楚，便于用户自己放文件。
        ///
        /// **这里曾经崩过**：包定位失败时占位文本 <c>&lt;插件包&gt;/Configs</c> 里的
        /// <c>&lt;</c> <c>&gt;</c> 是 Windows 非法路径字符，直接喂 <see cref="Path.Combine"/>
        /// 会抛 <c>ArgumentException: Illegal characters in path</c>，
        /// 把一条温和的告警升级成流水线中断（v0.3.3 实测）。
        /// 所以永远走 <see cref="SafeJoin"/>，不做任何路径合法性假设。
        /// </summary>
        public static string DescribeLookup(string relativePath)
        {
            string user = SafeJoin(UserRoot, relativePath);
            string pkgRoot = PackageConfigRoot;
            string pkg = pkgRoot != null
                ? SafeJoin(pkgRoot, relativePath)
                : "<插件包>/Configs/" + (relativePath ?? "");
            return $"{user}（用户覆盖）或 {pkg}（随包默认）";
        }

        /// <summary>拼路径但绝不抛：退化成纯字符串拼接。只用于告警文案，不做文件 IO。</summary>
        private static string SafeJoin(string root, string relative)
        {
            try
            {
                return string.IsNullOrEmpty(root) ? relative : Path.Combine(root, relative ?? "");
            }
            catch (Exception)
            {
                return (root ?? "").TrimEnd('/', '\\') + "/" + (relative ?? "");
            }
        }
    }
}
