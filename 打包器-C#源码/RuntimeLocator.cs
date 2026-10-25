// Dainric 打包器 · 运行时定位
// 作者: Dainric
//
// 目标：运行时目录不必和 exe 同层，放在任意位置都能被找到。
//
// 为什么不内置进 exe：
//   两个运行时合计 186 MB，内置后 exe 会涨到 246 MB，
//   且每次启动都要解压到 %TEMP%（慢，且受限环境可能没有写权限）。
//   改成「多位置搜索 + 记住选择」，效果更好也更省事。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Dainric.Packer
{
    /// <summary>运行时解析结果：到哪里找、找到了什么</summary>
    public sealed class RuntimeLocation
    {
        public string Dir { get; set; }
        public string Origin { get; set; }      // 来源说明，显示给用户看
        public bool Exists => !string.IsNullOrEmpty(Dir) && Directory.Exists(Dir);
    }

    public static class RuntimeLocator
    {
        public const string Dir312 = "runtime-py312";
        public const string Dir314 = "runtime-py314";
        public const string DirGeneric = "runtime";

        /// <summary>集中存放目录：%LOCALAPPDATA%\\Dainric</summary>
        public static string HomeDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Dainric");

        static string ConfigPath => Path.Combine(HomeDir, "config.json");

        // ── 配置读写（极简 JSON，避免引入依赖）──
        public static string LoadConfiguredDir(string key)
        {
            try
            {
                if (!File.Exists(ConfigPath)) return null;
                string txt = File.ReadAllText(ConfigPath);
                return Extract(txt, key);
            }
            catch { return null; }
        }

        public static void SaveConfiguredDir(string key, string value)
        {
            try
            {
                Directory.CreateDirectory(HomeDir);
                var map = new Dictionary<string, string>();
                if (File.Exists(ConfigPath))
                {
                    string txt = File.ReadAllText(ConfigPath);
                    foreach (var k in new[] { Dir312, Dir314, "lastUsed" })
                    {
                        string v = Extract(txt, k);
                        if (!string.IsNullOrEmpty(v)) map[k] = v;
                    }
                }
                map[key] = value;
                var sb = new StringBuilder();
                sb.AppendLine("{");
                int i = 0;
                foreach (var kv in map)
                {
                    sb.Append("  \"").Append(kv.Key).Append("\": \"")
                      .Append(kv.Value.Replace("\\", "\\\\")).Append("\"");
                    if (++i < map.Count) sb.Append(',');
                    sb.AppendLine();
                }
                sb.AppendLine("}");
                File.WriteAllText(ConfigPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        static string Extract(string json, string key)
        {
            var m = System.Text.RegularExpressions.Regex.Match(json,
                "\"" + System.Text.RegularExpressions.Regex.Escape(key) +
                "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return null;
            return m.Groups[1].Value.Replace("\\\\", "\\");
        }

        // ── 候选搜索根目录 ──
        /// <summary>所有可能存放 runtimes 的根目录，按优先级排列</summary>
        public static List<KeyValuePair<string, string>> SearchRoots()
        {
            var roots = new List<KeyValuePair<string, string>>();
            void add(string dir, string why)
            {
                if (!string.IsNullOrEmpty(dir))
                    roots.Add(new KeyValuePair<string, string>(dir, why));
            }

            string appDir = AppContext.BaseDirectory.TrimEnd('\\');
            add(appDir, "程序所在目录");
            add(Path.Combine(appDir, "runtime"), "程序目录下的 runtime");

            try
            {
                var up = Directory.GetParent(appDir);
                if (up != null)
                {
                    add(up.FullName, "上一级目录");
                    add(Path.Combine(up.FullName, "runtime"), "上一级目录下的 runtime");
                }
                var up2 = up?.Parent;
                if (up2 != null) add(up2.FullName, "上两级目录");
            }
            catch { }

            add(HomeDir, "集中目录 %LOCALAPPDATA%\\Dainric");
            add(Path.Combine(HomeDir, "runtime"), "集中目录下的 runtime");

            foreach (var special in new[]
            {
                Environment.SpecialFolder.DesktopDirectory,
                Environment.SpecialFolder.MyDocuments,
                Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.LocalApplicationData,
            })
            {
                try
                {
                    string p = Environment.GetFolderPath(special);
                    if (!string.IsNullOrEmpty(p))
                    {
                        add(p, "系统目录 " + special);
                        add(Path.Combine(p, "Dainric"), "系统目录下的 Dainric");
                    }
                }
                catch { }
            }

            string env = Environment.GetEnvironmentVariable("DAINRIC_RUNTIME_HOME");
            add(env, "环境变量 DAINRIC_RUNTIME_HOME");

            return roots;
        }

        /// <summary>在给定根目录下找指定名字的运行时目录</summary>
        static string Probe(string root, params string[] names)
        {
            foreach (var n in names)
            {
                string d = Path.Combine(root, n);
                if (File.Exists(Path.Combine(d, "python.exe")))
                    return d;
            }
            return null;
        }

        /// <summary>解析某个槽位的实际目录</summary>
        public static RuntimeLocation Locate(string key)
        {
            string[] names = key == RuntimeManager.Key312
                ? new[] { Dir312, "python312", "py312" }
                : new[] { Dir314, "python314", "py314", DirGeneric };

            // 1) 用户手动指定过的位置最优先
            string saved = LoadConfiguredDir(key);
            if (!string.IsNullOrEmpty(saved) && File.Exists(
                    Path.Combine(saved, "python.exe")))
                return new RuntimeLocation { Dir = saved, Origin = "已记住的位置" };

            // 2) 逐个候选根目录探测
            foreach (var root in SearchRoots())
            {
                if (!Directory.Exists(root.Key)) continue;
                string d = Probe(root.Key, names);
                if (d != null)
                    return new RuntimeLocation { Dir = d, Origin = root.Value };
            }
            return new RuntimeLocation { Dir = null, Origin = "未找到" };
        }

        /// <summary>把找到的位置记下来，下次启动直接命中</summary>
        public static void Remember(string key, string dir)
        {
            if (!string.IsNullOrEmpty(dir)) SaveConfiguredDir(key, dir);
        }

        /// <summary>把某个目录登记为指定槽位（复制到集中目录）</summary>
        public static bool Adopt(string key, string sourceDir,
                                 Action<string, string> log)
        {
            try
            {
                if (!RuntimeManager.LooksLikeRuntime(sourceDir))
                {
                    log?.Invoke("这个目录不像完整的 Python 运行时（要有 python.exe 和 Lib）",
                                "err");
                    return false;
                }
                // 校验里面有 PyInstaller
                var runner = new CommandRunner(log);
                var r = runner.Run(BuildCommand.From(
                    Path.Combine(sourceDir, "python.exe"), null,
                    new[] { "-c", "import PyInstaller,sys;" +
                                  "sys.stdout.write(PyInstaller.__version__)" },
                    null, 30000));
                if (r.ExitCode != 0)
                {
                    log?.Invoke("该运行时里没有 PyInstaller，请先在其中执行: "
                                + "python -m pip install pyinstaller", "err");
                    return false;
                }
                log?.Invoke("  检测到 PyInstaller " + r.StdOut.Trim(), "dim");

                string target = Path.Combine(HomeDir, key == RuntimeManager.Key312
                                             ? Dir312 : Dir314);
                if (!string.Equals(Path.GetFullPath(sourceDir),
                                   Path.GetFullPath(target),
                                   StringComparison.OrdinalIgnoreCase))
                {
                    log?.Invoke("  复制到集中目录（约 1 分钟）…", "dim");
                    if (Directory.Exists(target))
                        Directory.Delete(target, true);
                    CopyDir(sourceDir, target);
                }
                SaveConfiguredDir(key, target);
                log?.Invoke("  已登记: " + target, "ok");
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke("登记失败: " + ex.Message, "err");
                return false;
            }
        }

        static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(src))
            {
                string name = Path.GetFileName(d);
                if (name == "__pycache__") continue;
                CopyDir(d, Path.Combine(dst, name));
            }
        }
    }
}
