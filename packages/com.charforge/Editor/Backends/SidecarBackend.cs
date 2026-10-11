using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CharForge.EditorTools.Data;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CharForge.EditorTools.Backends
{
    /// <summary>
    /// 本仓库自带 Python 服务（<c>server/</c>）的后端适配器。属于「整单委托」型。
    ///
    /// **这是 v0.1 → v0.2 迁移的关键一环**：v0.1 的 Python 流水线是已经跑通并验证过的，
    /// 它的七段能力（提示词编排 / 画风锁定 / 生成 / 抠图 / 归一化 / 打包）没有理由丢掉。
    /// 区别只在于——v0.2 里它不再是"宿主"，而是**可被替换的下游**；
    /// Unity 侧保留第 7 段（装配），因为那才是插件形态独有的能力。
    ///
    /// 接口对齐 server/main.py：
    ///   GET  /api/health          探测
    ///   POST /api/jobs            异步建单 → { job_id, ws }
    ///   GET  /api/jobs/{job_id}   202=进行中，200=JobResult
    /// </summary>
    public class SidecarBackend : IWholeJobBackend
    {
        public string BaseUrl = "http://127.0.0.1:8000";

        /// <summary>整单超时（秒）。32 帧 + 本地 SDXL 需要留足时间。</summary>
        public int JobTimeoutSeconds = 1800;

        private static readonly HttpClient Http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        public string Id => "sidecar";
        public string DisplayName => "自带 Python 服务（server/）";
        public BackendKind Kind => BackendKind.Sidecar;
        public int Priority => 80;
        public BackendCapability Capabilities => BackendCapability.FrameGeneration | BackendCapability.WholeJob;

        public async Task<BackendStatus> ProbeAsync(CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                using var resp = await Http.GetAsync($"{BaseUrl.TrimEnd('/')}/api/health", cts.Token)
                    .ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    return BackendStatus.Fail(
                        $"服务响应异常（HTTP {(int)resp.StatusCode}）",
                        "确认已在本仓库根目录执行 python -m server.cli serve。");
                }

                var json = JObject.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
                return BackendStatus.Ok(
                    $"Python 服务就绪（版本 {json["version"]}，生成后端 {json["backend"]}）");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return BackendStatus.Fail(
                    $"3 秒内连不上 {BaseUrl}",
                    "点窗口里的「启动后端」由插件代管，或手动执行：python -m server.cli serve");
            }
            catch (Exception e)
            {
                return BackendStatus.Fail($"连接失败：{e.Message}", "检查地址与端口，或改用其他后端。");
            }
        }

        public Task<StyleProfile> ExtractStyleAsync(
            IReadOnlyList<string> referenceImagePaths, string presetName, CancellationToken ct)
        {
            // 整单型后端内部已包含风格锁定（style_lock 段），不需要外部逐段调用。
            return Task.FromResult<StyleProfile>(null);
        }

        public Task<CharacterCard> DescribeAsync(JobRequest request, string jobId, CancellationToken ct)
        {
            return Task.FromResult<CharacterCard>(null);
        }

        public Task<byte[]> GenerateFrameAsync(FrameRequest request, CancellationToken ct)
        {
            throw new NotSupportedException(
                "Sidecar 后端是整单委托型，请调用 RunWholeJobAsync，不要逐帧调用。");
        }

        public Task<byte[]> MattingAsync(byte[] pngBytes, CancellationToken ct)
        {
            return Task.FromResult<byte[]>(null);
        }

        // ────────────────────────── 整单执行 ──────────────────────────

        public async Task<WholeJobResult> RunWholeJobAsync(
            JobRequest request, string jobId, Action<string, float> onProgress, CancellationToken ct)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            onProgress?.Invoke("提交任务…", 2f);

            string body = JsonConvert.SerializeObject(request);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var createResp = await Http.PostAsync($"{BaseUrl.TrimEnd('/')}/api/jobs", content, ct)
                .ConfigureAwait(false);

            string createBody = await createResp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!createResp.IsSuccessStatusCode)
            {
                return new WholeJobResult
                {
                    Ok = false,
                    Error = $"建单失败（HTTP {(int)createResp.StatusCode}）：{createBody}"
                };
            }

            string serverJobId = (string)JObject.Parse(createBody)["job_id"];
            if (string.IsNullOrEmpty(serverJobId))
                return new WholeJobResult { Ok = false, Error = $"服务未返回 job_id：{createBody}" };

            onProgress?.Invoke($"服务端处理中（{serverJobId}）…", -1f);

            var started = DateTime.UtcNow;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if ((DateTime.UtcNow - started).TotalSeconds > JobTimeoutSeconds)
                {
                    return new WholeJobResult
                    {
                        Ok = false,
                        Error = $"超过 {JobTimeoutSeconds} 秒未完成。请查看服务端日志，" +
                                "或先在终端用 python -m server.cli smoke 单独验证流水线。"
                    };
                }

                await Task.Delay(800, ct).ConfigureAwait(false);

                using var pollResp = await Http
                    .GetAsync($"{BaseUrl.TrimEnd('/')}/api/jobs/{serverJobId}", ct)
                    .ConfigureAwait(false);

                if (pollResp.StatusCode == System.Net.HttpStatusCode.Accepted)
                {
                    double sec = (DateTime.UtcNow - started).TotalSeconds;
                    onProgress?.Invoke($"服务端处理中…已用 {sec:0} 秒", -1f);
                    continue;
                }

                if (!pollResp.IsSuccessStatusCode)
                {
                    return new WholeJobResult
                    {
                        Ok = false,
                        Error = $"查询任务失败（HTTP {(int)pollResp.StatusCode}）"
                    };
                }

                var result = JObject.Parse(await pollResp.Content.ReadAsStringAsync().ConfigureAwait(false));
                bool ok = result["ok"] != null && (bool)result["ok"];
                if (!ok)
                    return new WholeJobResult { Ok = false, Error = (string)result["error"] ?? "未知错误" };

                string outputDir = (string)result["output_dir"];
                if (string.IsNullOrEmpty(outputDir) || !Directory.Exists(outputDir))
                {
                    return new WholeJobResult
                    {
                        Ok = false,
                        Error = $"服务端未返回有效产物目录：{outputDir}"
                    };
                }

                onProgress?.Invoke("服务端完成，准备装配…", 92f);
                return new WholeJobResult
                {
                    Ok = true,
                    OutputDir = outputDir,
                    PrefabName = Path.GetFileName(outputDir.TrimEnd('/', '\\'))
                };
            }
        }

        public void Dispose() { }
    }
}
