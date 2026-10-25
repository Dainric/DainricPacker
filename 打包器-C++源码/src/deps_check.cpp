// Dainric 打包器 · 依赖检查
// 作者: Dainric
//
// 两处使用同一份实现：
//   1) 打包前的前置检查 —— 缺依赖直接拦下，不要白跑整条重试链
//   2) 「依赖 / 历史」页的依赖分析
//
// 两条来自 C# 版实测的结论，别改：
//   · 子进程输出用 ASCII 标记。中文 Windows 上 Python 的 stdout 是系统
//     ANSI 码页（GBK），读取端按 UTF-8 解会全乱码，字符串比较跟着失配。
//   · 读 .py 用 utf-8-sig。Notepad / PowerShell 存的文件带 BOM，
//     用 utf-8 读会让 ast.parse 报 invalid non-printable character U+FEFF，
//     整个检查哑掉。
#include "core.h"
#include <regex>
#include <sstream>
#include <algorithm>

namespace dainric
{

namespace
{

/// 在被检查的那个解释器里跑的扫描脚本。
/// 脚本路径用 argv 传，避开引号转义问题。输出格式：模块名|归类标记
const wchar_t* kScanScript =
    L"import ast, importlib.util, os, sys\n"
    L"p = sys.argv[1]\n"
    L"names = set()\n"
    L"try:\n"
    L"    with open(p, encoding='utf-8-sig') as f:\n"
    L"        tree = ast.parse(f.read())\n"
    L"    for n in ast.walk(tree):\n"
    L"        if isinstance(n, ast.Import):\n"
    L"            for a in n.names: names.add(a.name.split('.')[0])\n"
    L"        elif isinstance(n, ast.ImportFrom):\n"
    L"            if n.module and n.level == 0:\n"
    L"                names.add(n.module.split('.')[0])\n"
    L"except Exception as e:\n"
    L"    sys.stdout.write('PARSE_FAIL ' + str(e))\n"
    L"    raise SystemExit(0)\n"
    L"std = set(getattr(sys, 'stdlib_module_names', ()))\n"
    L"local = set()\n"
    L"d = os.path.dirname(os.path.abspath(p))\n"
    L"try:\n"
    L"    for f in os.listdir(d):\n"
    L"        if f.endswith('.py'): local.add(f[:-3])\n"
    L"        elif os.path.isdir(os.path.join(d, f)) and os.path.isfile(os.path.join(d, f, '__init__.py')):\n"
    L"            local.add(f)\n"
    L"except Exception:\n"
    L"    pass\n"
    L"for n in sorted(names):\n"
    L"    if n == '__future__':\n"
    L"        continue\n"
    L"    if n in local:\n"
    L"        sys.stdout.write(n + '|LOCAL\\n')\n"
    L"    elif n in std:\n"
    L"        sys.stdout.write(n + '|STD\\n')\n"
    L"    else:\n"
    L"        try:\n"
    L"            ok = importlib.util.find_spec(n) is not None\n"
    L"        except Exception:\n"
    L"            ok = False\n"
    L"        sys.stdout.write(n + ('|OK\\n' if ok else '|MISSING\\n'))\n";

/// 把子进程给出的 ASCII 标记映射成显示用的中文
std::wstring MapKind(const std::wstring& tag)
{
    std::wstring t = tag;
    // 去掉空白
    size_t a = t.find_first_not_of(L" \t\r\n");
    size_t b = t.find_last_not_of(L" \t\r\n");
    if (a == std::wstring::npos) return L"";
    t = t.substr(a, b - a + 1);

    std::wstring up = t;
    std::transform(up.begin(), up.end(), up.begin(),
                   [](wchar_t c) { return (wchar_t)towupper(c); });
    if (up == L"STD") return L"标准库";
    if (up == L"LOCAL") return L"本地";
    if (up == L"OK") return L"已安装";
    if (up == L"MISSING") return L"缺失";
    // 兼容旧的中文标记
    if (t == L"标准库" || t == L"本地" || t == L"已安装" || t == L"缺失")
        return t;
    return L"";
}

bool FileExists(const std::wstring& p)
{
    if (p.empty()) return false;
    DWORD a = GetFileAttributesW(p.c_str());
    return a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY);
}

} // namespace

