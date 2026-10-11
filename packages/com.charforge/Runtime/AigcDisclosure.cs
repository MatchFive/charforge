using UnityEngine;

namespace CharForge.Runtime
{
    /// <summary>
    /// AIGC 显式标识的**数据载体**（交互界面侧）。
    ///
    /// 为什么单独做成一个组件，而不是直接画个字：标识的**呈现方式**取决于具体游戏的 UI 体系
    /// （uGUI / UI Toolkit / 世界空间 / 外部悬浮层），工具不该替用户决定。
    /// 所以插件只负责把"这个角色需要标识、标识文案是什么、放在哪"结构化地带到运行期，
    /// 由 UI 层在合适的时机读取并渲染。
    ///
    /// 依据《人工智能生成合成内容标识办法》与强制性国标 GB 45438-2025
    /// （2025-09-01 实施）：人工智能生成合成内容在**交互界面的显式标识**是硬性要求。
    /// 注意工具的边界——本组件**不替代**图片/音频/视频自身的标识与元数据写入，
    /// 那是资产生产环节的责任（见 <c>docs/architecture.md</c> 与合规映射表）。
    /// </summary>
    [AddComponentMenu("CharForge/AIGC Disclosure")]
    [DisallowMultipleComponent]
    public class AigcDisclosure : MonoBehaviour
    {
        [Tooltip("显式标识文案。国标要求含「人工智能 / AI」与「生成 / 合成」要素。")]
        [SerializeField] private string labelText = "本内容由人工智能生成";

        [Tooltip("建议位置：corner（角标）/ bottom（底栏）/ dialog（对话首条）")]
        [SerializeField] private string placement = "corner";

        [Tooltip("关掉即表示该角色在交互界面不再需要显式标识——请确认合规判断后再关。")]
        [SerializeField] private bool required = true;

        [Tooltip("生成该角色的原始描述，便于运营侧追溯。")]
        [SerializeField] private string sourcePrompt = "";

        public string LabelText => labelText;
        public string Placement => placement;
        public bool Required => required;
        public string SourcePrompt => sourcePrompt;

        /// <summary>由装配器写入（Editor 侧通过 SerializedObject 设置）。</summary>
        public void Configure(string text, string placementHint, bool isRequired, string prompt)
        {
            if (!string.IsNullOrEmpty(text)) labelText = text;
            if (!string.IsNullOrEmpty(placementHint)) placement = placementHint;
            required = isRequired;
            sourcePrompt = prompt;
        }
    }
}
