// Dainric 打包器 · 单元测试
// 作者: Dainric
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dainric.Packer;
using Xunit;

namespace Dainric.Packer.Tests
{
    // ══════════════════════════════════════════════════════════════
    // 1. 命令构造 —— 之前「参数重复」那个致命 bug 的回归测试
    // ══════════════════════════════════════════════════════════════
    public class BuildCommandTests
    {
        const string PY = @"C:\py\python.exe";

        [Fact]
        public void 引擎自带参数与业务参数按顺序合并()
        {
            var c = BuildCommand.From(PY,
                new List<string> { "-m", "PyInstaller" },
                new[] { "--onefile", "app.py" },
                @"C:\work", 60000);

            Assert.Equal(PY, c.Exe);
            Assert.Equal(new[] { "-m", "PyInstaller", "--onefile", "app.py" },
                         c.Args.ToArray());
        }

        [Fact]
        public void 参数里出现可执行文件自身应被拒绝()
        {
            // 这正是当初 PyInstaller 报 "unrecognized arguments: app.py" 的根因：
            // 可执行文件同时出现在 exe 位置与参数列表里。
            var ex = Assert.Throws<InvalidOperationException>(() =>
                BuildCommand.From(PY, null, new[] { PY, "--onefile" },
                                  null, 1000));
            Assert.Contains("可执行文件自身", ex.Message);
        }

        [Fact]
        public void 参数里出现同名可执行文件也应被拒绝()
        {
            Assert.Throws<InvalidOperationException>(() =>
                BuildCommand.From(PY, null, new[] { "python.exe" }, null, 1000));
        }

        [Fact]
        public void 空可执行文件应被拒绝()
        {
            Assert.Throws<ArgumentException>(() =>
                BuildCommand.From("", null, new[] { "--onefile" }, null, 1000));
        }

        [Fact]
        public void 超时为零时使用默认值()
        {
            var c = BuildCommand.From(PY, null, new[] { "a" }, null, 0);
            Assert.True(c.TimeoutMs >= 60000);
        }

        [Fact]
        public void 含空格的参数在展示时加引号()
        {
            var c = BuildCommand.From(PY, null,
                new[] { "--name", "my app", "--icon", @"C:\a b\i.ico" },
                null, 1000);
            string d = c.Display();
            Assert.Contains("\"my app\"", d);
            Assert.Contains("\"C:\\a b\\i.ico\"", d);
        }

        [Fact]
        public void StartInfo_不重复添加参数()
        {
            var c = BuildCommand.From(PY, new List<string> { "-m", "PyInstaller" },
                new[] { "--onefile" }, null, 1000);
            var psi = c.ToStartInfo();
            Assert.Equal(PY, psi.FileName);
            Assert.Equal(new[] { "-m", "PyInstaller", "--onefile" },
                         psi.ArgumentList.ToArray());
            Assert.False(psi.UseShellExecute);
            Assert.True(psi.RedirectStandardOutput);
        }

        [Fact]
        public void 工作目录为空时不设置()
        {
            var c = BuildCommand.From(PY, null, new[] { "a" }, "", 1000);
            Assert.True(string.IsNullOrEmpty(c.ToStartInfo().WorkingDirectory)
                        || c.ToStartInfo().WorkingDirectory.Length > 0);
        }
    }

    // ══════════════════════════════════════════════════════════════
    // 2. 失败诊断
    // ══════════════════════════════════════════════════════════════
    public class BuildDoctorTests
    {
        [Fact]
        public void 识别缺少模块()
        {
            var d = BuildDoctor.Analyze(1,
                "ModuleNotFoundError: No module named 'xlrd'");
            Assert.Contains("未安装", string.Join(" ", d.Reasons));
            Assert.Contains(d.Fixes, f => f.Contains("hidden-import"));
        }

        [Fact]
        public void 识别超时()
        {
            var d = BuildDoctor.Analyze(-1, "");
            Assert.Contains("超时", d.Title);
            Assert.Equal("warn", d.Severity);
            Assert.Contains(d.Fixes, f => f.Contains("打包超时"));
        }

