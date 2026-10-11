using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CharForge.EditorTools.Data;
using CharForge.EditorTools.Util;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CharForge.EditorTools.Backends
{
    /// <summary>
    /// 本地 ComfyUI 后端（默认 http://127.0.0.1:8188）。**生产环境的首选**：
    /// 只有它同时具备 SDXL/Flux + LoRA + ControlNet + IP-Adapter，也就是"画风锁定"这件事的完整解法。
    ///
    /// 工作方式：读一份 **API 格式** 的 workflow JSON（ComfyUI 里 Settings → Enable Dev mode Options
    /// → "Save (API Format)"），把提示词/种子/尺寸补进去，走 /prompt → /history → /view 三接口取图。
    ///
    /// 节点绑定（哪个节点是正向提示词、哪个是种子）写在 workflow 同目录的 <c>*.bindings.json</c>：
    /// <code>
    /// { "positive": "6", "negative": "7", "seed": "3", "width": "5", "height": "5", "load_image": "10" }
    /// </code>
    /// 绑定文件缺失时会退化为启发式：取第一个 <c>CLIPTextEncode</c> 为正向、第二个为负向。
    /// 启发式只保证"能跑"，要精确请写绑定文件——这比在 C# 里猜节点语义可靠得多。
    /// </summary>
    public class ComfyUiBackend : IInferenceBackend
    {
        public string BaseUrl = "http://127.0.0.1:8188";

        /// <summary>API 格式 workflow 的绝对路径。为空时视为未配置。</summary>
        public string WorkflowApiJsonPath = "";

        /// <summary>单帧超时（秒）。ComfyUI 冷启动 + SDXL 20 步 / 1024px 通常 20-60 秒。</summary>
        public int FrameTimeoutSeconds = 240;

        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        public string Id => "comfyui";
        public string DisplayName => "本地 ComfyUI（质量最高）";
        public BackendKind Kind => BackendKind.ComfyUI;
        public int Priority => 100;
        public BackendCapability Capabilities => BackendCapability.FrameGeneration;

        // ────────────────────────── 告警收集 ──────────────────────────
        // 「姿势模板缺失」这类问题本后端知道、流水线不知道，却直接决定产出能不能采信。
        // 同一句话只报一次——16 帧重复 16 遍会把窗口日志冲掉。
        private readonly List<string> _warnings = new List<string>();
        private readonly HashSet<string> _warnedOnce = new HashSet<string>();

        private void Warn(string message)
        {
            if (_warnedOnce.Add(message)) _warnings.Add(message);
        }

        public IReadOnlyList<string> DrainWarnings()
        {
            var copy = _warnings.ToArray();
            _warnings.Clear();
            _warnedOnce.Clear();
            return copy;
        }

        // ────────────────────────── 探测 ──────────────────────────

        public async Task<BackendStatus> ProbeAsync(CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(WorkflowApiJsonPath) || !File.Exists(WorkflowApiJsonPath))
            {
                return BackendStatus.Fail(
                    "未配置 ComfyUI 工作流",
                    "在窗口「后端设置」里指定一份 API 格式的 workflow.json，" +
                    "或在 ComfyUI 中 Settings → Enable Dev mode → Save (API Format) 导出后拖进来。" +
                    "也可以直接切换到无需配置的占位后端先跑通流程。");
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                using var resp = await Http.GetAsync($"{BaseUrl.TrimEnd('/')}/system_stats", cts.Token)
                    .ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    return BackendStatus.Fail(
                        $"ComfyUI 响应异常（HTTP {(int)resp.StatusCode}）",
                        "确认 ComfyUI 已在运行；若改过端口，请在「后端设置」里同步更新地址。");
                }
                return BackendStatus.Ok($"ComfyUI 就绪：{BaseUrl}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return BackendStatus.Fail(
                    $"3 秒内连不上 {BaseUrl}",
                    "先在终端启动 ComfyUI（python main.py），或点窗口里的「启动后端」由插件代管。");
            }
            catch (Exception e)
            {
                return BackendStatus.Fail(
                    $"连接 ComfyUI 失败：{e.Message}",
                    "确认地址与端口正确（默认 127.0.0.1:8188），必要时检查防火墙。");
            }
        }

        public Task<StyleProfile> ExtractStyleAsync(
            IReadOnlyList<string> referenceImagePaths, string presetName, CancellationToken ct)
        {
            // 风格剖析交给 Sidecar（CLIP）或本地规则预设；ComfyUI 只负责按约束出图。
            return Task.FromResult<StyleProfile>(null);
        }

        public Task<CharacterCard> DescribeAsync(JobRequest request, string jobId, CancellationToken ct)
        {
            return Task.FromResult<CharacterCard>(null);
        }

        public Task<byte[]> MattingAsync(byte[] pngBytes, CancellationToken ct)
        {
            // ComfyUI 侧当然可以加 rembg 节点，但那要求用户在 workflow 里连好；
            // 这里返回 null 让流水线走本地抠图兜底，避免"必须改 workflow 才能跑"。
            return Task.FromResult<byte[]>(null);
        }

        // ────────────────────────── 出图 ──────────────────────────

        public async Task<byte[]> GenerateFrameAsync(FrameRequest request, CancellationToken ct)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (!File.Exists(WorkflowApiJsonPath))
                throw new FileNotFoundException($"找不到 ComfyUI 工作流：{WorkflowApiJsonPath}");

            var bindings = WorkflowBindings.Load(WorkflowApiJsonPath);
            var workflow = JObject.Parse(await ReadAllTextAsync(WorkflowApiJsonPath, ct).ConfigureAwait(false));
            bindings.Apply(workflow, request);
            await ApplyPoseTemplateAsync(workflow, bindings, request, ct).ConfigureAwait(false);

            string promptId = await SubmitAsync(workflow, ct).ConfigureAwait(false);

            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < FrameTimeoutSeconds)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(600, ct).ConfigureAwait(false);

                var image = await TryFetchOutputAsync(promptId, ct).ConfigureAwait(false);
                if (image != null) return image;
            }

            throw new TimeoutException(
                $"ComfyUI 在 {FrameTimeoutSeconds} 秒内未返回结果（prompt_id={promptId}）。" +
                "常见原因：模型过大导致显存换页、workflow 里存在未安装的自定义节点。请先在 ComfyUI 界面手动跑一次该 workflow。");
        }

        /// <summary>
        /// 把本帧的姿势骨架图送进 workflow 的 LoadImage 节点（即 ControlNet 的图像输入）。
        ///
        /// ComfyUI 的 LoadImage 读的是「它自己 input 目录下的文件名」，不是绝对路径，
        /// 所以本地文件必须先经 /upload/image 传过去再改节点上的文件名。
        ///
        /// 这条链路此前是**断的**：bindings 里解析了 load_image 却没有任何消费点，
        /// 于是无论用户准备了多少骨架图，ControlNet 拿到的永远是工作流里写死的那一张。
        /// 表面症状是「四向姿势没锁住」，实际是根本没接上——所以这里不但要接，
        /// 缺图时还必须说出来，否则同样的症状会再被误判成「模型不行」。
        /// </summary>
        private async Task ApplyPoseTemplateAsync(
            JObject workflow, WorkflowBindings bindings, FrameRequest request, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(bindings.LoadImage)) return;   // 该工作流没有 LoadImage 节点

            var inputs = workflow[bindings.LoadImage]?["inputs"];
            if (inputs == null)
            {
                Warn($"工作流里找不到 load_image 绑定的节点 {bindings.LoadImage}，姿势注入被跳过。");
                return;
            }

            if (string.IsNullOrEmpty(request.PoseTemplatePath) || !File.Exists(request.PoseTemplatePath))
            {
                // 文案里不带帧名，这样 16 帧只会报一次（Warn 有去重），
                // 否则同一条问题会刷 16 遍把日志冲掉。
                Warn(
                    "缺少姿势骨架图，本次各帧都沿用工作流里写死的那张图片——" +
                    "四向姿势不会被锁住。放置位置：" +
                    CharForgePaths.DescribeLookup(Path.Combine("pose_templates", "south_idle_00.png")) +
                    "；命名约定与制作方法见该目录下的 README。");
                return;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(request.PoseTemplatePath);
                string uploaded = await UploadImageAsync(bytes, request.SliceName + ".png", ct)
                    .ConfigureAwait(false);
                inputs["image"] = uploaded;
            }
            catch (Exception e)
            {
                Warn($"上传姿势图失败（{e.Message}），本帧沿用工作流里写死的图片。");
            }
        }

        private async Task<string> SubmitAsync(JObject workflow, CancellationToken ct)
        {
            var payload = new JObject
            {
                ["prompt"] = workflow,
                ["client_id"] = Guid.NewGuid().ToString("N")
            };

            using var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync($"{BaseUrl.TrimEnd('/')}/prompt", content, ct)
                .ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"ComfyUI 拒绝该工作流（HTTP {(int)resp.StatusCode}）：{Truncate(body, 600)}");
            }

            var json = JObject.Parse(body);
            var id = (string)json["prompt_id"];
            if (string.IsNullOrEmpty(id))
                throw new InvalidOperationException($"ComfyUI 未返回 prompt_id：{Truncate(body, 600)}");
            return id;
        }

        private async Task<byte[]> TryFetchOutputAsync(string promptId, CancellationToken ct)
        {
            using var resp = await Http.GetAsync($"{BaseUrl.TrimEnd('/')}/history/{promptId}", ct)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var json = JObject.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            var entry = json[promptId];
            var outputs = entry?["outputs"] as JObject;
            if (outputs == null) return null;

            foreach (var node in outputs.Properties())
            {
                var images = node.Value?["images"] as JArray;
                if (images == null || images.Count == 0) continue;

                var img = images[0];
                string filename = (string)img["filename"];
                string subfolder = (string)img["subfolder"] ?? "";
                string type = (string)img["type"] ?? "output";
                if (string.IsNullOrEmpty(filename)) continue;

                string url = $"{BaseUrl.TrimEnd('/')}/view?filename={Uri.EscapeDataString(filename)}" +
                             $"&subfolder={Uri.EscapeDataString(subfolder)}&type={Uri.EscapeDataString(type)}";
                using var imgResp = await Http.GetAsync(url, ct).ConfigureAwait(false);
                if (!imgResp.IsSuccessStatusCode) continue;
                return await imgResp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }
            return null;
        }

        /// <summary>上传一张图到 ComfyUI 的 input 目录（ControlNet 姿势模板 / 参考图用）。</summary>
        public async Task<string> UploadImageAsync(byte[] pngBytes, string fileName, CancellationToken ct)
        {
            using var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(pngBytes);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            content.Add(fileContent, "image", fileName);
            content.Add(new StringContent("true"), "overwrite");

            using var resp = await Http.PostAsync($"{BaseUrl.TrimEnd('/')}/upload/image", content, ct)
                .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = JObject.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            return (string)json["name"] ?? fileName;
        }

        private static async Task<string> ReadAllTextAsync(string path, CancellationToken ct)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";

        public void Dispose() { }

        // ────────────────────────── 节点绑定 ──────────────────────────

        private class WorkflowBindings
        {
            public string Positive;
            public string Negative;
            public string Seed;
            public string Width;
            public string Height;
            public string LoadImage;

            public static WorkflowBindings Load(string workflowPath)
            {
                string bindPath = Path.Combine(
                    Path.GetDirectoryName(workflowPath) ?? ".",
                    Path.GetFileNameWithoutExtension(workflowPath) + ".bindings.json");

                if (File.Exists(bindPath))
                {
                    var j = JObject.Parse(File.ReadAllText(bindPath));
                    return new WorkflowBindings
                    {
                        Positive = (string)j["positive"],
                        Negative = (string)j["negative"],
                        Seed = (string)j["seed"],
                        Width = (string)j["width"],
                        Height = (string)j["height"],
                        LoadImage = (string)j["load_image"]
                    };
                }

                // 启发式：取前两个 CLIPTextEncode。顺序按节点 id 数值排序，保证结果稳定可预期。
                var inferred = new WorkflowBindings();
                try
                {
                    var wf = JObject.Parse(File.ReadAllText(workflowPath));
                    var textNodes = new List<(int order, string id)>();
                    foreach (var p in wf.Properties())
                    {
                        if ((string)p.Value?["class_type"] == "CLIPTextEncode")
                            textNodes.Add((int.TryParse(p.Name, out var n) ? n : int.MaxValue, p.Name));
                    }
                    textNodes.Sort((a, b) => a.order.CompareTo(b.order));
                    if (textNodes.Count > 0) inferred.Positive = textNodes[0].id;
                    if (textNodes.Count > 1) inferred.Negative = textNodes[1].id;
                }
                catch { /* 解析失败就让调用方报出明确错误 */ }
                return inferred;
            }

            public void Apply(JObject workflow, FrameRequest req)
            {
                SetText(workflow, Positive, req.PositivePrompt);
                if (!string.IsNullOrEmpty(Negative)) SetText(workflow, Negative, req.NegativePrompt);
                SetValue(workflow, Seed, req.Seed >= 0 ? req.Seed : new System.Random().Next(1, int.MaxValue));
                SetValue(workflow, Width, req.Width);
                SetValue(workflow, Height, req.Height);
            }

            private static void SetText(JObject wf, string nodeId, string value)
            {
                if (string.IsNullOrEmpty(nodeId) || value == null) return;
                var inputs = wf[nodeId]?["inputs"];
                if (inputs != null) inputs["text"] = value;
            }

            private static void SetValue(JObject wf, string nodeId, int value)
            {
                if (string.IsNullOrEmpty(nodeId)) return;
                var inputs = wf[nodeId]?["inputs"];
                if (inputs == null) return;

                // KSampler 的 seed 与尺寸可能在不同节点；能设哪个设哪个，设不到就保留 workflow 原值。
                if (inputs["seed"] != null) inputs["seed"] = value;
                else if (inputs["noise_seed"] != null) inputs["noise_seed"] = value;
                if (inputs["width"] != null) inputs["width"] = value;
                if (inputs["height"] != null) inputs["height"] = value;
                if (inputs["batch_size"] != null) inputs["batch_size"] = 1;
            }
        }
    }
}
