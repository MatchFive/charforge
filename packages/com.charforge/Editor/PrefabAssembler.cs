using System.Collections.Generic;
using System.IO;
using System.Linq;
using CharForge.EditorTools.Data;
using CharForge.Runtime;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace CharForge.EditorTools
{
    /// <summary>
    /// 装配器：把「装配指令 JSON + 切片清单 + 人设」变成**可以直接拖进场景的 NPC prefab**。
    ///
    /// **这是整个项目最关键的一段代码，也是插件形态存在的理由。**
    /// 现有开源项目（SpriteForge / GenForgeSprite 等）全都停在「出图 + 打包」，
    /// 没有一个把 sprite 集装配成带 Animator、贴合碰撞体、寻路与人设组件的 prefab。
    /// 独立工具做不到这件事不是能力问题，而是**结构问题**：
    /// 它拿不到 Unity 的资产管线，产出只能是"一堆 PNG + 一个说明文档"。
    ///
    /// 装配出的层级：
    ///   NPC_xxx (根)
    ///     ├── SpriteRenderer   精灵（根物体上，动画曲线路径为空字符串）
    ///     ├── Animator         四向 idle / walk，AnyState + facing/isMoving 驱动
    ///     ├── NpcCharacter     身份 + 人设 + 朝向
    ///     ├── AigcDisclosure   AIGC 显式标识数据（合规）
    ///     ├── Rigidbody2D      按需
    ///     ├── Collider2D       按需（尺寸贴合实测角色轮廓）
    ///     └── SimplePatrol2D   巡逻（可选）
    /// </summary>
    public static class PrefabAssembler
    {
        public class AssembleReport
        {
            public bool ok;
            public string prefabAssetPath = "";
            public string assetDir = "";
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
            if (string.IsNullOrWhiteSpace(jobDir))
            {
                report.Log("产物目录为空。");
                return report;
            }

            jobDir = Path.GetFullPath(jobDir);
            string specPath = Path.Combine(jobDir, "assembly_spec.json");
            string manifestPath = Path.Combine(jobDir, "manifest.json");
            if (!File.Exists(specPath) || !File.Exists(manifestPath))
            {
                report.Log($"产物不完整，缺少 assembly_spec.json 或 manifest.json：{jobDir}");
                return report;
            }

            AssemblySpec spec;
            SpriteManifest manifest;
            try
            {
                spec = JsonConvert.DeserializeObject<AssemblySpec>(File.ReadAllText(specPath));
                manifest = JsonConvert.DeserializeObject<SpriteManifest>(File.ReadAllText(manifestPath));
            }
            catch (System.Exception e)
            {
                report.Log($"产物 JSON 解析失败：{e.Message}（契约版本需为 {CharForgeContractVersion.Current}）");
                return report;
            }

            if (spec == null || manifest == null || manifest.Sprites.Count == 0)
            {
                report.Log("产物内容为空或没有切片，装配中止。");
                return report;
            }

            string jobId = string.IsNullOrEmpty(spec.JobId) ? Path.GetFileName(jobDir) : spec.JobId;
            string prefabName = string.IsNullOrEmpty(spec.PrefabName) ? "NPC_Character" : spec.PrefabName;

            // ── 1. 产物同步进工程（Unity 只认 Assets/ 下的文件）──────
            string assetDir = ProjectAssetWriter.EnsureAssetFolder($"{CharForgeSettings.OutputAssetFolder}/{jobId}");
            foreach (var m in ProjectAssetWriter.SyncJobArtifacts(jobDir, assetDir)) report.Log(m);

            // ── 2. 写贴图导入设置，拿回所有切片 ──────────────────────
            string sheetAssetPath = $"{assetDir}/sheet.png";
            var sprites = SpriteImportSetup.Apply(sheetAssetPath, manifest);
            if (sprites.Count == 0)
            {
                report.Log("没有解析到任何切片，装配中止。请检查 manifest.json 的坐标与 sheet.png 是否匹配。");
                return report;
            }

            // ── 3. 生成动画片段与状态机 ──────────────────────────────
            var animatorResult = AnimatorWriter.Build(assetDir, prefabName, spec, sprites);
            foreach (var m in animatorResult.Messages) report.Log(m);
            if (!animatorResult.Ok)
            {
                report.Log("Animator 生成失败，装配中止。");
                return report;
            }

            // ── 4. 搭 GameObject 层级 ────────────────────────────────
            var go = BuildHierarchy(prefabName, assetDir, spec, manifest, sprites, animatorResult, report);

            // ── 5. 扩展点（Sprite Library 等可选能力在此挂接）────────
            var context = new AssembleContext
            {
                JobId = jobId,
                AssetDir = assetDir,
                PrefabName = prefabName,
                Spec = spec,
                Manifest = manifest,
                Sprites = sprites,
                Root = go
            };
            PostAssembleHooks.Raise(context);
            foreach (var m in context.Messages) report.Log(m);

            // ── 6. 存 prefab ────────────────────────────────────────
            string outDir = string.IsNullOrEmpty(prefabOutputDir) ? assetDir : prefabOutputDir;
            ProjectAssetWriter.EnsureAssetFolder(outDir);
            string prefabPath = $"{outDir}/{prefabName}.prefab";
            AssetDatabase.DeleteAsset(prefabPath);
            PrefabUtility.SaveAsPrefabAsset(go, prefabPath, out bool savedOk);
            Object.DestroyImmediate(go);
            AssetDatabase.SaveAssets();

            if (!savedOk)
            {
                report.Log($"prefab 保存失败：{prefabPath}");
                return report;
            }

            report.ok = true;
            report.prefabAssetPath = prefabPath;
            report.assetDir = assetDir;
            report.Log($"prefab 已生成：{prefabPath}");
            return report;
        }

        private static GameObject BuildHierarchy(
            string prefabName,
            string assetDir,
            AssemblySpec spec,
            SpriteManifest manifest,
            Dictionary<string, Sprite> sprites,
            AnimatorWriter.BuildResult animator,
            AssembleReport report)
        {
            var go = new GameObject(prefabName);

            // ── SpriteRenderer：默认状态的第一帧 ──
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sortingOrder = 0;
            sr.sprite = ResolveDefaultSprite(animator.DefaultStateName, sprites);

            // ── Animator ──
            var animComp = go.AddComponent<Animator>();
            animComp.runtimeAnimatorController = animator.Controller;

            // ── NpcCharacter（身份 + 人设 + 朝向）──
            var card = spec.CharacterCard ?? new CharacterCard();
            var npc = go.AddComponent<NpcCharacter>();
            var npcSo = new SerializedObject(npc);
            SetString(npcSo, "characterId", string.IsNullOrEmpty(card.Name) ? prefabName : card.Name);
            SetString(npcSo, "displayName", string.IsNullOrEmpty(card.Name) ? prefabName : card.Name);
            SetString(npcSo, "role", card.Role);
            npcSo.FindProperty("personaAsset").objectReferenceValue =
                AssetDatabase.LoadAssetAtPath<TextAsset>($"{assetDir}/persona.json");
            npcSo.FindProperty("dialogueEnabled").boolValue = spec.Dialogue != null && spec.Dialogue.Enabled;
            npcSo.FindProperty("maxTokens").intValue = spec.Dialogue != null ? spec.Dialogue.MaxTokens : 160;
            npcSo.ApplyModifiedProperties();

            // ── AIGC 显式标识（合规：交互界面侧）──
            if (spec.AesLabel != null && spec.AesLabel.Enabled)
            {
                var label = go.AddComponent<AigcDisclosure>();
                label.Configure(spec.AesLabel.Text, spec.AesLabel.Placement, true, card.SourcePrompt);
            }

            // ── 碰撞体：尺寸来自实测角色轮廓 ──
            var bounds = ResolveBounds(manifest);
            ApplyCollider(go, spec.Collider, bounds, report);

            // ── 移动 ──
            ApplyMovement(go, spec.Movement);

            return go;
        }

        private static Sprite ResolveDefaultSprite(string defaultState, Dictionary<string, Sprite> sprites)
        {
            if (!string.IsNullOrEmpty(defaultState))
            {
                string frame = $"{defaultState}_00";
                if (sprites.TryGetValue(frame, out var s)) return s;
            }
            return sprites.Values.FirstOrDefault();
        }

        /// <summary>
        /// 取角色包围盒。优先用 manifest 里的实测值；
        /// 缺失时按切片矩形与 PPU 估算——宁可偏大（挡住玩家）也不要偏小（穿模）。
        /// </summary>
        private static CharacterBounds ResolveBounds(SpriteManifest manifest)
        {
            if (manifest.CharacterBounds != null) return manifest.CharacterBounds;

            int ppu = Mathf.Max(1, manifest.PixelsPerUnit);
            var first = manifest.Sprites[0];
            float height = first.H / ppu * 0.85f;
            float width = first.W / ppu * 0.5f;

            return new CharacterBounds
            {
                Width = width,
                Height = height,
                CenterX = 0f,
                CenterY = height / 2f
            };
        }

        private static void ApplyCollider(
            GameObject go, ColliderSpec spec, CharacterBounds bounds, AssembleReport report)
        {
            spec = spec ?? new ColliderSpec();
            Vector2 offset = new Vector2(bounds.CenterX, bounds.CenterY);

            switch (spec.Shape)
            {
                case "circle":
                    var circle = go.AddComponent<CircleCollider2D>();
                    circle.radius = Mathf.Max(0.05f, bounds.Height * 0.35f);
                    circle.offset = new Vector2(bounds.CenterX, bounds.Height * 0.35f);
                    break;

                case "box":
                    var box = go.AddComponent<BoxCollider2D>();
                    box.size = new Vector2(bounds.Width, bounds.Height);
                    box.offset = offset;
                    break;

                default:
                    // 胶囊体默认竖直，是 2D 角色最贴合的形状（头顶/脚下圆角，腰身不收窄）。
                    var capsule = go.AddComponent<CapsuleCollider2D>();
                    capsule.direction = CapsuleDirection2D.Vertical;
                    capsule.size = new Vector2(bounds.Width, bounds.Height);
                    capsule.offset = offset;
                    break;
            }

            report.Log($"碰撞体：{spec.Shape} {bounds.Width:0.##}×{bounds.Height:0.##} 单位，" +
                       $"偏移 ({offset.x:0.##}, {offset.y:0.##})");
        }

        private static void ApplyMovement(GameObject go, MovementSpec movement)
        {
            movement = movement ?? new MovementSpec();
            if (movement.Mode == "none" || string.IsNullOrEmpty(movement.Mode)) return;

            var rb = go.AddComponent<Rigidbody2D>();
            rb.gravityScale = 0f;
            rb.freezeRotation = true;
            rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            rb.interpolation = RigidbodyInterpolation2D.Interpolate;

            var patrol = go.AddComponent<SimplePatrol2D>();
            var so = new SerializedObject(patrol);
            so.FindProperty("speed").floatValue = movement.Speed;

            var points = movement.PatrolPoints;
            if (points != null && points.Count > 0)
            {
                var arr = so.FindProperty("patrolPoints");
                arr.arraySize = points.Count;
                for (int i = 0; i < points.Count; i++)
                {
                    var p = points[i];
                    arr.GetArrayElementAtIndex(i).vector2Value =
                        new Vector2(p.Length > 0 ? p[0] : 0f, p.Length > 1 ? p[1] : 0f);
                }
            }
            so.ApplyModifiedProperties();
        }

        private static void SetString(SerializedObject so, string property, string value)
        {
            var prop = so.FindProperty(property);
            if (prop != null) prop.stringValue = value ?? "";
        }
    }
}