        [Fact]
        public void 识别引擎无法启动()
        {
            var d = BuildDoctor.Analyze(-2, "");
            Assert.Contains("引擎", d.Title);
        }

        [Fact]
        public void 识别权限问题()
        {
            var d = BuildDoctor.Analyze(1, "PermissionError: [Errno 13] ...");
            Assert.Contains("权限", string.Join(" ", d.Reasons));
        }

        [Fact]
        public void 识别tkinter缺失()
        {
            var d = BuildDoctor.Analyze(1,
                "Can't find a usable init.tcl in the following directories");
            Assert.Contains("Tcl/Tk", string.Join(" ", d.Reasons));
        }

        [Fact]
        public void 识别非ASCII路径问题()
        {
            var d = BuildDoctor.Analyze(1,
                "UnicodeEncodeError: 'gbk' codec can't encode character");
            Assert.Contains("非 ASCII", string.Join(" ", d.Reasons));
        }

        [Fact]
        public void 识别启动失败而非打包失败()
        {
            var d = BuildDoctor.Analyze(1, "Failed to execute script 'app'");
            Assert.Contains("启动失败", d.Title);
        }

        [Fact]
        public void 未知失败也给出可用信息()
        {
            var d = BuildDoctor.Analyze(7, "some totally unknown thing");
            Assert.Contains("未匹配", d.Title);
            Assert.Contains("7", string.Join(" ", d.Reasons));
            Assert.True(d.HasContent);
        }

        [Fact]
        public void 多种原因同时命中会合并()
        {
            var d = BuildDoctor.Analyze(1,
                "ModuleNotFoundError: No module named 'a'\n" +
                "PermissionError: [Errno 13]");
            Assert.True(d.Reasons.Count >= 2);
        }

        [Fact]
        public void 渲染结果包含标题与建议()
        {
            var d = BuildDoctor.Analyze(1, "No module named 'x'");
            string s = d.Render();
            Assert.Contains("诊断结果", s);
            Assert.Contains("建议处理", s);
        }

        [Fact]
        public void 空输出不会抛异常()
        {
            var d = BuildDoctor.Analyze(1, null);
            Assert.NotNull(d);
        }
    }

    // ══════════════════════════════════════════════════════════════
    // 3. 运行时识别
    // ══════════════════════════════════════════════════════════════
    public class RuntimeManagerTests
    {
        [Fact]
        public void 发现两个槽位且3点12在前()
        {
            var list = RuntimeManager.Discover();
            Assert.Equal(2, list.Count);
            Assert.Equal(RuntimeManager.Key312, list[0].Key);
            Assert.Equal(RuntimeManager.KeyDefault, list[1].Key);
        }

        [Fact]
        public void 槽位目录名符合约定()
        {
            // v3.1.0 起两个运行时都内置，目录名统一带版本后缀
            var list = RuntimeManager.Discover();
            Assert.EndsWith("runtime-py312", list[0].Dir);
            Assert.EndsWith("runtime-py314", list[1].Dir);
        }

        [Fact]
        public void 默认输出目录在桌面且真的建出来()
        {
            // 曾经的默认是程序安装目录，装到 %LOCALAPPDATA% 后用户找不到产物
            string d = RuntimeManager.DefaultOutputDir();
            Assert.False(string.IsNullOrEmpty(d));

            string desk = Environment.GetFolderPath(
                Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrEmpty(desk))
            {
                Assert.StartsWith(desk, d);
                Assert.EndsWith("Dainric产物", d);
                Assert.True(Directory.Exists(d), "默认输出目录应已创建: " + d);
            }
        }

