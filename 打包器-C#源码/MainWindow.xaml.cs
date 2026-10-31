// Dainric 打包器 · 原生 Windows 版
// 作者: Dainric
// 纯 .NET/WPF 实现：自身就是原生 EXE，不依赖 Python
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Dainric.Packer
{
    public partial class MainWindow : Window
    {
        // 版本号别再写常量：唯一来源是 DainricPacker.csproj 的 <Version>（程序集元数据）。
        // 之前这里写死 3.1.0，标题已经从程序集读到 3.2.0，启动日志却还显示 3.1.0。
        static readonly string APP_VERSION = AppVersionNumber();
        const int DEFAULT_TIMEOUT = 1800;


        // ── 运行状态 ──────────────────────────────────────────────
        Process _proc;
        volatile bool _busy;
        volatile bool _abort;
        readonly List<string> _log = new List<string>();
        string _script, _icon, _tmpIcon, _tmpVersion;
        string _verText;                 // 纯版本号，用于写资源文件
        string _engineDesc, _enginePath;  // 打包引擎说明与路径

        // ── 日志：可过滤 / 导出 / 每次构建自动落盘 ──────────────
        sealed class LogEntry
        {
            public DateTime Time;
            public string Text;
            public string Kind;
        }
        readonly List<LogEntry> _logEntries = new List<LogEntry>();
        int _runLogStart;                // 本次构建开始时的日志下标
        string _lastDiagText;            // 最近一次失败诊断，写入历史
        bool _logExpanded;
        double _savedSideWidth = 392;
        GridLength _savedFormStar = new GridLength(1, GridUnitType.Star);

        // ── UI 状态持久化（窗口大小/位置、上次脚本与输出目录）──
        public sealed class UiState
        {
            public double Left { get; set; }
            public double Top { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
            public bool Maximized { get; set; }
            public double SideWidth { get; set; } = 392;
            public string LastScript { get; set; }
            public string LastOutDir { get; set; }
            public string AdvHidden { get; set; }
            public string AdvExclude { get; set; }
            public string AdvExtra { get; set; }
            public string SignPfx { get; set; }
            public string SignTs { get; set; }
            public List<string> DataItems { get; set; }
            public bool CheckUpdateOnStartup { get; set; } = true;
            public string LastUpdateCheck { get; set; }
        }
        static string UiStateFile => Path.Combine(RuntimeLocator.HomeDir, "ui.json");
        string _pendingLastScript;
        string _savedOutDir;
        string _lastUpdateCheck;
        public bool CheckUpdateOnStartup { get; set; } = true;

        // ── v3.3.0：高级参数 / 数据文件 / 结果卡 / 批量结果 ──────
        bool _verAuto;
        readonly List<DataItem> _dataItems = new List<DataItem>();

        public sealed class DataItem
        {
            public string Source { get; set; }
            public string Dest { get; set; }
            public override string ToString()
                => Path.GetFileName(Source)
                   + (string.IsNullOrEmpty(Dest) || Dest == "." ? "" : "  →  " + Dest);
        }

        string _lastProductPath;
        string _lastProductHash;
        long _lastProductSize;

        sealed class BatchOutcome
        {
            public string Script, Name, Result, Product;
            public double Seconds;
            public long Size;
        }
        readonly List<BatchOutcome> _lastBatchResults = new List<BatchOutcome>();
        readonly Dictionary<string, string> _queueDetail =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr,
                                                ref int value, int size);

        HwndSource _sourceHook;

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam,
                       ref bool handled)
        {
            if (msg != SingleInstanceIpc.WM_COPYDATA) return IntPtr.Zero;
            try
            {
                var cds = Marshal.PtrToStructure<SingleInstanceIpc.COPYDATASTRUCT>(lParam);
                string text = "";
                if (cds.dwData == (IntPtr)SingleInstanceIpc.Magic && cds.cbData > 0)
                    text = Marshal.PtrToStringUni(cds.lpData) ?? "";
                Dispatcher.BeginInvoke(new Action(() => OpenExternalScript(text)));
                handled = true;
                return new IntPtr(1);
            }
            catch { }
            return IntPtr.Zero;
        }

        void DisposeProc()
        {
            try { if (_proc != null) { _proc.Dispose(); } } catch { }
            _proc = null;
        }

        /// <summary>
        /// 纯版本号（如 "3.2.0"）。唯一来源是 DainricPacker.csproj 的 &lt;Version&gt;，
        /// 升版本只改 csproj，标题和日志都从这里取，避免两处显示不一致。
        /// </summary>
        public static string AppVersionNumber()
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                var loc = asm.Location;
                if (!string.IsNullOrEmpty(loc) && File.Exists(loc))
                {
                    var fi = System.Diagnostics.FileVersionInfo.GetVersionInfo(loc);
                    string v = fi.ProductVersion ?? fi.FileVersion;
                    if (!string.IsNullOrEmpty(v))
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(
                            v, @"^\d+\.\d+(\.\d+)?");
                        if (m.Success) return m.Value;
                    }
                }
                var av = asm.GetName().Version;
                if (av != null)
                {
                    string v = av.Major + "." + av.Minor;
                    if (av.Build >= 0) v += "." + av.Build;
                    return v;
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// 标题栏的版本文本。从程序集元数据读，避免每次升版本都要改 XAML
        /// —— 之前硬编码，升 3.2.0 时标题还停在 3.1.0。
        /// </summary>
        public static string AppVersionText()
        {
            string v = AppVersionNumber();
            return string.IsNullOrEmpty(v) ? "原生版" : "v" + v + " · 原生版";
        }

        /// <summary>默认输出目录（实现放在 RuntimeManager，便于单测覆盖）</summary>
        static string DefaultOutDir() => RuntimeManager.DefaultOutputDir();

        // 两个纯函数放在 DependencyChecker 里，便于单测直接覆盖
        static List<string> SplitArgs(string t) => DependencyChecker.SplitArgs(t);
        static bool LooksLikeUsageError(string t) =>
            DependencyChecker.LooksLikeUsageError(t);

        public MainWindow()
        {
            try
            {
                InitializeComponent();
                VerLabel.Text = AppVersionText();     // 版本从程序集读，别再硬编码
                LoadUiState();                        // 窗口几何、上次脚本/输出目录
                OutDir.Text = string.IsNullOrEmpty(_savedOutDir)
                    ? DefaultOutDir() : _savedOutDir;
                InitLog();
                LoadHistory();
                DetectEngine();
            }
            catch (Exception ex)
            {
                App.CrashLog(ex, "MainWindow ctor");
                throw;
            }
            Loaded += (s, e) =>
            {
                Log("[*] Dainric 打包器 v" + APP_VERSION + " · 原生 Windows 版", "acc");
                Log("[*] 运行环境 .NET " + Environment.Version + " · " +
                    (Environment.Is64BitProcess ? "64" : "32") + " 位", "dim");
                int nRt = 0;
                foreach (var slot in RuntimeManager.Discover())
                {
                    if (!slot.Present) continue;
                    nRt++;
                    Log("[*] " + slot.Title + " → " + slot.Dir
                        + "  (" + slot.FoundBy + ")", "dim");
                }
                if (nRt > 0)
                    Log("[*] 已内置 " + nRt + " 个 Python 运行时，目标机无需安装任何环境",
                        "ok");
                else
                    Log("[*] 未附带运行时目录，打包需目标机自行安装 PyInstaller", "warn");
                Log("[!] 提示: 无控制台模式下目标机报错不显示窗口，排查请先用控制台模式", "warn");
                RuntimeManager.MigrateLegacyLayout();
                string dumprt = Environment.GetEnvironmentVariable("DAINRIC_RTDUMP");
                if (!string.IsNullOrEmpty(dumprt))
                {
                    try
                    {
                        File.WriteAllText(dumprt,
                            RuntimeManager.DumpDiagnostics(), Encoding.UTF8);
                    }
                    catch { }
                }
                if (BatchOutDir.Text.Length == 0)
                    BatchOutDir.Text = OutDir.Text;
                FirstRunCheck();
                string startupScript = !string.IsNullOrEmpty(App.PendingScript)
                    ? App.PendingScript : _pendingLastScript;
                if (!string.IsNullOrEmpty(startupScript)
                    && File.Exists(startupScript))
                {
                    LoadScript(startupScript);
                }

                // 开发自检：设置 DAINRIC_BATCHTEST=<目录> 时自动跑一次批量打包
                string bt = Environment.GetEnvironmentVariable("DAINRIC_BATCHTEST");
                if (!string.IsNullOrEmpty(bt))
                    Dispatcher.BeginInvoke(new Action(async () => await BatchSelfTest(bt)));
                else
                    Dispatcher.BeginInvoke(new Action(async () => await CheckUpdateAtStartupAsync()));
            };
        }

        /// <summary>启动阶段日志：只在文件里留痕，不打扰界面</summary>
        static void Boot(string msg)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "boot.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg
                    + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        // ══════════════ 引擎探测 ══════════════
        // 关键：探测绝对不能阻塞窗口创建。之前这里同步跑子进程做验证，
        // 结果窗口一直建不出来。现在改成「同步只做零成本判断 + 后台验证」。
        volatile bool _engineReady;
        volatile bool _engineChecked;

        void DetectEngine()
        {
            StatEngine.Text = "…";
            string baseDir = AppContext.BaseDirectory;
            bool hasRuntime = File.Exists(
                Path.Combine(baseDir, "runtime", "python.exe"));
            bool hasCli = (Which("pyinstaller.exe") ?? Which("pyinstaller")) != null;
            bool hasPy = (Which("python.exe") ?? Which("py.exe")) != null;

            if (!hasRuntime && !hasCli && !hasPy)
            {
                StatEngine.Text = "缺失";
                _engineChecked = true;
                Log("[-] 未找到 PyInstaller。", "err");
                Log("[!] 打包目标脚本需要它。两种方式：", "warn");
                Log("    1) 使用自带运行时的完整版（程序目录下含 runtime 文件夹）", "dim");
                Log("    2) 在目标机安装: pip install pyinstaller", "dim");
                return;
            }

            if (hasRuntime)
                Log("[*] 检测到随包运行时，正在验证…", "dim");
            else
                Log("[*] 正在后台验证打包引擎…", "dim");
            Task.Run(new Action(() =>
            {
                List<string> prefix = null;
                try { prefix = EnginePrefix(); }
                catch { }
                _engineChecked = true;

                if (prefix != null)
                {
                    _engineReady = true;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        StatEngine.Text = "就绪";
                        Log("[*] 打包引擎: " + (_engineDesc ?? "可用"), "acc");
                        Log("[*] 引擎路径: " + (_enginePath ?? ""), "dim");
                    }));
                }
                else
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        StatEngine.Text = "缺失";
                        Log("[-] 找到了 Python，但里面没有 PyInstaller。", "err");
                        Log("[!] 请在目标机执行: pip install pyinstaller", "warn");
                        Log("[!] 或改用自带运行时的完整版", "warn");
                    }));
                }
            }));
        }

        /// <summary>点打包时调用：等后台引擎验证完成，最多等 waitMs 毫秒。</summary>
        bool EnsureEngine(int waitMs = 45000)
        {
            if (_engineReady) return true;
            var sw = Stopwatch.StartNew();
            while (!_engineChecked && sw.ElapsedMilliseconds < waitMs)
                Thread.Sleep(120);

            if (_engineReady) return true;
            try
            {
                var pfx = EnginePrefix();
                if (pfx != null) { _engineReady = true; return true; }
            }
            catch { }
            return false;
        }

        /// <summary>按优先级查找 PyInstaller：随包运行时 → PATH → 常见安装位置</summary>
        string FindPyInstaller()
        {
            string baseDir = AppContext.BaseDirectory;

            // 1) 随包运行时（绿色版核心）：存在即用它，保证解压即可打包
            string py = Path.Combine(baseDir, "runtime", "python.exe");
            if (File.Exists(py)) return py;

            // 2) PATH
            string p = Which("pyinstaller.exe") ?? Which("pyinstaller");
            if (p != null) return p;

            // 3) python -m PyInstaller
            string sysPy = Which("python.exe") ?? Which("py.exe");
            if (sysPy != null) return sysPy;

            // 4) 常见安装位置
            foreach (var root in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\Programs\Python",
                @"C:\Python314", @"C:\Python313", @"C:\Python312", @"C:\Python311"
            })
            {
                if (!Directory.Exists(root)) continue;
                foreach (var d in Directory.GetDirectories(root))
                {
                    string c = Path.Combine(d, "python.exe");
                    if (File.Exists(c)) return c;
                }
                string c2 = Path.Combine(root, "python.exe");
                if (File.Exists(c2)) return c2;
            }
            return null;
        }

        static string Which(string exe)
        {
            string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathVar.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    string full = Path.Combine(dir.Trim(), exe);
                    if (File.Exists(full)) return full;
                }
                catch { }
            }
            return null;
        }

        /// <summary>验证一个 Python 里是否真的装了 PyInstaller，返回版本号或 null</summary>
        string ProbePyInstaller(string py)
        {
            try
            {
                int rc = RunCapture(py, new List<string>
                {
                    "-c", "import PyInstaller,sys;" +
                          "sys.stdout.write(PyInstaller.__version__)"
                }, 30000, out string so, out string se);
                if (rc == 0 && so.Trim().Length > 0) return so.Trim();
                return null;
            }
            catch { return null; }
        }

        volatile EngineInfo _engine;

        /// <summary>挑选打包引擎：3.12 兼容模式优先，其次默认运行时，最后系统环境。</summary>
        EngineInfo ResolveEngine()
        {
            var cached = _engine;
            if (cached != null && cached.Available) return cached;

            var info = new EngineInfo();

            // 1) 随包运行时，按 3.12 -> 3.14 顺序
            foreach (var slot in RuntimeManager.Discover())
            {
                if (!slot.Present) continue;
                string v = ProbePyInstaller(slot.PythonExe);
                if (v == null) continue;
                info.Available = true;
                info.Exe = slot.PythonExe;
                info.PreArgs = new List<string> { "-m", "PyInstaller" };
                info.Version = v;
                info.Source = slot.Key == RuntimeManager.Key312
                    ? "内置运行时 · 兼容模式" : "内置运行时";
                info.Detail = slot.Title + " · Python " + slot.Version;
                _engine = info;
                return info;
            }

            // 2) 系统的 pyinstaller 命令行入口
            string cli = Which("pyinstaller.exe") ?? Which("pyinstaller");
            if (cli != null)
            {
                var r = _runner.Run(BuildCommand.From(cli, null,
                    new[] { "--version" }, null, 30000));
                if (r.ExitCode == 0)
                {
                    info.Available = true;
                    info.Exe = cli;
                    info.PreArgs = new List<string>();
                    info.Version = r.StdOut.Trim();
                    info.Source = "系统 CLI";
                    info.Detail = cli;
                    _engine = info;
                    return info;
                }
            }

            // 3) 系统里的 Python（必须验证过 PyInstaller）
            foreach (var py in CandidatePythons())
            {
                string v = ProbePyInstaller(py);
                if (v == null) continue;
                info.Available = true;
                info.Exe = py;
                info.PreArgs = new List<string> { "-m", "PyInstaller" };
                info.Version = v;
                info.Source = "系统 Python";
                info.Detail = py;
                _engine = info;
                return info;
            }

            _engine = null;
            return null;
        }

        /// <summary>兼容旧调用点：返回可执行前缀（exe + 自带参数）</summary>
        List<string> EnginePrefix()
        {
            var e = ResolveEngine();
            if (e == null) return null;
            _engineDesc = e.Source + " · PyInstaller " + e.Version;
            _enginePath = e.Exe + (e.PreArgs.Count > 0
                ? " " + string.Join(" ", e.PreArgs) : "");
            var pfx = new List<string> { e.Exe };
            pfx.AddRange(e.PreArgs);
            return pfx;
        }

        /// <summary>列出所有可能带 PyInstaller 的 Python 解释器</summary>
        IEnumerable<string> CandidatePythons()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();

            // 0) 随包运行时最优先：绿色版靠它做到解压即用
            string bundled = Path.Combine(AppContext.BaseDirectory,
                                          "runtime", "python.exe");
            if (File.Exists(bundled) && seen.Add(bundled)) list.Add(bundled);

            foreach (var n in new[] { "python.exe", "py.exe" })
            {
                string w = Which(n);
                if (w != null && seen.Add(w)) list.Add(w);
            }

            var roots = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "Python"),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };
            foreach (var r in roots)
            {
                if (string.IsNullOrEmpty(r) || !Directory.Exists(r)) continue;
                IEnumerable<string> subs = new string[0];
                try { subs = Directory.GetDirectories(r, "Python3*"); }
                catch { }                        // 无权限时跳过，不抛
                foreach (var d in subs)
                {
                    string c = Path.Combine(d, "python.exe");
                    if (File.Exists(c) && seen.Add(c)) list.Add(c);
                }
            }
            return list;
        }

        // ══════════════ 界面事件 ══════════════
        void TitleBarDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        void ShowSingle(object sender, MouseButtonEventArgs e) => SwitchTo("single");

        void ShowBatch(object sender, MouseButtonEventArgs e) => SwitchTo("batch");

        void PickScript(object sender, MouseButtonEventArgs e)
        {
            if (_busy) return;
            var dlg = new OpenFileDialog
            {
                Title = "选择 Python 脚本",
                Filter = "Python 脚本|*.py|所有文件|*.*"
            };
            if (dlg.ShowDialog() == true) LoadScript(dlg.FileName);
        }

        void LoadScript(string path)
        {
            _script = path;
            ScriptName.Text = Path.GetFileName(path);
            ScriptName.Foreground = (Brush)FindResource("Label");
            try
            {
                var fi = new FileInfo(path);
                ScriptPath.Text = Path.GetDirectoryName(path) + " · " +
                                  (fi.Length / 1024.0).ToString("0.0") + " KB";
            }
            catch { ScriptPath.Text = Path.GetDirectoryName(path); }
            ScriptHint.Text = "双击打开项目主页";

            string stem = Path.GetFileNameWithoutExtension(path);
            OutName.Text = stem;
            Log("[*] 脚本: " + path, "acc");
            if (_verAuto) ResetVersionAuto();
            AnalyzeScript(path);
        }

        // ══════════════ 脚本识别 / 试运行 / 高级参数 ══════════════
        void ResetVersionAuto()
        {
            _verText = null;
            _verAuto = false;
            VerText.Text = "未设置";
            VerText.ToolTip = null;
            VerText.Foreground = (Brush)FindResource("Label2");
        }

        void AnalyzeScript(string path)
        {
            try
            {
                string text = File.ReadAllText(path);
                var notes = new List<string>();

                var mVer = Regex.Match(text,
                    @"__version__\s*=\s*[""']([^""'\r\n]{1,32})[""']");
                if (mVer.Success && (_verAuto || string.IsNullOrEmpty(_verText)))
                {
                    _verText = mVer.Groups[1].Value.Trim();
                    if (_verText.Length > 0)
                    {
                        _verAuto = true;
                        VerText.Text = _verText + " · 来自脚本";
                        VerText.ToolTip = "产物版本号将写入: " + _verText;
                        VerText.Foreground = (Brush)FindResource("Green");
                        notes.Add("版本 " + _verText);
                    }
                }

                bool gui = Regex.IsMatch(text,
                    @"\b(tkinter|PyQt5|PyQt6|PySide2|PySide6|wx|pygame|kivy)\b",
                    RegexOptions.IgnoreCase);
                bool input = Regex.IsMatch(text, @"\binput\s*\(");
                bool argparse = Regex.IsMatch(text, @"\bargparse\b");
                bool mp = Regex.IsMatch(text, @"\bmultiprocessing\b");
                bool files = Regex.IsMatch(text, @"__file__|\bopen\s*\(");
                if (gui) notes.Add("图形界面 → 建议无控制台");
                else if (input || argparse) notes.Add("命令行交互 → 建议控制台");
                if (mp) notes.Add("multiprocessing → 产物需 freeze_support");
                if (files) notes.Add("可能读取外部文件");

                ScriptAnalysis.Text = "自动识别：" + (notes.Count == 0
                    ? "未发现特殊依赖" : string.Join(" · ", notes));
                ScriptAnalysis.Visibility = Visibility.Visible;
            }
            catch
            {
                ScriptAnalysis.Visibility = Visibility.Collapsed;
            }
        }

        void TrialRunScript(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (_script == null || !File.Exists(_script))
            {
                Log("[-] 请先选择脚本", "err");
                return;
            }
            string py = FindPythonForCheck();
            if (py == null)
            {
                Log("[!] 找不到 Python 运行时，无法试运行", "warn");
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(py, "\"" + _script + "\"")
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(_script)
                });
                Log("[*] 已启动试运行（控制台窗口里查看输出）: "
                    + Path.GetFileName(_script), "acc");
            }
            catch (Exception ex)
            {
                Log("[-] 试运行失败: " + ex.Message, "err");
            }
        }

        static List<string> SplitList(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return list;
            foreach (var t in text.Split(
                new[] { ',', ';', ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                string s = t.Trim();
                if (s.Length > 0 && !list.Contains(s)) list.Add(s);
            }
            return list;
        }

        void RefreshDataList()
        {
            if (DataList == null) return;
            DataList.ItemsSource = null;
            DataList.ItemsSource = _dataItems;
        }

        void DataAddFiles(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择要打进产物的数据文件",
                Multiselect = true,
                Filter = "所有文件|*.*"
            };
            if (dlg.ShowDialog() != true) return;
            foreach (var f in dlg.FileNames)
                _dataItems.Add(new DataItem
                { Source = f, Dest = Path.GetFileName(f) });
            RefreshDataList();
            Log("[*] 已附加 " + dlg.FileNames.Length + " 个数据文件", "acc");
        }

        void DataAddFolder(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog { Title = "选择要打进产物的目录" };
            if (dlg.ShowDialog() != true) return;
            string name = new DirectoryInfo(dlg.FolderName).Name;
            _dataItems.Add(new DataItem { Source = dlg.FolderName, Dest = name });
            RefreshDataList();
            Log("[*] 已附加目录: " + dlg.FolderName, "acc");
        }

        void DataRemove(object sender, RoutedEventArgs e)
        {
            var sel = DataList.SelectedItems.Cast<DataItem>().ToList();
            foreach (var d in sel) _dataItems.Remove(d);
            RefreshDataList();
        }

        void DataCopySnippet(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(
                    "import os, sys\n\n"
                    + "def resource_path(rel):\n"
                    + "    base = getattr(sys, \"_MEIPASS\",\n"
                    + "                   os.path.dirname(os.path.abspath(__file__)))\n"
                    + "    return os.path.join(base, rel)\n\n"
                    + "# 用法: open(resource_path(\"data.json\"), encoding=\"utf-8\")\n");
                Status("resource_path 代码已复制", (Brush)FindResource("Blue"));
            }
            catch { }
        }

        void PickSignPfx(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择代码签名证书",
                Filter = "代码签名证书|*.pfx;*.p12|所有文件|*.*"
            };
            if (dlg.ShowDialog() == true) AdvSignPfx.Text = dlg.FileName;
        }

        /// <summary>单实例通信：第二个实例把脚本路径转过来</summary>
        public void OpenExternalScript(string path)
        {
            try
            {
                if (WindowState == WindowState.Minimized)
                    WindowState = WindowState.Normal;
                Show();
                Activate();
                Topmost = true;
                Topmost = false;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    if (_busy)
                    {
                        Log("[!] 打包进行中，暂时不切换脚本: " + path, "warn");
                        return;
                    }
                    SwitchTo("single");
                    LoadScript(path);
                    Log("[*] 已从外部打开脚本: " + path, "acc");
                }
                else
                {
                    Log("[*] 已激活运行中的窗口", "dim");
                }
            }
            catch { }
        }

        // ══════════════ 代码签名 ══════════════
        void TrySignProduct(string name, string product, BuildOptions o)
        {
            if (!CodeSigner.IsConfigured(o.SignPfx)) return;
            string target = product;
            if (Directory.Exists(target) && !File.Exists(target))
                target = Path.Combine(target, name + ".exe");
            Log("[*] 正在给产物签名…", "dim");
            string err;
            if (!CodeSigner.Sign(target, o.SignPfx, o.SignPassword,
                                 o.TimestampUrl, Log, out err))
            {
                Log("[!] 签名失败: " + err, "warn");
            }
        }

        // ══════════════ 结果卡 / SHA256 ══════════════
        static string Sha256OfFile(string path)
        {
            try
            {
                using (var sha = SHA256.Create())
                using (var fs = File.OpenRead(path))
                    return Convert.ToHexString(sha.ComputeHash(fs));
            }
            catch { return null; }
        }

        static long SizeOfProduct(string path)
        {
            try
            {
                if (File.Exists(path)) return new FileInfo(path).Length;
                if (Directory.Exists(path))
                {
                    long total = 0;
                    foreach (var f in Directory.EnumerateFiles(path, "*",
                        SearchOption.AllDirectories))
                    {
                        try { total += new FileInfo(f).Length; } catch { }
                    }
                    return total;
                }
            }
            catch { }
            return -1;
        }

        void HideResult()
        {
            ResultPanel.Visibility = Visibility.Collapsed;
            _lastProductPath = null;
            _lastProductHash = null;
            _lastProductSize = 0;
        }

        void ShowSuccessResult(string name, string product, double seconds)
        {
            _lastProductPath = product;
            _lastProductSize = SizeOfProduct(product);
            _lastProductHash = _lastProductSize > 0 && File.Exists(product)
                ? Sha256OfFile(product) : null;
            ResultPanel.Visibility = Visibility.Visible;
            ResultTitle.Text = "打包成功";
            ResultInfo.Text = name + " · " + seconds.ToString("0.0") + "s · "
                + (_lastProductSize > 0
                    ? (_lastProductSize / 1048576.0).ToString("0.0") + " MB"
                    : "产物路径未知") + "\n" + (product ?? "");
            ResultHash.Text = _lastProductHash != null
                ? "SHA256: " + _lastProductHash : "";
            ResultHash.Visibility = _lastProductHash != null
                ? Visibility.Visible : Visibility.Collapsed;
            BtnResultOpen.IsEnabled = BtnResultRun.IsEnabled = true;
            BtnResultCopyPath.IsEnabled = true;
            BtnResultDiag.Visibility = Visibility.Collapsed;
        }

        void ShowFailureResult(string message)
        {
            ResultPanel.Visibility = Visibility.Visible;
            ResultTitle.Text = "打包失败";
            ResultInfo.Text = string.IsNullOrEmpty(message)
                ? "详情见输出日志" : message;
            ResultHash.Visibility = Visibility.Collapsed;
            BtnResultOpen.IsEnabled = BtnResultRun.IsEnabled = false;
            BtnResultCopyPath.IsEnabled = false;
            BtnResultDiag.Visibility = string.IsNullOrEmpty(_lastDiagText)
                ? Visibility.Collapsed : Visibility.Visible;
        }

        void ResultOpen(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_lastProductPath)) return;
                if (File.Exists(_lastProductPath))
                    Process.Start(new ProcessStartInfo("explorer.exe",
                        "/select,\"" + _lastProductPath + "\"")
                    { UseShellExecute = true });
                else if (Directory.Exists(_lastProductPath))
                    Process.Start(new ProcessStartInfo("explorer.exe",
                        "\"" + _lastProductPath + "\"")
                    { UseShellExecute = true });
                else if (Directory.Exists(Path.GetDirectoryName(_lastProductPath)))
                    Process.Start(new ProcessStartInfo("explorer.exe",
                        "\"" + Path.GetDirectoryName(_lastProductPath) + "\"")
                    { UseShellExecute = true });
            }
            catch (Exception ex) { Log("[-] 打开产物失败: " + ex.Message, "err"); }
        }

        void ResultRun(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_lastProductPath)
                    || !File.Exists(_lastProductPath)) return;
                Process.Start(new ProcessStartInfo(_lastProductPath)
                { UseShellExecute = true });
            }
            catch (Exception ex) { Log("[-] 运行产物失败: " + ex.Message, "err"); }
        }

        void ResultCopyPath(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(_lastProductPath))
                    Clipboard.SetText(_lastProductPath);
                Status("产物路径已复制", (Brush)FindResource("Blue"));
            }
            catch { }
        }

        void ResultCopyDiag(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(string.IsNullOrEmpty(_lastDiagText)
                    ? "（没有诊断信息，详情见输出日志）" : _lastDiagText);
                Status("诊断信息已复制", (Brush)FindResource("Blue"));
            }
            catch { }
        }

        void PickOutDir(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog { Title = "选择输出目录" };
            if (dlg.ShowDialog() == true) OutDir.Text = dlg.FolderName;
        }

        void PickIcon(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择图标",
                Filter = "图标文件|*.ico|图片文件|*.png;*.bmp;*.jpg;*.jpeg|所有文件|*.*"
            };
            if (dlg.ShowDialog() != true) return;
            string p = dlg.FileName;
            if (p.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
            {
                _icon = p;
                IconText.Text = Path.GetFileName(p);
            }
            else
            {
                string ico = ConvertToIco(p);
                if (ico != null)
                {
                    _tmpIcon = ico;
                    _icon = ico;
                    IconText.Text = Path.GetFileName(p) + " (已转换)";
                    Log("[*] 图标已转换为 ICO", "acc");
                }
                else
                {
                    Log("[-] 图标转换失败，改用默认图标", "err");
                    return;
                }
            }
            IconText.Foreground = (Brush)FindResource("Blue");
        }

        void ResetIcon(object sender, RoutedEventArgs e)
        {
            _icon = null;
            IconText.Text = "使用默认图标";
            IconText.Foreground = (Brush)FindResource("Label2");
        }

        /// <summary>png/bmp/jpg → ICO。用 WPF 自带解码器，不依赖 System.Drawing</summary>
        string ConvertToIco(string src)
        {
            try
            {
                var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                    new Uri(src),
                    System.Windows.Media.Imaging.BitmapCreateOptions.None,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];

                var scaled = new System.Windows.Media.Imaging.TransformedBitmap(
                    frame, new ScaleTransform(256.0 / frame.PixelWidth,
                                              256.0 / frame.PixelHeight));
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(
                    System.Windows.Media.Imaging.BitmapFrame.Create(scaled));
                byte[] png;
                using (var ms = new MemoryStream())
                {
                    enc.Save(ms);
                    png = ms.ToArray();
                }

                // 单尺寸 ICO，图标数据用 PNG 压缩（Vista 及以上原生支持）
                string dst = Path.Combine(Path.GetTempPath(),
                    "dainric_" + Guid.NewGuid().ToString("N") + ".ico");
                using (var bw = new BinaryWriter(File.Create(dst)))
                {
                    bw.Write((ushort)0);          // reserved
                    bw.Write((ushort)1);          // type = icon
                    bw.Write((ushort)1);          // count
                    bw.Write((byte)0);            // width  0 表示 256
                    bw.Write((byte)0);            // height 0 表示 256
                    bw.Write((byte)0);            // palette
                    bw.Write((byte)0);            // reserved
                    bw.Write((ushort)1);          // color planes
                    bw.Write((ushort)32);         // bits per pixel
                    bw.Write((uint)png.Length);   // data size
                    bw.Write((uint)22);           // data offset = 6 + 16
                    bw.Write(png);
                }
                return dst;
            }
            catch (Exception ex)
            {
                Log("[-] 图标转换异常: " + ex.Message, "err");
                return null;
            }
        }

        void SetVersion(object sender, RoutedEventArgs e)
        {
            var w = new VersionDialog(_verText ?? "1.0.0") { Owner = this };
            if (w.ShowDialog() == true)
            {
                _verText = w.Result;
                _verAuto = false;
                // 标签上写带说明的文字，_verText 保持纯版本号供资源文件使用
                VerText.Text = w.Result + " · 已设置";
                VerText.ToolTip = "版本资源将写入: " + w.Result;
                VerText.Foreground = (Brush)FindResource("Green");
                Log("[*] 版本信息: " + w.Result, "acc");
            }
        }

        void OpenOutDir(object sender, RoutedEventArgs e)
        {
            try
            {
                string d = (OutDir.Text ?? "").Trim();
                if (d.Length == 0) d = DefaultOutDir();   // 空值不要掉进安装目录
                if (!Directory.Exists(d)) { Log("[-] 目录不存在: " + d, "err"); return; }
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + d + "\"")
                { UseShellExecute = true });
            }
            catch (Exception ex) { Log("[-] 打开目录失败: " + ex.Message, "err"); }
        }

        void CopyLog(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(ComposeLogText());
                Status("日志已复制到剪贴板", (Brush)FindResource("Blue"));
            }
            catch { }
        }

        void ClearLog(object sender, RoutedEventArgs e)
        {
            lock (_log) { _log.Clear(); _logEntries.Clear(); }
            LogBox.Document.Blocks.Clear();
        }

        void AbortBuild(object sender, RoutedEventArgs e)
        {
            if (!_busy) return;
            _abort = true;
            try { if (_proc != null && !_proc.HasExited) _proc.Kill(true); } catch { }
            Log("[*] 已请求终止打包", "warn");
            Status("正在终止…", (Brush)FindResource("Orange"));
        }

        // ══════════════ 拖拽 ══════════════
        void OnDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        void OnDrop(object sender, DragEventArgs e)
        {
            if (_busy) { Status("打包进行中，拖拽已禁用", (Brush)FindResource("Orange")); return; }
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            foreach (var f in files)
            {
                if (f.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
                {
                    LoadScript(f);
                    return;
                }
            }
            Log("[-] 拖入的不是 .py 文件", "err");
        }

        // ══════════════ 打包主流程 ══════════════
        static string ProductPathFor(BuildOptions o)
        {
            string p = Path.Combine(o.OutDir, o.OutName);
            return o.OneDir ? p : p + ".exe";
        }

        async void StartBuild(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            if (_batchMode || SwitchIsBatch())
            {
                try { await RunBatch(); }
                catch (Exception ex) { Log("[-] 批量异常: " + ex.Message, "err"); }
                return;
            }

            if (_script == null || !File.Exists(_script))
            {
                Log("[-] 请先选择脚本", "err");
                Status("请先选择脚本", (Brush)FindResource("Orange"));
                return;
            }
            string outName = OutName.Text.Trim();
            if (outName.Length == 0)
            {
                Log("[-] 请输入输出名", "err");
                Status("请输入输出名", (Brush)FindResource("Orange"));
                return;
            }
            if (!IsValidName(outName, out string why))
            {
                Log("[-] 输出名不可用: " + why, "err");
                Status("输出名不可用", (Brush)FindResource("Pink"));
                return;
            }

            Status("正在检查打包引擎…", (Brush)FindResource("Label2"));
            if (!EnsureEngine())
            {
                Log("[-] 没有可用的 PyInstaller，无法打包", "err");
                Log("[!] 请安装: pip install pyinstaller", "warn");
                Status("缺少打包引擎", (Brush)FindResource("Pink"));
                return;
            }
            var prefix = EnginePrefix();
            if (prefix == null)
            {
                Log("[-] 引擎在验证后被判定不可用", "err");
                Status("缺少打包引擎", (Brush)FindResource("Pink"));
                return;
            }

            _lastDiagText = null;
            lock (_log) _runLogStart = _logEntries.Count;
            HideResult();
            _busy = true; _abort = false;
            BtnStart.IsEnabled = false;
            BtnAbort.IsEnabled = true;
            SetRunning(true, "正在打包", "准备中…");
            ClearStats();
            Status("打包中", (Brush)FindResource("Orange"));

            var opt = new BuildOptions
            {
                Script = _script,
                OutName = outName,
                OutDir = OutDir.Text.Trim().Length > 0
                         ? OutDir.Text.Trim() : DefaultOutDir(),
                OneDir = RbOneDir.IsChecked == true,
                WindowMode = RbConsole.IsChecked == true ? "console"
                           : RbNoConsole.IsChecked == true ? "noconsole" : "auto",
                Uac = ChkUac.IsChecked == true,
                Clean = ChkClean.IsChecked == true,
                Timeout = ParseTimeout(Timeout.Text),
                Icon = _icon,
                Version = _verText,
                Prefix = prefix,
                TestRun = ChkSelfTest.IsChecked != false,
                TestArgs = SplitArgs(TestArgs.Text),
                CheckDeps = ChkCheckDeps.IsChecked != false,
                HiddenImports = SplitList(AdvHidden.Text),
                Excludes = SplitList(AdvExclude.Text),
                AddData = _dataItems
                    .Select(d => d.Source + ";" + d.Dest).ToList(),
                ExtraArgs = SplitArgs(AdvExtra.Text),
                SignPfx = AdvSignPfx.Text.Trim(),
                SignPassword = AdvSignPwd.Password,
                TimestampUrl = AdvSignTs.Text.Trim()
            };

            var sw = Stopwatch.StartNew();
            var tick = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(500) };
            tick.Tick += (a, b) => StatElapsed.Text = ((int)sw.Elapsed.TotalSeconds) + "s";
            tick.Start();

            bool ok = false;
            string err = null;
            await Task.Run(() =>
            {
                try { ok = Build(opt); }
                catch (Exception ex) { err = ex.Message; }
            });

            tick.Stop();
            sw.Stop();
            _busy = false;
            BtnStart.IsEnabled = true;
            BtnAbort.IsEnabled = false;

            if (err != null)
            {
                Log("[-] 异常: " + err, "err");
                SetRunning(false, "打包失败", "详情见输出日志");
                Status("打包失败", (Brush)FindResource("Pink"));
                ShowFailureResult(_lastDiagText ?? err);
                AddHistory(outName, "失败", sw.Elapsed.TotalSeconds,
                           _script, null, _lastDiagText ?? err);
                SaveRunLog(outName);
            }
            else if (ok)
            {
                SetRunning(false, "打包成功",
                           "耗时 " + sw.Elapsed.TotalSeconds.ToString("0.0") + "s");
                Status("打包成功", (Brush)FindResource("Green"));
                string prod = ProductPathFor(opt);
                TrySignProduct(outName, prod, opt);
                ShowSuccessResult(outName, prod, sw.Elapsed.TotalSeconds);
                AddHistory(outName, "成功", sw.Elapsed.TotalSeconds,
                           _script, prod, null, SizeOfProduct(prod));
                SaveRunLog(outName);
                Progress.Value = 100;
                ProgressText.Text = "100%";
            }
            else
            {
                SetRunning(false, "打包失败", "详情见输出日志");
                Status(_abort ? "已终止" : "打包失败",
                       (Brush)FindResource(_abort ? "Orange" : "Pink"));
                ShowFailureResult(_abort ? "已终止（详情见输出日志）"
                    : (_lastDiagText ?? "详情见输出日志"));
                AddHistory(outName, _abort ? "已终止" : "失败",
                           sw.Elapsed.TotalSeconds, _script, null, _lastDiagText);
                SaveRunLog(outName);
            }
        }

        bool SwitchIsBatch() => ViewBatch.Visibility == Visibility.Visible;

        static int ParseTimeout(string s)
        {
            if (int.TryParse((s ?? "").Trim(), out int v))
                return Math.Max(60, Math.Min(86400, v));
            return DEFAULT_TIMEOUT;
        }

        /// <summary>Windows 文件名合法性 + 保留设备名</summary>
        static bool IsValidName(string n, out string why)
        {
            why = null;
            if (n.Length == 0) { why = "不能为空"; return false; }
            if (n.EndsWith(".") || n.EndsWith(" ")) { why = "不能以点或空格结尾"; return false; }
            foreach (char c in Path.GetInvalidFileNameChars())
                if (n.IndexOf(c) >= 0) { why = "含非法字符 " + c; return false; }
            string[] reserved = { "CON", "PRN", "AUX", "NUL",
                "COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
                "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9" };
            string stem = n.Split('.')[0].Trim().ToUpperInvariant();
            foreach (var r in reserved)
                if (stem == r) { why = "是系统保留名 " + r; return false; }
            return true;
        }

        class BuildOptions
        {
            public string Script, OutName, OutDir, WindowMode, Icon, Version;
            public bool OneDir, Uac, Clean;
            public bool Batch;
            public int Timeout;
            public List<string> Prefix;
            /// <summary>打包成功后是否启动产物自检</summary>
            public bool TestRun = true;
            /// <summary>自检产物时附加的参数（有些脚本必须带参数才能启动）</summary>
            public List<string> TestArgs = new List<string>();
            /// <summary>打包前是否检查脚本依赖是否都已安装</summary>
            public bool CheckDeps = true;
            /// <summary>手动补充的 hidden-import</summary>
            public List<string> HiddenImports = new List<string>();
            /// <summary>要排除的模块</summary>
            public List<string> Excludes = new List<string>();
            /// <summary>附加数据，每项为 "源路径;目标目录"</summary>
            public List<string> AddData = new List<string>();
            /// <summary>原样追加给 PyInstaller 的额外参数</summary>
            public List<string> ExtraArgs = new List<string>();
            /// <summary>可选：打包成功后给产物签名</summary>
            public string SignPfx, SignPassword, TimestampUrl;

            /// <summary>克隆并追加额外的 PyInstaller 参数（用于自动修复重试）</summary>
            public BuildOptions WithExtraArgs(List<string> extra)
            {
                if (extra == null || extra.Count == 0) return this;
                var p = new List<string>(Prefix);
                p.AddRange(extra);
                return new BuildOptions
                {
                    Script = Script, OutName = OutName, OutDir = OutDir,
                    OneDir = OneDir, WindowMode = WindowMode, Uac = Uac,
                    Clean = Clean, Timeout = Timeout, Icon = Icon,
                    Version = Version, Prefix = p, Batch = Batch,
                    TestRun = TestRun, TestArgs = TestArgs,
                    CheckDeps = CheckDeps,
                    HiddenImports = new List<string>(HiddenImports),
                    Excludes = new List<string>(Excludes),
                    AddData = new List<string>(AddData),
                    ExtraArgs = new List<string>(ExtraArgs),
                    SignPfx = SignPfx, SignPassword = SignPassword,
                    TimestampUrl = TimestampUrl
                };
            }
        }

        bool Build(BuildOptions o)
        {
            try
            {
                return BuildWithRetry(o);
            }
            finally
            {
                // 无论成功、失败还是异常，临时文件都要清掉
                CleanupTemp();
            }
        }

        /// <summary>带自动修复的打包：失败时按诊断补 hidden-import 再试</summary>
        bool BuildWithRetry(BuildOptions o)
        {
            var applied = new HashSet<string>();   // 已用过的修复动作，避免死循环
            var addArgs = new List<string>();      // 累积追加的 PyInstaller 参数
            const int MAX_TRY = 4;
            string diag;

            for (int attempt = 1; attempt <= MAX_TRY; attempt++)
            {
                bool success = BuildCore(o.WithExtraArgs(addArgs), out diag);

                // PyInstaller 对「import 不存在的模块」是哑的：它照样返回 0、
                // 照样产出 exe，坏的只是产物。所以缺模块只能从产物自检里拿到，
                // 这条路径必须一起参与重试判断。
                if (success && !_selftestFailed)
                {
                    if (attempt > 1)
                        Log("[+] 自动修复后打包并自检通过（第 " + attempt
                            + " 次尝试）", "ok");
                    return true;
                }

                if (_abort) return false;

                // 打包失败看打包输出；打包成功但产物崩，看自检输出。
                // 两条路径都可能给出可自动执行的修复动作。
                var d = null as Diagnosis;
                try
                {
                    if (!success && !string.IsNullOrEmpty(diag))
                        d = BuildDoctor.Analyze(1, diag);
                    else if (success && !string.IsNullOrEmpty(_selftestText))
                        d = BuildDoctor.Analyze(_selftestCode, _selftestText);
                }
                catch { }

                var todo = new List<string>();     // 展示用
                if (d != null)
                {
                    foreach (var m in d.MissingModules)
                    {
                        if (!applied.Add("hidden:" + m)) continue;
                        addArgs.Add("--hidden-import"); addArgs.Add(m);
                        todo.Add(m + " (import)");
                    }
                    foreach (var m in d.CollectData)
                    {
                        if (!applied.Add("data:" + m)) continue;
                        addArgs.Add("--collect-data"); addArgs.Add(m);
                        todo.Add(m + " (数据文件)");
                    }
                    foreach (var m in d.CollectBinaries)
                    {
                        if (!applied.Add("bin:" + m)) continue;
                        addArgs.Add("--collect-binaries"); addArgs.Add(m);
                        todo.Add(m + " (二进制)");
                    }
                    foreach (var m in d.CollectSubmodules)
                    {
                        if (!applied.Add("sub:" + m)) continue;
                        addArgs.Add("--collect-submodules"); addArgs.Add(m);
                        todo.Add(m + " (子模块)");
                    }
                }

                if (todo.Count == 0 || attempt == MAX_TRY)
                {
                    if (success)
                    {
                        Log("[-] 产物自检未通过，且没有可自动执行的修复动作", "err");
                        ShowSelftestFailed();
                    }
                    return false;
                }

                string what = string.Join(", ", todo);
                Log("[!] 检测到可自动修复的问题: " + what, "warn");
                Log("[*] 已追加修复参数，重试中（第 " + (attempt + 1)
                    + " 次 / 共 " + MAX_TRY + " 次）…", "acc");
                if (_batchMode) Blog("[!] 自动修复: " + what, "warn");
                SetRun("自动修复重试", "已补 " + what);
            }
            return false;
        }

        /// <summary>把 BuildDoctor 的诊断写进日志</summary>
        void PrintDiagnosis(int exitCode, string output)
        {
            if (string.IsNullOrEmpty(output)) return;
            try
            {
                string report = BuildDoctor.Analyze(exitCode, output).Render();
                _lastDiagText = report;
                Log("", "dim");
                foreach (var line in report.Split('\n'))
                {
                    Log(line, "warn");
                    if (_batchMode) Blog(line, "warn");
                }
            }
            catch (Exception ex) { Log("[!] 诊断失败: " + ex.Message, "warn"); }
        }

        /// <summary>最近一次依赖检查发现的缺失模块，供一键安装使用</summary>
        List<string> _lastDepIssues;

        /// <summary>一键安装缺失依赖（走与打包相同的运行时）</summary>
        async void InstallDeps(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var py = FindPythonForCheck();
            if (py == null)
            {
                MessageBox.Show("没有可用的 Python 运行时，无法安装。",
                                "安装依赖", MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                return;
            }
            if (_lastDepIssues == null || _lastDepIssues.Count == 0)
            {
                MessageBox.Show("还没有检测到缺失依赖。\n先点「检查依赖」。",
                                "安装依赖", MessageBoxButton.OK,
                                MessageBoxImage.Information);
                return;
            }

            string names = string.Join(" ", _lastDepIssues);
            var r = MessageBox.Show(
                "将使用内置运行时安装以下模块：\n\n" + names
                + "\n\n来源：阿里云 PyPI 镜像（国内可直连）",
                "安装依赖", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r != MessageBoxResult.OK) return;

            _busy = true;
            SetRun("安装依赖", names);
            Log("[*] 安装依赖: " + names, "acc");
            foreach (var line in ("命令: " + py + " -m pip install " + names
                                  + " -i " + PipMirror).Split('\n'))
                Log(line, "dim");

            try
            {
                var args = new List<string> { "-m", "pip", "install" };
                args.AddRange(_lastDepIssues);
                args.Add("-i"); args.Add(PipMirror);
                args.Add("--disable-pip-version-check");

                var result = await Task.Run(() => _runner.Run(
                    BuildCommand.From(py, null, args, null, 600000),
                    line => Dispatcher.BeginInvoke(new Action(() =>
                        Log(line, Classify(line))))));

                if (result.ExitCode == 0)
                {
                    Log("[+] 依赖安装完成", "ok");
                    Status("依赖已安装", (Brush)FindResource("Green"));
                    _lastDepIssues = null;
                }
                else
                {
                    Log("[-] 安装失败，退出码 " + result.ExitCode, "err");
                    foreach (var line in result.StdErr.Split('\n'))
                        if (line.Trim().Length > 0) Log(line, "err");
                    Status("安装失败", (Brush)FindResource("Pink"));
                }
            }
            catch (Exception ex) { Log("[-] 安装异常: " + ex.Message, "err"); }
            finally { _busy = false; SetRun("就绪", "等待开始"); }
        }

        /// <summary>手动触发一次依赖检查（不动打包流程）</summary>
        void CheckDepsNow(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (string.IsNullOrEmpty(_script))
            {
                Log("[!] 请先选择脚本", "warn");
                return;
            }
            var py = FindPythonForCheck();
            if (py == null) { Log("[!] 没有可用的 Python 运行时", "warn"); return; }

            SetRun("依赖检查", Path.GetFileName(_script));
            Log("[*] 依赖检查…", "dim");
            try
            {
                var rep = DependencyChecker.Check(_script, py, _runner);
                foreach (var line in rep.RenderDetail(Path.GetFileName(_script))
                                      .Split('\n'))
                    if (line.Trim().Length > 0)
                        Log(line, rep.Clean ? "ok" : "warn");
                _lastDepIssues = rep.Missing.Count > 0 ? rep.Missing : null;
                Status(rep.Clean ? "依赖齐全" : "依赖缺失",
                       (Brush)FindResource(rep.Clean ? "Green" : "Pink"));
            }
            catch (Exception ex) { Log("[-] 检查失败: " + ex.Message, "err"); }
            SetRun("就绪", "等待开始");
        }

        const string PipMirror = "https://mirrors.aliyun.com/pypi/simple/";

        /// <summary>最近一次产物自检的结果，供自动修复使用</summary>
        string _selftestText;     // 自检抓到的完整输出
        int _selftestCode;        // 自检退出码
        bool _selftestFailed;     // 自检是否判定为失败
        string _selftestReport;   // 渲染好的诊断文本（给弹窗用）

        /// <summary>自检失败且无法自动修复时提示用户</summary>
        void ShowSelftestFailed()
        {
            string msg = "打包本身成功，但产物启动即失败。\n\n"
                       + (_selftestReport ?? "（无诊断信息）")
                       + "\n建议改用「控制台」模式重新打包，可看到完整报错。";
            Dispatcher.BeginInvoke(new Action(() =>
                MessageBox.Show(msg, "产物自检未通过",
                                MessageBoxButton.OK, MessageBoxImage.Warning)));
        }

        /// <summary>启动产物做自检，结论写入 _selftest* 字段</summary>
        bool SelfTestProduct(BuildOptions o)
        {
            _selftestText = null;
            _selftestCode = 0;
            _selftestFailed = false;
            _selftestReport = null;

            string exe = o.OneDir
                ? Path.Combine(o.OutDir, o.OutName, o.OutName + ".exe")
                : Path.Combine(o.OutDir, o.OutName + ".exe");
            if (!File.Exists(exe))
            {
                string alt = Path.Combine(o.OutDir, "dist", o.OutName + ".exe");
                if (File.Exists(alt)) exe = alt;
                else return true;              // 找不到产物：交给上层判定
            }

            var testArgs = o.TestArgs != null && o.TestArgs.Count > 0
                ? o.TestArgs.ToArray() : new string[0];
            string argNote = testArgs.Length > 0
                ? "（带 " + testArgs.Length + " 个自检参数）" : "";

            Log("[*] 产物自检：启动 " + Path.GetFileName(exe) + argNote
                + "（最多等 6 秒）…", "dim");
            SetRun("产物自检", Path.GetFileName(exe));

            string captured = null;
            var cmd = BuildCommand.From(exe, null, testArgs,
                                        Path.GetDirectoryName(exe), 6000);
            var sw = Stopwatch.StartNew();
            RunResult r;
            try
            {
                r = _runner.Run(cmd, line =>
                {
                    captured = captured == null ? line : captured + "\n" + line;
                });
            }
            catch (Exception ex)
            {
                Log("[!] 产物自检未能执行: " + ex.Message, "warn");
                return true;
            }
            sw.Stop();

            _selftestText = captured ?? r.Combined;
            _selftestCode = r.ExitCode;

            // 6 秒不退出：常驻 GUI 程序的正常表现
            if (r.TimedOut || r.ExitCode == 0)
            {
                Log("[+] 产物自检通过："
                    + (r.TimedOut ? "运行 6 秒未退出" : "正常退出"), "ok");
                if (!string.IsNullOrEmpty(captured))
                    Log("[*] 产物输出: " + captured.Replace("\n", " | "), "dim");
                if (_batchMode) Blog("[+] 产物自检通过", "ok");
                return true;
            }

            // 用法错误不算产物有问题：脚本本来就要收参数
            if (LooksLikeUsageError(_selftestText))
            {
                _selftestFailed = false;
                Log("[+] 产物能启动，但它要求命令行参数才继续运行", "ok");
                Log("[!] 自检按「通过」处理。若要真正跑完整流程，"
                    + "在「自检参数」里填上参数后重打。", "warn");
                if (!string.IsNullOrEmpty(captured))
                    Log("[*] 产物输出: " + captured.Replace("\n", " | "), "dim");
                if (_batchMode) Blog("[+] 产物需参数，自检按通过处理", "warn");
                return true;
            }

            _selftestFailed = true;
            Log("[-] 产物自检失败：" + sw.Elapsed.TotalSeconds.ToString("0.0")
                + " 秒内以退出码 " + r.ExitCode + " 退出", "err");
            if (!string.IsNullOrEmpty(captured))
                Log("[*] 产物输出: " + captured.Replace("\n", " | "), "err");

            _selftestReport = BuildDoctor.Analyze(r.ExitCode, _selftestText).Render();
            Log("", "dim");
            foreach (var line in _selftestReport.Split('\n')) Log(line, "warn");
            if (_batchMode) Blog("[!] 产物启动失败，见诊断", "warn");
            return false;
        }

        bool BuildCore(BuildOptions o, out string diagText)
        {
            diagText = null;

            // ── 1) 语法检查 ──
            SetRun("语法检查", Path.GetFileName(o.Script));
            Log("[*] 语法检查…", "dim");
            if (!SyntaxCheck(o.Script, out string syntaxErr))
            {
                Log("[-] 语法检查未通过: " + syntaxErr, "err");
                SetRun("语法错误", "打包未开始");
                Status("语法错误", (Brush)FindResource("Pink"));
                return false;
            }
            else if (syntaxErr == null && FindPythonForCheck() == null)
            {
                Log("[!] 未找到可用 Python，已跳过语法检查", "warn");
            }
            Log("[*] 语法检查通过", "ok");

            // ── 1.5) 依赖前置检查 ──
            // 放在打包前：脚本 import 的模块如果本机没装，
            // 后面整条「补 hidden-import 重试」都是白跑 —— 装都没装，
            // 补什么都不可能找得到。这里直接说清楚缺什么。
            if (o.CheckDeps)
            {
                try
                {
                    var py = FindPythonForCheck();
                    if (py != null)
                    {
                        SetRun("依赖检查", Path.GetFileName(o.Script));
                        Log("[*] 依赖检查…", "dim");
                        var rep = DependencyChecker.Check(o.Script, py, _runner);
                        foreach (var line in rep.RenderBrief().Split('\n'))
                            if (line.Trim().Length > 0) Log(line,
                                rep.Clean ? "dim" : "warn");

                        if (rep.Ran && rep.Missing.Count > 0)
                        {
                            _lastDepIssues = rep.Missing;
                            _lastDiagText = "依赖检查发现 " + rep.Missing.Count
                                + " 个模块未安装：" + string.Join("、", rep.Missing)
                                + "\n安装命令: python -m pip install "
                                + string.Join(" ", rep.Missing)
                                + "\n（已跳过打包，装好依赖后再试）";
                            Log("[-] 有 " + rep.Missing.Count
                                + " 个模块在本机运行时里没装，打包后产物会崩", "err");
                            Log("[!] 已跳过打包。先装依赖，或关闭「打包前检查依赖」",
                                "warn");
                            if (_batchMode) Blog("[-] 依赖缺失: "
                                + string.Join(", ", rep.Missing), "err");
                            SetRun("依赖缺失", string.Join(", ", rep.Missing));
                            Status("依赖缺失", (Brush)FindResource("Pink"));
                            return false;
                        }
                        _lastDepIssues = null;
                    }
                }
                catch (Exception ex)
                {
                    // 检查本身出问题不能挡住打包
                    Log("[!] 依赖检查未完成: " + ex.Message, "warn");
                }
            }

            // ── 2) 组装命令 ──
            var args = new List<string>(o.Prefix);
            args.Add(o.OneDir ? "--onedir" : "--onefile");
            args.Add(o.WindowMode == "console" ? "--console"
                   : o.WindowMode == "noconsole" ? "--noconsole"
                   : (HasGui(o.Script) ? "--noconsole" : "--console"));
            args.Add("--name"); args.Add(o.OutName);
            args.Add("--noconfirm");
            args.Add("--hidden-import"); args.Add("modulefinder");
            args.Add("--hidden-import"); args.Add("importlib.util");

            // 手动高级参数（留空就等于没有）。自动重试仍失败时由用户补充。
            foreach (var m in o.HiddenImports ?? new List<string>())
            {
                args.Add("--hidden-import"); args.Add(m);
                Log("[*] 手动 hidden-import: " + m, "dim");
            }
            foreach (var m in o.Excludes ?? new List<string>())
            {
                args.Add("--exclude-module"); args.Add(m);
                Log("[*] 排除模块: " + m, "dim");
            }
            foreach (var d in o.AddData ?? new List<string>())
            {
                args.Add("--add-data"); args.Add(d);
                Log("[*] 附加数据: " + d, "dim");
            }
            foreach (var a in o.ExtraArgs ?? new List<string>())
                args.Add(a);

            // 图标：用户选择优先，否则用内嵌默认图标
            string ico = o.Icon;
            if (ico == null || !File.Exists(ico))
            {
                ico = ExtractDefaultIcon();
                if (ico != null) Log("[*] 使用内置默认图标", "dim");
            }
            if (ico != null) { args.Add("--icon"); args.Add(ico); }

            if (o.Uac) { args.Add("--uac-admin"); Log("[*] 已启用管理员提权 (UAC)", "dim"); }

            // 版本信息
            if (!string.IsNullOrEmpty(o.Version) && o.Version != "未设置")
            {
                string vf = WriteVersionFile(o.OutName, o.Version);
                if (vf != null) { args.Add("--version-file"); args.Add(vf); }
            }
            args.Add(o.Script);

            Log("[*] 词法: " + o.Prefix[0], "dim");
            Log("[*] 命令: " + string.Join(" ", args), "dim");

            // ── 3) 工作目录：放在输出目录下，避免污染脚本目录 ──
            string work = o.OutDir;
            if (!Directory.Exists(work))
            {
                try { Directory.CreateDirectory(work); }
                catch (Exception ex)
                {
                    Log("[-] 无法创建输出目录: " + ex.Message, "err");
                    return false;
                }
            }

            // 记录构建前已存在的产物，便于只清理本次新建的
            string distPath = Path.Combine(work, "dist");
            string buildPath = Path.Combine(work, "build");
            bool distExisted = Directory.Exists(distPath);
            bool buildExisted = Directory.Exists(buildPath);
            string specPath = Path.Combine(work, o.OutName + ".spec");
            bool specExisted = File.Exists(specPath);

            SetRun(o.OneDir ? "构建目录" : "构建 EXE", "PyInstaller 运行中");
            Log("[*] 超时上限 " + o.Timeout + "s", "dim");

            // 传完整前缀：RunProcess 负责拆出可执行文件与它自己的参数。
            // 之前这里传 Prefix[0] 但 args 里又含整套 Prefix，
            // 结果 pyinstaller.exe 变成位置参数，PyInstaller 报
            // "unrecognized arguments: <脚本路径>"
            var runLines = new List<string>();
            int rc = RunProcess(o.Prefix, args, work, o.Timeout,
                                line => runLines.Add(line));
            diagText = string.Join("\n", runLines);
            if (_abort) { Log("[*] 已终止", "warn"); return false; }
            if (rc == -1)
            {
                Log("[-] 打包超时被终止（当前上限 " + o.Timeout + "s）", "err");
                return false;
            }
            if (rc != 0)
            {
                Log("[-] PyInstaller 返回码 " + rc + "，打包失败", "err");
                PrintDiagnosis(rc, diagText);
                return false;
            }

            // ── 4) 搬运产物：dist 本身就在输出目录，无需搬 ──
            string produced = o.OneDir
                ? Path.Combine(distPath, o.OutName, o.OutName + ".exe")
                : Path.Combine(distPath, o.OutName + ".exe");

            // 输出目录就是工作目录时，dist 下的产物即最终产物；搬到根更直观
            string final = o.OneDir
                ? Path.Combine(work, o.OutName)
                : Path.Combine(work, o.OutName + ".exe");

            bool moved = false;
            try
            {
                if (File.Exists(produced) && !PathsEqual(produced, final))
                {
                    if (File.Exists(final)) File.Delete(final);
                    File.Move(produced, final);
                    moved = true;
                }
                else if (o.OneDir && Directory.Exists(Path.GetDirectoryName(produced)))
                {
                    string srcDir = Path.GetDirectoryName(produced);
                    if (!PathsEqual(srcDir, final))
                    {
                        if (Directory.Exists(final)) Directory.Delete(final, true);
                        Directory.Move(srcDir, final);
                        moved = true;
                    }
                    else moved = true;
                }
            }
            catch (Exception ex)
            {
                Log("[!] 搬运产物失败: " + ex.Message, "warn");
                Log("[!] 构建结果仍在: " + produced, "warn");
            }

            string reportPath = o.OneDir ? final : final;
            if (File.Exists(produced) || moved || File.Exists(reportPath))
            {
                string exeForSize = o.OneDir
                    ? Path.Combine(reportPath, o.OutName + ".exe") : reportPath;
                long size = 0;
                try { if (File.Exists(exeForSize)) size = new FileInfo(exeForSize).Length; }
                catch { }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    StatSize.Text = size > 0
                        ? (size / 1048576.0).ToString("0.0") + "M" : "完成";
                    if (o.OneDir) Log("[+] 输出目录: " + reportPath, "ok");
                    else Log("[+] 输出: " + reportPath, "ok");
                    if (size > 0)
                        Log("[*] 文件大小: " + (size / 1048576.0).ToString("0.00") + " MB", "ok");
                    Log("[+] 所有依赖已打包进 EXE，目标机无需安装 Python", "ok");
                }));
            }
            else
            {
                Log("[-] 未找到构建产物: " + produced, "err");
                return false;
            }

            // ── 5) 清理：只删本次新建的 ──
            if (o.Clean)
            {
                try
                {
                    if (!buildExisted && Directory.Exists(buildPath))
                    { Directory.Delete(buildPath, true); Log("[*] 清理: build", "dim"); }
                    if (!specExisted && File.Exists(specPath))
                    { File.Delete(specPath); Log("[*] 清理: " + o.OutName + ".spec", "dim"); }
                    if (!distExisted && Directory.Exists(distPath))
                    { Directory.Delete(distPath, true); Log("[*] 清理: dist", "dim"); }
                }
                catch (Exception ex) { Log("[!] 清理失败: " + ex.Message, "warn"); }
            }

            // ── 6) 产物自检：提前暴露「能打包但跑不起来」 ──
            if (o.TestRun) SelfTestProduct(o);

            return true;
        }

        static bool PathsEqual(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
                                     StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>通用：跑一个进程并抓取输出，带超时。返回退出码，超时返回 -1。</summary>
        static int RunCapture(string exe, List<string> args, int timeoutMs,
                              out string stdout, out string stderr)
        {
            stdout = ""; stderr = "";
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                // GUI 进程创建子进程时必须显式不建窗口，
                // 否则可能尝试分配控制台而阻塞
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using (var p = new Process { StartInfo = psi })
            {
                var so = new StringBuilder();
                var se = new StringBuilder();
                var done = new ManualResetEventSlim(false);
                // 关键：stdout 与 stderr 都必须异步读完。
                // 只读一条流、另一条不读，写满管道缓冲区就会死锁。
                p.OutputDataReceived += (s, e) =>
                { if (e.Data != null) so.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) =>
                { if (e.Data != null) se.AppendLine(e.Data); };
                p.Exited += (s, e) => done.Set();

                p.Start();
                // 必须在这里再设一次：Process.Start() 会把 EnableRaisingEvents
                // 重置为 false，导致 Exited 不能及时触发（实测延迟到超时）
                p.EnableRaisingEvents = true;
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                if (!done.Wait(timeoutMs))
                {
                    try { p.Kill(true); } catch { }
                    try { p.WaitForExit(5000); } catch { }
                    stdout = so.ToString(); stderr = se.ToString();
                    return -1;
                }
                p.WaitForExit();
                stdout = so.ToString();
                stderr = se.ToString();
                return p.ExitCode;
            }
        }

        /// <summary>语法检查。用 ArgumentList 传参，不拼命令行字符串。</summary>
        bool SyntaxCheck(string script, out string err)
        {
            err = null;
            string py = FindPythonForCheck();
            if (py == null)
            {
                Log("[!] 无 Python 解释器，跳过语法检查", "warn");
                return true;
            }
            try
            {
                string code = "import py_compile,sys;" +
                              "py_compile.compile(sys.argv[1], doraise=True)";
                int rc = RunCapture(py,
                    new List<string> { "-c", code, script }, 30000,
                    out string so, out string se);

                if (rc == 0) return true;

                if (rc == -1)
                {
                    // 超时算检查未完成，不拦截打包，但要让用户知道
                    Log("[!] 语法检查超时，已跳过（不阻塞打包）", "warn");
                    return true;
                }

                string msg = (se + "\n" + so).Trim();
                int i = msg.IndexOf("SyntaxError", StringComparison.Ordinal);
                if (i < 0) i = msg.IndexOf("Error", StringComparison.Ordinal);
                err = i >= 0 ? msg.Substring(i) : msg;
                if (err.Length == 0) err = "退出码 " + rc;
                return false;
            }
            catch (Exception ex)
            {
                // 不再静默返回 true：检查本身出错时明确告知，并放行
                Log("[!] 语法检查无法执行: " + ex.Message + "（继续打包）", "warn");
                return true;
            }
        }

        /// <summary>判断一个 python.exe 是否真的能跑（排除 WindowsApps 的 0 字节别名）</summary>
        bool UsablePython(string py)
        {
            if (string.IsNullOrEmpty(py) || !File.Exists(py)) return false;
            try
            {
                // 应用执行别名占位符通常为 0 字节；再实际跑一次确认
                var fi = new FileInfo(py);
                if (fi.Length < 16 * 1024) return false;
                int rc = RunCapture(py, new List<string> { "-c", "print(1)" },
                                    15000, out _, out _);
                return rc == 0;
            }
            catch { return false; }
        }

        string _pyForCheck;
        bool _pyForCheckDone;

        /// <summary>找一个真正可用的 Python 解释器做语法检查与依赖分析</summary>
        string FindPythonForCheck()
        {
            if (_pyForCheckDone) return _pyForCheck;

            // 0) 随包/集中目录里的运行时最优先。
            //    打包走的是 RuntimeLocator，这里如果不认同一批目录，
            //    就会出现「能打包、但语法检查和依赖检查静默失效」。
            try
            {
                var slot = RuntimeManager.PickUsable(RuntimeManager.Discover());
                if (slot != null && UsablePython(slot.PythonExe))
                {
                    _pyForCheck = slot.PythonExe;
                    _pyForCheckDone = true;
                    return _pyForCheck;
                }
            }
            catch { }

            // 1) 随包运行时（绿色版）
            string bundled = Path.Combine(AppContext.BaseDirectory,
                                          "runtime", "python.exe");
            if (UsablePython(bundled)) { _pyForCheck = bundled; _pyForCheckDone = true;
                                         return _pyForCheck; }

            // 2) 逐个性价比验证候选（跳过无效别名）
            foreach (var c in CandidatePythons())
            {
                if (UsablePython(c)) { _pyForCheck = c; _pyForCheckDone = true;
                                       return _pyForCheck; }
            }

            // 3) PATH 上的 python.exe 兜底再验一次
            foreach (var n in new[] { "python.exe", "python3.exe" })
            {
                string w = Which(n);
                if (UsablePython(w)) { _pyForCheck = w; _pyForCheckDone = true;
                                       return _pyForCheck; }
            }

            _pyForCheck = null;
            _pyForCheckDone = true;
            return null;
        }

        static bool HasGui(string script)
        {
            try
            {
                string head = File.ReadAllText(script);
                return System.Text.RegularExpressions.Regex.IsMatch(
                    head, @"\b(tkinter|PyQt5|PyQt6|PySide2|PySide6|wx|pygame)\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>PyInstaller 版本资源文件</summary>
        string WriteVersionFile(string name, string ver)
        {
            try
            {
                var parts = ver.Split('.');
                var nums = new List<int>();
                foreach (var p in parts)
                    if (int.TryParse(p, out int n)) nums.Add(n);
                while (nums.Count < 4) nums.Add(0);
                string tuple = string.Join(", ", nums.GetRange(0, 4));

                string tmpl =
"VSVersionInfo(\n" +
"  ffi=FixedFileInfo(filevers=(" + tuple + "), prodvers=(" + tuple + "),\n" +
"    mask=0x3f, flags=0x0, OS=0x40004, fileType=0x1, subtype=0x0, date=(0,0)),\n" +
"  kids=[StringFileInfo([StringTable('040904B0',[\n" +
"    StringStruct('CompanyName', 'Dainric'),\n" +
"    StringStruct('FileDescription', '" + EscapePy(name) + "'),\n" +
"    StringStruct('FileVersion', '" + EscapePy(ver) + "'),\n" +
"    StringStruct('InternalName', '" + EscapePy(name) + "'),\n" +
"    StringStruct('OriginalFilename', '" + EscapePy(name) + ".exe'),\n" +
"    StringStruct('ProductName', '" + EscapePy(name) + "'),\n" +
"    StringStruct('ProductVersion', '" + EscapePy(ver) + "')])]),\n" +
"    VarFileInfo([VarStruct('Translation', [0x0409, 0x04B0])])])\n";

                string f = Path.Combine(Path.GetTempPath(),
                    "dainric_ver_" + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(f, tmpl, Encoding.UTF8);
                _tmpVersion = f;
                Log("[*] 版本资源: " + ver, "dim");
                return f;
            }
            catch (Exception ex)
            {
                Log("[-] 生成版本文件失败: " + ex.Message, "err");
                return null;
            }
        }

        static string EscapePy(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("'", "\\'");
        }

        /// <summary>把嵌入资源里的默认图标释放到临时文件</summary>
        string ExtractDefaultIcon()
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                string name = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("app.ico",
                                        StringComparison.OrdinalIgnoreCase));
                if (name == null) return null;
                using (var s = asm.GetManifestResourceStream(name))
                {
                    if (s == null) return null;
                    string f = Path.Combine(Path.GetTempPath(),
                        "dainric_ico_" + Guid.NewGuid().ToString("N") + ".ico");
                    using (var fs = File.Create(f)) s.CopyTo(fs);
                    return f;
                }
            }
            catch { return null; }
        }

        // ── 进程执行：逐行读取输出 ──
        int RunProcess(List<string> prefix, List<string> args, string workDir,
                       int timeoutSec, Action<string> onLine = null)
        {
            string exe = prefix[0];
            // prefix 里 exe 之后的元素（如 python.exe -m PyInstaller）是它自己的参数
            var preArgs = prefix.Count > 1
                ? prefix.GetRange(1, prefix.Count - 1)
                : new List<string>();
            // args 的头几个元素就是 prefix，去掉它们避免重复
            var realArgs = new List<string>(args);
            if (realArgs.Count >= prefix.Count)
            {
                bool same = true;
                for (int i = 0; i < prefix.Count; i++)
                    if (!string.Equals(realArgs[i], prefix[i],
                                       StringComparison.OrdinalIgnoreCase))
                    { same = false; break; }
                if (same) realArgs.RemoveRange(0, prefix.Count);
            }

            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var a in preArgs) psi.ArgumentList.Add(a);
            foreach (var a in realArgs) psi.ArgumentList.Add(a);

            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var done = new ManualResetEventSlim(false);
            int lastPct = -1;

            _proc.OutputDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                Log(e.Data, Classify(e.Data));
                onLine?.Invoke(e.Data);
                int p = Estimate(e.Data, ref lastPct);
                if (p >= 0) SetProgress(p);
            };
            _proc.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null) Log(e.Data, "warn");
            };
            _proc.Exited += (s, e) => done.Set();

            _proc.Start();
            // 同上：Start() 后必须重设，否则 Exited 不及时，超时判断会失效
            _proc.EnableRaisingEvents = true;
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();

            if (!done.Wait(TimeSpan.FromSeconds(timeoutSec)))
            {
                Log("[!] 已超时，正在终止 PyInstaller 进程树…", "warn");
                try { _proc.Kill(true); } catch { }
                try { _proc.WaitForExit(5000); } catch { }   // 确认真的退出
                DisposeProc();                                // 超时路径同样要释放
                return -1;
            }
            _proc.WaitForExit();
            int rc = _proc.ExitCode;
            DisposeProc();
            return rc;
        }

        /// <summary>把 PyInstaller 输出映射到 0-100 进度</summary>
        static int Estimate(string line, ref int last)
        {
            int v = last;
            if (line.Contains("Analyzing")) v = Math.Max(v, 8);
            else if (line.Contains("Processing module hooks")) v = Math.Max(v, 20);
            else if (line.Contains("Checking Analysis")) v = Math.Max(v, 25);
            else if (line.Contains("Building PYZ")) v = Math.Max(v, 38);
            else if (line.Contains("Building PKG")) v = Math.Max(v, 50);
            else if (line.Contains("Building COLLECT")) v = Math.Max(v, 62);
            else if (line.Contains("Building EXE")) v = Math.Max(v, 72);
            else if (line.Contains("Copying bootloader")) v = Math.Max(v, 82);
            else if (line.Contains("Appending archive")) v = Math.Max(v, 90);
            else if (line.Contains("Build complete")) v = Math.Max(v, 99);
            else return -1;
            if (v == last) return -1;
            last = v;
            return v;
        }

        static string Classify(string s)
        {
            if (s.StartsWith("ERROR") || s.Contains("ERROR:")) return "err";
            if (s.StartsWith("WARNING")) return "warn";
            if (s.Contains("INFO:")) return "dim";
            return "dim";
        }

        // ══════════════ 界面辅助（跨线程）══════════════
        void SetProgress(int p)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Progress.Value = p;
                ProgressText.Text = p + "%";
            }));
        }

        void SetRun(string title, string sub)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                RunTitle.Text = title;
                RunSub.Text = sub;
            }));
        }

        void SetRunning(bool on, string title, string sub)
        {
            SetRun(title, sub);
            TopStatus.Text = on ? "打包中" : "";
        }

        void ClearStats()
        {
            StatElapsed.Text = "—";
            StatSize.Text = "—";
            Progress.Value = 0;
            ProgressText.Text = "0%";
        }

        void Status(string text, Brush color)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                StatusText.Text = text;
                StatusDot.Fill = color;
            }));
        }

        void CleanupTemp()
        {
            foreach (var f in new[] { _tmpIcon, _tmpVersion })
            {
                try { if (f != null && File.Exists(f)) File.Delete(f); } catch { }
            }
            _tmpIcon = _tmpVersion = null;
        }

        protected override void OnClosed(EventArgs e)
        {
            _abort = true;
            try { if (_sourceHook != null) _sourceHook.RemoveHook(WndProc); } catch { }
            try { if (_proc != null && !_proc.HasExited) _proc.Kill(true); } catch { }
            CleanupTemp();
            base.OnClosed(e);
        }

        // ══════════════ 窗口外观 / 快捷键 / 状态持久化 ══════════════

        /// <summary>强制深色标题栏（Win10 1809+），避免系统浅色主题下白条配黑界面</summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                IntPtr h = new WindowInteropHelper(this).Handle;
                int on = 1;
                if (DwmSetWindowAttribute(h, 20, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(h, 19, ref on, sizeof(int));
                _sourceHook = HwndSource.FromHwnd(h);
                if (_sourceHook != null) _sourceHook.AddHook(WndProc);
            }
            catch { }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            SaveUiState();
            base.OnClosing(e);
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            if (e.Key == Key.Escape)
            {
                if (_busy) { AbortBuild(null, null); e.Handled = true; return; }
                if (_logExpanded) { SetLogExpanded(false); e.Handled = true; return; }
            }
            if (ctrl && e.Key == Key.O && !_busy)
            {
                PickScript(null, null);
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.L)
            {
                ClearLog(null, null);
                e.Handled = true;
            }
            else if (ctrl && shift && e.Key == Key.C)
            {
                CopyLog(null, null);
                e.Handled = true;
            }
            else if ((e.Key == Key.F5 || (ctrl && e.Key == Key.Enter))
                     && BtnStart.IsEnabled && !_busy)
            {
                StartBuild(null, null);
                e.Handled = true;
            }
        }

        void SetLogExpanded(bool on)
        {
            if (on == _logExpanded) return;
            if (on)
            {
                if (SideCol.Width.IsAbsolute) _savedSideWidth = SideCol.Width.Value;
                _savedFormStar = FormCol.Width;
                FormCol.Width = new GridLength(0);
                SplitCol.Width = new GridLength(0);
                SideCol.Width = new GridLength(1, GridUnitType.Star);
                BtnLogExpand.Content = "还原";
                LogSplitter.IsEnabled = false;
            }
            else
            {
                FormCol.Width = _savedFormStar;
                SplitCol.Width = new GridLength(6);
                SideCol.Width = new GridLength(_savedSideWidth > 260 ? _savedSideWidth : 392);
                BtnLogExpand.Content = "放大";
                LogSplitter.IsEnabled = true;
            }
            _logExpanded = on;
        }

        void ToggleLogExpand(object sender, RoutedEventArgs e)
            => SetLogExpanded(!_logExpanded);

        void ShowAbout(object sender, RoutedEventArgs e)
        {
            var w = new AboutDialog(CheckUpdateOnStartup) { Owner = this };
            w.ShowDialog();
            if (w.SettingsChanged)
            {
                CheckUpdateOnStartup = w.CheckUpdateOnStartup;
                SaveUiState();
            }
        }

        /// <summary>启动时静默检查更新；默认每天最多一次</summary>
        async Task CheckUpdateAtStartupAsync()
        {
            try
            {
                if (!CheckUpdateOnStartup) return;
                DateTime last;
                if (!string.IsNullOrEmpty(_lastUpdateCheck)
                    && DateTime.TryParse(_lastUpdateCheck, out last)
                    && (DateTime.Now - last).TotalHours < 24)
                    return;

                _lastUpdateCheck = DateTime.Now.ToString("o");
                try { SaveUiState(); } catch { }

                var info = await UpdateChecker.FetchAsync();
                if (!UpdateChecker.IsNewer(info.Version, APP_VERSION)) return;

                string notes = (info.Notes ?? "").Trim();
                if (notes.Length > 500) notes = notes.Substring(0, 500) + "…";
                var r = MessageBox.Show(this,
                    "发现新版本 v" + info.Version
                    + "\n当前版本 v" + APP_VERSION
                    + "\n\n" + (notes.Length > 0 ? notes : "（没有更新说明）")
                    + "\n\n是否打开下载页？",
                    "检查更新", MessageBoxButton.YesNo,
                    MessageBoxImage.Information);
                if (r == MessageBoxResult.Yes)
                    UpdateChecker.OpenDownload(info);
            }
            catch (Exception ex)
            {
                // 离线 / 清单暂时不可用：静默失败，不打扰使用
                try { Log("[*] 检查更新失败（已忽略）: " + ex.Message, "dim"); } catch { }
            }
        }

        void LoadUiState()
        {
            try
            {
                if (!File.Exists(UiStateFile)) return;
                var st = JsonSerializer.Deserialize<UiState>(
                    File.ReadAllText(UiStateFile));
                if (st == null) return;
                if (st.Width >= MinWidth && st.Height >= MinHeight)
                {
                    var r = new Rect(st.Left, st.Top, st.Width, st.Height);
                    var vr = new Rect(SystemParameters.VirtualScreenLeft,
                                      SystemParameters.VirtualScreenTop,
                                      SystemParameters.VirtualScreenWidth,
                                      SystemParameters.VirtualScreenHeight);
                    if (r.Left < vr.Right - 120 && r.Right > vr.Left + 120
                        && r.Top < vr.Bottom - 80 && r.Bottom > vr.Top + 80)
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual;
                        Left = st.Left; Top = st.Top;
                        Width = st.Width; Height = st.Height;
                    }
                }
                if (st.Maximized) WindowState = WindowState.Maximized;
                if (st.SideWidth >= 260 && st.SideWidth <= 900)
                {
                    SideCol.Width = new GridLength(st.SideWidth);
                    _savedSideWidth = st.SideWidth;
                }
                _savedOutDir = st.LastOutDir;
                _pendingLastScript = st.LastScript;
                AdvHidden.Text = st.AdvHidden ?? "";
                AdvExclude.Text = st.AdvExclude ?? "";
                AdvExtra.Text = st.AdvExtra ?? "";
                AdvSignPfx.Text = st.SignPfx ?? "";
                AdvSignTs.Text = st.SignTs ?? "";
                CheckUpdateOnStartup = st.CheckUpdateOnStartup;
                _lastUpdateCheck = st.LastUpdateCheck;
                _dataItems.Clear();
                if (st.DataItems != null)
                {
                    foreach (var s in st.DataItems)
                    {
                        int k = s.IndexOf('|');
                        if (k <= 0) continue;
                        _dataItems.Add(new DataItem
                        {
                            Source = s.Substring(0, k),
                            Dest = s.Substring(k + 1)
                        });
                    }
                }
                RefreshDataList();
            }
            catch { }
        }

        void SaveUiState()
        {
            try
            {
                var r = WindowState == WindowState.Maximized
                    ? RestoreBounds : new Rect(Left, Top, Width, Height);
                double side = SideCol.Width.IsAbsolute ? SideCol.Width.Value
                    : (SidePanel.ActualWidth > 0 ? SidePanel.ActualWidth : 392);
                var st = new UiState
                {
                    Left = r.Left, Top = r.Top, Width = r.Width, Height = r.Height,
                    Maximized = WindowState == WindowState.Maximized,
                    SideWidth = side,
                    LastScript = _script,
                    LastOutDir = OutDir.Text,
                    AdvHidden = AdvHidden.Text,
                    AdvExclude = AdvExclude.Text,
                    AdvExtra = AdvExtra.Text,
                    SignPfx = AdvSignPfx.Text,
                    SignTs = AdvSignTs.Text,
                    CheckUpdateOnStartup = CheckUpdateOnStartup,
                    LastUpdateCheck = _lastUpdateCheck,
                    DataItems = _dataItems
                        .Select(d => d.Source + "|" + d.Dest).ToList()
                };
                Directory.CreateDirectory(RuntimeLocator.HomeDir);
                File.WriteAllText(UiStateFile,
                    JsonSerializer.Serialize(st, new JsonSerializerOptions
                    { WriteIndented = true }),
                    new UTF8Encoding(false));
            }
            catch { }
        }


        // ══════════════ 页面切换 ══════════════
        void PaintNav(string active)
        {
            var on = (Brush)FindResource("Blue");
            var off = Brushes.Transparent;
            var fgOn = Brushes.White;
            var fgOff = (Brush)FindResource("Label");

            NavSingle.Background = active == "single" ? on : off;
            NavSingleText.Foreground = active == "single" ? fgOn : fgOff;
            NavBatch.Background = active == "batch" ? on : off;
            NavBatchText.Foreground = active == "batch" ? fgOn : fgOff;
            NavDeps.Background = active == "deps" ? on : off;
            NavDepsText.Foreground = active == "deps" ? fgOn : fgOff;
            NavRuntime.Background = active == "runtime" ? on : off;
            NavRuntimeText.Foreground = active == "runtime" ? fgOn : fgOff;
        }

        bool _batchMode;

        void SwitchTo(string page)
        {
            bool single = page == "single";
            bool batch = page == "batch";

            if (!single && _logExpanded) SetLogExpanded(false);

            if (page == "runtime") RefreshRuntimePage();
            ViewRuntime.Visibility = page == "runtime" ? Visibility.Visible
                                                        : Visibility.Collapsed;
            SingleForm.Visibility = single ? Visibility.Visible : Visibility.Collapsed;
            SidePanel.Visibility = single ? Visibility.Visible : Visibility.Collapsed;
            ViewBatch.Visibility = batch ? Visibility.Visible : Visibility.Collapsed;
            ViewDeps.Visibility = page == "deps" ? Visibility.Visible
                                                 : Visibility.Collapsed;
            _batchMode = batch;

            BtnStart.Content = batch ? "开始批量" : "开始打包";
            BtnOpenDir.Visibility = (page == "deps" || page == "runtime")
                ? Visibility.Collapsed : Visibility.Visible;
            PaintNav(page);

            if (page == "deps")
            {
                AnalyzeDeps(null, null);
                RefreshHistory();
            }
        }

        void ShowDeps(object sender, MouseButtonEventArgs e) => SwitchTo("deps");

        // ══════════════ 批量队列 ══════════════
        readonly List<string> _queue = new List<string>();

        void QueueAdd(object sender, RoutedEventArgs e)
        {
            if (_busy) { Log("[!] 打包进行中，无法修改队列", "warn"); return; }
            var dlg = new OpenFileDialog
            {
                Title = "选择要批量打包的脚本",
                Filter = "Python 脚本|*.py|所有文件|*.*",
                Multiselect = true
            };
            if (dlg.ShowDialog() != true) return;
            foreach (var f in dlg.FileNames) QueueAppend(f);
            RefreshQueue();
        }

        void QueueAppend(string path)
        {
            if (!_queue.Contains(path, StringComparer.OrdinalIgnoreCase))
                _queue.Add(path);
        }

        void QueueRemove(object sender, RoutedEventArgs e)
        {
            if (_busy) { Log("[!] 打包进行中，无法修改队列", "warn"); return; }
            var sel = QueueList.SelectedItems.Cast<QueueItem>().ToList();
            foreach (var it in sel) _queue.RemoveAll(
                p => string.Equals(p, it.FullPath, StringComparison.OrdinalIgnoreCase));
            RefreshQueue();
        }

        void QueueClear(object sender, RoutedEventArgs e)
        {
            if (_busy) { Log("[!] 打包进行中，无法修改队列", "warn"); return; }
            _queue.Clear();
            RefreshQueue();
            if (BatchLogBox != null) BatchLogBox.Document.Blocks.Clear();
            BatchStatus.Text = "等待开始";
            BatchDone.Text = "0/0"; BatchOk.Text = "0"; BatchFail.Text = "0";
            BatchProgress.Value = 0; BatchProgressText.Text = "0%";
        }

        void QueueDrop(object sender, DragEventArgs e)
        {
            if (_busy || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            int n = 0;
            foreach (var f in (string[])e.Data.GetData(DataFormats.FileDrop))
            {
                if (f.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
                { QueueAppend(f); n++; }
            }
            if (n > 0) { RefreshQueue(); Blog("[*] 加入 " + n + " 个脚本", "acc"); }
            else Blog("[-] 未发现 .py 文件", "err");
        }

        void PickBatchOutDir(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog { Title = "选择批量输出目录" };
            if (dlg.ShowDialog() == true) BatchOutDir.Text = dlg.FolderName;
        }

        class QueueItem
        {
            public string FullPath { get; set; }
            public string Name { get; set; }
            public string State { get; set; }
            public string Mark { get; set; }
            public string Status { get; set; }
            public string Detail { get; set; }
        }

        void RefreshQueue()
        {
            QueueList.Items.Clear();
            foreach (var p in _queue)
            {
                string mark = "○";
                string st = "等待";
                string state = "wait";
                string detail = "";
                if (_queueState.TryGetValue(p, out string s))
                {
                    if (s == "ok") { mark = "●"; st = "成功"; state = "ok"; }
                    else if (s == "fail") { mark = "●"; st = "失败"; state = "fail"; }
                    else if (s == "run") { mark = "●"; st = "进行中"; state = "run"; }
                }
                if (_queueDetail.TryGetValue(p, out string d)) detail = d;
                QueueList.Items.Add(new QueueItem
                {
                    FullPath = p,
                    Name = Path.GetFileName(p),
                    State = state, Mark = mark, Status = st, Detail = detail
                });
            }
            int n = _queue.Count;
            int ok = _queueState.Values.Count(v => v == "ok");
            int fail = _queueState.Values.Count(v => v == "fail");
            QueueCount.Text = n + " 个文件";
            BatchDone.Text = (ok + fail) + "/" + n;
            BatchOk.Text = ok.ToString();
            BatchFail.Text = fail.ToString();
        }

        readonly Dictionary<string, string> _queueState =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // ══════════════ 批量打包 ══════════════
        async Task<bool> RunBatch()
        {
            if (_queue.Count == 0)
            {
                Log("[-] 队列为空，请先添加脚本", "err");
                Status("队列为空", (Brush)FindResource("Orange"));
                return false;
            }
            Status("正在检查打包引擎…", (Brush)FindResource("Label2"));
            if (!EnsureEngine())
            {
                Log("[-] 没有可用的 PyInstaller，无法打包", "err");
                Log("[!] 请安装: pip install pyinstaller", "warn");
                Status("缺少打包引擎", (Brush)FindResource("Pink"));
                return false;
            }
            var prefix = EnginePrefix();
            if (prefix == null)
            {
                Log("[-] 引擎在验证后被判定不可用", "err");
                Status("缺少打包引擎", (Brush)FindResource("Pink"));
                return false;
            }

            _lastDiagText = null;
            lock (_log) _runLogStart = _logEntries.Count;
            _busy = true; _abort = false;
            BtnStart.IsEnabled = false;
            BtnAbort.IsEnabled = true;
            _queueState.Clear();
            _queueDetail.Clear();
            _lastBatchResults.Clear();
            BtnRetryFailed.IsEnabled = false;
            BtnExportBatch.IsEnabled = false;
            RefreshQueue();

            string outDir = BatchOutDir.Text.Trim().Length > 0
                ? BatchOutDir.Text.Trim() : DefaultOutDir();

            var sw = Stopwatch.StartNew();
            var tick = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(500) };
            tick.Tick += (a, b) =>
            {
                double el = sw.Elapsed.TotalSeconds;
                BatchRunSub.Text = "已用 " + ((int)el) + "s";
            };
            tick.Start();

            int ok = 0, fail = 0, total = _queue.Count;
            for (int i = 0; i < total; i++)
            {
                if (_abort)
                {
                    Blog("[*] 已终止，剩余 " + (total - i) + " 个未处理", "warn");
                    break;
                }
                string script = _queue[i];
                string name = Path.GetFileNameWithoutExtension(script);

                _queueState[script] = "run";
                RefreshQueue();
                BatchRunTitle.Text = "批量 " + (i + 1) + "/" + total;
                BatchRunSub.Text = name;
                BatchStatus.Text = "正在打包 " + (i + 1) + "/" + total;

                Blog("", "dim");
                Blog("────────────────────────────────────────", "dim");
                Blog("[*] [" + (i + 1) + "/" + total + "] " + name, "acc");

                var o = new BuildOptions
                {
                    Script = script,
                    OutName = name,
                    OutDir = outDir,
                    OneDir = RbOneDir.IsChecked == true,
                    WindowMode = RbConsole.IsChecked == true ? "console"
                               : RbNoConsole.IsChecked == true ? "noconsole" : "auto",
                    Uac = ChkUac.IsChecked == true,
                    Clean = ChkClean.IsChecked == true,
                    Timeout = ParseTimeout(Timeout.Text),
                    Icon = _icon,
                    Version = _verText,
                    Prefix = prefix,
                    Batch = true,
                    HiddenImports = SplitList(AdvHidden.Text),
                    Excludes = SplitList(AdvExclude.Text),
                    AddData = _dataItems
                        .Select(d => d.Source + ";" + d.Dest).ToList(),
                    ExtraArgs = SplitArgs(AdvExtra.Text),
                    SignPfx = AdvSignPfx.Text.Trim(),
                    SignPassword = AdvSignPwd.Password,
                    TimestampUrl = AdvSignTs.Text.Trim()
                };

                bool one = false;
                _lastDiagText = null;
                var fsw = Stopwatch.StartNew();
                await Task.Run(() =>
                {
                    try { one = BuildWithRetry(o); }
                    catch (Exception ex) { Blog("[-] 异常: " + ex.Message, "err"); }
                });
                fsw.Stop();

                _queueState[script] = one ? "ok" : "fail";
                if (one) ok++; else fail++;
                Blog("[*] [" + (i + 1) + "/" + total + "] " + name + " -> "
                     + (one ? "成功" : "失败") + " (" + fsw.Elapsed.TotalSeconds
                       .ToString("0.0") + "s)", one ? "ok" : "err");

                string prod = ProductPathFor(o);
                if (one) TrySignProduct(name, prod, o);
                long prodSize = one ? SizeOfProduct(prod) : -1;
                _queueDetail[script] = one
                    ? fsw.Elapsed.TotalSeconds.ToString("0.0") + "s"
                      + (prodSize > 0
                          ? " · " + (prodSize / 1048576.0).ToString("0.0") + " MB"
                          : "")
                    : "失败";
                _lastBatchResults.Add(new BatchOutcome
                {
                    Script = script, Name = name,
                    Result = one ? "成功" : "失败",
                    Product = one ? prod : null,
                    Seconds = fsw.Elapsed.TotalSeconds,
                    Size = prodSize
                });
                AddHistory(name, one ? "成功" : "失败",
                           fsw.Elapsed.TotalSeconds, script,
                           one ? prod : null,
                           one ? null : _lastDiagText,
                           one ? prodSize : -1);

                double pct = (i + 1) * 100.0 / total;
                BatchProgress.Value = pct;
                BatchProgressText.Text = ((int)pct) + "%";
                RefreshQueue();
            }

            tick.Stop();
            sw.Stop();
            _busy = false;
            BtnStart.IsEnabled = true;
            BtnAbort.IsEnabled = false;

            string summary = "成功 " + ok + " · 失败 " + fail;
            BatchRunTitle.Text = "批量完成";
            BatchRunSub.Text = summary + " · 用时 "
                + sw.Elapsed.TotalSeconds.ToString("0.0") + "s";
            BatchStatus.Text = _abort ? "已终止" : summary;
            Blog("", "dim");
            Blog("[*] 批量结束 —— " + summary + "，总耗时 "
                 + sw.Elapsed.TotalSeconds.ToString("0.0") + "s",
                 fail == 0 ? "ok" : "warn");
            Status(fail == 0 ? "批量全部成功" : "批量完成 " + summary,
                   (Brush)FindResource(fail == 0 ? "Green" : "Orange"));
            BtnRetryFailed.IsEnabled = fail > 0;
            BtnExportBatch.IsEnabled = _lastBatchResults.Count > 0;
            SaveRunLog("batch_" + ok + "ok_" + fail + "fail");
            RefreshQueue();
            return fail == 0;
        }

        // ══════════════ 批量结果：重跑失败 / 导出 ══════════════
        void RetryFailed(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var fails = _lastBatchResults
                .Where(r => r.Result == "失败")
                .Select(r => r.Script)
                .Where(s => !string.IsNullOrEmpty(s) && File.Exists(s))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (fails.Count == 0)
            {
                Log("[!] 没有可重跑的失败脚本", "warn");
                return;
            }
            _queue.Clear();
            _queue.AddRange(fails);
            _queueState.Clear();
            _queueDetail.Clear();
            RefreshQueue();
            Log("[*] 已把 " + fails.Count
                + " 个失败脚本放回队列，点「开始批量」即可重跑", "acc");
            Status("已准备重跑失败项", (Brush)FindResource("Blue"));
        }

        static string CsvCell(string s)
            => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

        void ExportBatch(object sender, RoutedEventArgs e)
        {
            if (_lastBatchResults.Count == 0)
            {
                Log("[!] 还没有批量结果可导出", "warn");
                return;
            }
            var dlg = new SaveFileDialog
            {
                Title = "导出批量打包结果",
                Filter = "CSV 文件|*.csv|所有文件|*.*",
                FileName = "批量结果_" + DateTime.Now.ToString("yyyyMMdd-HHmmss")
                           + ".csv"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("脚本,产物名,结果,耗时(秒),大小(MB),产物路径");
                foreach (var r in _lastBatchResults)
                {
                    sb.AppendLine(string.Join(",", new[]
                    {
                        CsvCell(r.Script), CsvCell(r.Name), CsvCell(r.Result),
                        r.Seconds.ToString("0.0", CultureInfo.InvariantCulture),
                        r.Size > 0
                            ? (r.Size / 1048576.0).ToString("0.0",
                                CultureInfo.InvariantCulture) : "",
                        CsvCell(r.Product ?? "")
                    }));
                }
                File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
                Log("[+] 批量结果已导出: " + dlg.FileName, "ok");
                Status("批量结果已导出", (Brush)FindResource("Blue"));
            }
            catch (Exception ex) { Log("[-] 导出失败: " + ex.Message, "err"); }
        }

        // ══════════════ 依赖分析 ══════════════
        void AnalyzeDeps(object sender, RoutedEventArgs e)
        {
            if (_script == null || !File.Exists(_script))
            {
                DepTarget.Text = "未选择脚本";
                DepBox.Text = "先在「单文件打包」里选一个脚本，再回到这里查看依赖。";
                return;
            }
            DepTarget.Text = Path.GetFileName(_script);

            string py = FindPythonForCheck();
            if (py == null)
            {
                DepBox.Text = "需要 Python 解释器才能分析依赖。\r\n"
                            + "把便携运行时放到程序目录的 runtime\\ 下即可。";
                return;
            }

            DepBox.Text = "分析中…";
            string script = _script;
            Task.Run(() =>
            {
                string outText;
                try
                {
                    // 与打包前的前置检查共用同一份实现，避免两处逻辑漂移
                    var rep = DependencyChecker.Check(script, py, _runner);
                    outText = rep.RenderDetail(Path.GetFileName(script));
                    _lastDepIssues = rep.Missing.Count > 0 ? rep.Missing : null;
                }
                catch (Exception ex) { outText = "分析异常：" + ex.Message; }

                Dispatcher.BeginInvoke(new Action(() => DepBox.Text = outText));
            });
        }

        // ══════════════ 打包历史 ══════════════
        public class HistItem
        {
            public string Time { get; set; }
            public string Name { get; set; }
            public string Result { get; set; }
            public string Elapsed { get; set; }
            public string Script { get; set; }
            public string Product { get; set; }
            public string Detail { get; set; }
            public string Size { get; set; }

            [System.Text.Json.Serialization.JsonIgnore]
            public string Tip => Name
                + (string.IsNullOrEmpty(Script) ? "" : "\n脚本: " + Script)
                + (string.IsNullOrEmpty(Product) ? "" : "\n产物: " + Product)
                + (string.IsNullOrEmpty(Size) ? "" : "\n大小: " + Size)
                + (string.IsNullOrEmpty(Detail) ? "" : "\n" + Detail);

            [System.Text.Json.Serialization.JsonIgnore]
            public string Sub => Time + " · 耗时 " + Elapsed
                + (string.IsNullOrEmpty(Size) ? "" : " · " + Size);
        }

        readonly List<HistItem> _history = new List<HistItem>();
        static string HistoryFile => Path.Combine(RuntimeLocator.HomeDir, "history.json");
        HistItem SelectedHist => HistList.SelectedItem as HistItem;

        void LoadHistory()
        {
            try
            {
                if (!File.Exists(HistoryFile)) return;
                var list = JsonSerializer.Deserialize<List<HistItem>>(
                    File.ReadAllText(HistoryFile));
                if (list == null) return;
                _history.Clear();
                foreach (var h in list.Take(200)) _history.Add(h);
                RefreshHistory();
            }
            catch { }
        }

        void SaveHistory()
        {
            try
            {
                Directory.CreateDirectory(RuntimeLocator.HomeDir);
                File.WriteAllText(HistoryFile,
                    JsonSerializer.Serialize(_history, new JsonSerializerOptions
                    { WriteIndented = true }),
                    new UTF8Encoding(false));
            }
            catch { }
        }

        static long FileSizeSafe(string path)
        {
            try { return File.Exists(path) ? new FileInfo(path).Length : -1; }
            catch { return -1; }
        }

        void AddHistory(string name, string result, double sec,
                        string script = null, string product = null,
                        string detail = null, long size = -1)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _history.Insert(0, new HistItem
                {
                    Time = DateTime.Now.ToString("MM-dd HH:mm"),
                    Name = string.IsNullOrEmpty(name) ? "(未命名)" : name,
                    Result = result,
                    Elapsed = sec.ToString("0.0") + "s",
                    Script = script,
                    Product = product,
                    Detail = detail,
                    Size = size > 0
                        ? (size / 1048576.0).ToString("0.0") + " MB" : ""
                });
                if (_history.Count > 200) _history.RemoveAt(_history.Count - 1);
                RefreshHistory();
                SaveHistory();
            }));
        }

        void RefreshHistory()
        {
            HistList.ItemsSource = null;
            HistList.ItemsSource = _history;
        }

        void ClearHistory(object sender, RoutedEventArgs e)
        {
            _history.Clear();
            RefreshHistory();
            SaveHistory();
        }

        void HistRightClick(object sender, MouseButtonEventArgs e)
        {
            var dep = e.OriginalSource as DependencyObject;
            while (dep != null && !(dep is ListBoxItem))
                dep = VisualTreeHelper.GetParent(dep);
            if (dep is ListBoxItem item) item.IsSelected = true;
        }

        void HistOpen(object sender, RoutedEventArgs e) => OpenHistoryProduct();
        void HistOpenDouble(object sender, MouseButtonEventArgs e) => OpenHistoryProduct();

        void OpenHistoryProduct()
        {
            var h = SelectedHist;
            if (h == null) return;
            try
            {
                if (!string.IsNullOrEmpty(h.Product) && File.Exists(h.Product))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe",
                        "/select,\"" + h.Product + "\"") { UseShellExecute = true });
                    return;
                }
                if (!string.IsNullOrEmpty(h.Product) && Directory.Exists(h.Product))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe",
                        "\"" + h.Product + "\"") { UseShellExecute = true });
                    return;
                }
                string dir = string.IsNullOrEmpty(h.Product)
                    ? null : Path.GetDirectoryName(h.Product);
                if (string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(h.Script))
                    dir = Path.GetDirectoryName(h.Script);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe",
                        "\"" + dir + "\"") { UseShellExecute = true });
                    return;
                }
                Log("[!] 这条记录没有可打开的产物位置", "warn");
            }
            catch (Exception ex) { Log("[-] 打开失败: " + ex.Message, "err"); }
        }

        void HistReload(object sender, RoutedEventArgs e)
        {
            var h = SelectedHist;
            if (h == null) return;
            if (string.IsNullOrEmpty(h.Script) || !File.Exists(h.Script))
            {
                Log("[!] 脚本已不存在: " + (h.Script ?? "(无路径)"), "warn");
                return;
            }
            SwitchTo("single");
            LoadScript(h.Script);
        }

        void HistCopyDiag(object sender, RoutedEventArgs e)
        {
            var h = SelectedHist;
            if (h == null) return;
            try
            {
                Clipboard.SetText(string.IsNullOrEmpty(h.Detail)
                    ? "（该记录没有诊断信息）" : h.Detail);
                Status("诊断信息已复制", (Brush)FindResource("Blue"));
            }
            catch { }
        }

        void HistDelete(object sender, RoutedEventArgs e)
        {
            var h = SelectedHist;
            if (h == null) return;
            _history.Remove(h);
            RefreshHistory();
            SaveHistory();
        }

        // ══════════════ 批量日志 ══════════════
        void Blog(string msg, string kind)
        {
            lock (_log) { _log.Add(msg); }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Brush fg = KindBrush(kind);
                var p = new Paragraph(new Run(msg) { Foreground = fg })
                { Margin = new Thickness(0, 0, 0, 1) };
                BatchLogBox.Document.Blocks.Add(p);
                BatchLogBox.ScrollToEnd();
            }));
        }

        Brush KindBrush(string kind)
        {
            switch (kind)
            {
                case "ok": return (Brush)FindResource("Green");
                case "err": return (Brush)FindResource("Pink");
                case "warn": return (Brush)FindResource("Orange");
                case "acc": return (Brush)FindResource("Blue");
                default: return (Brush)FindResource("Label3");
            }
        }


        // ══════════════ 指定运行时位置 ══════════════
        void LocateRuntime312(object sender, RoutedEventArgs e)
            => LocateRuntime(RuntimeManager.Key312);

        void LocateRuntimeDefault(object sender, RoutedEventArgs e)
            => LocateRuntime(RuntimeManager.KeyDefault);

        void LocateRuntime(string key)
        {
            var dlg = new OpenFolderDialog
            {
                Title = "选择 Python 运行时目录（里面应有 python.exe 与 Lib）"
            };
            if (dlg.ShowDialog() != true) return;
            string src = dlg.FolderName;

            var runner = new CommandRunner(Log);
            string exe = Path.Combine(src, "python.exe");
            if (!RuntimeManager.LooksLikeRuntime(src))
            {
                Log("[-] 这个目录不像完整的 Python 运行时: " + src, "err");
                Log("[!] 需要同时有 python.exe 和 Lib 文件夹", "warn");
                Status("目录不符合要求", (Brush)FindResource("Pink"));
                return;
            }
            var probe = runner.Run(BuildCommand.From(exe, null,
                new[] { "-c", "import PyInstaller,sys;"
                              + "sys.stdout.write(PyInstaller.__version__)" },
                null, 30000));
            if (probe.ExitCode != 0)
            {
                Log("[-] 该运行时里没有 PyInstaller", "err");
                Log("[!] 请先在这个环境里执行: python -m pip install pyinstaller",
                    "warn");
                Status("缺少 PyInstaller", (Brush)FindResource("Pink"));
                return;
            }

            // 直接记住位置即可，不搬动文件（用户可能就想让它待在原地）
            RuntimeLocator.Remember(key, src);
            _engine = null;
            _engineChecked = false;
            _engineReady = false;
            _pyForCheckDone = false;

            Log("[+] 已指定运行时位置: " + src
                + "  (PyInstaller " + probe.StdOut.Trim() + ")", "ok");
            Status("运行时位置已记录", (Brush)FindResource("Green"));
            RefreshRuntimePage();

            Task.Run(new Action(() =>
            {
                try { EnginePrefix(); } catch { }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    StatEngine.Text = _engineReady ? "就绪" : "缺失";
                    if (_engineDesc != null)
                        Log("[*] 打包引擎: " + _engineDesc, "acc");
                }));
            }));
        }

        // ══════════════ 首次运行引导 ══════════════
        void FirstRunCheck()
        {
            // 非阻塞：引擎验证在后台，这里只在确实缺失时给一次性提示
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            bool hasRuntime = File.Exists(Path.Combine(baseDir, "runtime", "python.exe"));
            bool hasCli = (Which("pyinstaller.exe") ?? Which("pyinstaller")) != null;
            bool hasPy = (Which("python.exe") ?? Which("py.exe")) != null;
            if (hasRuntime || hasCli || hasPy) return;
            Log("", "dim");
            Log("[!] 没有检测到可用的打包引擎", "warn");
            Log("[!] 打包目标脚本需要 PyInstaller。两种解决方式：", "warn");
            Log("    1) 使用自带运行时的完整版（程序目录下应有 runtime\\ 文件夹）", "dim");
            Log("    2) 在目标机安装: pip install pyinstaller", "dim");
            Dispatcher.BeginInvoke(new Action(() =>
            {
                MessageBox.Show(this,
                    "没有检测到 PyInstaller，暂时无法打包。\n\n" +
                    "解决方式：\n" +
                    "1) 使用自带运行时的完整版（程序目录下应含 runtime 文件夹）\n" +
                    "2) 或在本机安装：pip install pyinstaller\n\n" +
                    "界面其它功能不受影响。",
                    "Dainric 打包器", MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }));
        }


        // ══════════════ 开发自检（仅环境变量触发）══════════════
        async Task BatchSelfTest(string outDir)
        {
            var report = new StringBuilder();
            try
            {
                Directory.CreateDirectory(outDir);
                string srcDir = Path.Combine(outDir, "src");
                Directory.CreateDirectory(srcDir);

                var scripts = new List<string>();
                foreach (var nm in new[] { "alpha", "beta" })
                {
                    string f = Path.Combine(srcDir, nm + ".py");
                    File.WriteAllText(f, "print('" + nm + "')\n");
                    scripts.Add(f);
                }
                report.AppendLine("created " + scripts.Count + " scripts");

                // 依赖检查的正反两面都要覆盖：
                //   gamma 只 import 标准库 -> 应当通过，且不该被误判
                //   delta import 一个不存在的模块 -> 应当在打包前被拦下
                string g = Path.Combine(srcDir, "gamma.py");
                File.WriteAllText(g, "import json\nprint(json.dumps({'g': 1}))\n");
                scripts.Add(g);
                report.AppendLine("+ gamma.py  (仅标准库，依赖检查应通过)");

                string d = Path.Combine(srcDir, "delta.py");
                File.WriteAllText(d, "import dainric_absent_zz\nprint(1)\n");
                scripts.Add(d);
                report.AppendLine("+ delta.py  (缺模块，依赖检查应拦下)");

                SwitchTo("batch");
                foreach (var s in scripts) QueueAppend(s);
                RefreshQueue();
                BatchOutDir.Text = Path.Combine(outDir, "dist");
                Directory.CreateDirectory(BatchOutDir.Text);
                report.AppendLine("queue=" + _queue.Count);

                var sw = Stopwatch.StartNew();
                bool ok = await RunBatch();
                sw.Stop();
                report.AppendLine("RunBatch=" + ok + " elapsed="
                                  + sw.Elapsed.TotalSeconds.ToString("0.0") + "s");

                foreach (var kv in _queueState)
                    report.AppendLine("  " + Path.GetFileName(kv.Key) + " -> " + kv.Value);

                var dist = Path.Combine(outDir, "dist");
                if (Directory.Exists(dist))
                {
                    foreach (var f in Directory.GetFiles(dist, "*.exe"))
                        report.AppendLine("  EXE " + Path.GetFileName(f) + " "
                            + (new FileInfo(f).Length / 1048576.0).ToString("0.00") + " MB");
                }
            }
            catch (Exception ex)
            {
                report.AppendLine("EXCEPTION: " + ex);
            }
            finally
            {
                try
                {
                    report.AppendLine();
                    report.AppendLine("--- 界面日志 ---");
                    lock (_log)
                        foreach (var l in _log) report.AppendLine(l);
                    File.WriteAllText(Path.Combine(outDir, "batchtest.txt"),
                                      report.ToString(), Encoding.UTF8);
                }
                catch { }
                Dispatcher.BeginInvoke(new Action(() => Close()));
            }
        }


        // ══════════════ 运行时 / 兼容模式 ══════════════
        readonly CommandRunner _runner = new CommandRunner(null);
        List<RuntimeSlot> _slots = new List<RuntimeSlot>();
        string _activeSlotKey = RuntimeManager.Key312;

        void ShowRuntime(object sender, MouseButtonEventArgs e) => SwitchTo("runtime");

        void RefreshRuntimePage()
        {
            try
            {
                _slots = RuntimeManager.Discover();
                foreach (var s in _slots)
                    RuntimeManager.FillDetails(s, _runner);

                var a = _slots.First(s => s.Key == RuntimeManager.Key312);
                var b = _slots.First(s => s.Key == RuntimeManager.KeyDefault);
                PaintSlot(a, Slot312Badge, Slot312BadgeText, Slot312Detail, BtnUse312);
                PaintSlot(b, SlotDefBadge, SlotDefBadgeText, SlotDefDetail, BtnUseDef);

                var pick = RuntimeManager.PickUsable(_slots);
                RuntimeActive.Text = pick == null
                    ? "当前没有可用的打包运行时，请先导入一个。"
                    : "当前生效: " + pick.Title
                      + (pick.Version.Length > 0 ? "  (Python " + pick.Version + ")" : "")
                      + "   PyInstaller " + pick.PyInstallerVersion;
            }
            catch (Exception ex)
            {
                RuntimeActive.Text = "读取运行时信息失败: " + ex.Message;
            }
        }

        void PaintSlot(RuntimeSlot s, Border badge, TextBlock badgeText,
                       TextBlock detail, Button useBtn)
        {
            if (!s.Present)
            {
                badge.Background = (Brush)FindResource("Surface3");
                badgeText.Text = "未找到";
                badgeText.Foreground = (Brush)FindResource("Label2");
                detail.Text = "已在程序目录、上级目录、%LOCALAPPDATA%\\Dainric、"
                              + "桌面、文档等处查找，均未找到。\n"
                              + "可用「指定位置…」手动指明。";
                useBtn.IsEnabled = false;
                return;
            }
            if (s.Usable)
            {
                badge.Background = (Brush)FindResource("Green");
                badgeText.Text = "可用";
                badgeText.Foreground = Brushes.White;
                // 把「在哪儿找到的」一并显示，方便用户确认程序认的是哪一份
                detail.Text = "Python " + s.Version + " · PyInstaller "
                              + s.PyInstallerVersion
                              + "\n位置: " + s.Dir
                              + "\n来源: " + s.FoundBy;
                useBtn.IsEnabled = true;
            }
            else
            {
                badge.Background = (Brush)FindResource("Orange");
                badgeText.Text = "缺少 PyInstaller";
                badgeText.Foreground = Brushes.White;
                detail.Text = "Python " + (s.Version ?? "") + " 已就位，但没有 PyInstaller"
                              + "（在它下面执行 pip install pyinstaller 即可）";
                useBtn.IsEnabled = false;
            }
        }

        void UseRuntime312(object sender, RoutedEventArgs e)
            => ActivateSlot(RuntimeManager.Key312);

        void UseRuntimeDefault(object sender, RoutedEventArgs e)
            => ActivateSlot(RuntimeManager.KeyDefault);

        void ActivateSlot(string key)
        {
            var s = _slots.FirstOrDefault(x => x.Key == key);
            if (s == null || !s.Usable)
            {
                Log("[-] 该运行时不可用，无法切换", "err");
                return;
            }
            _activeSlotKey = key;
            _engine = null;              // 让引擎按新运行时重新探测
            _engineChecked = false;
            _engineReady = false;
            _pyForCheckDone = false;

            Log("[*] 已切换到 " + s.Title + "（Python " + s.Version
                + " · PyInstaller " + s.PyInstallerVersion + "）", "ok");
            Status("已切换到 " + s.Title, (Brush)FindResource("Green"));
            RefreshRuntimePage();

            Task.Run(new Action(() =>
            {
                try { EnginePrefix(); } catch { }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    StatEngine.Text = _engineReady ? "就绪" : "缺失";
                    Log("[*] 打包引擎: " + (_engineDesc ?? "未知"), "acc");
                }));
            }));
        }

        // ══════════════ 日志 ══════════════
        void InitLog()
        {
            LogBox.Document.Blocks.Clear();
            // 不要把 PageWidth 定死（以前设成 2000）：日志面板只有 ~370 DIP 宽，
            // 固定页宽会让长行超出面板、又没有横向滚动条，后半截日志直接看不见。
            // 保持 NaN = 按面板宽度自动换行，再长的路径也能完整读到。
            LogBox.Document.PageWidth = double.NaN;
            lock (_log) _logEntries.Clear();
            if (LogSearchHint != null) LogSearchHint.Visibility = Visibility.Visible;
        }

        static bool IsIssueKind(string kind) => kind == "err" || kind == "warn";

        bool LogMatches(LogEntry e)
        {
            if (ChkOnlyIssues != null && ChkOnlyIssues.IsChecked == true
                && !IsIssueKind(e.Kind)) return false;
            string q = LogSearch == null ? "" : (LogSearch.Text ?? "").Trim();
            return q.Length == 0
                || e.Text.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        void AppendLogEntry(LogEntry e)
        {
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 1) };
            p.Inlines.Add(new Run(e.Time.ToString("HH:mm:ss "))
            { Foreground = (Brush)FindResource("Label3") });
            p.Inlines.Add(new Run(e.Text) { Foreground = KindBrush(e.Kind) });
            LogBox.Document.Blocks.Add(p);
            if (ChkAutoScroll == null || ChkAutoScroll.IsChecked != false)
                LogBox.ScrollToEnd();
        }

        void RefreshLogView()
        {
            LogBox.Document.Blocks.Clear();
            LogEntry[] snapshot;
            lock (_log) snapshot = _logEntries.ToArray();
            foreach (var e in snapshot)
                if (LogMatches(e)) AppendLogEntry(e);
        }

        void LogSearchChanged(object sender, TextChangedEventArgs e) => ApplyLogFilter();

        void LogFilterChanged(object sender, RoutedEventArgs e) => ApplyLogFilter();

        void ApplyLogFilter()
        {
            if (LogSearchHint != null && LogSearch != null)
                LogSearchHint.Visibility = string.IsNullOrEmpty(LogSearch.Text)
                    ? Visibility.Visible : Visibility.Collapsed;
            RefreshLogView();
        }

        void Log(string msg, string kind)
        {
            var entry = new LogEntry
            { Time = DateTime.Now, Text = msg, Kind = kind };
            lock (_log)
            {
                _log.Add(msg);
                _logEntries.Add(entry);
                if (_logEntries.Count > 4000) _logEntries.RemoveAt(0);
            }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (LogMatches(entry)) AppendLogEntry(entry);
            }));
        }

        string ComposeLogText()
        {
            LogEntry[] snapshot;
            lock (_log) snapshot = _logEntries.ToArray();
            var sb = new StringBuilder();
            foreach (var e in snapshot)
                sb.AppendLine(e.Time.ToString("yyyy-MM-dd HH:mm:ss") + "  " + e.Text);
            return sb.ToString();
        }

        void ExportLog(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new SaveFileDialog
                {
                    Title = "导出日志",
                    Filter = "日志文件|*.log|文本文件|*.txt|所有文件|*.*",
                    FileName = "Dainric日志_"
                        + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log"
                };
                if (dlg.ShowDialog() != true) return;
                File.WriteAllText(dlg.FileName, ComposeLogText(),
                                  new UTF8Encoding(false));
                Status("日志已导出", (Brush)FindResource("Blue"));
            }
            catch (Exception ex)
            {
                Log("[-] 导出日志失败: " + ex.Message, "err");
            }
        }

        /// <summary>把最近一次构建的日志自动落盘，方便事后排查和反馈</summary>
        void SaveRunLog(string tag)
        {
            try
            {
                LogEntry[] snapshot;
                lock (_log) snapshot = _logEntries.ToArray();
                int start = Math.Min(_runLogStart, snapshot.Length);
                if (snapshot.Length - start < 2) return;
                string dir = Path.Combine(RuntimeLocator.HomeDir, "logs");
                Directory.CreateDirectory(dir);
                string safe = Regex.Replace(tag ?? "build", @"[^\w\-.]+", "_");
                if (safe.Length == 0) safe = "build";
                var sb = new StringBuilder();
                foreach (var e in snapshot.Skip(start))
                    sb.AppendLine(e.Time.ToString("yyyy-MM-dd HH:mm:ss")
                                  + "  " + e.Text);
                File.WriteAllText(Path.Combine(dir,
                    DateTime.Now.ToString("yyyyMMdd-HHmmss") + "_" + safe + ".log"),
                    sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

    }
}
