// 关于对话框 · 作者: Dainric
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace Dainric.Packer
{
    public partial class AboutDialog : Window
    {
        public bool CheckUpdateOnStartup { get; private set; }
        public bool SettingsChanged { get; private set; }
        bool _ready;

        public AboutDialog(bool checkUpdateOnStartup = true)
        {
            InitializeComponent();
            CheckUpdateOnStartup = checkUpdateOnStartup;
            CheckUpdateBox.IsChecked = checkUpdateOnStartup;
            _ready = true;
            string ver = MainWindow.AppVersionText();
            VerTextBlock.Text = ver;

            string build = "";
            try
            {
                string exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                    build = File.GetLastWriteTime(exe).ToString("yyyy-MM-dd HH:mm");
            }
            catch { }

            InfoVersion.Text = "版本：" + ver
                + (build.Length > 0 ? "（构建于 " + build + "）" : "");
            InfoEnv.Text = "运行环境：.NET " + Environment.Version + " · "
                + (Environment.Is64BitProcess ? "64 位" : "32 位");
            InfoOs.Text = "系统：" + Environment.OSVersion.VersionString;
            InfoDir.Text = "安装目录：" + AppContext.BaseDirectory;
            InfoData.Text = "数据目录：" + RuntimeLocator.HomeDir;

            MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed) DragMove();
            };
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
        }

        void OpenInstallDir(object sender, RoutedEventArgs e)
            => OpenDir(AppContext.BaseDirectory);

        void OpenLogDir(object sender, RoutedEventArgs e)
        {
            try
            {
                string d = Path.Combine(RuntimeLocator.HomeDir, "logs");
                Directory.CreateDirectory(d);
                OpenDir(d);
            }
            catch { }
        }

        void OpenHelp(object sender, RoutedEventArgs e)
        {
            try
            {
                string p = Path.Combine(AppContext.BaseDirectory, "使用说明.txt");
                if (File.Exists(p))
                    Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
                else
                    MessageBox.Show(this, "没有找到 使用说明.txt。\n"
                        + "可以从安装目录或绿色版目录里打开。", "使用说明",
                        MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch { }
        }

        void CopyDiag(object sender, RoutedEventArgs e)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("Dainric 打包器 " + MainWindow.AppVersionText());
                sb.AppendLine("运行环境: .NET " + Environment.Version
                    + " · " + (Environment.Is64BitProcess ? "64 位" : "32 位"));
                sb.AppendLine("系统: " + Environment.OSVersion.VersionString);
                sb.AppendLine("安装目录: " + AppContext.BaseDirectory);
                sb.AppendLine("数据目录: " + RuntimeLocator.HomeDir);
                sb.AppendLine("进程路径: " + (Environment.ProcessPath ?? ""));
                Clipboard.SetText(sb.ToString());
                MessageBox.Show(this, "诊断信息已复制到剪贴板。", "复制诊断信息",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch { }
        }

        void CloseDialog(object sender, RoutedEventArgs e) => Close();

        // 一键卸载：先退出程序，再启动 Inno 卸载器
        void UninstallClicked(object sender, RoutedEventArgs e)
        {
            string unins = Path.Combine(AppContext.BaseDirectory, "unins000.exe");
            if (!File.Exists(unins))
            {
                MessageBox.Show(this,
                    "当前是绿色版，没有卸载程序\n"
                    + "直接删除程序文件夹即可\n"
                    + "数据目录：%LOCALAPPDATA%\\Dainric",
                    "卸载", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var r = MessageBox.Show(this,
                "将卸载 Dainric 打包器\n\n"
                + "会删除：程序、内置运行时、配置、历史、日志\n"
                + "不会删除你打包出来的产物\n\n"
                + "确定继续吗？",
                "卸载 Dainric 打包器",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;

            try
            {
                var psi = new ProcessStartInfo("cmd.exe",
                    "/c ping -n 2 127.0.0.1 >nul & start \"\" \""
                    + unins + "\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法启动卸载程序\n" + ex.Message,
                    "卸载失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            Application.Current.Shutdown();
        }

        void UpdateSettingChanged(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            CheckUpdateOnStartup = CheckUpdateBox.IsChecked == true;
            SettingsChanged = true;
        }

        async void CheckUpdateClicked(object sender, RoutedEventArgs e)
        {
            BtnCheckUpdate.IsEnabled = false;
            UpdateStatus.Text = "正在检查更新…";
            try
            {
                var info = await UpdateChecker.FetchAsync();
                string current = MainWindow.AppVersionNumber();
                if (UpdateChecker.IsNewer(info.Version, current))
                {
                    string notes = (info.Notes ?? "").Trim();
                    if (notes.Length > 600) notes = notes.Substring(0, 600) + "…";
                    string msg = "发现新版本 v" + info.Version
                        + "\n当前版本 v" + current + "\n\n"
                        + (notes.Length > 0 ? notes : "（没有更新说明）")
                        + "\n\n是否打开下载页？";
                    UpdateStatus.Text = "发现新版本 v" + info.Version;
                    var r = MessageBox.Show(this, msg, "检查更新",
                        MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (r == MessageBoxResult.Yes)
                        UpdateChecker.OpenDownload(info);
                }
                else
                {
                    UpdateStatus.Text = "已是最新版本 v" + current;
                }
            }
            catch (Exception ex)
            {
                UpdateStatus.Text = "检查失败：" + ex.Message;
            }
            finally
            {
                BtnCheckUpdate.IsEnabled = true;
            }
        }

        static void OpenDir(string d)
        {
            try
            {
                if (!Directory.Exists(d)) return;
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + d + "\"")
                { UseShellExecute = true });
            }
            catch { }
        }
    }
}