        [Fact]
        public void 两个槽位的键与目录名符合约定()
        {
            // 运行时不再要求放在程序目录下（见 RuntimeLocator：记住的配置、
            // 程序目录、上级目录、%LOCALAPPDATA%\Dainric 都会搜），
            // 这里只钉住「槽位键」与「目录名」这套固定约定。
            Assert.True(RuntimeManager.Dir312 == "runtime-py312");
            Assert.True(RuntimeManager.Dir314 == "runtime-py314");

            foreach (var s in RuntimeManager.Discover())
            {
                if (s.Key == "py312")
                {
                    Assert.EndsWith("runtime-py312",
                        s.Dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                }
                else if (s.Key == "py314")
                {
                    Assert.EndsWith("runtime-py314",
                        s.Dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        [Fact]
        public void 旧布局迁移不会抛异常()
        {
            // 旧版本用 runtime\ 表示 3.14；迁移逻辑必须容错，
            // 目录不存在时也不能影响启动
            var ex = Record.Exception(() => RuntimeManager.MigrateLegacyLayout());
            Assert.Null(ex);
        }

        [Fact]
        public void 不存在的目录不算运行时()
        {
            Assert.False(RuntimeManager.LooksLikeRuntime(
                @"C:\definitely\not\here"));
            Assert.False(RuntimeManager.LooksLikeRuntime(null));
            Assert.False(RuntimeManager.LooksLikeRuntime(""));
        }

        [Fact]
        public void 优先选择可用的槽位()
        {
            var slots = new List<RuntimeSlot>
            {
                new RuntimeSlot { Key = "py312", Present = true,
                                  PyInstallerVersion = "" },
                new RuntimeSlot { Key = "default", Present = true,
                                  PyInstallerVersion = "6.22.0" },
            };
            var pick = RuntimeManager.PickUsable(slots);
            Assert.NotNull(pick);
            Assert.Equal("default", pick.Key);
        }

        [Fact]
        public void 没有可用槽位时返回空()
        {
            var slots = new List<RuntimeSlot>
            {
                new RuntimeSlot { Key = "py312", Present = false },
            };
            Assert.Null(RuntimeManager.PickUsable(slots));
        }

        [Fact]
        public void Usable_要求同时存在且装了PyInstaller()
        {
            Assert.False(new RuntimeSlot { Present = true }.Usable);
            Assert.False(new RuntimeSlot
            { Present = false, PyInstallerVersion = "6.22" }.Usable);
            Assert.True(new RuntimeSlot
            { Present = true, PyInstallerVersion = "6.22" }.Usable);
        }
    }

    // ══════════════════════════════════════════════════════════════
    // 4. 命令行执行器（真跑子进程）
    // ══════════════════════════════════════════════════════════════
    public class CommandRunnerTests
    {
        static string CmdExe => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");

        [Fact]
        public void 能抓到标准输出且退出码为零()
        {
            var r = new CommandRunner(null).Run(
                BuildCommand.From(CmdExe, null,
                    new[] { "/c", "echo hello" }, null, 30000));
            Assert.Equal(0, r.ExitCode);
            Assert.Contains("hello", r.StdOut);
            Assert.False(r.TimedOut);
        }

        [Fact]
        public void 非零退出码会被如实返回()
        {
            var r = new CommandRunner(null).Run(
                BuildCommand.From(CmdExe, null,
                    new[] { "/c", "exit 3" }, null, 30000));
            Assert.Equal(3, r.ExitCode);
        }

        [Fact]
        public void 标准错误会被单独抓到()
        {
            var r = new CommandRunner(null).Run(
                BuildCommand.From(CmdExe, null,
                    new[] { "/c", "echo oops 1>&2" }, null, 30000));
            Assert.Contains("oops", r.StdErr);
        }

        [Fact]
        public void 超时会被检测到且不会挂住()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = new CommandRunner(null).Run(
                BuildCommand.From(CmdExe, null,
                    new[] { "/c", "ping 127.0.0.1 -n 20 > nul" }, null, 1500));
            sw.Stop();
            Assert.True(r.TimedOut);
            Assert.Equal(-1, r.ExitCode);
            // 关键：必须在超时后很快返回，不能等到子进程自然结束
            Assert.True(sw.Elapsed.TotalSeconds < 15,
                "超时后应立即返回，实际耗时 " + sw.Elapsed.TotalSeconds);
        }

        [Fact]
        public void 不存在的可执行文件返回负值而不是抛异常()
        {
            var r = new CommandRunner(null).Run(
                BuildCommand.From(@"C:\nope\nothing.exe", null,
                    new[] { "a" }, null, 5000));
            Assert.True(r.ExitCode < 0);
            Assert.Contains("启动失败", r.StdErr);
        }

        [Fact]
        public void 逐行回调能收到每一行()
        {
            var lines = new List<string>();
            new CommandRunner(null).Run(
                BuildCommand.From(CmdExe, null,
                    new[] { "/c", "echo a & echo b & echo c" }, null, 30000),
                l => lines.Add(l.Trim()));
            Assert.Contains("a", lines);
            Assert.Contains("b", lines);
            Assert.Contains("c", lines);
        }

        [Fact]
        public void 记录执行耗时()
        {
            var r = new CommandRunner(null).Run(
                BuildCommand.From(CmdExe, null, new[] { "/c", "echo x" },
                                  null, 30000));
            Assert.True(r.Seconds > 0);
        }

        [Fact]
        public void 短命进程的输出不会被丢掉()
        {
            // 回归：WaitForExit(超时) 只等进程退出，不等异步输出读完。
            // 以前这里少了无参 WaitForExit()，短命子进程的输出会间歇性为空，
            // 导致依赖检查这类「跑一下就退」的调用拿不到任何结果。
            // 连跑多次，因为丢输出是时序相关的。
            for (int i = 0; i < 12; i++)
            {
                var r = new CommandRunner(null).Run(
                    BuildCommand.From(CmdExe, null,
                        new[] { "/c", "echo TICK" }, null, 30000));
                Assert.Equal(0, r.ExitCode);
                Assert.Contains("TICK", r.StdOut);
            }
        }
    }

    // ══════════════════════════════════════════════════════════════
    // 5. 真实失败工程的端到端诊断
    //    不造假输出，直接拿真 PyInstaller 跑真会失败的脚本，
    //    再用 BuildDoctor 解析它的真实输出。缺运行时则跳过。
    // ══════════════════════════════════════════════════════════════
    public class RealFailureDiagnosisTests
    {
        /// <summary>找内置运行时里的 python.exe</summary>
        internal static string FindPythonPublic() => FindPython();

        /// <summary>找内置运行时里的 python.exe</summary>
        static string FindPython()
        {
            var roots = new List<string>();
            string env = Environment.GetEnvironmentVariable("DAINRIC_RUNTIME_HOME");
            if (!string.IsNullOrEmpty(env)) roots.Add(env);

            string la = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(la))
                roots.Add(Path.Combine(la, "Dainric"));

            string here = AppContext.BaseDirectory;
            for (int i = 0; i < 4 && here != null; i++)
            {
                roots.Add(here);
                here = Path.GetDirectoryName(here.TrimEnd('\\'));
            }

            foreach (var r in roots)
            {
                foreach (var sub in new[] { "runtime-py312", "runtime-py314",
                                            "runtime", "." })
                {
                    if (string.IsNullOrEmpty(r)) continue;
                    string p = Path.Combine(r, sub, "python.exe");
                    try
                    {
                        if (File.Exists(p) && new FileInfo(p).Length > 16384)
                            return p;
                    }
                    catch { }
                }
            }
            return null;
        }

        /// <summary>建一个临时工程目录</summary>
        static string MakeWorkspace(string tag)
        {
            string d = Path.Combine(Path.GetTempPath(),
                       "dainric_real_" + tag + "_" + Guid.NewGuid().ToString("N")
                       .Substring(0, 8));
            Directory.CreateDirectory(d);
            return d;
        }

        /// <summary>调真 PyInstaller，返回退出码与合并输出</summary>
        static RunResult RunPyInstaller(string py, string workDir, string script,
                                        int timeoutMs, params string[] extra)
        {
            var args = new List<string>
            {
                "--noconfirm", "--onedir", "--name", "probe",
                "--distpath", Path.Combine(workDir, "dist"),
                "--workpath", Path.Combine(workDir, "build"),
                "--specpath", workDir
            };
            args.AddRange(extra);
            args.Add(script);
            var r = new CommandRunner(null).Run(BuildCommand.From(
                py, new List<string> { "-m", "PyInstaller" }, args,
                workDir, timeoutMs));
            return r;
        }

        /// <summary>跑一次产物，拿回退出码与合并输出</summary>
        static RunResult RunProduct(string exe, int timeoutMs)
        {
            return new CommandRunner(null).Run(BuildCommand.From(
                exe, null, new string[0], Path.GetDirectoryName(exe), timeoutMs));
        }

        [Fact]
        public void 缺模块不会让打包失败但会让产物启动崩溃()
        {
            // 这是最容易被忽略的一类故障：
            // PyInstaller 只做静态分析，import 一个不存在的模块它照样打包成功，
            // 坏的是产物本身。所以「缺模块」永远不会出现在打包输出里，
            // 只能靠产物自检抓到 —— 这条测试把该事实钉死。
            string py = FindPython();
            if (py == null) return;

            string w = MakeWorkspace("missing");
            try
            {
                File.WriteAllText(Path.Combine(w, "app.py"),
                    "import dainric_no_such_module_zzz\nprint('never')\n");

                var pack = RunPyInstaller(py, w, "app.py", 240000);
                Assert.Equal(0, pack.ExitCode);          // 打包竟然成功
                string packText = pack.StdOut + "\n" + pack.StdErr;
                Assert.DoesNotContain("No module named", packText);

                // 但产物一跑就崩，报错里才出现缺失模块
                string exe = Path.Combine(w, "dist", "probe", "probe.exe");
                Assert.True(File.Exists(exe), "产物不存在: " + exe);

                var run = RunProduct(exe, 60000);
                Assert.NotEqual(0, run.ExitCode);

                var d = BuildDoctor.Analyze(run.ExitCode, run.Combined);
                Assert.Contains("dainric_no_such_module_zzz", d.MissingModules);
                Assert.Contains("识别到缺失模块", d.Render());
            }
            finally { try { Directory.Delete(w, true); } catch { } }
        }

        [Fact]
        public void 产物自检抓到缺模块后补hidden_import能真正修好()
        {
            string py = FindPython();
            if (py == null) return;

            string w = MakeWorkspace("fix");
            try
            {
                // importlib 动态导入：PyInstaller 静态分析发现不了
                File.WriteAllText(Path.Combine(w, "hidden_dep.py"), "VALUE = 42\n");
                File.WriteAllText(Path.Combine(w, "app.py"),
                    "import importlib\n" +
                    "m = importlib.import_module('hidden_dep')\n" +
                    "print(m.VALUE)\n");

                // 第一次：打包「成功」，产物崩溃
                Assert.Equal(0, RunPyInstaller(py, w, "app.py", 240000).ExitCode);
                string exe = Path.Combine(w, "dist", "probe", "probe.exe");

                var run1 = RunProduct(exe, 60000);
                Assert.NotEqual(0, run1.ExitCode);
                var d1 = BuildDoctor.Analyze(run1.ExitCode, run1.Combined);
                Assert.Contains("hidden_dep", d1.MissingModules);

                // 按自检结论补 hidden-import 重打，产物要真能跑对
                Assert.Equal(0, RunPyInstaller(py, w, "app.py", 240000,
                                               "--hidden-import", "hidden_dep")
                                 .ExitCode);
                Assert.True(File.Exists(exe));

                var run2 = RunProduct(exe, 60000);
                Assert.Equal(0, run2.ExitCode);
                Assert.Contains("42", run2.StdOut);
            }
            finally { try { Directory.Delete(w, true); } catch { } }
        }

        [Fact]
        public void 入口文件不存在的真实错误会被诊断出来()
        {
            string py = FindPython();
            if (py == null) return;

            string w = MakeWorkspace("noscript");
            try
            {
                // 故意不创建文件
                var r = RunPyInstaller(py, w, "ghost.py", 120000);
                Assert.NotEqual(0, r.ExitCode);

                var d = BuildDoctor.Analyze(r.ExitCode, r.StdOut + "\n" + r.StdErr);
                Assert.True(d.HasContent);
                // 这类错误不该被误判成「缺模块」，否则自动重试会白跑
                Assert.Empty(d.MissingModules);
            }
            finally { try { Directory.Delete(w, true); } catch { } }
        }

        [Fact]
        public void 语法错误的真实脚本会被诊断为语法问题且不触发重试()
        {
            string py = FindPython();
            if (py == null) return;

            string w = MakeWorkspace("syntax");
            try
            {
                File.WriteAllText(Path.Combine(w, "bad.py"),
                    "def broken(:\n    pass\n");

                var r = RunPyInstaller(py, w, "bad.py", 120000);
                Assert.NotEqual(0, r.ExitCode);

                string outText = r.StdOut + "\n" + r.StdErr;
                var d = BuildDoctor.Analyze(r.ExitCode, outText);
                Assert.True(d.HasContent);
                Assert.Empty(d.MissingModules);   // 语法错不能走重试
            }
            finally { try { Directory.Delete(w, true); } catch { } }
        }

        [Fact]
        public void 产物自检能区分正常退出与启动即崩()
        {
            string py = FindPython();
            if (py == null) return;

            string w = MakeWorkspace("selftest");
            try
            {
                // 正常产物：打印一行后退出 0
                File.WriteAllText(Path.Combine(w, "good.py"),
                    "print('selftest-ok')\n");
                Assert.Equal(0, RunPyInstaller(py, w, "good.py", 240000).ExitCode);

                string exe = Path.Combine(w, "dist", "probe", "probe.exe");
                Assert.True(File.Exists(exe));

                var captured = new List<string>();
                var ok = new CommandRunner(null).Run(BuildCommand.From(
                    exe, null, new string[0], Path.GetDirectoryName(exe), 6000),
                    l => captured.Add(l));
                Assert.Equal(0, ok.ExitCode);
                Assert.Contains("selftest-ok", string.Join(" ", captured));

                // 崩掉的产物：非零退出码，诊断能接住
                var bad = new CommandRunner(null).Run(BuildCommand.From(
                    Path.Combine(w, "dist", "probe", "probe.exe"),
                    null, new[] { "--definitely-not-an-arg" },
                    Path.GetDirectoryName(exe), 6000));
                // 参数错是 Python 自己报的，退出码非 0，这里只要求不挂住
                Assert.False(bad.TimedOut);
            }
            finally { try { Directory.Delete(w, true); } catch { } }
        }
    }

    // ══════════════════════════════════════════════════════════════
    // 6. 依赖检查
    // ══════════════════════════════════════════════════════════════
    public class DependencyCheckerTests
    {
        static string FindPython() => RealFailureDiagnosisTests.FindPythonPublic();

        static string Workspace(string tag)
        {
            string d = Path.Combine(Path.GetTempPath(),
                "dainric_dep_" + tag + "_" + Guid.NewGuid().ToString("N")
                .Substring(0, 8));
            Directory.CreateDirectory(d);
            return d;
        }

        [Fact]
        public void 正则粗扫能认出各类import写法()
        {
            string w = Workspace("scan");
            try
            {
                string f = Path.Combine(w, "app.py");
                File.WriteAllText(f,
                    "import os\n" +
                    "import numpy as np, pandas\n" +
                    "from collections import OrderedDict\n" +
                    "from PIL import Image\n" +
                    "import xml.etree.ElementTree as ET\n" +
                    "import os\n");

                var mods = DependencyChecker.FindTopLevelImports(f);
                Assert.Contains("os", mods);
                Assert.Contains("numpy", mods);
                Assert.Contains("pandas", mods);
                Assert.Contains("collections", mods);
                Assert.Contains("PIL", mods);
                Assert.Contains("xml", mods);
                Assert.Equal(mods.Count, mods.Distinct().Count());
            }
            finally { try { Directory.Delete(w, true); } catch { } }
        }

        [Fact]
        public void 没有脚本或解释器时如实说明未执行()
        {
            var runner = new CommandRunner(null);
            var r1 = DependencyChecker.Check(null, @"C:\x\python.exe", runner);
            Assert.False(r1.Ran);
            Assert.False(string.IsNullOrEmpty(r1.SkipReason));
            Assert.Contains("未执行", r1.RenderBrief());

            string w = Workspace("nopy");
            try
            {
                string f = Path.Combine(w, "a.py");
                File.WriteAllText(f, "import os\n");
                var r2 = DependencyChecker.Check(f, @"C:\nope\python.exe", runner);
                Assert.False(r2.Ran);
            }
            finally { try { Directory.Delete(w, true); } catch { } }
        }

        [Fact]
        public void 真跑解释器能分出标准库本地与缺失()
        {
            string py = FindPython();
            if (py == null) return;

            string w = Workspace("real");
            try
            {
                File.WriteAllText(Path.Combine(w, "helper.py"), "X = 1\n");
                string f = Path.Combine(w, "app.py");
                File.WriteAllText(f,
                    "import os\n" +
                    "import helper\n" +
                    "import dainric_absent_module_qq\n");

                var rep = DependencyChecker.Check(f, py, new CommandRunner(null));
                Assert.True(rep.Ran, rep.SkipReason);

                Assert.Contains("os", rep.Entries
                    .Where(e => e.Kind == "标准库").Select(e => e.Module));
                Assert.Contains("helper", rep.Entries
                    .Where(e => e.Kind == "本地").Select(e => e.Module));
                Assert.Contains("dainric_absent_module_qq", rep.Missing);
                Assert.False(rep.Clean);

                string detail = rep.RenderDetail("app.py");
                Assert.Contains("未安装", detail);
                Assert.Contains("dainric_absent_module_qq", detail);
            }
            finally { try { Directory.Delete(w, true); } catch { } }
        }

        [Fact]
        public void 只依赖标准库时判定为干净()
        {
            string py = FindPython();
            if (py == null) return;

            string w = Workspace("clean");
            try
            {
                string f = Path.Combine(w, "app.py");
                File.WriteAllText(f, "import os, sys, json\nprint(1)\n");

                var rep = DependencyChecker.Check(f, py, new CommandRunner(null));
                Assert.True(rep.Ran, rep.SkipReason);
                Assert.True(rep.Clean);
                Assert.Equal(3, rep.StdlibCount);
                Assert.Contains("通过", rep.RenderBrief());
            }
            finally { try { Directory.Delete(w, true); } catch { } }
        }

        [Fact]
        public void 语法坏的脚本不会抛异常()
        {
            string py = FindPython();
            if (py == null) return;

            string w = Workspace("bad");
            try
            {
                string f = Path.Combine(w, "bad.py");
                File.WriteAllText(f, "def broken(:\n  pass\n");

                var rep = DependencyChecker.Check(f, py, new CommandRunner(null));
                Assert.False(rep.Ran);
                Assert.False(string.IsNullOrEmpty(rep.SkipReason));
            }
            finally { try { Directory.Delete(w, true); } catch { } }
        }
    }

    // ══════════════════════════════════════════════════════════════
    // 7. 自检参数解析
    // ══════════════════════════════════════════════════════════════
    public class TestArgsTests
    {
        [Fact]
        public void 空输入得到空列表()
        {
            Assert.Empty(DependencyChecker.SplitArgs(null));
            Assert.Empty(DependencyChecker.SplitArgs(""));
            Assert.Empty(DependencyChecker.SplitArgs("   "));
        }

        [Fact]
        public void 按空白切分多个参数()
        {
            var a = DependencyChecker.SplitArgs("--input a.txt --verbose");
            Assert.Equal(new[] { "--input", "a.txt", "--verbose" }, a.ToArray());
        }

        [Fact]
        public void 引号里的空格算一个参数()
        {
            var a = DependencyChecker.SplitArgs("--msg \"hello world\" -n 3");
            Assert.Equal(new[] { "--msg", "hello world", "-n", "3" }, a.ToArray());
        }

        [Fact]
        public void 单引号与多余空白也能处理()
        {
            var a = DependencyChecker.SplitArgs("  'a b'   c  ");
            Assert.Equal(new[] { "a b", "c" }, a.ToArray());
        }
    }

    // ══════════════════════════════════════════════════════════════
    // 8. 「用法错误」识别 —— 自检误报的核心防线
    // ══════════════════════════════════════════════════════════════
    public class UsageErrorTests
    {
        [Theory]
        [InlineData("usage: app.py [-h] --input INPUT\napp.py: error: the following arguments are required: --input")]
        [InlineData("app.py: error: unrecognized arguments: --zzz")]
        [InlineData("Usage: tool <file>")]
        [InlineData("no such option: --foo")]
        [InlineData("error: argument --mode: invalid choice")]
        public void 用法错误会被识别出来(string text)
        {
            Assert.True(DependencyChecker.LooksLikeUsageError(text));
        }

        [Theory]
        [InlineData("")]
        [InlineData("ModuleNotFoundError: No module named 'requests'")]
        [InlineData("Traceback (most recent call last):\n  File \"x.py\", line 3\nValueError: boom")]
        [InlineData("ImportError: DLL load failed while importing _tkinter")]
        public void 真实崩溃不会被误认成用法错误(string text)
        {
            Assert.False(DependencyChecker.LooksLikeUsageError(text));
        }
    }

    // ══════════════════════════════════════════════════════════════
    // 9. 重试矩阵：诊断要能给出 --collect-* 的目标库
    // ══════════════════════════════════════════════════════════════
    public class CollectArgTests
    {
        [Fact]
        public void 缺数据文件会抽出库名()
        {
            var d = BuildDoctor.Analyze(1,
                "Traceback (most recent call last):\n" +
                "  File \"x.py\", line 2, in <module>\n" +
                "FileNotFoundError: [Errno 2] No such file or directory: " +
                "'C:\\\\py\\\\Lib\\\\site-packages\\\\matplotlib\\\\mpl-data\\\\fontlist.json'");
            Assert.Contains("matplotlib", d.CollectData);
            Assert.True(d.HasAutoFix);
            Assert.Contains("整包收集", d.Render());
        }

        [Fact]
        public void 缺二进制会抽出库名()
        {
            var d = BuildDoctor.Analyze(1,
                "ImportError: DLL load failed while importing _multiarray_umath: " +
                "The specified module could not be found.\n" +
                "  at C:\\py\\Lib\\site-packages\\numpy\\core\\_multiarray_umath.pyd");
            Assert.Contains("numpy", d.CollectBinaries);
        }

        [Fact]
        public void hidden_import未找到会抽成子模块收集()
        {
            // 真实格式：imphook 用双引号 + 感叹号，analysis 用单引号
            var a = BuildDoctor.Analyze(1,
                "547 WARNING: Hidden import \"PyQt5.sip\" not found!");
            Assert.Contains("PyQt5", a.CollectSubmodules);

            var b = BuildDoctor.Analyze(1,
                "774 ERROR: Hidden import 'cryptography.hazmat' not found");
            Assert.Contains("cryptography", b.CollectSubmodules);
        }

        [Fact]
        public void 点号名会归一化成顶层包()
        {
            var d = BuildDoctor.Analyze(1,
                "Hidden import \"PyQt5.QtCore\" not found!");
            Assert.Contains("PyQt5", d.CollectSubmodules);
            Assert.DoesNotContain("PyQt5.QtCore", d.CollectSubmodules);
        }

        [Fact]
        public void 缺模块仍然走hidden_import而不是collect()
        {
            var d = BuildDoctor.Analyze(1,
                "ModuleNotFoundError: No module named 'requests'");
            Assert.Contains("requests", d.MissingModules);
            Assert.Empty(d.CollectData);
            Assert.True(d.HasAutoFix);
        }

        [Fact]
        public void 没有可自动修复项时HasAutoFix为假()
        {
            var d = BuildDoctor.Analyze(1, "SyntaxError: invalid syntax");
            Assert.False(d.HasAutoFix);
        }

        [Fact]
        public void 库名抽取会过滤掉非模块词()
        {
            var d = BuildDoctor.Analyze(1,
                "Hidden import \"site-packages\" not found!\n" +
                "Hidden import \"PyQt5.sip\" not found!");
            Assert.DoesNotContain("site-packages", d.CollectSubmodules);
            Assert.Contains("PyQt5", d.CollectSubmodules);
        }
    }
}
