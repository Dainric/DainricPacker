// 版本信息对话框 · 作者: Dainric
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;

namespace Dainric.Packer
{
    public partial class VersionDialog : Window
    {
        public string Result { get; private set; }

        public VersionDialog(string current)
        {
            InitializeComponent();
            VerInput.Text = string.IsNullOrWhiteSpace(current) ? "1.0.0" : current;
            VerInput.SelectAll();
            Loaded += (s, e) => VerInput.Focus();
            MouseLeftButtonDown += (s, e) =>
            {
                if (e.ChangedButton == MouseButton.Left) DragMove();
            };
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) Ok(null, null);
                if (e.Key == Key.Escape) Cancel(null, null);
            };
        }

        void Ok(object sender, RoutedEventArgs e)
        {
            string v = (VerInput.Text ?? "").Trim();
            if (!Regex.IsMatch(v, @"^\d+(\.\d+)*$"))
            {
                MessageBox.Show(this, "版本号格式错误，应为 1.0 或 1.0.0.0", "提示",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Result = v;
            DialogResult = true;
        }

        void Cancel(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
