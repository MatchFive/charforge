using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace CharForge.Runtime.InputSystemSupport
{
    /// <summary>
    /// 新输入系统（Input System 包）支持：把 <c>Keyboard</c> 读取注册到
    /// <see cref="NpcKeyboardDriver.InputProvider"/>。
    ///
    /// **为什么单独一个程序集**：核心程序集 CharForge.Runtime 不能硬引用 Unity.InputSystem
    /// ——没装该包的用户一编译就炸。本程序集用 <c>versionDefines</c> + <c>defineConstraints</c>
    /// 门控：工程装了 com.unity.inputsystem 才编译，装不装都不报错（与 Sprite Library
    /// 集成同一范式）。工程启用的是经典后端时 ENABLE_INPUT_SYSTEM 未定义，
    /// <see cref="Register"/> 不注册，核心自动回落到 <c>Input.GetKey</c>。
    ///
    /// 不用整个文件包 <c>#if</c>：那样在经典后端下本程序集会变成空程序集，
    /// 空程序集在构建期容易出幺蛾子，留一个空壳类更稳。
    /// </summary>
    internal static class NpcInputSystemSupport
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Register()
        {
#if ENABLE_INPUT_SYSTEM
            NpcKeyboardDriver.InputProvider = Read;
#endif
        }

#if ENABLE_INPUT_SYSTEM
        private static Vector2 Read()
        {
            // 无键盘设备时 Keyboard.current 为 null（编辑器初始化早期、或纯手柄环境）。
            var kb = Keyboard.current;
            if (kb == null) return Vector2.zero;

            float x = 0f, y = 0f;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) x -= 1f;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) x += 1f;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) y -= 1f;
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) y += 1f;
            return new Vector2(x, y);
        }
#endif
    }
}
