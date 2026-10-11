using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CharForge.EditorTools.Data;
using UnityEngine;

// 这里**刻意不 using** 推理引擎的命名空间。
//
// 原因：程序集引用是编译期硬依赖，而 AI 推理包是**可选依赖**——
// 一旦在本程序集的 references 里写死，没装该包的工程就会报「程序集引用找不到」，
// 插件连打开都做不到。而 using 一个未被引用的命名空间，同样会直接 CS0234/CS0246。
// 两者都会把"可选"变成"必须"，这是开源插件最不该犯的错。
//
// 本文件目前只用字符串与 Task 描述能力，不触碰推理引擎的任何类型，因此零引用即可编译。
// 等你开始写真正的扩散流程（下方 GenerateFrameAsync 的 TODO）时，再按下面做：
//
//   1. 查清装配名（不同版本不同，别照抄博客）：
//        Library/PackageCache/com.unity.ai.inference@<版本>/Runtime/*.asmdef
//      打开这个文件看 "name" 字段。2.2 起通常是 Unity.InferenceEngine；
//      旧的 com.unity.sentis（≤2.1）则是 Unity.Sentis。
//   2. 把该装配名加进 Editor/com.charforge.Editor.asmdef 的 "references"。
//   3. 那时才在文件顶部 using 对应命名空间。
//
// 命名空间对应关系（2026 年事实，来源：Unity 官方升级指南）：
//   com.unity.ai.inference ≥ 2.2  →  using Unity.InferenceEngine;
//   com.unity.sentis      ≤ 2.1  →  using Unity.Sentis;
//   包名与命名空间不是一回事：2.4 起包的显示名又改回 "Sentis"，但包 id 仍是
//   com.unity.ai.inference，命名空间仍是 Unity.InferenceEngine。

namespace CharForge.EditorTools.Backends
{
    /// <summary>
    /// Unity 引擎内推理后端（Sentis / Unity Inference Engine）。
    ///
    /// **它的价值是"删掉配置门槛"**：模型在工程内，进程内推理，不装 Python、不起服务、
    /// 不联网。用户第一次打开窗口就能出东西——这点直接决定开源项目有没有第二个用户。
    ///
    /// **同时必须诚实说明它的边界**：
    /// - 只能跑小体量 / 已导出的 ONNX 模型，SDXL 级别在消费级显卡上很慢；
    /// - **无法训练 LoRA**，而"项目画风锁定"本质是训练问题（Unity 官方也得外购 Scenario /
    ///   Layer AI 的 LoRA）。所以它能验证流程，但替代不了 ComfyUI 做正式生产。
    /// - 需要把扩散流程拆成多个 ONNX（text encoder / UNet / VAE）分别调度，
    ///   这一段的实现是**留给使用者的核心工作**（见下方 TODO），本文件只搭好管线骨架。
    ///
    /// 启用方式：在 Package Manager 安装 com.unity.ai.inference（≥2.0），
    /// Editor 程序集会自动通过 versionDefine 点亮 CHARFORGE_SENTIS 宏，本后端随即出现
    /// （无需改代码、也无需改程序集引用）。装了这个包但还没写实现时，探测会直接告诉你
    /// 下一步该做什么，而不是静默失败。
    /// </summary>
    public class SentisBackend : IInferenceBackend
    {
        /// <summary>工程内的 ONNX 模型路径（Assets/ 开头）。多模型流水线时指向主 UNet。</summary>
        public string ModelAssetPath = "";

        /// <summary>显式打开才允许被自动选中——实验性能力不该悄悄抢走默认后端。</summary>
        public bool EnableExperimental = false;

        public string Id => "sentis";
        public string DisplayName => "Unity 引擎内推理（Sentis）";
        public BackendKind Kind => BackendKind.Sentis;
        public int Priority => 60;
        public BackendCapability Capabilities =>
#if CHARFORGE_SENTIS
            BackendCapability.FrameGeneration;
#else
            BackendCapability.None;
#endif

