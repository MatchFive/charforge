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
            AssetDatabase.DeleteAsset(controllerPath);

            var controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
            controller.AddParameter("facing", AnimatorControllerParameterType.Int);
            controller.AddParameter("isMoving", AnimatorControllerParameterType.Bool);
            // 保留 moveX/moveY 供后续接入 8 向或混合树；当前状态机不用，但先声明好避免改 prefab。
            controller.AddParameter("moveX", AnimatorControllerParameterType.Float);
            controller.AddParameter("moveY", AnimatorControllerParameterType.Float);

            var sm = controller.layers[0].stateMachine;
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
                AssetDatabase.DeleteAsset(clipPath);
                var clip = BuildSpriteClip(anim.Name, frames, anim.FrameRate, anim.Loop, clipPath);

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

            AssetDatabase.SaveAssets();
            result.Controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(controllerPath);
            result.ControllerPath = controllerPath;
            result.Messages.Add($"状态机就绪：{states.Count} 个片段，默认 {result.DefaultStateName}");
            return result;
        }

        private static AnimationClip BuildSpriteClip(
            string name, Sprite[] frames, int frameRate, bool loop, string assetPath)
        {
            int rate = Mathf.Max(1, frameRate);
            var clip = new AnimationClip { name = name, frameRate = rate };

            // SpriteRenderer 挂在 prefab 根物体上，所以曲线路径是空字符串。
            var binding = new EditorCurveBinding
            {
                type = typeof(SpriteRenderer),
                path = string.Empty,
                propertyName = "m_Sprite"
            };

            var keys = new ObjectReferenceKeyframe[frames.Length];
            for (int i = 0; i < frames.Length; i++)
            {
                keys[i] = new ObjectReferenceKeyframe
                {
                    time = i / (float)rate,
                    value = frames[i]
                };
            }
            AnimationUtility.SetObjectReferenceCurve(clip, binding, keys);

            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = loop;
            AnimationUtility.SetAnimationClipSettings(clip, settings);

            AssetDatabase.CreateAsset(clip, assetPath);
            return clip;
        }
    }
}
