using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace CharForge.EditorTools.Services
{
    /// <summary>
    /// 后端进程托管：让"必须先在终端起服务"这一步从用户视野里消失。
    ///
    /// 设计原则（这是插件形态与独立工具的分水岭）：
    /// 1. **先探测，再拉起**——端口已被占用说明用户自己已经跑着一个，直接复用，绝不重复启动。
    /// 2. **输出转发**——子进程 stdout/stderr 实时写进 Unity Console，出问题不用切终端。
    /// 3. **退出即清**——编辑器退出、窗口关闭、用户点停止，三种情况都要 Kill 整棵进程树，
    ///    否则会留下占着显存的孤儿进程（这是同类工具最常见的投诉）。
    /// 4. **失败给下一步**——不抛裸异常，返回带 NextAction 的结果，让窗口能直接渲染成一句人话。
    /// </summary>
    [InitializeOnLoad]
    public static class BackendProcessManager
    {
        public class LaunchProfile
        {
            public string Name = "";
            public string Executable = "";
            public string Arguments = "";
            public string WorkingDirectory = "";
            public int Port;
        }

        private class ManagedProcess
        {
            public Process Process;
            public LaunchProfile Profile;
        }

        private static readonly Dictionary<int, ManagedProcess> Managed = new Dictionary<int, ManagedProcess>();
        private static readonly object Gate = new object();

        static BackendProcessManager()
        {
            EditorApplication.quitting += StopAll;
            AssemblyReloadEvents.beforeAssemblyReload += StopAll;
        }

        // ────────────────────────── 探测 ──────────────────────────

        /// <summary>端口是否有进程在监听。用来区分"已启动"与"未启动"。</summary>
        public static bool IsPortOpen(int port, int timeoutMs = 400)
        {
            try
            {
                using var client = new TcpClient();
                var task = client.ConnectAsync("127.0.0.1", port);
                return task.Wait(timeoutMs) && client.Connected;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>把 URL 里的端口解析出来，解析不到返回 -1。</summary>
        public static int PortFromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return -1;
            try
            {
                var uri = new Uri(url.Contains("://") ? url : "http://" + url);
                return uri.Port;
            }
            catch
            {
                return -1;
            }
        }

        // ────────────────────────── 拉起 ──────────────────────────

        public class EnsureResult
        {
            public bool Running;
            public bool AlreadyWasRunning;
            public string Message = "";
            public string NextAction = "";
        }

        /// <summary>
        /// 确保后端在跑。已在跑 → 直接复用；未在跑且有启动命令 → 代为拉起并等待端口就绪。
        /// </summary>
        public static async Task<EnsureResult> EnsureRunningAsync(
            LaunchProfile profile, Action<string> log, CancellationToken ct, int readyTimeoutSeconds = 60)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));

            if (IsPortOpen(profile.Port))
            {
                log?.Invoke($"端口 {profile.Port} 已有服务在运行，直接复用（不重复启动）。");
                return new EnsureResult
                {
                    Running = true,
                    AlreadyWasRunning = true,
                    Message = $"{profile.Name} 已在运行（端口 {profile.Port}）"
                };
            }

            if (string.IsNullOrWhiteSpace(profile.Executable))
            {
                return new EnsureResult
                {
                    Running = false,
                    Message = $"{profile.Name} 未运行，且未配置启动命令",
                    NextAction = $"在「后端设置」里填写启动命令，或手动启动后点「重新探测」。"
                };
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = profile.Executable,
                    Arguments = profile.Arguments ?? "",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                if (!string.IsNullOrWhiteSpace(profile.WorkingDirectory) && Directory.Exists(profile.WorkingDirectory))
                    psi.WorkingDirectory = profile.WorkingDirectory;

                var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                proc.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) log?.Invoke($"[{profile.Name}] {e.Data}"); };
                proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) log?.Invoke($"[{profile.Name}!] {e.Data}"); };

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                lock (Gate) Managed[profile.Port] = new ManagedProcess { Process = proc, Profile = profile };
                log?.Invoke($"已启动 {profile.Name}（PID {proc.Id}），等待端口 {profile.Port} 就绪…");

                var started = DateTime.UtcNow;
                while ((DateTime.UtcNow - started).TotalSeconds < readyTimeoutSeconds)
                {
                    if (ct.IsCancellationRequested)
                    {
                        Stop(profile.Port);
                        ct.ThrowIfCancellationRequested();
                    }
                    if (proc.HasExited)
                    {
                        return new EnsureResult
                        {
                            Running = false,
                            Message = $"{profile.Name} 启动后立即退出（退出码 {proc.ExitCode}）",
                            NextAction = "请看 Console 里转发的子进程输出，通常是依赖缺失或路径写错。"
                        };
                    }
                    if (IsPortOpen(profile.Port))
                    {
                        log?.Invoke($"{profile.Name} 已就绪。");
                        return new EnsureResult { Running = true, Message = $"{profile.Name} 已启动并就绪" };
                    }
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }

                return new EnsureResult
                {
                    Running = false,
                    Message = $"{profile.Name} 启动后 {readyTimeoutSeconds} 秒内端口仍未就绪",
                    NextAction = "检查 Console 中转发的子进程日志；模型首次加载较慢时可适当调大等待时间。"
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                return new EnsureResult
                {
                    Running = false,
                    Message = $"{profile.Name} 启动失败：{e.Message}",
                    NextAction = "确认「启动命令」写的是可执行文件路径（不是文件夹），必要时填绝对路径。"
                };
            }
        }

        // ────────────────────────── 停止 ──────────────────────────

        /// <summary>停止指定端口上由本插件托管的进程。不是我们启的就不动。</summary>
        public static void Stop(int port)
        {
            ManagedProcess managed;
            lock (Gate)
            {
                if (!Managed.TryGetValue(port, out managed)) return;
                Managed.Remove(port);
            }
            KillTree(managed, $"停止 {managed.Profile.Name}");
        }

        public static void StopAll()
        {
            List<ManagedProcess> snapshot;
            lock (Gate)
            {
                snapshot = new List<ManagedProcess>(Managed.Values);
                Managed.Clear();
            }
            foreach (var m in snapshot) KillTree(m, $"编辑器退出，清理 {m.Profile.Name}");
        }

        public static IEnumerable<string> ManagedNames()
        {
            lock (Gate)
                foreach (var kv in Managed)
                    yield return $"{kv.Value.Profile.Name}（端口 {kv.Key}，PID {kv.Value.Process.Id}）";
        }

        private static void KillTree(ManagedProcess managed, string reason)
        {
            try
            {
                if (managed?.Process == null) return;
                if (managed.Process.HasExited) return;

                // 必须杀整棵树：ComfyUI / uvicorn 都可能再派生 worker，
                // 只杀父进程会留下占着显存的孤儿（这是同类工具最常见的投诉）。
                managed.Process.Kill(entireProcessTree: true);
                managed.Process.WaitForExit(5000);
                Debug.Log($"[CharForge] {reason} 完成。");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CharForge] {reason} 时出错：{e.Message}");
            }
        }

        // ────────────────────────── 组装启动配置 ──────────────────────────

        /// <summary>按当前设置组装 ComfyUI 的启动配置。</summary>
        public static LaunchProfile BuildComfyProfile()
        {
            var profile = new LaunchProfile
            {
                Name = "ComfyUI",
                Executable = "",
                Arguments = "",
                Port = PortFromUrl(CharForgeSettings.ComfyUiUrl),
                WorkingDirectory = ""
            };
            ApplyCommand(profile, CharForgeSettings.ComfyUiLaunchCommand);
            return profile;
        }

        /// <summary>按当前设置组装自带 Python 服务的启动配置。</summary>
        public static LaunchProfile BuildSidecarProfile()
        {
            string dir = CharForgeSettings.SidecarWorkingDir;
            if (string.IsNullOrWhiteSpace(dir))
                dir = Directory.GetParent(Application.dataPath)?.FullName ?? "";

            return new LaunchProfile
            {
                Name = "Python 服务",
                Executable = string.IsNullOrWhiteSpace(CharForgeSettings.SidecarPythonExe)
                    ? "python"
                    : CharForgeSettings.SidecarPythonExe,
                Arguments = "-m server.cli serve",
                WorkingDirectory = dir,
                Port = PortFromUrl(CharForgeSettings.SidecarUrl)
            };
        }

        /// <summary>把"可执行文件 + 参数"写成一行命令拆开；留空表示只探测不代管。</summary>
        private static void ApplyCommand(LaunchProfile profile, string commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine)) return;

            int space = commandLine.IndexOf(' ');
            if (space < 0)
            {
                profile.Executable = commandLine.Trim().Trim('"');
                profile.Arguments = "";
            }
            else
            {
                profile.Executable = commandLine.Substring(0, space).Trim().Trim('"');
                profile.Arguments = commandLine.Substring(space + 1).Trim();
            }
        }
    }
}