        public Task<BackendStatus> ProbeAsync(CancellationToken ct)
        {
#if !CHARFORGE_SENTIS
            return Task.FromResult(BackendStatus.Fail(
                "未安装 Unity 推理引擎包",
                "Package Manager → Unity Registry → 搜索并安装「Inference Engine / Sentis」(com.unity.ai.inference ≥ 2.0)；" +
                "安装后本后端会自动出现在列表中。若只想立刻跑通流程，先切到占位后端。"));
#else
            if (!EnableExperimental)
            {
                return Task.FromResult(BackendStatus.Fail(
                    "引擎内推理为实验性功能，默认关闭",
                    "在「后端设置」里勾选「启用引擎内推理（实验性）」，并指定工程内的 ONNX 模型。"));
            }
            if (string.IsNullOrWhiteSpace(ModelAssetPath) || !File.Exists(ToAbsolute(ModelAssetPath)))
            {
                return Task.FromResult(BackendStatus.Fail(
                    "未指定工程内的 ONNX 模型",
                    "把导出的 ONNX 放入 Assets/（Sentis 会自动转成 ModelAsset），再在这里填入它的工程路径，" +
                    "例如 Assets/CharForge/Models/sd_unet.onnx。"));
            }
            return Task.FromResult(BackendStatus.Ok($"引擎内推理就绪（模型 {Path.GetFileName(ModelAssetPath)}）"));
#endif
        }

        public Task<StyleProfile> ExtractStyleAsync(
            IReadOnlyList<string> referenceImagePaths, string presetName, CancellationToken ct)
        {
            // CLIP 风格相似度可以用 Sentis 跑，但需要单独的视觉编码器 ONNX；此处不假装支持。
            return Task.FromResult<StyleProfile>(null);
        }

        public Task<CharacterCard> DescribeAsync(JobRequest request, string jobId, CancellationToken ct)
        {
            return Task.FromResult<CharacterCard>(null);
        }

        public Task<byte[]> MattingAsync(byte[] pngBytes, CancellationToken ct)
        {
            return Task.FromResult<byte[]>(null);
        }

        /// <summary>实验性后端目前不上报问题；抠图走流水线的本地兜底。</summary>
        public IReadOnlyList<string> DrainWarnings() => Array.Empty<string>();

        public Task<byte[]> GenerateFrameAsync(FrameRequest request, CancellationToken ct)
        {
#if !CHARFORGE_SENTIS
            throw new NotSupportedException(
                "未安装 com.unity.ai.inference。请在 Package Manager 中安装后重试，或切换到其他后端。");
#else
            // ── 需要你实现的部分（与 Python 侧 generate._generate_local 的地位等价）──
            //
            // Sentis 只负责"跑一张张量"，扩散流程要自己串。建议按下面四步做，
            // 每一步单独验证过再往上叠（顺序错了会卡死在最难调试的那一环）：
            //
            //   1. 文本编码：CLIP text encoder ONNX → prompt 转 embedding
            //   2. 采样循环：UNet ONNX 反复前向，配 scheduler 去噪（先用 8-12 步的
            //      快速采样器把链路跑通，再谈画质）
            //   3. VAE 解码：latent → RGB 张量
            //   4. 张量 → PNG：这里必须回主线程（Texture2D + EncodeToPNG）
            //
            // 骨架代码（请按实际模型输入输出名调整）：
            //
            //   var model = ModelLoader.Load(AssetDatabase.LoadAssetAtPath<ModelAsset>(ModelAssetPath), ...);
            //   using var worker = new Worker(model, BackendType.GPUCompute);
            //   worker.Schedule(inputTensor);
            //   using var output = worker.PeekOutput("output_0") as Tensor<float>;
            //   output.CompleteOperationsAndDownload();
            //   var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            //   tex.SetPixels32(ToColor32(output));      // NCHW → RGBA，注意 y 轴翻转
            //   return tex.EncodeToPNG();
            //
            // 提示：8GB 显存以下建议 fp16 + 512px + 快速采样器；超过 1024px 会开始换页。
            throw new NotImplementedException(
                "引擎内推理的扩散流程尚未实现（见 SentisBackend.GenerateFrameAsync 内的 TODO 与 docs/roadmap.md 第 4 阶段）。" +
                "在此之前请使用 ComfyUI 或 Python 服务后端。");
#endif
        }

        private static string ToAbsolute(string assetPath)
        {
            string trimmed = assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                ? assetPath.Substring("Assets/".Length)
                : assetPath;
            return Path.Combine(Application.dataPath, trimmed);
        }

        public void Dispose() { }
    }
}
