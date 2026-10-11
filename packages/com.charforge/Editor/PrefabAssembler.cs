using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CharForge.Runtime;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace CharForge.EditorTools
{
    /// <summary>
    /// 装配器：读服务端产出的 assembly_spec.json + manifest.json + persona.json，
    /// 生成一个可直接拖进场景的 NPC prefab。
    ///
    /// **这是整个项目最关键的一段代码。** 现有开源项目（SpriteForge / GenForgeSprite 等）
    /// 全都停在「出图 + 打包」，没有一个把 sprite 集装配成带 Animator、碰撞、寻路与
    /// 人设组件的 prefab。差别就在这里。
    ///
    /// 装配出的层级：
    ///   NPC_xxx (根)
    ///     ├── SpriteRenderer   精灵
    ///     ├── Animator         四向 idle / walk 状态机
    ///     ├── NpcCharacter     身份 + 人设 + 朝向
    ///     ├── Rigidbody2D      按需
    ///     ├── Collider2D       按需
    ///     └── SimplePatrol2D   巡逻/跟随（可选）
    /// </summary>
    public static class PrefabAssembler
    {
        private const string AssetRoot = "Assets/CharForge";

        public class AssembleReport
        {
            public bool ok;
            public string prefabAssetPath;
            public string assetDir;
            public readonly List<string> messages = new List<string>();
            public void Log(string m)
            {
                messages.Add(m);
                Debug.Log($"[CharForge] {m}");
            }
        }

        /// <summary>从产物目录装配 prefab。</summary>
        public static AssembleReport Assemble(string jobDir, string prefabOutputDir = null)
        {
            var report = new AssembleReport();
            jobDir = Path.GetFullPath(jobDir);

            string specPath = Path.Combine(jobDir, "assembly_spec.json");
            string manifestPath = Path.Combine(jobDir, "manifest.json");
            if (!File.Exists(specPath) || !File.Exists(manifestPath))
            {
                report.ok = false;
                report.Log($"产物不完整，缺少 assembly_spec.json 或 manifest.json：{jobDir}");
                return report;
            }

            var spec = JObject.Parse(File.ReadAllText(specPath));
            var manifest = JObject.Parse(File.ReadAllText(manifestPath));
            string jobId = (string)spec["job_id"] ?? Path.GetFileName(jobDir);
            string prefabName = (string)spec["prefab_name"] ?? "NPC_Character";

            // ── 1. 把产物拷进 Assets（Unity 只能导入 Assets 下的资源）──────
            string assetDir = $"{AssetRoot}/{jobId}";
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "CharForge", jobId));
            foreach (var f in new[] { "sheet.png", "manifest.json", "assembly_spec.json", "persona.json" })
            {
                string src = Path.Combine(jobDir, f);
                if (File.Exists(src)) File.Copy(src, Path.Combine(assetDir, f), true);
            }
            AssetDatabase.Refresh();
            report.assetDir = assetDir;
            report.Log($"产物已同步到 {assetDir}");

            // ── 2. 配置贴图导入 + 拿到所有切片 ──────────────────────────
            string sheetAssetPath = $"{assetDir}/sheet.png";
            var sprites = SpriteImportSetup.Apply(sheetAssetPath, $"{assetDir}/manifest.json");
            if (sprites.Count == 0)
            {
                report.ok = false;
                report.Log("没有解析到任何切片，装配中止");
                return report;
            }

            // ── 3. 建动画片段与状态机 ──────────────────────────────────
            string animDir = $"{assetDir}/Animations";
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "CharForge", jobId, "Animations"));
            AssetDatabase.Refresh();

            string controllerPath = $"{animDir}/{prefabName}.controller";
            AssetDatabase.DeleteAsset(controllerPath);
            var controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
            controller.AddParameter("facing", AnimatorControllerParameterType.Int);
            controller.AddParameter("isMoving", AnimatorControllerParameterType.Bool);
            controller.AddParameter("moveX", AnimatorControllerParameterType.Float);
            controller.AddParameter("moveY", AnimatorControllerParameterType.Float);

            var stateMachine = controller.layers[0].stateMachine;
            var createdClips = new List<AnimationClip>();
            string defaultState = null;

            foreach (var clipSpec in spec["animations"] as JArray ?? new JArray())
            {
                string clipName = (string)clipSpec["name"];
                int frameRate = clipSpec["frame_rate"] != null ? (int)clipSpec["frame_rate"] : 8;
                bool loop = clipSpec["loop"] == null || (bool)clipSpec["loop"];
                var frameNames = (clipSpec["sprites"] as JArray)?.Select(t => (string)t).ToList() ?? new List<string>();

                var frameSprites = frameNames
                    .Where(n => sprites.ContainsKey(n))
                    .Select(n => sprites[n])
                    .ToArray();

                if (frameSprites.Length == 0)
                {
                    report.Log($"跳过空片段 {clipName}（切片缺失）");
                    continue;
                }

                var clip = BuildSpriteClip(clipName, frameSprites, frameRate, loop, $"{animDir}/{clipName}.anim");
                createdClips.Add(clip);

                var state = stateMachine.AddState(clipName);
                state.motion = clip;

                if (defaultState == null || clipName.EndsWith("_idle") && clipName.StartsWith("south"))
                    defaultState = clipName;
            }

            if (createdClips.Count == 0)
            {
                report.ok = false;
                report.Log("没有任何有效动画片段，装配中止");
                return report;
            }
            if (defaultState != null && stateMachine.states.Any(s => s.state.name == defaultState))
                stateMachine.defaultState = stateMachine.states.First(s => s.state.name == defaultState).state;

            report.Log($"状态机就绪：{createdClips.Count} 个片段，默认 {defaultState}");

            // ── 4. 建 GameObject 层级 ────────────────────────────────
            var go = new GameObject(prefabName);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprites.Values.FirstOrDefault(s => s.name == (defaultState != null ? $"{defaultState}_00" : ""))
                       ?? sprites.Values.First();
            sr.sortingOrder = 0;

            var animator = go.AddComponent<Animator>();
            animator.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(controllerPath);

            // NpcCharacter
            var card = spec["character_card"] as JObject;
            var npc = go.AddComponent<NpcCharacter>();
            var so = new SerializedObject(npc);
            so.FindProperty("characterId").stringValue = (string)card?["name"] ?? prefabName;
            so.FindProperty("displayName").stringValue = (string)card?["name"] ?? prefabName;
            so.FindProperty("role").stringValue = (string)card?["role"] ?? "";
            var personaAsset = AssetDatabase.LoadAssetAtPath<TextAsset>($"{assetDir}/persona.json");
            so.FindProperty("personaAsset").objectReferenceValue = personaAsset;
            var dialogue = spec["dialogue"] as JObject;
            bool dialogueEnabled = dialogue != null && dialogue["enabled"] != null && (bool)dialogue["enabled"];
            so.FindProperty("dialogueEnabled").boolValue = dialogueEnabled;
            so.FindProperty("maxTokens").intValue = dialogue?["max_tokens"] != null ? (int)dialogue["max_tokens"] : 160;
            so.ApplyModifiedProperties();

            // 碰撞体
            var colliderSpec = spec["collider"] as JObject;
            string shape = (string)colliderSpec?["shape"] ?? "capsule";
            switch (shape)
            {
                case "circle":
                    var circle = go.AddComponent<CircleCollider2D>();
                    circle.radius = colliderSpec?["radius"] != null ? (float)colliderSpec["radius"] : 0.3f;
                    break;
                case "box":
                    var box = go.AddComponent<BoxCollider2D>();
                    if (colliderSpec?["size"] is JArray boxSize)
                        box.size = new Vector2((float)boxSize[0], (float)boxSize[1]);
                    break;
                default:
                    var cap = go.AddComponent<CapsuleCollider2D>();
                    if (colliderSpec?["size"] is JArray capSize)
                        cap.size = new Vector2((float)capSize[0], (float)capSize[1]);
                    break;
            }

            // 移动
            var movementSpec = spec["movement"] as JObject;
            string mode = (string)movementSpec?["mode"] ?? "none";
            if (mode != "none")
            {
                var rb = go.AddComponent<Rigidbody2D>();
                rb.gravityScale = 0f;
                rb.freezeRotation = true;
                rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
                rb.interpolation = RigidbodyInterpolation2D.Interpolate;

                var patrol = go.AddComponent<SimplePatrol2D>();
                var pso = new SerializedObject(patrol);
                pso.FindProperty("speed").floatValue = movementSpec?["speed"] != null ? (float)movementSpec["speed"] : 1.5f;
                var points = movementSpec?["patrol_points"] as JArray;
                if (points != null && points.Count > 0)
                {
                    var arr = pso.FindProperty("patrolPoints");
                    arr.arraySize = points.Count;
                    for (int i = 0; i < points.Count; i++)
                    {
                        var p = points[i] as JArray;
                        arr.GetArrayElementAtIndex(i).vector2Value = new Vector2((float)p[0], (float)p[1]);
                    }
                }
                pso.ApplyModifiedProperties();
            }

            // ── 5. 存 prefab ───────────────────────────────────────
            string outDir = string.IsNullOrEmpty(prefabOutputDir) ? assetDir : prefabOutputDir;
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "..", outDir));
            string prefabPath = $"{outDir}/{prefabName}.prefab";
            PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
            UnityEngine.Object.DestroyImmediate(go);
            AssetDatabase.SaveAssets();

            report.ok = true;
            report.prefabAssetPath = prefabPath;
            report.Log($"prefab 已生成：{prefabPath}");
            return report;
        }

        private static AnimationClip BuildSpriteClip(
            string name, Sprite[] frames, int frameRate, bool loop, string assetPath)
        {
            var clip = new AnimationClip { name = name, frameRate = Mathf.Max(1, frameRate) };

            var binding = new EditorCurveBinding
            {
                type = typeof(SpriteRenderer),
                path = string.Empty,          // SpriteRenderer 在根物体上
                propertyName = "m_Sprite"
            };

            var keys = new ObjectReferenceKeyframe[frames.Length];
            for (int i = 0; i < frames.Length; i++)
            {
                keys[i] = new ObjectReferenceKeyframe
                {
                    time = i / (float)Mathf.Max(1, frameRate),
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
