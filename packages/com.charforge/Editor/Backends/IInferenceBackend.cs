using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CharForge.EditorTools.Data;

namespace CharForge.EditorTools.Backends
{
    public enum BackendKind
    {
        /// <summary>占位图，用于 CI 与纯流程自测，零依赖。</summary>
        Mock = 0,

        /// <summary>Unity 引擎内推理（com.unity.ai.inference）。零外部进程。</summary>
        Sentis = 1,

        /// <summary>本地 ComfyUI（127.0.0.1:8188）。质量最高，支持 LoRA / ControlNet。</summary>
        ComfyUI = 2,

        /// <summary>本仓库自带的 Python 服务（server/）。</summary>
        Sidecar = 3,

        /// <summary>远端 / 商业 API（Scenario、Layer AI、自建服务）。</summary>
        Http = 4
    }

    /// <summary>后端能力位。装配层据此决定"哪些环节交给后端、哪些本地兜底"。</summary>
    [Flags]
    public enum BackendCapability
    {
        None = 0,
        FrameGeneration = 1 << 0,   // 文本 → 图像
        StyleAnalysis = 1 << 1,     // 参考图 → 风格约束
        Matting = 1 << 2,           // 抠图（去背）
        CharacterCard = 1 << 3,     // 描述 → 结构化角色卡（LLM）

        /// <summary>能以「整单委托」方式跑完全流程（见 <see cref="IWholeJobBackend"/>）。</summary>
        WholeJob = 1 << 4,

        /// <summary>什么都不缺，可以端到端跑完一条流水线。</summary>
        Full = FrameGeneration | StyleAnalysis | Matting | CharacterCard
    }

    /// <summary>后端健康状态。<b>NextAction 必填</b>——用户不该看到"失败了"而不知道该干什么。</summary>
    public class BackendStatus
    {
        public bool Ready;
        public string Message = "";
        public string NextAction = "";

        public static BackendStatus Ok(string message) => new BackendStatus { Ready = true, Message = message };
        public static BackendStatus Fail(string message, string nextAction) =>
            new BackendStatus { Ready = false, Message = message, NextAction = nextAction };
    }

    /// <summary>后端单帧生成请求（已由流水线补全提示词与风格约束）。</summary>
    public class FrameRequest
    {
        public string JobId;
        public string CharacterName;
        public string Direction;          // south | north | east | west
        public string Action;             // idle | walk
        public int Index;                 // 该片段的第几帧
        public int TotalFrames;
        public int Width = 512;
        public int Height = 512;
        public string PositivePrompt = "";
        public string NegativePrompt = "";
        public int Seed = -1;

        /// <summary>参考图绝对路径（风格锁定用）。</summary>
        public List<string> ReferenceImages = new List<string>();

        /// <summary>风格约束标签（由 StyleAnalysis 或预设给出）。</summary>
        public List<string> StyleTags = new List<string>();

        /// <summary>
        /// 本帧对应的 ControlNet 姿势骨架图绝对路径（可为空）。
        ///
        /// 四向一致的**真正来源不是提示词，是姿势约束**：同一句提示词分别生成正/背/侧面，
        /// 模型会给出四个身材比例都不一样的人。喂上骨架图后身体比例与姿态被锁住，
        /// 差异只剩视角。为空时后端应当照常出图，但必须在告警里说明「没有姿势约束」——
        /// 静默降级会让人以为一致性是坏的，其实是根本没约束。
        /// </summary>
        public string PoseTemplatePath = "";

        public string SliceName => $"{Direction}_{Action}_{Index:00}";
    }

    /// <summary>
    /// 推理后端抽象。
    ///
    /// **这是 v0.2 架构的核心接口。** Unity 侧负责一切与资产、编辑器、交互相关的逻辑；
    /// 后端只负责它不可替代的那件事——扩散推理与风格分析。
    /// 因此"换后端"= 换一个下拉框，装配代码一行不改。
    ///
    /// 实现约定：
    /// - 所有方法必须可被取消，且**不得占用主线程**（内部用 ConfigureAwait(false)）。
    /// - 返回的图像统一为 PNG 字节；抠图能力缺失时返回 null，由流水线本地兜底。
    /// - 任何失败都要给出可执行的 NextAction，不要只丢异常信息。
    /// </summary>
    public interface IInferenceBackend : IDisposable
    {
        string Id { get; }
        string DisplayName { get; }

        BackendKind Kind { get; }

        /// <summary>自动选择时的优先级，数值大的优先。</summary>
        int Priority { get; }

        BackendCapability Capabilities { get; }

        /// <summary>探测可用性。必须在 3 秒内返回，否则视为不可用。</summary>
        Task<BackendStatus> ProbeAsync(CancellationToken ct);