std::vector<std::wstring> DepReport::Missing() const
{
    std::vector<std::wstring> r;
    for (const auto& e : entries) if (e.Missing()) r.push_back(e.module);
    return r;
}

int DepReport::StdlibCount() const
{
    int n = 0;
    for (const auto& e : entries) if (e.kind == L"标准库") ++n;
    return n;
}

int DepReport::LocalCount() const
{
    int n = 0;
    for (const auto& e : entries) if (e.kind == L"本地") ++n;
    return n;
}

int DepReport::InstalledCount() const
{
    int n = 0;
    for (const auto& e : entries) if (e.kind == L"已安装") ++n;
    return n;
}

std::wstring DepReport::RenderDetail(const std::wstring& scriptName) const
{
    if (!ran) return L"依赖分析未完成：" + skipReason;

    std::wstring r;
    r += L"脚本: " + scriptName + L"\n\n";

    auto missing = Missing();
    int third = InstalledCount() + (int)missing.size();
    r += L"第三方依赖 (" + std::to_wstring(third) + L")\n";
    if (third == 0) r += L"  （无）\n";
    for (const auto& e : entries)
        if (e.kind == L"已安装") r += L"  " + e.module + L"  已安装\n";

    if (!missing.empty())
    {
        r += L"\n未安装 (" + std::to_wstring(missing.size())
           + L")  ← 打包后运行会报错\n";
        for (const auto& m : missing) r += L"  " + m + L"  缺失\n";
        r += L"\n安装: python -m pip install ";
        for (size_t i = 0; i < missing.size(); ++i)
            r += (i ? L" " : L"") + missing[i];
        r += L"\n";
    }

    r += L"\n标准库 " + std::to_wstring(StdlibCount())
       + L" 个 · 本地模块 " + std::to_wstring(LocalCount()) + L" 个\n\n";
    r += L"提示: 第三方库会被 PyInstaller 自动收集并打包进 EXE。\n";
    r += L"      未安装的模块必须先装好，补 hidden-import 是没用的。\n";
    return r;
}

std::wstring DepReport::RenderBrief() const
{
    if (!ran) return L"依赖检查未执行：" + skipReason;
    auto missing = Missing();
    if (missing.empty())
        return L"依赖检查通过：第三方 " + std::to_wstring(InstalledCount())
             + L" 个 · 标准库 " + std::to_wstring(StdlibCount())
             + L" 个 · 本地 " + std::to_wstring(LocalCount()) + L" 个";

    std::wstring r = L"依赖检查发现 " + std::to_wstring(missing.size())
                   + L" 个模块未安装：\n";
    for (const auto& m : missing) r += L"  · " + m + L"\n";
    r += L"安装命令: python -m pip install ";
    for (size_t i = 0; i < missing.size(); ++i)
        r += (i ? L" " : L"") + missing[i];
    return r;
}

DepReport CheckDependencies(const std::wstring& scriptPath,
                            const std::wstring& pythonExe,
                            Runner& runner)
{
    DepReport rep;

    if (scriptPath.empty() || !FileExists(scriptPath))
    {
        rep.skipReason = L"没有可检查的脚本";
        return rep;
    }
    if (pythonExe.empty() || !FileExists(pythonExe))
    {
        rep.skipReason = L"没有可用的 Python 运行时";
        return rep;
    }

    Cmd c;
    c.exe = pythonExe;
    c.args = { L"-c", kScanScript, scriptPath };
    c.timeoutMs = 60000;

    RunResult r = runner.Run(c);
    // 脚本可能把诊断写到 stderr，退出码非 0 时要把两边都算上
    std::wstring text = r.out;
    if (r.exitCode != 0)
    {
        rep.skipReason = L"分析失败: " + r.Combined();
        return rep;
    }
    if (text.find(L"PARSE_FAIL") != std::wstring::npos)
    {
        rep.skipReason = L"脚本无法解析为 Python 语法";
        return rep;
    }

    std::wstringstream ss(text);
    std::wstring line;
    while (std::getline(ss, line))
    {
        while (!line.empty() && (line.back() == L'\r' || line.back() == L'\n'))
            line.pop_back();
        size_t bar = line.rfind(L'|');
        if (bar == std::wstring::npos || bar == 0) continue;
        std::wstring mod = line.substr(0, bar);
        std::wstring kind = MapKind(line.substr(bar + 1));
        if (mod.empty() || kind.empty()) continue;
        ImportEntry e;
        e.module = mod;
        e.kind = kind;
        rep.entries.push_back(e);
    }
    rep.ran = true;
    return rep;
}

