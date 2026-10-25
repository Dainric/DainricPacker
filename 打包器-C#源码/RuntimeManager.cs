// Dainric 打包器 · 运行时管理（含 Python 3.12 兼容模式）
// 作者: Dainric
//
// 运行时目录不再要求与 exe 同层：实际位置由 RuntimeLocator 解析。
// 搜索顺序：记住的位置 → 程序目录 → 上级目录 → 集中目录
//           → 桌面/文档/用户目录 → 环境变量 DAINRIC_RUNTIME_HOME
// 命中后会被记住，下次启动直接使用。
//
// 为什么不内置进 exe：两个运行时合计 186 MB，内置后 exe 涨到 246 MB，
// 且每次启动都要解压到 %TEMP%，受限环境还可能没有写权限。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Dainric.Packer
{
    public sealed class RuntimeSlot
    {
        public string Key { get; set; }        // py312 / default
        public string Title { get; set; }
        public string Note { get; set; }
        public string Dir { get; set; }
        public string PythonExe { get; set; }
        public string FoundBy { get; set; }    // 在哪儿找到的
        public bool Present { get; set; }
        public string Version { get; set; } = "";
        public string PyInstallerVersion { get; set; } = "";
        public bool Usable => Present && !string.IsNullOrEmpty(PyInstallerVersion);
    }

    public static class RuntimeManager
    {
        public const string Key312 = "py312";
        public const string KeyDefault = "default";

        public static string AppDir => AppContext.BaseDirectory;

        // 保留旧名字，内部转发到定位器
        public const string Dir312 = RuntimeLocator.Dir312;
        public const string Dir314 = RuntimeLocator.Dir314;

        /// <summary>首选目录（仅用于显示与兜底）</summary>
        public static string DirFor(string key) =>
            RuntimeLocator.Locate(key).Dir
            ?? Path.Combine(AppDir, key == Key312 ? Dir312 : Dir314);

        /// <summary>旧布局迁移：程序目录下的 runtime 改名成 runtime-py314</summary>
        public static void MigrateLegacyLayout()
        {
            try
            {
                string legacy = Path.Combine(AppDir, RuntimeLocator.DirGeneric);
                string target = Path.Combine(AppDir, Dir314);
                if (Directory.Exists(legacy) && !Directory.Exists(target))
                    Directory.Move(legacy, target);
            }
            catch { }
        }

        /// <summary>枚举两个运行时槽位并解析实际位置</summary>
        public static List<RuntimeSlot> Discover()
        {
            var defs = new List<RuntimeSlot>
            {
                new RuntimeSlot
                {
                    Key = Key312,
                    Title = "Python 3.12 · 兼容模式",
                    Note = "三方库生态覆盖最广，老库与科学计算包优先选它",
                },
                new RuntimeSlot
                {
                    Key = KeyDefault,
                    Title = "Python 3.14 · 默认",
                    Note = "版本较新，部分依赖尚未适配的库会装不上",
                },
            };

            foreach (var s in defs)
            {
                var loc = RuntimeLocator.Locate(s.Key);
                s.Dir = loc.Dir ?? Path.Combine(AppDir,
                    s.Key == Key312 ? Dir312 : Dir314);
                s.FoundBy = loc.Origin;
                s.PythonExe = Path.Combine(s.Dir, "python.exe");
                s.Present = loc.Exists;
                if (loc.Exists) RuntimeLocator.Remember(s.Key, loc.Dir);
            }
            return defs;
        }

        /// <summary>读取运行时自报的版本（同步，仅在运行时页调用）</summary>
        public static void FillDetails(RuntimeSlot slot, CommandRunner runner)
        {
            if (!slot.Present) return;
            try
            {
                var v = runner.Run(BuildCommand.From(slot.PythonExe, null,
                    new[] { "-c", "import sys;print(sys.version.split()[0])" },
                    null, 20000));
                if (v.ExitCode == 0) slot.Version = v.StdOut.Trim();

                var p = runner.Run(BuildCommand.From(slot.PythonExe, null,
                    new[] { "-c",
                        "import PyInstaller,sys;" +
                        "sys.stdout.write(PyInstaller.__version__)" },
                    null, 30000));
                if (p.ExitCode == 0) slot.PyInstallerVersion = p.StdOut.Trim();
            }
            catch { }
        }

        /// <summary>优先选可用的：3.12 在前</summary>
        public static RuntimeSlot PickUsable(IEnumerable<RuntimeSlot> slots) =>
            slots.FirstOrDefault(s => s.Usable);

        /// <summary>目录是否像一个可用的 Python 运行时</summary>
        public static bool LooksLikeRuntime(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            if (!File.Exists(Path.Combine(dir, "python.exe"))) return false;
            return Directory.Exists(Path.Combine(dir, "Lib"));
        }

        /// <summary>把用户指定的目录登记为某个槽位</summary>
        public static bool Register(string key, string sourceDir,
                                    Action<string, string> log)
            => RuntimeLocator.Adopt(key, sourceDir, log);

        /// <summary>把实际位置与搜索范围整理成文本，便于排查</summary>
        public static string DescribeLocations()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var s in Discover())
            {
                sb.AppendLine(s.Title);
                sb.AppendLine("  位置: " + s.Dir);
                sb.AppendLine("  来源: " + s.FoundBy);
                sb.AppendLine("  状态: " + (s.Present ? "已找到" : "未找到"));
            }
            sb.AppendLine();
            sb.AppendLine("搜索过这些位置：");
            foreach (var r in RuntimeLocator.SearchRoots())
                sb.AppendLine("  " + r.Key + "    (" + r.Value + ")");
            return sb.ToString();
        }

        /// <summary>排查用：DAINRIC_RTDUMP 触发，把详情写文件</summary>
        public static string DumpDiagnostics() => DescribeLocations();

        /// <summary>默认输出目录：桌面下的 Dainric产物。
        /// 刻意不用程序安装目录 —— 装到 %LOCALAPPDATA% 后用户找不到产物。</summary>
        public static string DefaultOutputDir()
        {
            try
            {
                string desk = Environment.GetFolderPath(
                    Environment.SpecialFolder.DesktopDirectory);
                if (!string.IsNullOrEmpty(desk))
                {
                    string d = Path.Combine(desk, "Dainric产物");
                    Directory.CreateDirectory(d);
                    return d;
                }
            }
            catch { }
            return AppContext.BaseDirectory;
        }
    }
}
