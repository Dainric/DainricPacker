// Dainric 打包器 · 命令行模式
// 作者: Dainric
//
// 给自动化 / CI 用的无界面入口，例如：
//   Dainric打包器.exe --build app.py --out dist --onedir --json
//   Dainric打包器.exe --build app.py --hidden-import pandas._libs.tslibs
//   Dainric打包器.exe --build app.py --sign-pfx cert.pfx --sign-password ***
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Dainric.Packer
{
    public static class CliRunner
    {
        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(uint dwProcessId);
        const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

        public static bool IsCli(string[] args)
        {
            if (args == null || args.Length == 0) return false;
            string a = (args[0] ?? "").ToLowerInvariant();
            return a == "--build" || a == "-b" || a == "--help" || a == "-h"
                || a == "--version";
        }

        sealed class CliOptions
        {
            public string Script, OutDir, Name, Icon, Window = "console", AppVersion;
            public string SignPfx, SignPassword, TimestampUrl;
            public bool OneDir, NoSelfTest, Json;
            public int Timeout = 1800;
            public List<string> Hidden = new List<string>();
            public List<string> Exclude = new List<string>();
            public List<string> AddData = new List<string>();
            public List<string> Extra = new List<string>();
        }

        public static int Run(string[] args)
        {
            try { AttachConsole(ATTACH_PARENT_PROCESS); } catch { }
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            var o = new CliOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string a = (args[i] ?? "").ToLowerInvariant();
                switch (a)
                {
                    case "--build":
                    case "-b":
                        o.Script = Next(args, ref i); break;
                    case "--out":
                    case "-o":
                        o.OutDir = Next(args, ref i); break;
                    case "--name":
                        o.Name = Next(args, ref i); break;
                    case "--icon":
                        o.Icon = Next(args, ref i); break;
                    case "--onedir":
                        o.OneDir = true; break;
                    case "--onefile":
                        o.OneDir = false; break;
                    case "--console":
                        o.Window = "console"; break;
                    case "--noconsole":
                        o.Window = "noconsole"; break;
                    case "--window":
                        o.Window = (Next(args, ref i) ?? "console").ToLowerInvariant(); break;
                    case "--app-version":
                        o.AppVersion = Next(args, ref i); break;
                    case "--hidden-import":
                        AddModules(o.Hidden, Next(args, ref i)); break;
                    case "--exclude-module":
                        AddModules(o.Exclude, Next(args, ref i)); break;
                    case "--add-data":
                        { string v = Next(args, ref i); if (!string.IsNullOrWhiteSpace(v)) o.AddData.Add(v); }
                        break;
                    case "--extra":
                        { string v = Next(args, ref i); if (!string.IsNullOrWhiteSpace(v)) o.Extra.Add(v); }
                        break;
                    case "--timeout":
                        if (int.TryParse(Next(args, ref i), out int t))
                            o.Timeout = Math.Max(60, Math.Min(86400, t));
                        break;
                    case "--no-selftest":
                        o.NoSelfTest = true; break;
                    case "--json":
                        o.Json = true; break;
                    case "--sign-pfx":
                        o.SignPfx = Next(args, ref i); break;
                    case "--sign-password":
                        o.SignPassword = Next(args, ref i); break;
                    case "--timestamp-url":
                        o.TimestampUrl = Next(args, ref i); break;
                    case "--help":
                    case "-h":
                        PrintHelp(); return 0;
                    case "--version":
                        Console.WriteLine("Dainric 打包器 v" + AppVersionNumber());
                        return 0;
                    default:
                        Console.Error.WriteLine("未知参数: " + args[i]);
                        PrintHelp();
                        return 2;
                }
            }

            if (string.IsNullOrWhiteSpace(o.Script))
            {
                Console.Error.WriteLine("缺少 --build <脚本.py>");
                PrintHelp();
                return 2;
            }
            o.Script = Path.GetFullPath(o.Script);
            if (!File.Exists(o.Script))
            {
                Console.Error.WriteLine("脚本不存在: " + o.Script);
                return 2;
            }
            string stem = Path.GetFileNameWithoutExtension(o.Script);
            if (string.IsNullOrWhiteSpace(o.Name)) o.Name = stem;
            if (IsBadName(o.Name, out string why))
            {
                Console.Error.WriteLine("产物名不可用: " + why);
                return 2;
            }
            if (string.IsNullOrWhiteSpace(o.OutDir))
                o.OutDir = RuntimeManager.DefaultOutputDir();
            o.OutDir = Path.GetFullPath(o.OutDir);
            if (o.Window != "console" && o.Window != "noconsole" && o.Window != "auto")
                o.Window = "console";

            var started = DateTime.Now;
            var sw = Stopwatch.StartNew();
            string error = null;
            string final = null;
            long size = 0;
            bool signed = false;
            string signError = null;

            try
            {
                Directory.CreateDirectory(o.OutDir);

                // 1) 找运行时可用的 PyInstaller
                string py = FindPythonWithPyInstaller(out string engineNote);
                if (py == null)
                {
                    error = "没有找到可用的 PyInstaller 运行时（内置运行时或 PATH 里的 Python）";
                    return Finish(o, false, o.Script, final, size, sw.Elapsed, error,
                                  signed, signError, started);
                }
                Console.WriteLine("[*] 打包引擎: " + engineNote);
                Console.WriteLine("[*] 运行时: " + py);

                // 2) 组命令
                var business = new List<string>
                {
                    o.OneDir ? "--onedir" : "--onefile",
                    o.Window == "console" ? "--console"
                        : o.Window == "noconsole" ? "--noconsole"
                        : (LooksGui(o.Script) ? "--noconsole" : "--console"),
                    "--name", o.Name, "--noconfirm",
                    "--hidden-import", "modulefinder",
                    "--hidden-import", "importlib.util"
                };
                foreach (var m in o.Hidden)
                {
                    business.Add("--hidden-import");
                    business.Add(m);
                }
                foreach (var m in o.Exclude)
                {
                    business.Add("--exclude-module");
                    business.Add(m);
                }
                foreach (var d in o.AddData)
                {
                    business.Add("--add-data");
                    business.Add(d);
                }
                foreach (var x in o.Extra) business.Add(x);

                if (!string.IsNullOrWhiteSpace(o.Icon))
                {
                    if (File.Exists(o.Icon))
                    {
                        business.Add("--icon");
                        business.Add(Path.GetFullPath(o.Icon));
                    }
                    else
                    {
                        Console.WriteLine("[!] 图标不存在，忽略: " + o.Icon);
                    }
                }

                string verFile = null;
                if (!string.IsNullOrWhiteSpace(o.AppVersion))
                {
                    verFile = WriteVersionFile(o.Name, o.AppVersion);
                    if (verFile != null)
                    {
                        business.Add("--version-file");
                        business.Add(verFile);
                    }
                }
                business.Add(o.Script);

                var cmd = BuildCommand.From(py, new[] { "-m", "PyInstaller" },
                                            business, o.OutDir, o.Timeout * 1000);
                Console.WriteLine("[*] 命令: " + cmd.Display());
                Console.WriteLine("[*] 输出目录: " + o.OutDir);

                var runner = new CommandRunner((m, k) =>
                {
                    if (!string.IsNullOrEmpty(m)) Console.WriteLine(m);
                });
                var rr = runner.Run(cmd, line => Console.WriteLine(line));
                if (verFile != null) { try { File.Delete(verFile); } catch { } }

                if (rr.ExitCode == -1)
                {
                    error = "打包超时（" + o.Timeout + "s）";
                    return Finish(o, false, o.Script, final, 0, sw.Elapsed, error,
                                  false, null, started);
                }
                if (rr.ExitCode != 0)
                {
                    error = "PyInstaller 返回码 " + rr.ExitCode;
                    return Finish(o, false, o.Script, null, 0, sw.Elapsed, error,
                                  false, null, started);
                }

                // 3) 搬运产物
                string work = o.OutDir;
                string dist = Path.Combine(work, "dist");
                string produced = o.OneDir
                    ? Path.Combine(dist, o.Name, o.Name + ".exe")
                    : Path.Combine(dist, o.Name + ".exe");
                final = o.OneDir
                    ? Path.Combine(work, o.Name)
                    : Path.Combine(work, o.Name + ".exe");
                try
                {
                    if (o.OneDir && Directory.Exists(Path.GetDirectoryName(produced)))
                    {
                        string src = Path.GetDirectoryName(produced);
                        if (Directory.Exists(final)) Directory.Delete(final, true);
                        Directory.Move(src, final);
                    }
                    else if (File.Exists(produced))
                    {
                        if (File.Exists(final)) File.Delete(final);
                        File.Move(produced, final);
                    }
                }
                catch (Exception ex)
                {
                    error = "搬运产物失败: " + ex.Message;
                    return Finish(o, false, o.Script, final, 0, sw.Elapsed, error,
                                  false, null, started);
                }

                string exeForUse = o.OneDir
                    ? Path.Combine(final, o.Name + ".exe") : final;
                if (!File.Exists(exeForUse))
                {
                    error = "未找到构建产物";
                    return Finish(o, false, o.Script, final, 0, sw.Elapsed, error,
                                  false, null, started);
                }
                size = new FileInfo(exeForUse).Length;

                // 4) 清理构建中间文件
                try
                {
                    if (Directory.Exists(Path.Combine(work, "build")))
                        Directory.Delete(Path.Combine(work, "build"), true);
                    if (Directory.Exists(dist)) Directory.Delete(dist, true);
                    string spec = Path.Combine(work, o.Name + ".spec");
                    if (File.Exists(spec)) File.Delete(spec);
                }
                catch { }

                // 5) 自检
                bool selftestOk = true;
                string selftestDetail = "跳过";
                if (!o.NoSelfTest)
                {
                    selftestOk = SelfTest(exeForUse, out selftestDetail);
                    Console.WriteLine("[*] 产物自检: " + selftestDetail);
                    if (!selftestOk)
                    {
                        error = "产物自检失败（" + selftestDetail + "）";
                        return Finish(o, false, o.Script, exeForUse, size,
                                      sw.Elapsed, error, false, null, started);
                    }
                }

                // 6) 签名
                if (CodeSigner.IsConfigured(o.SignPfx))
                {
                    signed = CodeSigner.Sign(exeForUse, o.SignPfx, o.SignPassword,
                        o.TimestampUrl, (m, k) => Console.WriteLine(m), out signError);
                    if (!signed)
                        Console.WriteLine("[!] 签名失败: " + signError);
                }

                var result = new
                {
                    ok = true,
                    version = AppVersionNumber(),
                    script = o.Script,
                    output = exeForUse,
                    size = size,
                    seconds = Math.Round(sw.Elapsed.TotalSeconds, 1),
                    selftest = selftestDetail,
                    signed,
                    sign_error = signError,
                    started = started.ToString("yyyy-MM-dd HH:mm:ss")
                };
                Emit(o, result);
                return signed || !CodeSigner.IsConfigured(o.SignPfx) ? 0 : 1;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return Finish(o, false, o.Script, final, size, sw.Elapsed, error,
                              signed, signError, started);
            }
        }

        static int Finish(CliOptions o, bool ok, string script, string output,
                          long size, TimeSpan elapsed, string error,
                          bool signed, string signError, DateTime started)
        {
            Console.Error.WriteLine("[-] 失败: " + error);
            Emit(o, new
            {
                ok = false,
                version = AppVersionNumber(),
                script,
                output,
                size,
                seconds = Math.Round(elapsed.TotalSeconds, 1),
                error,
                signed,
                sign_error = signError,
                started = started.ToString("yyyy-MM-dd HH:mm:ss")
            });
            return 1;
        }

        static void Emit(CliOptions o, object result)
        {
            if (!o.Json) return;
            try
            {
                Console.WriteLine("---JSON---");
                Console.WriteLine(JsonSerializer.Serialize(result,
                    new JsonSerializerOptions { WriteIndented = false }));
            }
            catch { }
            Console.WriteLine("---END---");
        }

        static string Next(string[] args, ref int i)
        {
            if (i + 1 >= args.Length) return null;
            i++;
            return args[i];
        }

        static void AddModules(List<string> list, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (var s in text.Split(new[] { ',', ';', ' ', '\t' },
                                         StringSplitOptions.RemoveEmptyEntries))
            {
                string t = s.Trim();
                if (t.Length > 0 && !list.Contains(t)) list.Add(t);
            }
        }

        static bool IsBadName(string name, out string why)
        {
            why = null;
            if (string.IsNullOrWhiteSpace(name)) { why = "不能为空"; return true; }
            if (name.EndsWith(".") || name.EndsWith(" ")) { why = "不能以点或空格结尾"; return true; }
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            { why = "含非法字符"; return true; }
            return false;
        }

        /// <summary>找带 PyInstaller 的 Python：先找本程序内置/集中运行时，再找 PATH</summary>
        static string FindPythonWithPyInstaller(out string note)
        {
            note = "";
            try
            {
                var runner = new CommandRunner(null);
                var slots = RuntimeManager.Discover();
                foreach (var s in slots) RuntimeManager.FillDetails(s, runner);
                var slot = RuntimeManager.PickUsable(slots);
                if (slot != null)
                {
                    note = slot.Title + " · PyInstaller " + slot.PyInstallerVersion;
                    return slot.PythonExe;
                }
            }
            catch { }

            foreach (var name in new[] { "python.exe", "python", "py.exe" })
            {
                string py = Which(name);
                if (py == null) continue;
                if (VerifyPyInstaller(py, out string ver))
                {
                    note = "系统 Python · PyInstaller " + ver;
                    return py;
                }
            }
            return null;
        }

        static bool VerifyPyInstaller(string py, out string version)
        {
            version = "";
            try
            {
                var psi = new ProcessStartInfo(py)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add("import PyInstaller;print(PyInstaller.__version__)");
                using (var p = Process.Start(psi))
                {
                    string so = p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(20000);
                    if (p.ExitCode == 0 && so.Trim().Length > 0)
                    {
                        version = so.Trim();
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        static bool SelfTest(string exe, out string detail)
        {
            detail = "";
            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(exe)
                };
                var sb = new StringBuilder();
                using (var p = new Process { StartInfo = psi })
                {
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(6000))
                    {
                        try { p.Kill(true); } catch { }
                        detail = "运行 6 秒未退出（按通过处理）";
                        return true;
                    }
                    try { p.WaitForExit(); } catch { }
                    string text = sb.ToString();
                    if (p.ExitCode == 0)
                    {
                        detail = "退出码 0";
                        return true;
                    }
                    if (DependencyChecker.LooksLikeUsageError(text))
                    {
                        detail = "退出码 " + p.ExitCode + "，但输出是用法错误（按通过处理）";
                        return true;
                    }
                    detail = "退出码 " + p.ExitCode
                             + (text.Length > 0 ? "；输出: " + Trim(text, 200) : "");
                    return false;
                }
            }
            catch (Exception ex)
            {
                detail = "启动失败: " + ex.Message;
                return false;
            }
        }

        static string Trim(string s, int n)
            => string.IsNullOrEmpty(s) ? "" :
               (s.Length <= n ? s.Replace("\r", "").Replace("\n", " ") :
                s.Substring(0, n).Replace("\r", "").Replace("\n", " ") + "…");

        static bool LooksGui(string script)
        {
            try
            {
                string head = File.ReadAllText(script);
                return System.Text.RegularExpressions.Regex.IsMatch(
                    head, @"\b(tkinter|PyQt5|PyQt6|PySide2|PySide6|wx|pygame|kivy)\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            catch { return false; }
        }

        static string WriteVersionFile(string name, string ver)
        {
            try
            {
                var nums = new List<int>();
                foreach (var p in (ver ?? "").Split('.'))
                    if (int.TryParse(p, out int n)) nums.Add(n);
                while (nums.Count < 4) nums.Add(0);
                string tuple = string.Join(", ", nums.GetRange(0, 4));
                string tmpl =
                    "VSVersionInfo(\n" +
                    "  ffi=FixedFileInfo(filevers=(" + tuple + "), prodvers=(" + tuple + "),\n" +
                    "    mask=0x3f, flags=0x0, OS=0x40004, fileType=0x1, subtype=0x0, date=(0,0)),\n" +
                    "  kids=[StringFileInfo([StringTable('040904B0',[\n" +
                    "    StringStruct('CompanyName', 'Dainric'),\n" +
                    "    StringStruct('FileDescription', '" + Esc(name) + "'),\n" +
                    "    StringStruct('FileVersion', '" + Esc(ver) + "'),\n" +
                    "    StringStruct('InternalName', '" + Esc(name) + "'),\n" +
                    "    StringStruct('OriginalFilename', '" + Esc(name) + ".exe'),\n" +
                    "    StringStruct('ProductName', '" + Esc(name) + "'),\n" +
                    "    StringStruct('ProductVersion', '" + Esc(ver) + "')])]),\n" +
                    "    VarFileInfo([VarStruct('Translation', [0x0409, 0x04B0])])])\n";
                string f = Path.Combine(Path.GetTempPath(),
                    "dainric_cli_ver_" + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(f, tmpl, Encoding.UTF8);
                return f;
            }
            catch { return null; }
        }

        static string Esc(string s)
            => (s ?? "").Replace("\\", "\\\\").Replace("'", "\\'");

        static string AppVersionNumber()
        {
            try
            {
                var v = System.Reflection.Assembly.GetExecutingAssembly()
                                                      .GetName().Version;
                if (v != null)
                {
                    string s = v.Major + "." + v.Minor;
                    if (v.Build >= 0) s += "." + v.Build;
                    return s;
                }
            }
            catch { }
            return "0.0.0";
        }

        static string Which(string exe)
        {
            try
            {
                foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "")
                                  .Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(d)) continue;
                    string p = Path.Combine(d.Trim(), exe);
                    if (File.Exists(p)) return p;
                }
            }
            catch { }
            return null;
        }

        static void PrintHelp()
        {
            Console.WriteLine(
@"Dainric 打包器 · 命令行模式

用法:
  Dainric打包器.exe --build <脚本.py> [选项]

选项:
  --out, -o <目录>         输出目录（默认 桌面\Dainric产物）
  --name <名称>            产物名（默认脚本名）
  --onefile / --onedir     单文件（默认）/ 目录形态
  --console / --noconsole  窗口模式；--window auto 按脚本自动判断
  --icon <app.ico>         产物图标
  --app-version <x.y.z>    写入产物版本资源
  --hidden-import <m>      手动 hidden-import（可重复）
  --exclude-module <m>     排除模块（可重复）
  --add-data <源;目标>     附加数据（可重复）
  --extra <参数>           追加给 PyInstaller 的单个参数（可重复）
  --timeout <秒>           打包超时，默认 1800
  --no-selftest            跳过产物自检
  --json                   结束时输出 JSON 结果（---JSON--- 包裹）
  --sign-pfx <证书.pfx>    打包成功后签名
  --sign-password <密码>   pfx 密码
  --timestamp-url <URL>    时间戳服务器（可选）
  --version                显示版本
  --help, -h               显示本帮助

示例:
  Dainric打包器.exe --build app.py --out dist --onedir --json
  Dainric打包器.exe --build app.py --hidden-import pandas._libs.tslibs
");
        }
    }
}
