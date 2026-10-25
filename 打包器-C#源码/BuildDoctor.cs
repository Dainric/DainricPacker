// Dainric 打包器 · 打包失败诊断
// 作者: Dainric
//
// 打包失败时，原始日志动辄几百行，用户看不出真正原因。
// 这里把常见失败模式识别出来，直接给出「缺什么 / 怎么修」。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Dainric.Packer
{
    public sealed class Diagnosis
    {
        public string Title { get; set; }            // 一句话结论
        public List<string> Reasons { get; } = new List<string>();
        public List<string> Fixes { get; } = new List<string>();
        public string Severity { get; set; } = "err"; // err / warn

        /// <summary>日志里提到的缺失模块名，用于自动补 hidden-import</summary>
        public List<string> MissingModules { get; } = new List<string>();

        /// <summary>可从日志里抽出的「需要整包收集」的库名。
        /// 例如 matplotlib 缺数据文件 → 用 --collect-data matplotlib 能救回来。
        /// 这些同样参与自动重试（见 BuildWithRetry）。</summary>
        public List<string> CollectData { get; } = new List<string>();
        public List<string> CollectBinaries { get; } = new List<string>();
        public List<string> CollectSubmodules { get; } = new List<string>();

        /// <summary>本次诊断是否给出了可自动执行的修复动作</summary>
        public bool HasAutoFix =>
            MissingModules.Count > 0 || CollectData.Count > 0
            || CollectBinaries.Count > 0 || CollectSubmodules.Count > 0;

        /// <summary>本次诊断对应的完整原始输出，供重试判断使用</summary>
        public string RawOutput { get; set; } = "";

        public bool HasContent => Reasons.Count > 0 || Fixes.Count > 0;

        public string Render()
        {
            var sb = new StringBuilder();
            sb.AppendLine("诊断结果");
            sb.AppendLine(new string('-', 52));
            sb.AppendLine(Title);
            if (Reasons.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("可能原因");
                foreach (var r in Reasons) sb.AppendLine("  · " + r);
            }
            if (MissingModules.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("识别到缺失模块");
                foreach (var m in MissingModules) sb.AppendLine("  · " + m);
            }
            if (CollectData.Count + CollectBinaries.Count
                + CollectSubmodules.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("识别到需要整包收集的库");
                foreach (var m in CollectData)
                    sb.AppendLine("  · " + m + "  (数据文件)");
                foreach (var m in CollectBinaries)
                    sb.AppendLine("  · " + m + "  (二进制)");
                foreach (var m in CollectSubmodules)
                    sb.AppendLine("  · " + m + "  (子模块)");
            }
            if (Fixes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("建议处理");
                foreach (var f in Fixes) sb.AppendLine("  · " + f);
            }
            return sb.ToString();
        }
    }

    /// <summary>把 PyInstaller 的输出翻译成用户能看懂的结论</summary>
    public static class BuildDoctor
    {
        sealed class Rule
        {
            public string Id;
            public Func<string, bool> Match;
            public string Reason;
            public string[] Fixes;
            /// <summary>从输出里抽出缺失模块名用的正则（可选）</summary>
            public string Modules;
            /// <summary>抽出「需要 --collect-data 的库名」用的正则（可选）</summary>
            public string DataLibs;
            /// <summary>抽出「需要 --collect-binaries 的库名」用的正则（可选）</summary>
            public string BinaryLibs;
            /// <summary>抽出「需要 --collect-submodules 的库名」用的正则（可选）</summary>
            public string SubLibs;
        }

        // 顺序有意义：越靠前越具体
        static readonly Rule[] Rules = new[]
        {
            new Rule
            {
                Id = "missing-module",
                Match = s => Regex.IsMatch(s,
                    @"ModuleNotFoundError: No module named '([^']+)'") ||
                    Regex.IsMatch(s, @"ImportError: No module named '?([\w\.]+)'?"),
                Modules = @"No module named '([^']+)'",
                Reason = "脚本导入了未安装（或未被自动收集）的模块",
                Fixes = new[]
                {
                    "在「高级参数」里把缺失的模块填进 hidden-import",
                    "或先安装它：pip install <模块名>",
                    "若模块是运行时才导入的（importlib / 插件式加载），必须手动加 hidden-import",
                }
            },
            // 插件/子模块没被收录。
            // 必须排在 hidden-import-hint 之前：Analyze 是「全部命中」而不是
            // 「首个命中」，两条规则都会匹配同一行，谁在前面谁先抽到库名。
            // 真实格式有两种（大小写与引号都不同，必须都覆盖）：
            //   imphook:   Hidden import "foo.bar" not found!
            //   analysis:  Hidden import 'foo.bar' not found
            new Rule
            {
                Id = "collect-submodules",
                Match = s => s.IndexOf("idden import",
                                       StringComparison.Ordinal) >= 0
                             && Regex.IsMatch(s, @"not found",
                                              RegexOptions.IgnoreCase),
                SubLibs = @"idden import\s+['""]([A-Za-z_][\w.\-]*)",
                Reason = "某个库的子模块是插件式加载的，静态分析收不全",
                Fixes = new[]
                {
                    "打包时加上 --collect-submodules <库名> 整包收集",
                    "常见于 PyQt5 / PySide6 / cryptography / 各种 plugin 框架",
                }
            },
            new Rule
            {
                Id = "hidden-import-hint",
                Match = s => s.IndexOf("idden import",
                                       StringComparison.Ordinal) >= 0
                             && Regex.IsMatch(s, @"not found",
                                              RegexOptions.IgnoreCase),
                Reason = "PyInstaller 报告有 hidden import 找不到",
                Fixes = new[]
                {
                    "确认该模块已安装；拼写是否正确",
                    "不需要的可以忽略，它通常只是警告",
                }
            },
            new Rule
            {
                Id = "dll-missing",
                Match = s => s.Contains("Failed to load Python DLL")
                             || s.Contains("api-ms-win-crt")
                             || Regex.IsMatch(s,
                                 "cannot open shared object|DLL load failed"),
                Reason = "目标机缺少 VC++ 运行时或系统 DLL（常见于精简版 Windows）",
                Fixes = new[]
                {
                    "让目标机安装 Microsoft Visual C++ Redistributable (x64)",
                    "或改用「目录」形态打包，依赖更容易排查",
                }
            },
            new Rule
            {
                Id = "tkinter-missing",
                Match = s => s.Contains("_tkinter")
                             || s.Contains("Tcl init")
                             || s.Contains("Can't find a usable init.tcl"),
                Reason = "Tcl/Tk 数据文件缺失，tkinter 程序无法启动",
                Fixes = new[]
                {
                    "确认打包器自带的 runtime 完整（tcl 目录不能删）",
                    "若用系统 Python，请确认它是完整安装而非 embeddable 版",
                }
            },
            new Rule
            {
                Id = "permission",
                Match = s => s.Contains("PermissionError")
                             || s.Contains("Access is denied")
                             || s.Contains("拒绝访问"),
                Reason = "文件被占用或没有写入权限",
                Fixes = new[]
                {
                    "关闭正在运行的旧 EXE（它占用了输出文件）",
                    "换一个输出目录，避免使用系统保护目录",
                    "暂停杀毒软件对该目录的实时扫描",
                }
            },
            new Rule
            {
                Id = "locked-output",
                Match = s => s.Contains("being used by another process")
                             || s.Contains("另一个程序正在使用"),
                Reason = "目标 EXE 正在运行，无法覆盖",
                Fixes = new[] { "关闭正在运行的旧程序后重试" }
            },
            new Rule
            {
                Id = "disk-full",
                Match = s => s.Contains("No space left")
                             || s.Contains("磁盘空间不足"),
                Reason = "磁盘空间不足",
                Fixes = new[] { "清理输出目录与 %TEMP% 后重试" }
            },
            new Rule
            {
                Id = "nonascii-path",
                Match = s => s.Contains("UnicodeEncodeError")
                             || s.Contains("UnicodeDecodeError")
                             || s.Contains("codec can't encode"),
                Reason = "路径或输出含非 ASCII 字符，编码环节出错",
                Fixes = new[]
                {
                    "把脚本与输出目录改为纯英文路径",
                    "避免中文用户名下的桌面/文档目录",
                }
            },
            new Rule
            {
                Id = "recursion",
                Match = s => s.Contains("RecursionError")
                             || s.Contains("maximum recursion depth"),
                Reason = "依赖分析递归过深",
                Fixes = new[]
                {
                    "在脚本里把递归逻辑改成迭代",
                    "对大型库可先用 --exclude-module 排除不需要的部分",
                }
            },
            new Rule
            {
                Id = "spec-error",
                Match = s => Regex.IsMatch(s,
                    @"SyntaxError|invalid syntax") && s.Contains(".spec"),
                Reason = "自动生成的 .spec 文件有语法问题",
                Fixes = new[]
                {
                    "删除输出目录下的 .spec 后重试",
                    "输出名不要含空格或特殊字符",
                }
            },
            new Rule
            {
                Id = "syntax",
                Match = s => s.Contains("SyntaxError"),
                Reason = "被打包的脚本本身有语法错误",
                Fixes = new[] { "先单独运行 python 脚本名 确认能跑通" }
            },
            new Rule
            {
                Id = "hook-error",
                Match = s => s.Contains("hook-") && s.Contains("ERROR"),
                Reason = "某个第三方库的 PyInstaller 钩子执行失败",
                Fixes = new[]
                {
                    "升级 PyInstaller 与出错库到最新版",
                    "在日志中搜索 hook- 找到是哪个库，再排除或单独收集它",
                }
            },
            new Rule
            {
                Id = "bootloader",
                Match = s => s.Contains("Failed to execute script")
                             || s.Contains("Failed to start embedded python"),
                Reason = "打包出的 EXE 启动失败（不是打包失败）",
                Fixes = new[]
                {
                    "改用「控制台」形态重新打包，就能看到真实报错",
                    "常见原因是缺 hidden-import 或数据文件",
                }
            },
            // ── 以下三条是「能自动救」的失败：抽库名 -> 补 --collect-* 重试 ──

            // Matplotlib is currently using agg, which is a non-GUI backend
            // / 找不到 fontlist.json / 各种 *.dat 读不到
            new Rule
            {
                Id = "collect-data",
                Match = s => Regex.IsMatch(s,
                    @"FileNotFoundError.*?(\.json|\.dat|\.txt|\.csv|\.ttf|\.qm|\.mo|\.ui)\b")
                    || Regex.IsMatch(s,
                    @"Unable to find\s+\S+\.(json|dat|mo|qm)")
                    || s.Contains("fontlist.json")
                    || Regex.IsMatch(s,
                    @"No such file or directory.*?site-packages[\\/](\w+)"),
                DataLibs =
                    @"site-packages[\\/]+([A-Za-z_]\w*)"
                    + @"|Unable to find[^\r\n]*?[\\/](\w+)[\\/]",
                Reason = "运行期缺少库自带的数据文件（字体、图标、模板、翻译等）",
                Fixes = new[]
                {
                    "打包时加上 --collect-data <库名> 把数据文件一起收进去",
                    "本工具已自动识别并重试；若仍失败，手动指定该库",
                }
            },

            // 缺 .dll / .pyd / .so
            new Rule
            {
                Id = "collect-binaries",
                Match = s => Regex.IsMatch(s,
                    @"DLL load failed|OSError.*?cannot open|"
                    + @"ImportError.*?\.(pyd|dll|so)\b")
                    || Regex.IsMatch(s,
                    @"Failed to (load|find) (library|binary)\s+(\S+)"),
                BinaryLibs =
                    @"site-packages[\\/]+([A-Za-z_]\w*)"
                    + @"|Failed to (?:load|find) (?:library|binary)\s+([A-Za-z_]\w*)",
                Reason = "运行期缺少库自带的二进制（.pyd / .dll / .so）",
                Fixes = new[]
                {
                    "打包时加上 --collect-binaries <库名>",
                    "常见于 numpy / scipy / opencv / torch 这类带原生扩展的库",
                }
            },
        };

        /// <summary>根据退出码与完整输出给出诊断</summary>
        public static Diagnosis Analyze(int exitCode, string output)
        {
            var d = new Diagnosis();
            string text = output ?? "";
            d.RawOutput = text;

            var hits = Rules.Where(r =>
            {
                try { return r.Match(text); } catch { return false; }
            }).ToList();

            if (exitCode == -1)
            {
                d.Title = "打包超时被终止";
                d.Severity = "warn";
                d.Reasons.Add("构建时间超过了设定的超时上限");
                d.Fixes.Add("在界面把「打包超时」调大（例如 3600 秒）");
                d.Fixes.Add("含大型库（torch / PyQt / numpy）的项目通常需要几分钟到几十分钟");
                return d;
            }
            if (exitCode == -2)
            {
                d.Title = "无法启动打包引擎";
                d.Fixes.Add("确认 runtime 目录存在且完整");
                d.Fixes.Add("或在目标机安装 PyInstaller: pip install pyinstaller");
                return d;
            }

            if (hits.Count == 0)
            {
                d.Title = "打包失败（未匹配到已知失败模式）";
                d.Severity = "warn";
                d.Reasons.Add("退出码 " + exitCode);
                var last = LastMeaningfulLines(text, 3);
                foreach (var l in last) d.Reasons.Add("日志末尾: " + l);
                d.Fixes.Add("在「构建选项」里选「控制台」再打一次，能看到更完整的报错");
                d.Fixes.Add("把输出日志复制出来搜索 ERROR 关键字");
                return d;
            }

            if (hits.Any(h => h.Id == "bootloader"))
                d.Title = "打包出的 EXE 启动失败";
            else
                d.Title = "打包失败";

            var seen = new HashSet<string>();
            foreach (var h in hits)
            {
                if (seen.Add(h.Reason)) d.Reasons.Add(h.Reason);
                foreach (var f in h.Fixes)
                    if (!d.Fixes.Contains(f)) d.Fixes.Add(f);

                // 抽出真实缺失的模块名，供自动补 hidden-import 用
                if (!string.IsNullOrEmpty(h.Modules))
                {
                    foreach (Match mm in Regex.Matches(text, h.Modules))
                    {
                        string mod = mm.Groups[1].Value.Trim();
                        // 只接受合法的顶层模块名
                        if (IsTopModule(mod) && !d.MissingModules.Contains(mod))
                            d.MissingModules.Add(mod);
                    }
                }

                // 抽出需要整包收集的库名，供自动补 --collect-* 用
                Harvest(text, h.DataLibs, d.CollectData);
                Harvest(text, h.BinaryLibs, d.CollectBinaries);
                Harvest(text, h.SubLibs, d.CollectSubmodules);
            }
            return d;
        }

        /// <summary>把正则命中的库名收进目标列表（跳过标准库与自身）</summary>
        static void Harvest(string text, string pattern, List<string> into)
        {
            if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(text))
                return;
            try
            {
                foreach (Match m in Regex.Matches(text, pattern))
                {
                    // 正则有多个候选捕获组，取第一个非空的
                    string name = null;
                    for (int g = 1; g < m.Groups.Count; g++)
                    {
                        if (m.Groups[g].Success
                            && !string.IsNullOrWhiteSpace(m.Groups[g].Value))
                        {
                            name = m.Groups[g].Value.Trim();
                            break;
                        }
                    }
                    if (!IsTopModule(name)) continue;
                    // 归一化成顶层包：--collect-* 收的是整个包，
                    // 给 "PyQt5.sip" 不如给 "PyQt5" 收得全，也更不容易出岔子
                    string top = name.Split('.')[0];
                    if (!IsTopModule(top)) continue;
                    if (into.Contains(top)) continue;
                    into.Add(top);
                }
            }
            catch { }
        }

        /// <summary>是否像一个可以拿去做 --collect-* 的顶层库名</summary>
        static bool IsTopModule(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (!Regex.IsMatch(s, @"^[A-Za-z_][\w.\-]*$")) return false;
            // 这些是 PyInstaller 自己的字眼，不是库名。
            // 用子串判断：正则从 "site-packages" 里只能吃到 "site"，
            // 等值比较会漏掉它。
            string low = s.ToLowerInvariant();
            if ("site-packages".Contains(low)) return false;
            if ("python".Contains(low)) return false;
            if ("lib".Contains(low) && low.Length <= 3) return false;
            switch (low)
            {
                case "scripts":
                case "import":
                case "module":
                case "error":
                case "none":
                case "dist":
                case "build":
                    return false;
            }
            return true;
        }

        static List<string> LastMeaningfulLines(string text, int n)
        {
            var lines = (text ?? "").Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0
                            && !l.StartsWith("INFO:")
                            && !l.StartsWith("WARNING:"))
                .ToList();
            return lines.Skip(Math.Max(0, lines.Count - n)).ToList();
        }
    }
}
