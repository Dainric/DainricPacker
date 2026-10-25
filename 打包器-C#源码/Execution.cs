// Dainric 打包器 · 命令执行层
// 作者: Dainric
//
// 设计要点：命令只在这里构造，且构造成完整的「可执行文件 + 参数」整体。
// 之前把 List<string> 传来传去，导致 exe 同时出现在 exe 位置和参数列表里，
// PyInstaller 直接把可执行文件当成了脚本名。这里从结构上消除这类问题。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Dainric.Packer
{
    /// <summary>一条完整的外部命令：可执行文件与参数不可分离，也无法重复。</summary>
    public sealed class BuildCommand
    {
        public string Exe { get; }
        public IReadOnlyList<string> Args { get; }
        public string WorkingDir { get; }
        public int TimeoutMs { get; }

        private BuildCommand(string exe, IEnumerable<string> args,
                             string workDir, int timeoutMs)
        {
            if (string.IsNullOrWhiteSpace(exe))
                throw new ArgumentException("可执行文件不能为空", nameof(exe));
            Exe = exe;
            Args = (args ?? Enumerable.Empty<string>())
                   .Where(a => a != null).ToList().AsReadOnly();
            WorkingDir = workDir;
            TimeoutMs = timeoutMs > 0 ? timeoutMs : 600_000;

            // 结构性防护：参数里绝不允许再出现可执行文件自身，
            // 否则会被目标程序当成位置参数（PyInstaller 的真实踩坑）。
            var self = Path.GetFileName(Exe);
            foreach (var a in Args)
            {
                if (string.Equals(a, Exe, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileName(a), self,
                                  StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "参数中不应包含可执行文件自身: " + a);
            }
        }

        /// <summary>由「引擎前缀 + 业务参数」构造。前缀里 exe 之后的项是它自己的参数。</summary>
        public static BuildCommand From(string engineExe,
                                        IReadOnlyList<string> engineArgs,
                                        IEnumerable<string> businessArgs,
                                        string workDir, int timeoutMs)
        {
            var all = new List<string>();
            if (engineArgs != null) all.AddRange(engineArgs);
            if (businessArgs != null) all.AddRange(businessArgs);
            return new BuildCommand(engineExe, all, workDir, timeoutMs);
        }

        /// <summary>诊断用：完整命令行（已按需加引号）</summary>
        public string Display()
        {
            var sb = new StringBuilder(Quote(Exe));
            foreach (var a in Args) sb.Append(' ').Append(Quote(a));
            return sb.ToString();
        }

        static string Quote(string s) =>
            s.IndexOf(' ') >= 0 ? "\"" + s + "\"" : s;

        public ProcessStartInfo ToStartInfo()
        {
            var psi = new ProcessStartInfo(Exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            if (!string.IsNullOrEmpty(WorkingDir))
                psi.WorkingDirectory = WorkingDir;
            foreach (var a in Args) psi.ArgumentList.Add(a);
            return psi;
        }
    }

    /// <summary>执行结果</summary>
    public sealed class RunResult
    {
        public int ExitCode { get; set; }          // -1 表示超时
        public string StdOut { get; set; } = "";
        public string StdErr { get; set; } = "";
        public double Seconds { get; set; }
        public bool TimedOut => ExitCode == -1;
        public string Combined => (StdOut + "\n" + StdErr).Trim();
    }

    /// <summary>命令执行器。只接受已经构造好的 BuildCommand，不做任何参数推断。</summary>
    public sealed class CommandRunner
    {
        readonly Action<string, string> _log;     // (文本, 级别)
        Process _current;

        public CommandRunner(Action<string, string> logger) => _log = logger;

        public bool IsRunning => _current != null;

        public void Abort()
        {
            var p = _current;
            if (p == null) return;
            try { if (!p.HasExited) p.Kill(true); } catch { }
        }

        /// <summary>执行并抓取全部输出。可选逐行回调用于实时进度。</summary>
        public RunResult Run(BuildCommand cmd, Action<string> onLine = null)
        {
            var res = new RunResult();
            var sw = Stopwatch.StartNew();
            var so = new StringBuilder();
            var se = new StringBuilder();

            using (var p = new Process { StartInfo = cmd.ToStartInfo() })
            {
                // 每行都记录，便于失败诊断
                p.OutputDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    so.AppendLine(e.Data);
                    onLine?.Invoke(e.Data);
                };
                p.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    se.AppendLine(e.Data);
                    onLine?.Invoke(e.Data);
                };

                try
                {
                    p.Start();
                }
                catch (Exception ex)
                {
                    res.StdErr = "启动失败: " + ex.Message;
                    res.ExitCode = -2;
                    res.Seconds = sw.Elapsed.TotalSeconds;
                    return res;
                }

                // 必须在 Start 之后再设：Process.Start() 会把它重置为 false，
                // 否则 WaitForExit 要等到超时才返回（实测慢 250 倍）。
                try { p.EnableRaisingEvents = true; } catch { }

                _current = p;
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                bool exited = p.WaitForExit(cmd.TimeoutMs);
                if (!exited)
                {
                    _log?.Invoke("命令超时，正在终止进程树…", "warn");
                    try { p.Kill(true); } catch { }
                    try { p.WaitForExit(5000); } catch { }
                    res.ExitCode = -1;
                }
                else
                {
                    // 关键：WaitForExit(int) 只等进程退出，不等异步输出读完。
                    // 短命进程会在这里丢掉最后几行（表现为输出为空）。
                    // 无参 WaitForExit() 会一直等到重定向流彻底读干净。
                    try { p.WaitForExit(); } catch { }
                    try { res.ExitCode = p.ExitCode; }
                    catch { res.ExitCode = -3; }
                }
                _current = null;
            }

            res.StdOut = so.ToString();
            res.StdErr = se.ToString();
            res.Seconds = sw.Elapsed.TotalSeconds;
            return res;
        }
    }

    /// <summary>打包引擎所在位置与版本</summary>
    public sealed class EngineInfo
    {
        public bool Available { get; set; }
        public string Exe { get; set; } = "";
        public List<string> PreArgs { get; set; } = new List<string>();
        public string Version { get; set; } = "";
        public string Source { get; set; } = "";   // 内置运行时 / 系统 Python / CLI
        public string Detail { get; set; } = "";

        public BuildCommand Pack(string workDir, IEnumerable<string> businessArgs,
                                 int timeoutMs) =>
            BuildCommand.From(Exe, PreArgs, businessArgs, workDir, timeoutMs);
    }
}
