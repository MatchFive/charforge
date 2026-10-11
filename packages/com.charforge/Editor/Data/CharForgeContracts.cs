using System.Collections.Generic;
using Newtonsoft.Json;

namespace CharForge.EditorTools.Data
{
    /// <summary>
    /// C# 侧的产物契约。
    ///
    /// **这些类型必须与 <c>schemas/*.schema.json</c> 以及 Python 侧
    /// <c>server/models/schemas.py</c> 逐字段保持一致。** 三处任一处改名都要同步改另两处，
    /// 这是本项目唯一的强耦合点，也是刻意的：契约稳定之后，后端换成 Sentis、ComfyUI
    /// 还是远端 API，装配逻辑一行都不用改。
    /// </summary>
    public static class CharForgeContractVersion
    {
        public const string Current = "0.2.0";
    }

    // ────────────────────────── 请求（Unity → 后端） ──────────────────────────

    /// <summary>一次生成任务的输入。字段名对应 Python <c>JobRequest</c>。</summary>
    public class JobRequest
    {
        [JsonProperty("prompt")] public string Prompt = "";
        [JsonProperty("reference_images")] public List<string> ReferenceImages = new List<string>();
        [JsonProperty("style_preset")] public string StylePreset = "custom";
        [JsonProperty("actions")] public List<string> Actions = new List<string> { "idle", "walk" };
        [JsonProperty("directions")] public List<string> Directions = new List<string> { "south", "north", "east", "west" };
        [JsonProperty("movement")] public MovementSpec Movement = new MovementSpec();
        [JsonProperty("dialogue_enabled")] public bool DialogueEnabled = true;
        [JsonProperty("forbidden")] public List<string> Forbidden = new List<string>();
        [JsonProperty("character_name", NullValueHandling = NullValueHandling.Ignore)] public string CharacterName;
        [JsonProperty("era_or_period", NullValueHandling = NullValueHandling.Ignore)] public string EraOrPeriod;
        [JsonProperty("reference_notes", NullValueHandling = NullValueHandling.Ignore)] public string ReferenceNotes;
        [JsonProperty("frames_per_clip")] public int FramesPerClip = 4;
    }

    public class MovementSpec
    {
        [JsonProperty("mode")] public string Mode = "none";        // none | rigidbody | astar | navmesh
        [JsonProperty("speed")] public float Speed = 1.5f;
        [JsonProperty("patrol_points")] public List<float[]> PatrolPoints = new List<float[]>();
        [JsonProperty("arrive_threshold")] public float ArriveThreshold = 0.1f;
    }

    // ────────────────────────── 角色卡 ──────────────────────────

    public class CharacterCard
    {
        [JsonProperty("job_id")] public string JobId = "";
        [JsonProperty("source_prompt")] public string SourcePrompt = "";
        [JsonProperty("name")] public string Name = "";
        [JsonProperty("role")] public string Role = "";
        [JsonProperty("personality")] public List<string> Personality = new List<string>();
        [JsonProperty("appearance")] public string Appearance = "";
        [JsonProperty("palette")] public List<string> Palette = new List<string>();
        [JsonProperty("silhouette_notes")] public string SilhouetteNotes = "";
        [JsonProperty("forbidden")] public List<string> Forbidden = new List<string>();
        [JsonProperty("actions")] public List<string> Actions = new List<string>();
        [JsonProperty("directions")] public List<string> Directions = new List<string>();
        [JsonProperty("style_tags")] public List<string> StyleTags = new List<string>();
        [JsonProperty("era_or_period", NullValueHandling = NullValueHandling.Ignore)] public string EraOrPeriod;
        [JsonProperty("reference_notes", NullValueHandling = NullValueHandling.Ignore)] public string ReferenceNotes;
    }

    // ────────────────────────── 图片清单 ──────────────────────────

    public class SheetInfo
    {
        [JsonProperty("path")] public string Path = "";
        [JsonProperty("cell_width")] public int CellWidth = 512;
        [JsonProperty("cell_height")] public int CellHeight = 512;
        [JsonProperty("columns")] public int Columns = 4;
        [JsonProperty("rows")] public int Rows = 1;
        [JsonProperty("frame_count")] public int FrameCount = 0;
    }

    /// <summary>
    /// 单个切片的矩形。<b>注意坐标系</b>：<c>y</c> 已是 Unity 的**左下原点**值，
    /// 与 <c>TextureImporter</c> / <c>SpriteRect</c> 的坐标系一致，不要再翻转。
    /// </summary>
    public class SliceInfo
    {
        [JsonProperty("name")] public string Name = "";
        [JsonProperty("x")] public float X;
        [JsonProperty("y")] public float Y;
        [JsonProperty("w")] public float W;
        [JsonProperty("h")] public float H;
        [JsonProperty("pivot_x")] public float PivotX = 0.5f;
        [JsonProperty("pivot_y")] public float PivotY = 0f;
    }

    /// <summary>角色可视范围（世界单位，相对脚底锚点）。用于生成贴合角色的碰撞体。</summary>
    public class CharacterBounds
    {
        [JsonProperty("width")] public float Width = 1f;
        [JsonProperty("height")] public float Height = 2f;
        [JsonProperty("center_x")] public float CenterX;
        [JsonProperty("center_y")] public float CenterY;
    }

