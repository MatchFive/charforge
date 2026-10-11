using UnityEngine;

namespace CharForge.Runtime
{
    /// <summary>
    /// 最简 2D 巡逻/跟随行为。刻意做得很简单——它是「能让 prefab 动起来」的最小实现，
    /// 不是行为树。要复杂行为请接 Unity Behavior 包，把本组件当参考。
    ///
    /// 两种模式：
    ///   Patrol：在巡逻点之间来回走
    ///   Follow：跟随目标 Transform，进入 stopDistance 后停下
    /// </summary>
    [AddComponentMenu("CharForge/Simple Patrol 2D")]
    [RequireComponent(typeof(Rigidbody2D))]
    public class SimplePatrol2D : MonoBehaviour
    {
        public enum Mode { Patrol, Follow }

        [Header("模式")]
        [SerializeField] private Mode mode = Mode.Patrol;

        [Header("巡逻")]
        [Tooltip("巡逻点（世界坐标）。为空则原地待机。")]
        [SerializeField] private Vector2[] patrolPoints = new Vector2[0];
        [SerializeField] private float arriveThreshold = 0.1f;

        [Header("跟随")]
        [SerializeField] private Transform followTarget;
        [SerializeField] private float stopDistance = 1.2f;

        [Header("移动")]
        [SerializeField] private float speed = 1.5f;

        private Rigidbody2D _rb;
        private Animator _animator;
        private NpcCharacter _npc;
        private int _targetIndex;
        private Vector2 _prefabOrigin;

        private void Awake()
        {
            _rb = GetComponent<Rigidbody2D>();
            _animator = GetComponent<Animator>();
            _npc = GetComponent<NpcCharacter>();

            _rb.gravityScale = 0f;
            _rb.freezeRotation = true;
            _rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            _rb.interpolation = RigidbodyInterpolation2D.Interpolate;

            _prefabOrigin = transform.position;
        }

        private void FixedUpdate()
        {
            Vector2 current = _rb.position;
            Vector2 target = mode == Mode.Patrol ? NextPatrolPoint(current) : FollowPoint();

            Vector2 delta = target - current;
            bool shouldMove = delta.magnitude > arriveThreshold;
            Vector2 direction = shouldMove ? delta.normalized : Vector2.zero;

            if (shouldMove)
            {
                _rb.MovePosition(current + direction * speed * Time.fixedDeltaTime);
            }

            ApplyAnimation(direction, shouldMove);
        }

        private Vector2 NextPatrolPoint(Vector2 current)
        {
            if (patrolPoints == null || patrolPoints.Length == 0) return current;

            // 巡逻点按 prefab 原点存储，运行时转成世界坐标，便于同一个 prefab 多处复用
            Vector2 target = _prefabOrigin + patrolPoints[_targetIndex];
            if (Vector2.Distance(current, target) <= arriveThreshold)
            {
                _targetIndex = (_targetIndex + 1) % patrolPoints.Length;
                target = _prefabOrigin + patrolPoints[_targetIndex];
            }
            return target;
        }

        private Vector2 FollowPoint()
        {
            if (followTarget == null) return _rb.position;
            Vector2 self = _rb.position;
            Vector2 tgt = followTarget.position;
            if (Vector2.Distance(self, tgt) <= stopDistance) return self;
            return tgt;
        }

        private void ApplyAnimation(Vector2 direction, bool moving)
        {
            if (_npc != null && moving)
            {
                _npc.SetFacing(NpcCharacter.DirectionFrom(direction));
            }

            if (_animator == null) return;
            _animator.SetBool("isMoving", moving);
            _animator.SetFloat("moveX", direction.x);
            _animator.SetFloat("moveY", direction.y);
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (patrolPoints == null || patrolPoints.Length == 0) return;
            Gizmos.color = new Color(0.2f, 0.72f, 0.55f, 0.9f);
            Vector3 origin = Application.isPlaying ? (Vector3)_prefabOrigin : transform.position;
            for (int i = 0; i < patrolPoints.Length; i++)
            {
                Vector3 a = origin + (Vector3)patrolPoints[i];
                Vector3 b = origin + (Vector3)patrolPoints[(i + 1) % patrolPoints.Length];
                Gizmos.DrawSphere(a, 0.08f);
                Gizmos.DrawLine(a, b);
            }
        }
#endif
    }
}
