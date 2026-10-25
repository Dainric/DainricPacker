// 关于对话框 · 作者: Dainric
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;

namespace Dainric.Packer
{
    public partial class AboutDialog : Window
    {
        public AboutDialog()
        {
            InitializeComponent();
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
