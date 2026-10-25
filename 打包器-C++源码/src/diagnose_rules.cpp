// Dainric 打包器 · 打包失败诊断
// 作者: Dainric
//
// 打包失败时原始日志动辄几百行，用户看不出真正原因。
// 这里把常见失败模式识别出来，直接给出「缺什么 / 怎么修」。
//
// 规则顺序有意义：Diagnose 是「全部命中」而不是「首个命中」，
// 多条规则会匹配同一行文本，谁在前面谁先抽到库名。
// collect-submodules 必须排在 hidden-import-hint 之前。
#include "core.h"
#include <regex>
#include <sstream>
#include <algorithm>

namespace dainric
{

namespace
{

std::wstring Lower(std::wstring s)
{
    std::transform(s.begin(), s.end(), s.begin(),
                   [](wchar_t c) { return (wchar_t)towlower(c); });
    return s;
}

bool Has(const std::wstring& s, const wchar_t* needle)
{
    return s.find(needle) != std::wstring::npos;
}

/// 大小写不敏感的子串判断
bool HasI(const std::wstring& s, const wchar_t* needle)
{
    return Lower(s).find(Lower(needle)) != std::wstring::npos;
}

/// 是否像一个可以拿去做 --collect-* 的顶层库名。
/// 用子串判断而非等值：正则从 "site-packages" 里只能吃到 "site"，
/// 等值比较会漏掉它。
bool IsTopModule(const std::wstring& s)
{
    if (s.empty()) return false;
    if (!std::regex_match(s, std::wregex(L"^[A-Za-z_][A-Za-z0-9_.\\-]*$")))
        return false;

    std::wstring low = Lower(s);
    auto inhay = [&](const wchar_t* hay) {
        return std::wstring(hay).find(low) != std::wstring::npos;
    };
    if (inhay(L"site-packages")) return false;
    if (inhay(L"python")) return false;
    if (low.size() <= 3 && inhay(L"lib")) return false;

    static const wchar_t* bad[] = { L"scripts", L"import", L"module",
                                    L"error", L"none", L"dist", L"build" };
    for (auto b : bad) if (low == b) return false;
    return true;
}

/// 把正则命中的库名收进目标列表。
/// 正则有多个候选捕获组，取第一个非空的；随后归一化成顶层包
/// —— --collect-* 收的是整个包，给 "PyQt5.sip" 不如给 "PyQt5" 收得全。
void Harvest(const std::wstring& text, const wchar_t* pattern,
             std::vector<std::wstring>& into)
{
    if (!pattern || !*pattern) return;
    try
    {
        std::wregex re(pattern);
        auto b = std::wsregex_iterator(text.begin(), text.end(), re);
        for (auto it = b; it != std::wsregex_iterator(); ++it)
        {
            const std::wsmatch& m = *it;
            std::wstring name;
            for (size_t g = 1; g < m.size(); ++g)
            {
                if (m[g].matched && !m[g].str().empty())
                {
                    name = m[g].str();
                    break;
                }
            }
            if (!IsTopModule(name)) continue;
            size_t dot = name.find(L'.');
            std::wstring top = (dot == std::wstring::npos)
                             ? name : name.substr(0, dot);
            if (!IsTopModule(top)) continue;
            if (std::find(into.begin(), into.end(), top) == into.end())
                into.push_back(top);
        }
    }
    catch (...) { }   // 正则本身出错不能影响诊断
}

struct Rule
{
    const wchar_t* id;
    bool (*match)(const std::wstring&);
    const wchar_t* reason;
    const wchar_t* const* fixes;
    size_t fixCount;
    const wchar_t* modules;
    const wchar_t* dataLibs;
    const wchar_t* binaryLibs;
    const wchar_t* subLibs;
};

// ── 各规则的匹配函数 ──

bool M_MissingModule(const std::wstring& s)
{
    return std::regex_search(s, std::wregex(
               L"ModuleNotFoundError: No module named '([^']+)'"))
        || std::regex_search(s, std::wregex(
               L"ImportError: No module named '?([\\w\\.]+)'?"));
}
bool M_HiddenImport(const std::wstring& s)
{
    return HasI(s, L"idden import") && HasI(s, L"not found");
}
bool M_DllMissing(const std::wstring& s)
{
    return Has(s, L"Failed to load Python DLL") || Has(s, L"api-ms-win-crt")
        || std::regex_search(s, std::wregex(
               L"cannot open shared object|DLL load failed"));
}
bool M_Tkinter(const std::wstring& s)
{
    return Has(s, L"_tkinter") || Has(s, L"Tcl init")
        || Has(s, L"Can't find a usable init.tcl");
}
bool M_Permission(const std::wstring& s)
{
    return Has(s, L"PermissionError") || Has(s, L"Access is denied")
        || Has(s, L"拒绝访问");
}
bool M_LockedOutput(const std::wstring& s)
{
    return Has(s, L"being used by another process")
        || Has(s, L"另一个程序正在使用");
}
bool M_DiskFull(const std::wstring& s)
{
    return Has(s, L"No space left") || Has(s, L"磁盘空间不足");
}
bool M_NonAscii(const std::wstring& s)
{
    return Has(s, L"UnicodeEncodeError") || Has(s, L"UnicodeDecodeError")
        || Has(s, L"codec can't encode");
}
bool M_Recursion(const std::wstring& s)
{
    return Has(s, L"RecursionError") || Has(s, L"maximum recursion depth");
}
bool M_SpecError(const std::wstring& s)
{
    return std::regex_search(s, std::wregex(L"SyntaxError|invalid syntax"))
        && Has(s, L".spec");
}
bool M_Syntax(const std::wstring& s) { return Has(s, L"SyntaxError"); }
bool M_HookError(const std::wstring& s)
{
    return Has(s, L"hook-") && Has(s, L"ERROR");
}
bool M_Bootloader(const std::wstring& s)
{
    return Has(s, L"Failed to execute script")
        || Has(s, L"Failed to start embedded python");
}
bool M_CollectData(const std::wstring& s)
{
    return std::regex_search(s, std::wregex(
               L"FileNotFoundError.*?(\\.json|\\.dat|\\.txt|\\.csv|\\.ttf|\\.qm|\\.mo|\\.ui)\\b"))
        || std::regex_search(s, std::wregex(
               L"Unable to find\\s+\\S+\\.(json|dat|mo|qm)"))
        || Has(s, L"fontlist.json")
        || std::regex_search(s, std::wregex(
               L"No such file or directory.*?site-packages[\\\\/](\\w+)"));
}
bool M_CollectBinaries(const std::wstring& s)
{
    return std::regex_search(s, std::wregex(
               L"DLL load failed|OSError.*?cannot open|ImportError.*?\\.(pyd|dll|so)\\b"))
        || std::regex_search(s, std::wregex(
               L"Failed to (load|find) (library|binary)\\s+(\\S+)"));
}

// 各规则的建议列表（C 数组便于放进 POD 表）
const wchar_t* F_Module[] = {
    L"在脚本所在环境安装它：python -m pip install <模块名>",
    L"若模块是运行时才导入的（importlib / 插件式加载），可手动加 hidden-import" };
const wchar_t* F_Sub[] = {
    L"打包时加上 --collect-submodules <库名> 整包收集",
    L"常见于 PyQt5 / PySide6 / cryptography / 各种 plugin 框架" };
const wchar_t* F_Hint[] = {
    L"确认该模块已安装；拼写是否正确",
    L"不需要的可以忽略，它通常只是警告" };
const wchar_t* F_Dll[] = {
    L"让目标机安装 Microsoft Visual C++ Redistributable (x64)",
    L"或改用「目录」形态打包，依赖更容易排查" };
const wchar_t* F_Tk[] = {
    L"确认打包器自带的运行时完整（tcl 目录不能删）",
    L"若用系统 Python，请确认它是完整安装而非 embeddable 版" };
const wchar_t* F_Perm[] = {
    L"关闭正在运行的旧 EXE（它占用了输出文件）",
    L"换一个输出目录，避免使用系统保护目录",
    L"暂停杀毒软件对该目录的实时扫描" };
const wchar_t* F_Lock[] = { L"关闭正在运行的旧程序后重试" };
const wchar_t* F_Disk[] = { L"清理输出目录与临时目录后重试" };
const wchar_t* F_Data[] = {
    L"打包时加上 --collect-data <库名> 把数据文件一起收进去",
    L"本工具已自动识别并重试；若仍失败，手动指定该库" };
const wchar_t* F_Bin[] = {
    L"打包时加上 --collect-binaries <库名>",
    L"常见于 numpy / scipy / opencv / torch 这类带原生扩展的库" };
const wchar_t* F_NonAscii[] = {
    L"把脚本与输出目录改为纯英文路径",
    L"避免中文用户名下的桌面/文档目录" };
const wchar_t* F_Rec[] = {
    L"在脚本里把递归逻辑改成迭代",
    L"对大型库可先用 --exclude-module 排除不需要的部分" };
const wchar_t* F_Spec[] = {
    L"删除输出目录下的 .spec 后重试",
    L"输出名不要含空格或特殊字符" };
const wchar_t* F_Syn[] = { L"先单独运行 python 脚本名 确认能跑通" };
const wchar_t* F_Hook[] = {
    L"升级 PyInstaller 与出错库到最新版",
    L"在日志中搜索 hook- 找到是哪个库，再排除或单独收集它" };
const wchar_t* F_Boot[] = {
    L"改用「控制台」形态重新打包，就能看到真实报错",
    L"常见原因是缺 hidden-import 或数据文件" };

#define RULE(id, fn, reason, arr, mods, data, bin, sub) \
    { id, fn, reason, arr, sizeof(arr)/sizeof(arr[0]), mods, data, bin, sub }

const Rule kRules[] =
{
    RULE(L"missing-module", M_MissingModule,
         L"脚本导入了未安装（或未被自动收集）的模块", F_Module,
         L"No module named '([^']+)'", nullptr, nullptr, nullptr),

    // 必须排在 hidden-import-hint 之前
    RULE(L"collect-submodules", M_HiddenImport,
         L"某个库的子模块是插件式加载的，静态分析收不全", F_Sub,
         nullptr, nullptr, nullptr,
         L"idden import\\s+['\"]([A-Za-z_][\\w.\\-]*)"),

    RULE(L"hidden-import-hint", M_HiddenImport,
         L"PyInstaller 报告有 hidden import 找不到", F_Hint,
         nullptr, nullptr, nullptr, nullptr),

    RULE(L"dll-missing", M_DllMissing,
         L"目标机缺少 VC++ 运行时或系统 DLL（常见于精简版 Windows）", F_Dll,
         nullptr, nullptr, nullptr, nullptr),

    RULE(L"tkinter-missing", M_Tkinter,
         L"Tcl/Tk 数据文件缺失，tkinter 程序无法启动", F_Tk,
         nullptr, nullptr, nullptr, nullptr),

    RULE(L"permission", M_Permission,
         L"文件被占用或没有写入权限", F_Perm,
         nullptr, nullptr, nullptr, nullptr),

    RULE(L"locked-output", M_LockedOutput,
         L"目标 EXE 正在运行，无法覆盖", F_Lock,
         nullptr, nullptr, nullptr, nullptr),

    RULE(L"disk-full", M_DiskFull,
         L"磁盘空间不足", F_Disk,
         nullptr, nullptr, nullptr, nullptr),

    RULE(L"collect-data", M_CollectData,
         L"运行期缺少库自带的数据文件（字体、图标、模板、翻译等）", F_Data,
         nullptr,
         L"site-packages[\\\\/]+([A-Za-z_]\\w*)|Unable to find[^\\r\\n]*?[\\\\/](\\w+)[\\\\/]",
         nullptr, nullptr),

    RULE(L"collect-binaries", M_CollectBinaries,
         L"运行期缺少库自带的二进制（.pyd / .dll / .so）", F_Bin,
         nullptr, nullptr,
         L"site-packages[\\\\/]+([A-Za-z_]\\w*)|Failed to (?:load|find) (?:library|binary)\\s+([A-Za-z_]\\w*)",
         nullptr),

    RULE(L"nonascii-path", M_NonAscii,
         L"路径或输出含非 ASCII 字符，编码环节出错", F_NonAscii,
         nullptr, nullptr, nullptr, nullptr),

    RULE(L"recursion", M_Recursion,
         L"依赖分析递归过深", F_Rec,
         nullptr, nullptr, nullptr, nullptr),

    RULE(L"spec-error", M_SpecError,
         L"自动生成的 .spec 文件有语法问题", F_Spec,
         nullptr, nullptr, nullptr, nullptr),

    RULE(L"syntax", M_Syntax,
         L"被打包的脚本本身有语法错误", F_Syn,
         nullptr, nullptr, nullptr, nullptr),

    RULE(L"hook-error", M_HookError,
         L"某个第三方库的 PyInstaller 钩子执行失败", F_Hook,
         nullptr, nullptr, nullptr, nullptr),

    RULE(L"bootloader", M_Bootloader,
         L"打包出的 EXE 启动失败（不是打包失败）", F_Boot,
         nullptr, nullptr, nullptr, nullptr),
};

#undef RULE

std::vector<std::wstring> LastMeaningfulLines(const std::wstring& text, int n)
{
    std::vector<std::wstring> lines;
    std::wstringstream ss(text);
    std::wstring line;
    while (std::getline(ss, line))
    {
        while (!line.empty() && (line.back() == L'\r' || line.back() == L' '))
            line.pop_back();
        size_t s = line.find_first_not_of(L" \t");
        if (s == std::wstring::npos) continue;
        line = line.substr(s);
        if (line.rfind(L"INFO:", 0) == 0) continue;
        if (line.rfind(L"WARNING:", 0) == 0) continue;
        lines.push_back(line);
    }
    if ((int)lines.size() <= n) return lines;
    return std::vector<std::wstring>(lines.end() - n, lines.end());
}

} // namespace

bool LooksLikeUsageError(const std::wstring& text)
{
    if (text.empty()) return false;
    static const wchar_t* marks[] =
    {
        L"usage:", L"the following arguments are required",
        L"unrecognized arguments", L"error: argument",
        L"error: the following arguments", L"no such option",
        L"missing required argument", L"expected at least",
        L"请提供", L"缺少参数", L"用法:",
    };
    std::wstring low = Lower(text);
    for (auto m : marks)
        if (low.find(Lower(m)) != std::wstring::npos) return true;
    return false;
}

Diagnosis Diagnose(int exitCode, const std::wstring& output)
{
    Diagnosis d;
    std::wstring text = output;
    d.rawOutput = text;

    if (exitCode == -1)
    {
        d.title = L"打包超时被终止";
        d.reasons.push_back(L"构建时间超过了设定的超时上限");
        d.fixes.push_back(L"在「高级选项」里把打包超时调大（例如 3600 秒）");
        d.fixes.push_back(L"含大型库（torch / PyQt / numpy）的项目通常需要几分钟到几十分钟");
        return d;
    }
    if (exitCode == -2)
    {
        d.title = L"无法启动打包引擎";
        d.fixes.push_back(L"确认运行时目录存在且完整");
        d.fixes.push_back(L"或在目标机安装 PyInstaller: pip install pyinstaller");
        return d;
    }

    std::vector<const Rule*> hits;
    for (const auto& r : kRules)
    {
        bool m = false;
        try { m = r.match(text); } catch (...) { m = false; }
        if (m) hits.push_back(&r);
    }

    if (hits.empty())
    {
        d.title = L"打包失败（未匹配到已知失败模式）";
        d.reasons.push_back(L"退出码 " + std::to_wstring(exitCode));
        for (const auto& l : LastMeaningfulLines(text, 3))
            d.reasons.push_back(L"日志末尾: " + l);
        d.fixes.push_back(L"在「构建选项」里选「控制台」再打一次，能看到更完整的报错");
        d.fixes.push_back(L"把输出日志复制出来搜索 ERROR 关键字");
        return d;
    }

    bool boot = false;
    for (auto h : hits)
        if (std::wstring(h->id) == L"bootloader") boot = true;
    d.title = boot ? L"打包出的 EXE 启动失败" : L"打包失败";

    for (auto h : hits)
    {
        if (std::find(d.reasons.begin(), d.reasons.end(),
                      std::wstring(h->reason)) == d.reasons.end())
            d.reasons.push_back(h->reason);

        for (size_t i = 0; i < h->fixCount; ++i)
            if (std::find(d.fixes.begin(), d.fixes.end(),
                          std::wstring(h->fixes[i])) == d.fixes.end())
                d.fixes.push_back(h->fixes[i]);

        if (h->modules)
        {
            try
            {
                std::wregex re(h->modules);
                auto b = std::wsregex_iterator(text.begin(), text.end(), re);
                for (auto it = b; it != std::wsregex_iterator(); ++it)
                {
                    std::wstring mod = (*it)[1].str();
                    size_t s = mod.find_first_not_of(L" \t");
                    if (s == std::wstring::npos) continue;
                    mod = mod.substr(s);
                    if (IsTopModule(mod)
                        && std::find(d.missingModules.begin(),
                                     d.missingModules.end(), mod)
                           == d.missingModules.end())
                        d.missingModules.push_back(mod);
                }
            }
            catch (...) { }
        }

        Harvest(text, h->dataLibs, d.collectData);
        Harvest(text, h->binaryLibs, d.collectBinaries);
        Harvest(text, h->subLibs, d.collectSubmodules);
    }
    return d;
}

std::wstring Diagnosis::Render() const
{
    std::wstring r;
    r += L"诊断结果\n";
    r += L"----------------------------------------------------\n";
    r += title + L"\n";

    if (!reasons.empty())
    {
        r += L"\n可能原因\n";
        for (const auto& x : reasons) r += L"  · " + x + L"\n";
    }
    if (!missingModules.empty())
    {
        r += L"\n识别到缺失模块\n";
        for (const auto& x : missingModules) r += L"  · " + x + L"\n";
    }
    if (!collectData.empty() || !collectBinaries.empty()
        || !collectSubmodules.empty())
    {
        r += L"\n识别到需要整包收集的库\n";
        for (const auto& x : collectData) r += L"  · " + x + L"  (数据文件)\n";
        for (const auto& x : collectBinaries) r += L"  · " + x + L"  (二进制)\n";
        for (const auto& x : collectSubmodules) r += L"  · " + x + L"  (子模块)\n";
    }
    if (!fixes.empty())
    {
        r += L"\n建议处理\n";
        for (const auto& x : fixes) r += L"  · " + x + L"\n";
    }
    return r;
}

} // namespace dainric