        /// <summary>参考图 → 风格约束。能力缺失时返回 null。</summary>
        Task<StyleProfile> ExtractStyleAsync(
            IReadOnlyList<string> referenceImagePaths, string presetName, CancellationToken ct);

        /// <summary>描述 → 结构化角色卡。能力缺失时返回 null（流水线走本地规则版）。</summary>
        Task<CharacterCard> DescribeAsync(JobRequest request, string jobId, CancellationToken ct);

        /// <summary>生成单帧 PNG。</summary>
        Task<byte[]> GenerateFrameAsync(FrameRequest request, CancellationToken ct);

        /// <summary>抠图。不支持时返回 null。</summary>
        Task<byte[]> MattingAsync(byte[] pngBytes, CancellationToken ct);

        /// <summary>
        /// 取出并清空后端在生成过程中累积的告警（例如「姿势模板缺失，本次没有姿势约束」）。
        ///
        /// 为什么要有这个口子：这类问题后端自己知道，流水线自己不知道，
        /// 而它恰恰决定了產出能不能被采信。丢掉它，用户只会看到「一致性很差」
        /// 却查不到原因。不实现的后端返回空集合即可。
        /// </summary>
        IReadOnlyList<string> DrainWarnings();
    }

    /// <summary>
    /// 「整单委托」型后端：把一个完整 JobRequest 交给它跑完第 1–6 段，
    /// 产出落盘的中间产物目录，再由 Unity 侧完成第 7 段装配。
    ///
    /// 为什么要有这条路径：本仓库 v0.1 的 Python 流水线是**已经验证跑通的**，
    /// 它的接口粒度是「整单」而不是「单帧」。硬把它拆成逐帧调用只会浪费已有资产。
    /// 于是这里显式承认两种形态：
    ///   - 逐帧型（ComfyUI / Sentis）：Unity 侧编排每一段，控制力最强
    ///   - 整单型（Sidecar / 远端服务）：Unity 只做装配，复用成本最低
    /// 二者对装配层完全等价——都只要求产出合法目录。
    /// </summary>
    public interface IWholeJobBackend : IInferenceBackend
    {
        Task<WholeJobResult> RunWholeJobAsync(
            JobRequest request,
            string jobId,
            Action<string, float> onProgress,
            CancellationToken ct);
    }

    public class WholeJobResult
    {
        public bool Ok;
        public string OutputDir = "";      // 含 manifest.json / assembly_spec.json / persona.json / sheet.png
        public string PrefabName = "";
        public string Error = "";
    }

    /// <summary>
    /// 后端注册表。刻意用显式注册而非反射扫描：
    /// 插件的依赖面必须可预测，反射扫描会让"某个包没装"变成运行时惊喜。
    /// </summary>
    public static class InferenceBackendRegistry
    {
        private static readonly List<IInferenceBackend> Backends = new List<IInferenceBackend>();
        private static readonly object Gate = new object();
        private static bool _initialized;

        public static void EnsureInitialized()
        {
            lock (Gate)
            {
                if (_initialized) return;
                _initialized = true;

                Register(new MockBackend());
                Register(new ComfyUiBackend());
                Register(new SidecarBackend());
                Register(new SentisBackend());
            }
        }

        public static void Register(IInferenceBackend backend)
        {
            if (backend == null) return;
            lock (Gate)
            {
                Backends.RemoveAll(b => b.Id == backend.Id);
                Backends.Add(backend);
            }
        }

        public static IReadOnlyList<IInferenceBackend> All()
        {
            EnsureInitialized();
            lock (Gate) return Backends.ToArray();
        }

        public static IInferenceBackend Find(string id)
        {
            EnsureInitialized();
            lock (Gate) return Backends.Find(b => b.Id == id);
        }

        /// <summary>按优先级取第一个探测就绪的后端。</summary>
        public static async Task<IInferenceBackend> PickReadyAsync(CancellationToken ct)
        {
            EnsureInitialized();
            var all = All();
            var sorted = new List<IInferenceBackend>(all);
            sorted.Sort((a, b) => b.Priority.CompareTo(a.Priority));

            foreach (var backend in sorted)
            {
                try
                {
                    var status = await backend.ProbeAsync(ct).ConfigureAwait(false);
                    if (status != null && status.Ready) return backend;
                }
                catch (OperationCanceledException) { throw; }
                catch { /* 探测失败按不可用处理，继续找下一个 */ }
            }
            return null;
        }

        public static void DisposeAll()
        {
            lock (Gate)
            {
                foreach (var b in Backends)
                {
                    try { b.Dispose(); } catch { /* 释放失败不该阻断退出 */ }
                }
                Backends.Clear();
                _initialized = false;
            }
        }
    }
}
