// Dainric 打包器 · 依赖检查
// 作者: Dainric
//
// 一处实现，两处使用：
//   1) 打包前的前置检查 —— 缺依赖直接拦下，不要白跑整条重试链
//   2) 「依赖 / 历史」页的依赖分析
// 以前这两件事各写了一遍（页面里内嵌了一段 Python），迟早会漂移，现在合并。
//
// 实现要点：
//   用标准库 ast 解析脚本，不执行用户代码。
//   可用性判断放在「被检查的那个解释器」里做 —— 不能拿 A 的 sys.path 判 B。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Dainric.Packer
{
    /// <summary>一个导入项的归类</summary>
    public sealed class ImportEntry
    {
        public string Module { get; set; }
        public string Kind { get; set; }      // 标准库 / 本地 / 已安装 / 缺失
        public bool Missing => Kind == "缺失";
        public bool ThirdParty => Kind == "已安装" || Kind == "缺失";
        public string Note { get; set; } = ""; // 缺失原因
    }

    /// <summary>检查结果</summary>
    public sealed class DependencyReport
    {
        public bool Ran { get; set; }              // 是否真的执行了
        public string SkipReason { get; set; }     // 未执行的原因
        public List<ImportEntry> Entries { get; } = new List<ImportEntry>();
        public string RawOutput { get; set; } = "";

        public List<string> Missing =>
            Entries.Where(e => e.Missing).Select(e => e.Module).ToList();
        public List<string> Installed =>
            Entries.Where(e => e.Kind == "已安装").Select(e => e.Module).ToList();
        public int StdlibCount => Entries.Count(e => e.Kind == "标准库");
        public int LocalCount => Entries.Count(e => e.Kind == "本地");

        public bool Clean => Ran && Missing.Count == 0;

        /// <summary>「依赖 / 历史」页用的完整报告</summary>
        public string RenderDetail(string scriptName)
        {
            if (!Ran) return "依赖分析未完成：" + (SkipReason ?? "原因未知");

            var sb = new StringBuilder();
            sb.AppendLine("脚本: " + scriptName);
            sb.AppendLine();

            var installed = Installed;
            sb.AppendLine("第三方依赖 (" + (installed.Count + Missing.Count) + ")");
            if (installed.Count == 0 && Missing.Count == 0) sb.AppendLine("  （无）");
            foreach (var m in installed)
                sb.AppendLine(string.Format("  {0,-22} 已安装", m));

            if (Missing.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("未安装 (" + Missing.Count + ")  ← 打包后运行会报错");
                foreach (var e in Entries.Where(x => x.Missing))
                {
                    sb.AppendLine(string.Format("  {0,-22} 缺失{1}", e.Module,
                        string.IsNullOrEmpty(e.Note) ? "" : "  " + e.Note));
                }
                sb.AppendLine();
                sb.AppendLine("安装: python -m pip install "
                              + string.Join(" ", Missing));
            }

            sb.AppendLine();
            sb.AppendLine("标准库 " + StdlibCount + " 个 · 本地模块 "
                          + LocalCount + " 个");
            sb.AppendLine();
            sb.AppendLine("提示: 第三方库会被 PyInstaller 自动收集并打包进 EXE。");
            sb.AppendLine("      未安装的模块必须先装好，补 hidden-import 是没用的。");
            return sb.ToString();
        }

        /// <summary>打包流程里用的紧凑报告</summary>
        public string RenderBrief()
        {
            if (!Ran) return "依赖检查未执行：" + (SkipReason ?? "原因未知");
            if (Missing.Count == 0)
                return "依赖检查通过：第三方 " + Installed.Count
                     + " 个 · 标准库 " + StdlibCount
                     + " 个 · 本地 " + LocalCount + " 个";
            var sb = new StringBuilder();
            sb.AppendLine("依赖检查发现 " + Missing.Count + " 个模块未安装：");
            foreach (var e in Entries.Where(x => x.Missing))
                sb.AppendLine("  · " + e.Module
                    + (string.IsNullOrEmpty(e.Note) ? "" : "  —— " + e.Note));
            sb.Append("安装命令: python -m pip install ")
              .AppendLine(string.Join(" ", Missing));
            return sb.ToString();
        }
    }

    public static class DependencyChecker
    {
        /// <summary>完整流程：扫 import -> 去掉标准库/本地 -> 验可用性</summary>
        public static DependencyReport Check(string scriptPath, string pythonExe,
                                             CommandRunner runner)
        {
            var rep = new DependencyReport();

            if (string.IsNullOrEmpty(scriptPath) || !File.Exists(scriptPath))
            {
                rep.SkipReason = "没有可检查的脚本";
                return rep;
            }
            if (string.IsNullOrEmpty(pythonExe) || !File.Exists(pythonExe))
            {
                rep.SkipReason = "没有可用的 Python 运行时";
                return rep;
            }

            // 一次性在目标解释器里完成解析 + 归类，避免多次启动进程
            var args = new List<string> { "-c", ScanScript, scriptPath };
            var cmd = BuildCommand.From(pythonExe, null, args, null, 60000);
            RunResult r;
            try { r = runner.Run(cmd); }
            catch (Exception ex)
            {
                rep.SkipReason = "执行失败: " + ex.Message;
                return rep;
            }

            string text = r.StdOut ?? "";
            if (r.ExitCode != 0)
            {
                rep.SkipReason = "分析失败: " + (r.StdErr + text).Trim();
                return rep;
            }
            if (text.Contains("PARSE_FAIL"))
            {
                rep.SkipReason = "脚本无法解析为 Python 语法";
                return rep;
            }
            rep.RawOutput = text;

            foreach (var line in text.Split('\n'))
            {
                string s = line.Trim();
                int bar = s.LastIndexOf('|');
                if (bar <= 0) continue;
                string mod = s.Substring(0, bar);
                string kind = MapKind(s.Substring(bar + 1));
                if (mod.Length == 0 || kind == null) continue;
                rep.Entries.Add(new ImportEntry { Module = mod, Kind = kind });
            }
            rep.Ran = true;
            return rep;
        }

        /// <summary>
        /// 把子进程给出的 ASCII 标记映射成显示用的中文。
        /// 刻意不让中文跨进程边界：中文 Windows 上 Python 的 stdout 用
        /// 系统 ANSI 码页（GBK），而读取端按 UTF-8 解，中文标记会全部乱码，
        /// 归类判断跟着一起失配 —— 这个坑踩过一次。
        /// </summary>
        static string MapKind(string tag)
        {
            switch ((tag ?? "").Trim().ToUpperInvariant())
            {
                case "STD": return "标准库";
                case "LOCAL": return "本地";
                case "OK": return "已安装";
                case "MISSING": return "缺失";
            }
            // 兼容旧的中文标记
            string t = (tag ?? "").Trim();
            if (t == "标准库" || t == "本地" || t == "已安装" || t == "缺失")
                return t;
            return null;
        }

        /// <summary>
        /// 在目标解释器里跑的扫描脚本。
        /// 脚本路径用 argv 传，避开引号转义问题。
        /// 输出格式：模块名|归类
        /// 注意用 utf-8-sig 读：Notepad / PowerShell 存出来的 .py 常带 BOM，
        /// 用 utf-8 读会把 BOM 当代码，ast.parse 直接报 «invalid non-printable
        /// character U+FEFF» —— 依赖检查会因此整个哑掉。
        /// </summary>
        const string ScanScript =
            "import ast, importlib.util, os, sys\n" +
            "p = sys.argv[1]\n" +
            "names = set()\n" +
            "try:\n" +
            "    with open(p, encoding='utf-8-sig') as f:\n" +
            "        tree = ast.parse(f.read())\n" +
            "    for n in ast.walk(tree):\n" +
            "        if isinstance(n, ast.Import):\n" +
            "            for a in n.names: names.add(a.name.split('.')[0])\n" +
            "        elif isinstance(n, ast.ImportFrom):\n" +
            "            if n.module and n.level == 0:\n" +
            "                names.add(n.module.split('.')[0])\n" +
            "except Exception as e:\n" +
            "    sys.stdout.write('PARSE_FAIL ' + str(e))\n" +
            "    raise SystemExit(0)\n" +
            "std = set(getattr(sys, 'stdlib_module_names', ()))\n" +
            "local = set()\n" +
            "d = os.path.dirname(os.path.abspath(p))\n" +
            "try:\n" +
            "    for f in os.listdir(d):\n" +
            "        if f.endswith('.py'): local.add(f[:-3])\n" +
            "        elif os.path.isdir(os.path.join(d, f)) and \\\n" +
            "                os.path.isfile(os.path.join(d, f, '__init__.py')):\n" +
            "            local.add(f)\n" +
            "except Exception:\n" +
            "    pass\n" +
            "for n in sorted(names):\n" +
            "    if n == '__future__':\n" +
            "        continue\n" +
            "    if n in local:\n" +
            "        sys.stdout.write(n + '|LOCAL\\n')\n" +
            "    elif n in std:\n" +
            "        sys.stdout.write(n + '|STD\\n')\n" +
            "    else:\n" +
            "        try:\n" +
            "            ok = importlib.util.find_spec(n) is not None\n" +
            "        except Exception:\n" +
            "            ok = False\n" +
            "        sys.stdout.write(n + ('|OK\\n' if ok else '|MISSING\\n'))\n";

        // ── 下面两个只在拿不到解释器时用，作为纯文本兜底 ──

        static readonly Regex ImportRe = new Regex(
            @"^[ \t]*import[ \t]+([^\r\n#]+)", RegexOptions.Multiline);
        static readonly Regex FromRe = new Regex(
            @"^[ \t]*from[ \t]+([A-Za-z_][\w\.]*)[ \t]+import\b",
            RegexOptions.Multiline);

        /// <summary>
        /// 把「自检参数」输入框拆成参数列表。
        /// 支持引号包裹（含空格的参数），没有引号就按空白切分。
        /// </summary>
        public static List<string> SplitArgs(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(text)) return list;

            var cur = new StringBuilder();
            char quote = '\0';
            foreach (char c in text)
            {
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                    else cur.Append(c);
                }
                else if (c == '"' || c == '\'') quote = c;
                else if (char.IsWhiteSpace(c))
                {
                    if (cur.Length > 0) { list.Add(cur.ToString()); cur.Clear(); }
                }
                else cur.Append(c);
            }
            if (cur.Length > 0) list.Add(cur.ToString());
            return list;
        }

        /// <summary>
        /// 判断输出是否属于「程序在抱怨用法」而不是「程序坏了」。
        /// 打的是需要命令行参数的脚本时，裸启动必然走到这里；
        /// 把它当失败报出去就是误报 —— 产物本身是好的。
        /// </summary>
        public static bool LooksLikeUsageError(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string[] marks =
            {
                "usage:", "Usage:",
                "the following arguments are required",
                "unrecognized arguments",
                "error: argument",
                "error: the following arguments",
                "no such option",
                "missing required argument",
                "expected at least",
                "请提供", "缺少参数", "用法:",
            };
            foreach (var m in marks)
                if (text.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        /// <summary>不启动进程时，用正则粗扫一遍脚本的顶层 import</summary>
        public static List<string> FindTopLevelImports(string scriptPath)
        {
            var found = new List<string>();
            if (string.IsNullOrEmpty(scriptPath) || !File.Exists(scriptPath))
                return found;
            string src;
            try { src = File.ReadAllText(scriptPath); }
            catch { return found; }

            void Take(string name)
            {
                string top = (name ?? "").Split('.')[0].Trim();
                if (Regex.IsMatch(top, @"^[A-Za-z_][A-Za-z0-9_]*$")
                    && top != "__future__" && !found.Contains(top))
                    found.Add(top);
            }

            foreach (Match m in ImportRe.Matches(src))
                foreach (var part in m.Groups[1].Value.Split(','))
                {
                    string n = part.Trim();
                    int sp = n.IndexOf(' ');
                    if (sp > 0) n = n.Substring(0, sp);
                    Take(n);
                }
            foreach (Match m in FromRe.Matches(src))
                Take(m.Groups[1].Value);

            return found;
        }
    }
}
