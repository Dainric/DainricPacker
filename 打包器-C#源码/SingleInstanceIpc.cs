// Dainric 打包器 · 单实例通信（WM_COPYDATA）
// 作者: Dainric
//
// 第二次双击程序（或右键「用 Dainric 打包」一个 .py）时，
// 不新开窗口，而是把脚本路径通过 WM_COPYDATA 交给已经运行的实例。
// 用窗口消息而不是命名管道：实现更短，也不受管道权限/沙箱限制。
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Dainric.Packer
{
    public static class SingleInstanceIpc
    {
        public const int WM_COPYDATA = 0x004A;
        public const int Magic = 0x44414E52;   // 'DANR'

        [StructLayout(LayoutKind.Sequential)]
        public struct COPYDATASTRUCT
        {
            public IntPtr dwData;
            public int cbData;
            public IntPtr lpData;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam,
                                         ref COPYDATASTRUCT lParam);

        /// <summary>把消息发给已经运行的主窗口；找不到窗口返回 false</summary>
        public static bool Send(string message)
        {
            try
            {
                IntPtr hwnd = FindWindow(null, "Dainric 打包器");
                if (hwnd == IntPtr.Zero) return false;

                byte[] bytes = Encoding.Unicode.GetBytes(message ?? "");
                IntPtr mem = Marshal.AllocHGlobal(bytes.Length + 2);
                try
                {
                    Marshal.Copy(bytes, 0, mem, bytes.Length);
                    Marshal.WriteInt16(mem, bytes.Length, 0);
                    var cds = new COPYDATASTRUCT
                    {
                        dwData = (IntPtr)Magic,
                        cbData = bytes.Length + 2,
                        lpData = mem
                    };
                    SendMessage(hwnd, WM_COPYDATA, IntPtr.Zero, ref cds);
                    return true;
                }
                finally
                {
                    Marshal.FreeHGlobal(mem);
                }
            }
            catch { return false; }
        }
    }
}
