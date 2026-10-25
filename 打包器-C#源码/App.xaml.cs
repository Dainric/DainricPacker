// Dainric 打包器 · 原生 Windows 版
// 作者: Dainric
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Dainric.Packer
{
    public partial class App : Application
    {
        /// <summary>启动参数里带的 .py，交给 MainWindow 载入</summary>
        public static string PendingScript;

        static Mutex _singleMutex;
        static bool _ownsMutex;

        // 崩溃日志：写文件名和异常详情，避免静默退出
        public static void CrashLog(Exception ex, string where)
        {
            try
            {
                // 单文件发布时 Assembly.Location 返回空串，必须用 AppContext
                string p = Path.Combine(AppContext.BaseDirectory, "crash.log");
                var sb = new StringBuilder();
                sb.AppendLine("=== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                              + "  " + where + " ===");
                sb.AppendLine(ex.ToString());
                if (ex.InnerException != null)
                {
                    sb.AppendLine("--- InnerException ---");
                    sb.AppendLine(ex.InnerException.ToString());
                }
                sb.AppendLine();
                File.AppendAllText(p, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += (s, args) =>
            {
                CrashLog(args.Exception, "DispatcherUnhandledException");
                MessageBox.Show("程序发生错误：\n\n" + args.Exception.Message +
                                "\n\n完整堆栈已写入 crash.log",
                                "Dainric 打包器", MessageBoxButton.OK,
                                MessageBoxImage.Error);
                args.Handled = true;
                Shutdown(1);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
                CrashLog(args.ExceptionObject as Exception, "AppDomain");

            // ── 1) 命令行模式：不建窗口，直接打包 ──
            if (CliRunner.IsCli(e.Args))
            {
                int code = 2;
                try { code = CliRunner.Run(e.Args); }
                catch (Exception ex)
                {
                    try { Console.Error.WriteLine(ex.Message); } catch { }
                    CrashLog(ex, "CliRunner");
                    code = 1;
                }
                Shutdown(code);
                return;
            }

            // ── 2) 单实例：已有实例时把脚本路径转过去，然后退出 ──
            bool created = false;
            try
            {
                _singleMutex = new Mutex(true,
                    @"Local\DainricPacker_SingleInstance", out created);
                _ownsMutex = created;
            }
            catch { created = true; }

            if (!created)
            {
                string path = FirstScriptArg(e.Args);
                SingleInstanceIpc.Send(path);
                Shutdown(0);
                return;
            }

            PendingScript = FirstScriptArg(e.Args);

            try
            {
                base.OnStartup(e);
            }
            catch (Exception ex)
            {
                CrashLog(ex, "OnStartup");
                throw;
            }

            // 没有 StartupUri，窗口由这里显式创建（CLI 模式才能完全无窗口）
            var win = new MainWindow();
            MainWindow = win;
            win.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                if (_ownsMutex && _singleMutex != null) _singleMutex.ReleaseMutex();
            }
            catch { }
            try { if (_singleMutex != null) _singleMutex.Dispose(); } catch { }
            base.OnExit(e);
        }

        static string FirstScriptArg(string[] args)
        {
            try
            {
                foreach (var a in args ?? new string[0])
                {
                    if (!string.IsNullOrWhiteSpace(a)
                        && a.EndsWith(".py", StringComparison.OrdinalIgnoreCase)
                        && File.Exists(a))
                        return Path.GetFullPath(a);
                }
            }
            catch { }
            return "";
        }
    }
}
