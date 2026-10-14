using System;
using UnityEngine;

namespace CharForge.Runtime
{
    /// <summary>
    /// 键盘试走驱动（WASD / 方向键）。装配时选「键盘试走」移动方式会自动挂上，
    /// 用于在 Play 模式里验证各朝向帧序列之间的切换。
    ///
    /// **输入后端双路兼容**：工程 Active Input Handling 设为「Input System 包」时，
    /// 经典 <c>UnityEngine.Input</c> 被禁用，直接调用会**每帧抛**
    /// <c>InvalidOperationException</c>（实机踩过：Play 一秒刷屏上百条）。
    /// 这里不硬引用 Input System 程序集（没装该包的用户会编译不过），改为：
    ///  · 经典后端（含 Both）→ 自身直接读 <c>Input.GetKey</c>；
    ///  · 新输入系统后端 → 由可选程序集 <c>CharForge.Runtime.InputSystem</c>
    ///    （`versionDefines` + `defineConstraints` 门控，装不装都不报错）
    ///    在场景加载前把 <see cref="InputProvider"/> 注册进来。
    ///
    /// 朝向编码经 <see cref="NpcCharacter.SetFacing"/>（FacingDirection 枚举：South=0/North=1/
    /// East=2/West=3），与装配器状态机里的 facing 条件是**同一套规范索引**——
    /// 任何一边改了枚举值，状态机条件就会静默对不上（v0.4.8 及之前按勾选顺序编号时踩过）。
    /// </summary>
    [AddComponentMenu("CharForge/NPC Keyboard Driver")]
    public class NpcKeyboardDriver : MonoBehaviour
    {
        /// <summary>
        /// 输入读取钩子：启用新输入系统时由可选程序集注册；为 null 时回落到经典 Input。
        /// 供扩展替换（例如接 UI 摇杆）时也可自行赋值。
        /// </summary>
        public static Func<Vector2> InputProvider;

        [Tooltip("移动速度（单位/秒）。")]
        [SerializeField] private float speed = 1.5f;

        [Tooltip("停下时把 Animator.speed 置 0，定格在当前帧当待机。关掉则原地继续走循环。")]
        [SerializeField] private bool pauseWhenIdle = true;

        private Animator _animator;
        private NpcCharacter _npc;
        private Rigidbody2D _rb;

        private void Awake()
        {
            _animator = GetComponent<Animator>();
            _npc = GetComponent<NpcCharacter>();
            _rb = GetComponent<Rigidbody2D>();
        }

        private void Update()
        {
            Vector2 move = ReadInput();
            bool moving = move.sqrMagnitude > 0.0001f;

            if (_animator != null)
            {
                _animator.SetBool("isMoving", moving);
                _animator.speed = !moving && pauseWhenIdle ? 0f : 1f;
            }

            if (!moving) return;

            // 只在动的时候切朝向：站着不动保持最后一帧的朝向，符合直觉。
            if (_npc != null) _npc.SetFacing(NpcCharacter.DirectionFrom(move));

            float step = speed * Time.deltaTime;
            if (_rb != null) _rb.MovePosition(_rb.position + move.normalized * step);
            else transform.position += (Vector3)(move.normalized * step);
        }

        private static Vector2 ReadInput()
        {
            if (InputProvider != null) return InputProvider();
#if ENABLE_LEGACY_INPUT_MANAGER
            float x = 0f, y = 0f;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) x -= 1f;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) x += 1f;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) y -= 1f;
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) y += 1f;
            return new Vector2(x, y);
#else
            // 工程只启用了新输入系统、但 Input System 包不在（可选程序集未编译）：
            // 静默返回 0 而不是每帧抛异常——坏掉的东西要说得出话，但刷屏不算说话。
            return Vector2.zero;
#endif
        }
    }
}