std::vector<std::wstring> ScanImports(const std::wstring& scriptPath)
{
    std::vector<std::wstring> found;
    if (scriptPath.empty() || !FileExists(scriptPath)) return found;

    // 读文件：先按 UTF-8（忽略 BOM）解，失败再按系统码页
    HANDLE h = CreateFileW(scriptPath.c_str(), GENERIC_READ, FILE_SHARE_READ,
                           nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL,
                           nullptr);
    if (h == INVALID_HANDLE_VALUE) return found;

    std::string bytes;
    char buf[8192];
    DWORD n = 0;
    while (ReadFile(h, buf, sizeof(buf), &n, nullptr) && n > 0)
        bytes.append(buf, n);
    CloseHandle(h);
    if (bytes.empty()) return found;

    int need = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS,
                                   bytes.data(), (int)bytes.size(), nullptr, 0);
    std::wstring src;
    if (need > 0)
    {
        src.resize(need);
        MultiByteToWideChar(CP_UTF8, 0, bytes.data(), (int)bytes.size(),
                            &src[0], need);
    }
    else
    {
        need = MultiByteToWideChar(CP_ACP, 0, bytes.data(), (int)bytes.size(),
                                   nullptr, 0);
        if (need <= 0) return found;
        src.resize(need);
        MultiByteToWideChar(CP_ACP, 0, bytes.data(), (int)bytes.size(),
                            &src[0], need);
    }
    if (!src.empty() && src[0] == 0xFEFF) src.erase(0, 1);

    auto take = [&](std::wstring name) {
        // 去掉 as 别名
        size_t sp = name.find(L' ');
        if (sp != std::wstring::npos) name = name.substr(0, sp);
        size_t dot = name.find(L'.');
        std::wstring top = (dot == std::wstring::npos)
                         ? name : name.substr(0, dot);
        size_t a = top.find_first_not_of(L" \t\r\n");
        size_t b = top.find_last_not_of(L" \t\r\n");
        if (a == std::wstring::npos) return;
        top = top.substr(a, b - a + 1);
        if (top == L"__future__") return;
        if (!std::regex_match(top, std::wregex(L"^[A-Za-z_][A-Za-z0-9_]*$")))
            return;
        if (std::find(found.begin(), found.end(), top) == found.end())
            found.push_back(top);
    };

    try
    {
        // std::regex 的 multiline 用 regex_constants 命名空间；
        // 这里其实靠 ^ 锚定行首，配上逐行扫描即可，不依赖 multiline
        std::wregex reImport(L"^[ \\t]*import[ \\t]+([^\\r\\n#]+)");
        std::wregex reFrom(L"^[ \\t]*from[ \\t]+([A-Za-z_][\\w\\.]*)[ \\t]+import\\b");

        // 逐行处理，天然按行首匹配
        std::wstring line;
        std::wstring all = src;
        size_t pos = 0;
        while (pos <= all.size())
        {
            size_t nl = all.find(L'\n', pos);
            line = (nl == std::wstring::npos) ? all.substr(pos)
                                              : all.substr(pos, nl - pos);
            pos = (nl == std::wstring::npos) ? all.size() + 1 : nl + 1;

            std::wsmatch m;
            if (std::regex_search(line, m, reImport))
            {
                std::wstring list = m[1].str();
                size_t start = 0;
                while (start <= list.size())
                {
                    size_t comma = list.find(L',', start);
                    std::wstring part = (comma == std::wstring::npos)
                        ? list.substr(start)
                        : list.substr(start, comma - start);
                    take(part);
                    if (comma == std::wstring::npos) break;
                    start = comma + 1;
                }
            }
            else if (std::regex_search(line, m, reFrom))
            {
                take(m[1].str());
            }
        }
    }
    catch (...) { }

    return found;
}

} // namespace dainric