    public class SpriteManifest
    {
        [JsonProperty("job_id")] public string JobId = "";
        [JsonProperty("sheet")] public SheetInfo Sheet = new SheetInfo();
        [JsonProperty("sprites")] public List<SliceInfo> Sprites = new List<SliceInfo>();

        [JsonProperty("pixels_per_unit")] public int PixelsPerUnit = 100;
        [JsonProperty("filter_mode")] public string FilterMode = "Bilinear";
        [JsonProperty("texture_type")] public string TextureType = "Sprite";
        [JsonProperty("alpha_is_transparency")] public bool AlphaIsTransparency = true;

        /// <summary>缺失时为 null：装配器会退化为按贴图实际包围盒现算。</summary>
        [JsonProperty("character_bounds", NullValueHandling = NullValueHandling.Ignore)]
        public CharacterBounds CharacterBounds;
    }

    // ────────────────────────── 动画与装配指令 ──────────────────────────

    public class AnimationSpec
    {
        [JsonProperty("name")] public string Name = "";
        [JsonProperty("direction")] public string Direction = "south";
        [JsonProperty("action")] public string Action = "idle";
        [JsonProperty("sprites")] public List<string> Sprites = new List<string>();
        [JsonProperty("frame_rate")] public int FrameRate = 8;
        [JsonProperty("loop")] public bool Loop = true;
    }

    public class ColliderSpec
    {
        [JsonProperty("shape")] public string Shape = "capsule";   // capsule | circle | box
        [JsonProperty("radius")] public float Radius = 0.3f;
        [JsonProperty("size")] public List<float> Size = new List<float> { 0.6f, 1.0f };
        [JsonProperty("offset")] public List<float> Offset = new List<float> { 0f, 0f };
    }

    public class DialogueSpec
    {
        [JsonProperty("enabled")] public bool Enabled = true;
        [JsonProperty("provider")] public string Provider = "llm-unity";
        [JsonProperty("persona_path")] public string PersonaPath = "persona.json";
        [JsonProperty("max_tokens")] public int MaxTokens = 160;
    }

    /// <summary>交互界面 AIGC 显式标识（GB 45438-2025 的界面侧落点）。</summary>
    public class AesLabelSpec
    {
        [JsonProperty("enabled")] public bool Enabled = true;
        [JsonProperty("text")] public string Text = "本内容由人工智能生成";
        [JsonProperty("placement")] public string Placement = "corner";
    }

    /// <summary>服务端产出的装配指令。可重放：改了人设不用重新出图。</summary>
    public class AssemblySpec
    {
        [JsonProperty("job_id")] public string JobId = "";
        [JsonProperty("prefab_name")] public string PrefabName = "NPC_Character";
        [JsonProperty("character_card")] public CharacterCard CharacterCard = new CharacterCard();
        [JsonProperty("animations")] public List<AnimationSpec> Animations = new List<AnimationSpec>();
        [JsonProperty("collider")] public ColliderSpec Collider = new ColliderSpec();
        [JsonProperty("movement")] public MovementSpec Movement = new MovementSpec();
        [JsonProperty("dialogue")] public DialogueSpec Dialogue = new DialogueSpec();
        [JsonProperty("aes_label")] public AesLabelSpec AesLabel = new AesLabelSpec();
    }

    // ────────────────────────── 人设 ──────────────────────────

    public class Persona
    {
        [JsonProperty("id")] public string Id = "";
        [JsonProperty("display_name")] public string DisplayName = "";
        [JsonProperty("role")] public string Role = "";
        [JsonProperty("personality")] public List<string> Personality = new List<string>();
        [JsonProperty("era_or_period", NullValueHandling = NullValueHandling.Ignore)] public string EraOrPeriod;
        [JsonProperty("knowledge_domains")] public List<string> KnowledgeDomains = new List<string>();
        [JsonProperty("forbidden")] public List<string> Forbidden = new List<string>();
        [JsonProperty("persona_prompt")] public string PersonaPrompt = "";
        [JsonProperty("greeting")] public string Greeting = "";
        [JsonProperty("max_tokens")] public int MaxTokens = 160;
        [JsonProperty("source_prompt")] public string SourcePrompt = "";
    }

    // ────────────────────────── 中间产物 ──────────────────────────

    /// <summary>后端返回的一帧图像（未抠图的原图）。</summary>
    public class GeneratedFrame
    {
        public string SliceName;
        public string Direction;
        public string Action;
        public int Index;
        public byte[] PngBytes;
    }

    /// <summary>风格剖析结果：参考图 → 可复用的风格约束。</summary>
    public class StyleProfile
    {
        [JsonProperty("preset")] public string Preset = "custom";
        [JsonProperty("positive_tags")] public List<string> PositiveTags = new List<string>();
        [JsonProperty("negative_tags")] public List<string> NegativeTags = new List<string>();
        [JsonProperty("palette")] public List<string> Palette = new List<string>();
        [JsonProperty("reference_count")] public int ReferenceCount = 0;
        [JsonProperty("source")] public string Source = "rule-based";
    }
}
