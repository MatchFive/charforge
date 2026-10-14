using System.Collections.Generic;
using System.IO;
using System.Linq;
using CharForge.EditorTools.Data;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace CharForge.EditorTools
{
    /// <summary>
    /// 从装配指令生成 AnimatorController 与 AnimationClip。**插件形态独有的第二处能力。**
    ///
    /// 设计取舍：状态机用 <c>AnyState</c> 驱动，而不是给每对状态连一条transition。
    /// 理由——四向 × 两动作 = 8 个状态，两两互联是 56 条 transition，改一次朝向逻辑就要重连一遍；
    /// 而 AnyState + 两个参数（facing / isMoving）只需 8 条，且新增动作时零改动。
    ///
    /// 参数契约（运行期行为脚本按这个驱动，不要改名）：
    ///   facing    int    0=南 1=北 2=东 3=西   （与 CharForge.Runtime.FacingDirection 一致）
    ///   isMoving  bool   是否在移动
    /// </summary>
    public static class AnimatorWriter
    {
        public class BuildResult
        {
            public RuntimeAnimatorController Controller;
            public string ControllerPath = "";
            public string DefaultStateName = "";
            public readonly List<string> Messages = new List<string>();
            public bool Ok => Controller != null;
        }

        private static readonly Dictionary<string, int> FacingIndex = new Dictionary<string, int>
        {
            { "south", 0 }, { "north", 1 }, { "east", 2 }, { "west", 3 }
        };

        public static BuildResult Build(
            string assetDir,
            string prefabName,
            AssemblySpec spec,
            Dictionary<string, Sprite> sprites)
        {
            var result = new BuildResult();
            if (spec == null || spec.Animations == null || spec.Animations.Count == 0)
            {
                result.Messages.Add("装配指令里没有动画片段，跳过 Animator 生成。");
                return result;
            }

            Directory.CreateDirectory(Path.Combine(Application.dataPath, "..", assetDir, "Animations"));
            AssetDatabase.Refresh();

            string controllerPath = $"{assetDir}/Animations/{prefabName}.controller";

            // ── 复用而非删档重建 ──
            // DeleteAsset + CreateAnimatorControllerAtPath 会让"正在显示该控制器的 Animator 窗口"
            // 持有失效的图节点，从而在 UnityEditor.Graphs.Graph.OnEnable 抛 NullReferenceException；
            // 而且每次运行都换 GUID，会打断已保存 prefab / 场景对控制器与 clip 的引用。
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
            if (!IsHealthy(controller))
            {
                AssetDatabase.DeleteAsset(controllerPath);
                controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
                if (controller == null)
                {
                    result.Messages.Add($"创建 AnimatorController 失败：{controllerPath}");
                    return result;
                }
            }

            // 参数：先清空再加，避免重复 AddParameter 抛异常，也保证与当前契约严格一致。
            for (int i = controller.parameters.Length - 1; i >= 0; i--)
                controller.RemoveParameter(i);
            controller.AddParameter("facing", AnimatorControllerParameterType.Int);
            controller.AddParameter("isMoving", AnimatorControllerParameterType.Bool);
            // 保留 moveX/moveY 供后续接入 8 向或混合树；当前状态机不用，但先声明好避免改 prefab。
            controller.AddParameter("moveX", AnimatorControllerParameterType.Float);
            controller.AddParameter("moveY", AnimatorControllerParameterType.Float);

            var sm = controller.layers[0].stateMachine;
            // 清掉上一轮遗留的状态；RemoveState 会连带移除引用它的 transition（含 AnyState 出边）。
            foreach (var child in sm.states)
                sm.RemoveState(child.state);

            var states = new List<(AnimationSpec spec, AnimatorState state)>();

            foreach (var anim in spec.Animations)
            {
                var frames = anim.Sprites
                    .Where(n => sprites.ContainsKey(n))
                    .Select(n => sprites[n])
                    .ToArray();

                if (frames.Length == 0)
                {
                    result.Messages.Add($"跳过空片段 {anim.Name}（切片缺失）");
                    continue;
                }
                if (frames.Length < anim.Sprites.Count)
                {
                    result.Messages.Add($"片段 {anim.Name} 有 {anim.Sprites.Count - frames.Length} 帧缺失，已按剩余帧生成");
                }

                string clipPath = $"{assetDir}/Animations/{anim.Name}.anim";
                var clip = EnsureSpriteClip(anim.Name, frames, anim.FrameRate, anim.Loop, clipPath);

                var state = sm.AddState(anim.Name);
                state.motion = clip;
                state.speed = 1f;
                states.Add((anim, state));
            }

            if (states.Count == 0)
            {
                result.Messages.Add("没有任何有效动画片段，Animator 生成中止。");
                return result;
            }

            // ── 默认状态：南向待机优先，退化到第一个可用片段 ──
            var preferred = states.FirstOrDefault(s => s.spec.Name == "south_idle");
            var chosen = preferred.state != null ? preferred : states[0];
            sm.defaultState = chosen.state;
            result.DefaultStateName = chosen.spec.Name;

            // ── AnyState 驱动 ──
            foreach (var (anim, state) in states)
            {
                if (!FacingIndex.TryGetValue(anim.Direction ?? "south", out int facing)) continue;
                if (state == sm.defaultState) continue;   // 默认状态不需要自跳转

                var t = sm.AddAnyStateTransition(state);
                t.hasExitTime = false;
                t.duration = 0f;
                t.canTransitionToSelf = false;
                t.hasFixedDuration = true;
                t.AddCondition(AnimatorConditionMode.Equals, facing, "facing");
                t.AddCondition(
                    anim.Action == "walk" ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot,
                    0f, "isMoving");
            }

            EditorUtility.SetDirty(sm);
            AssetDatabase.SaveAssets();
            result.Controller = controller;
            result.ControllerPath = controllerPath;
            result.Messages.Add($"状态机就绪：{states.Count} 个片段，默认 {result.DefaultStateName}");
            return result;
        }

        /// <summary>
        /// 控制器是否可用。半损坏的 AnimatorController（layers 空 / stateMachine 丢 / 无状态）
        /// 会触发 UnityEditor.Graphs.Graph.OnEnable 的 NullReferenceException，
        /// 这里统一判定为"不可用"，交给上层重建。
        /// </summary>
        private static bool IsHealthy(AnimatorController c)
        {
            if (c == null) return false;
            try
            {
                if (c.layers == null || c.layers.Length == 0) return false;
                if (c.layers[0].stateMachine == null) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 精灵曲线的**唯一绑定源**（SpriteRenderer 挂在 prefab 根物体上，所以路径是空字符串）。
        /// 写片段与校验必须用同一条绑定，否则校验的根本不是同一条曲线。各写一份迟早漂移。
        /// </summary>
        internal static readonly EditorCurveBinding SpriteCurveBinding = new EditorCurveBinding
        {
            type = typeof(SpriteRenderer),
            path = string.Empty,
            propertyName = "m_Sprite"
        };

        /// <summary>
        /// **读磁盘**数精灵关键帧——校验唯一可信的来源。
        /// 返回 -1 表示该路径没有片段资产。别用内存对象数：见 <see cref="EnsureSpriteClip"/> 里的落盘说明，
        /// 内存里有关键帧、磁盘上没有，是完全可能出现的状态。
        /// </summary>
        internal static int CountSpriteKeysOnDisk(string assetPath)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
            if (clip == null) return -1;
            return AnimationUtility.GetObjectReferenceCurve(clip, SpriteCurveBinding)?.Length ?? 0;
        }

        /// <summary>
        /// 生成 / 就地刷新精灵帧动画片段。已有同名资产时**只更新关键帧，不删档重建**——
        /// 保留 GUID，控制器里的 motion 引用才不会被打断（否则 Animator 窗口会抛 NRE）。
        /// </summary>
        internal static AnimationClip EnsureSpriteClip(
            string name, Sprite[] frames, int frameRate, bool loop, string assetPath)
        {
            int rate = Mathf.Max(1, frameRate);
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
            if (clip == null)
            {
                clip = new AnimationClip { name = name, frameRate = rate };
                // CreateAsset 只会把"此刻这个对象"写进文件——此刻它还是空的。
                AssetDatabase.CreateAsset(clip, assetPath);
            }
            clip.frameRate = rate;

            var keys = new ObjectReferenceKeyframe[frames.Length];
            for (int i = 0; i < frames.Length; i++)
            {
                keys[i] = new ObjectReferenceKeyframe
                {
                    time = i / (float)rate,
                    value = frames[i]
                };
            }
            // SetObjectReferenceCurve 会整条替换，无需先清空。
            AnimationUtility.SetObjectReferenceCurve(clip, SpriteCurveBinding, keys);

            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = loop;
            AnimationUtility.SetAnimationClipSettings(clip, settings);

            EditorUtility.SetDirty(clip);

            // ★ 就地立刻落盘，**不要**把这一步留给调用方最后统一 SaveAssets。
            //
            // 上面 CreateAsset 写出去的是空片段，曲线与循环设置此刻只在内存里。
            // 只要在这之后再有任意一次 AssetDatabase 操作（下一次循环的
            // LoadAssetAtPath / CreateAsset 就足够），Unity 会结清该资产的待导入，
            // 把刚落盘的 .anim 重新读进来**顶掉内存实例**——脏标记连同未落盘的改动一起没了，
            // 磁盘上留下一个 0 关键帧的空片段，而且全过程一声不响。
            //
            // v0.5.6 及之前正是把落盘留到装配末尾统一做，症状是「一批片段里只有最后一个有帧」
            // （最后一个之后没有新的资产操作，正好赶上那次统一 save）。看着随机，实为顺序。
            AssetDatabase.SaveAssets();
            return clip;
        }
    }
}
