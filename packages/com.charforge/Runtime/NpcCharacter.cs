using System;
using UnityEngine;

namespace CharForge.Runtime
{
    /// <summary>四向朝向。与 Python 侧的 Direction 枚举一一对应，改名要同步。</summary>
    public enum FacingDirection
    {
        South = 0,
        North = 1,
        East = 2,
        West = 3
    }

    /// <summary>
    /// 生成的 NPC 角色组件。
    ///
    /// 这里刻意保持"薄"：只负责持有身份与人设、提供系统提示词、以及维护朝向。
    /// 真正的对话推理交给外部（LLMUnity / HTTP 服务 / 你自己的实现），
    /// 这样换对话后端时不需要重新生成 prefab。
    /// </summary>
    [AddComponentMenu("CharForge/NPC Character")]
    [DisallowMultipleComponent]
    public class NpcCharacter : MonoBehaviour
    {
        [Header("身份")]
        [SerializeField] private string characterId = "";
        [SerializeField] private string displayName = "";
        [SerializeField] private string role = "";

        [Header("人设")]
        [Tooltip("由流水线产出的 persona.json。放入 TextAsset 即可。")]
        [SerializeField] private TextAsset personaAsset;

        [Header("对话")]
        [SerializeField] private bool dialogueEnabled = false;
        [Tooltip("对话后端地址（可选）。留空表示由外部驱动，本组件不主动请求。")]
        [SerializeField] private string dialogueEndpoint = "";
        [SerializeField] private int maxTokens = 160;

        [Header("朝向")]
        [SerializeField] private FacingDirection facing = FacingDirection.South;

        public string CharacterId => characterId;
        public string DisplayName => displayName;
        public string Role => role;
        public bool DialogueEnabled => dialogueEnabled;
        public int MaxTokens => maxTokens;
        public FacingDirection Facing => facing;

        /// <summary>人设原文（JSON 字符串）。对话后端可直接使用。</summary>
        public string PersonaJson => personaAsset != null ? personaAsset.text : string.Empty;

        /// <summary>
        /// 从人设 JSON 里取出 persona_prompt 字段。
        /// 不依赖 JSON 库的简化实现——够用，且不会因为多装依赖而报错。
        /// 需要更严谨的解析时改用 Newtonsoft。
        /// </summary>
        public string GetSystemPrompt()
        {
            string json = PersonaJson;
            if (string.IsNullOrEmpty(json)) return string.Empty;

            const string key = "\"persona_prompt\"";
            int keyIndex = json.IndexOf(key, StringComparison.Ordinal);
            if (keyIndex < 0) return json;

            int colon = json.IndexOf(':', keyIndex);
            if (colon < 0) return string.Empty;

            int start = json.IndexOf('"', colon + 1);
            if (start < 0) return string.Empty;

            var sb = new System.Text.StringBuilder();
            for (int i = start + 1; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '\\' && i + 1 < json.Length)
                {
                    char next = json[i + 1];
                    sb.Append(next == 'n' ? '\n' : next);
                    i++;
                    continue;
                }
                if (c == '"') break;
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>由行为脚本调用，切朝向并驱动 Animator。</summary>
        public void SetFacing(FacingDirection dir)
        {
            facing = dir;
            var animator = GetComponent<Animator>();
            if (animator == null) return;
            animator.SetInteger("facing", (int)facing);
        }

        /// <summary>根据移动向量推断朝向。</summary>
        public static FacingDirection DirectionFrom(Vector2 move)
        {
            if (move.sqrMagnitude < 0.0001f) return FacingDirection.South;
            if (Mathf.Abs(move.x) >= Mathf.Abs(move.y))
                return move.x >= 0f ? FacingDirection.East : FacingDirection.West;
            return move.y >= 0f ? FacingDirection.North : FacingDirection.South;
        }

        private void Reset()
        {
            characterId = gameObject.name;
            displayName = gameObject.name;
        }
    }
}
