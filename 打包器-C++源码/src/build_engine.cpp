// Dainric 打包器 · 构建引擎
// 作者: Dainric
//
// 两条来自 C# 版实测的结论，别改：
//
//   1) PyInstaller 对「import 一个不存在的模块」是哑的：
//      它照样返回 0、照样产出 exe，坏的只是产物本身。
//      所以「缺模块」永远不会出现在打包输出里，只能靠产物自检抓到。
//      重试判断因此必须同时看「打包输出」和「自检输出」两条路。
//
//   2) 补 hidden-import 救不了「模块根本没装」。
//      那种情况要在打包前用依赖检查拦住，不能指望重试。
#include "core.h"
#include <regex>
#include <algorithm>

namespace dainric
{

namespace
{

bool FileExists(const std::wstring& p)
{
    if (p.empty()) return false;
    DWORD a = GetFileAttributesW(p.c_str());
    return a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY);
}

bool DirExists(const std::wstring& p)
{
    if (p.empty()) return false;
    DWORD a = GetFileAttributesW(p.c_str());
    return a != INVALID_FILE_ATTRIBUTES && (a & FILE_ATTRIBUTE_DIRECTORY);
}

/// 递归删目录。目录非空时 RemoveDirectory 会失败，必须自己走一遍。
void RemoveDir(const std::wstring& dir)
{
    if (!DirExists(dir)) return;
    WIN32_FIND_DATAW fd{};
    std::wstring pat = dir + L"\\*";
    HANDLE h = FindFirstFileW(pat.c_str(), &fd);
    if (h != INVALID_HANDLE_VALUE)
    {
        do
        {
            if (wcscmp(fd.cFileName, L".") == 0
                || wcscmp(fd.cFileName, L"..") == 0)
                continue;
            std::wstring child = dir + L"\\" + fd.cFileName;
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)
                RemoveDir(child);
            else
            {
                SetFileAttributesW(child.c_str(), FILE_ATTRIBUTE_NORMAL);
                DeleteFileW(child.c_str());
            }
        } while (FindNextFileW(h, &fd));
        FindClose(h);
    }
    RemoveDirectoryW(dir.c_str());
}

std::wstring Join(const std::wstring& a, const std::wstring& b)
{
    if (a.empty()) return b;
    std::wstring r = a;
    if (r.back() != L'\\' && r.back() != L'/') r += L'\\';
    return r + b;
}

std::wstring FileNameOf(const std::wstring& p)
{
    size_t s = p.find_last_of(L"\\/");
    return (s == std::wstring::npos) ? p : p.substr(s + 1);
}

std::vector<std::wstring> SplitLines(const std::wstring& s)
{
    std::vector<std::wstring> r;
    std::wstring cur;
    for (wchar_t c : s)
    {
        if (c == L'\n') { r.push_back(cur); cur.clear(); }
        else if (c != L'\r') cur += c;
    }
    if (!cur.empty()) r.push_back(cur);
    return r;
}

std::wstring Flatten(const std::wstring& s)
{
    std::wstring r;
    for (wchar_t c : s)
    {
        if (c == L'\n' || c == L'\r')
        {
            if (!r.empty() && r.back() != L' ') r += L' ';
        }
        else r += c;
    }
    return r;
}

/// 读文本文件：先按 UTF-8（忽略 BOM）解，失败再按系统码页
bool ReadText(const std::wstring& path, std::wstring& out)
{
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ,
                           nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL,
                           nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;

    std::string bytes;
    char buf[8192];
    DWORD n = 0;
    while (ReadFile(h, buf, sizeof(buf), &n, nullptr) && n > 0)
        bytes.append(buf, n);
    CloseHandle(h);

    if (bytes.size() >= 3 && (unsigned char)bytes[0] == 0xEF
        && (unsigned char)bytes[1] == 0xBB && (unsigned char)bytes[2] == 0xBF)
        bytes.erase(0, 3);

    int need = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS,
                                   bytes.data(), (int)bytes.size(), nullptr, 0);
    UINT cp = CP_UTF8;
    if (need <= 0)
    {
        need = MultiByteToWideChar(CP_ACP, 0, bytes.data(), (int)bytes.size(),
                                   nullptr, 0);
        cp = CP_ACP;
        if (need <= 0) return false;
    }
    out.assign(need, L'\0');
    MultiByteToWideChar(cp, 0, bytes.data(), (int)bytes.size(), &out[0], need);
    return true;
}

} // namespace

std::wstring ProductPathOf(const BuildOptions& o)
{
    if (o.oneDir) return Join(Join(o.outDir, o.outName), o.outName + L".exe");
    return Join(o.outDir, o.outName + L".exe");
}

bool ScriptHasGui(const std::wstring& scriptPath)
{
    std::wstring src;
    if (!ReadText(scriptPath, src)) return false;
    try
    {
        return std::regex_search(src, std::wregex(
            L"\\b(tkinter|PyQt5|PyQt6|PySide2|PySide6|wx|pygame)\\b",
            std::regex::icase));
    }
    catch (...) { return false; }
}

Lv ClassifyPyInstallerLine(const std::wstring& line)
{
    if (line.find(L"ERROR") != std::wstring::npos
        || line.find(L"Traceback") != std::wstring::npos
        || line.find(L"error:") != std::wstring::npos)
        return Lv::Err;
    if (line.find(L"WARNING") != std::wstring::npos) return Lv::Warn;
    if (line.find(L"completed successfully") != std::wstring::npos)
        return Lv::Ok;
    return Lv::Dim;
}

// ══════════════ Packer ══════════════

/// 克隆并把自动修复用的额外参数接到引擎前缀后面。
/// 刻意不污染原对象：重试要能回到初始状态。
BuildOptions BuildOptions::WithExtraArgs(
    const std::vector<std::wstring>& extra) const
{
    BuildOptions o = *this;
    if (!extra.empty())
        o.enginePrefix.insert(o.enginePrefix.end(), extra.begin(), extra.end());
    return o;
}

Packer::Packer(Runner& runner, LogFn log, RunFn setRun)
    : runner_(runner), log_(std::move(log)), setRun_(std::move(setRun))
{
}

void Packer::Abort()
{
    aborted_ = true;
    runner_.Abort();
}

bool Packer::Build(const BuildOptions& o)
{
    std::vector<std::wstring> applied;   // 已用过的修复动作，防死循环
    std::vector<std::wstring> addArgs;   // 累积追加的 PyInstaller 参数
    const int kMaxTry = 4;

    for (int attempt = 1; attempt <= kMaxTry; ++attempt)
    {
        std::wstring packText, product;
        bool ok = BuildOnce(o.WithExtraArgs(addArgs), packText, product);

        // 打包成功不代表产物能用：自检才是最终判据
        if (ok && !selftestFailed_)
        {
            if (attempt > 1)
                log_(L"[+] 自动修复后打包并自检通过（第 "
                     + std::to_wstring(attempt) + L" 次尝试）", Lv::Ok);
            return true;
        }
        if (aborted_) return false;

        // 打包失败看打包输出；打包成功但产物崩，看自检输出
        Diagnosis d;
        if (!ok && !packText.empty())
            d = Diagnose(1, packText);
        else if (ok && !selftestText_.empty())
            d = Diagnose(selftestCode_, selftestText_);

        std::vector<std::wstring> todo;
        auto push = [&](const wchar_t* tag, const std::wstring& mod,
                        const wchar_t* flag) {
            std::wstring key = std::wstring(tag) + L":" + mod;
            if (std::find(applied.begin(), applied.end(), key) != applied.end())
                return;
            applied.push_back(key);
            addArgs.push_back(flag);
            addArgs.push_back(mod);
            todo.push_back(mod);
        };

        for (const auto& m : d.missingModules) push(L"hidden", m, L"--hidden-import");
        for (const auto& m : d.collectData) push(L"data", m, L"--collect-data");
        for (const auto& m : d.collectBinaries) push(L"bin", m, L"--collect-binaries");
        for (const auto& m : d.collectSubmodules) push(L"sub", m, L"--collect-submodules");

        if (todo.empty() || attempt == kMaxTry)
        {
            if (ok)
                log_(L"[-] 产物自检未通过，且没有可自动执行的修复动作", Lv::Err);
            log_(L"", Lv::Dim);
            for (const auto& line : SplitLines(d.Render()))
                log_(line, Lv::Warn);
            return false;
        }

        std::wstring what;
        for (size_t i = 0; i < todo.size(); ++i)
            what += (i ? L", " : L"") + todo[i];
        log_(L"[!] 检测到可自动修复的问题: " + what, Lv::Warn);
        log_(L"[*] 已追加修复参数，重试中（第 " + std::to_wstring(attempt + 1)
             + L" 次 / 共 " + std::to_wstring(kMaxTry) + L" 次）…", Lv::Acc);
    }
    return false;
}

std::wstring Packer::FindPython()
{
    if (!cachedPython_.empty()) return cachedPython_;

    auto slots = RuntimeManager::Discover();
    RuntimeSlot* pick = RuntimeManager::PickUsable(slots);
    if (pick && FileExists(pick->pythonExe))
        return cachedPython_ = pick->pythonExe;

    // 有 python.exe 但没装 PyInstaller 的，也够做依赖检查
    for (auto& s : slots)
        if (s.present && FileExists(s.pythonExe))
            return cachedPython_ = s.pythonExe;

    return L"";
}

bool Packer::BuildOnce(const BuildOptions& o, std::wstring& packText,
                       std::wstring& productPath)
{
    packText.clear();
    productPath.clear();
    selftestText_.clear();
    selftestCode_ = 0;
    selftestFailed_ = false;

    if (o.script.empty() || !FileExists(o.script))
    {
        log_(L"[-] 脚本不存在: " + o.script, Lv::Err);
        return false;
    }
    if (o.outName.empty())
    {
        log_(L"[-] 产物名称为空", Lv::Err);
        return false;
    }

    // ── 1) 依赖前置检查 ──
    // 放在打包前：模块没装时后面整条重试链都是白跑
    if (o.checkDeps)
    {
        std::wstring py = FindPython();
        if (!py.empty())
        {
            log_(L"[*] 依赖检查…", Lv::Dim);
            DepReport rep = CheckDependencies(o.script, py, runner_);
            for (const auto& line : SplitLines(rep.RenderBrief()))
                if (!line.empty())
                    log_(line, rep.Clean() ? Lv::Dim : Lv::Warn);

            if (rep.ran && !rep.Missing().empty())
            {
                log_(L"[-] 有 " + std::to_wstring(rep.Missing().size())
                     + L" 个模块在本机运行时里没装，打包后产物会崩", Lv::Err);
                log_(L"[!] 已跳过打包。先装依赖，或关闭「打包前检查依赖」",
                     Lv::Warn);
                return false;
            }
        }
    }

    // ── 2) 组装命令 ──
    // prefix 的头一项是 exe，其余是它自己的参数（如 -m PyInstaller）
    std::vector<std::wstring> prefix = o.enginePrefix;
    if (prefix.empty())
    {
        log_(L"[-] 打包引擎未就绪", Lv::Err);
        return false;
    }

    std::vector<std::wstring> args(prefix.begin() + 1, prefix.end());
    args.push_back(o.oneDir ? L"--onedir" : L"--onefile");
    if (o.windowMode == L"console") args.push_back(L"--console");
    else if (o.windowMode == L"noconsole") args.push_back(L"--noconsole");
    else args.push_back(ScriptHasGui(o.script) ? L"--noconsole" : L"--console");

    args.push_back(L"--name");  args.push_back(o.outName);
    args.push_back(L"--noconfirm");
    // PyInstaller 自身行为需要的两个，缺了在某些环境下分析不全
    args.push_back(L"--hidden-import"); args.push_back(L"modulefinder");
    args.push_back(L"--hidden-import"); args.push_back(L"importlib.util");

    if (!o.iconPath.empty() && FileExists(o.iconPath))
    {
        args.push_back(L"--icon"); args.push_back(o.iconPath);
    }
    if (!o.versionFile.empty() && FileExists(o.versionFile))
    {
        args.push_back(L"--version-file"); args.push_back(o.versionFile);
    }
    if (o.uac)
    {
        args.push_back(L"--uac-admin");
        log_(L"[*] 已启用管理员提权 (UAC)", Lv::Dim);
    }

    // 构建产物目录显式指定，默认的 dist / build 会被塞进工作目录，
    // 用户看到的就不是「输出目录里一个 exe」而是一堆中间目录
    std::wstring distPath = Join(o.outDir, L"dist");
    std::wstring buildPath = Join(o.outDir, L"build");
    bool distExisted = DirExists(distPath);
    bool buildExisted = DirExists(buildPath);

    args.push_back(L"--distpath"); args.push_back(distPath);
    args.push_back(L"--workpath"); args.push_back(buildPath);
    args.push_back(L"--specpath"); args.push_back(o.outDir);

    // 脚本路径必须放在最后，且只能出现一次。
    // C# 版踩过「exe / 脚本同时出现在两个位置」的坑，
    // 这里结构上就只有这一处添加点。
    args.push_back(o.script);

    productPath = ProductPathOf(o);

    // ── 3) 执行 ──
    Cmd c;
    c.exe = prefix.front();
    c.args = args;
    c.workDir = o.outDir;
    c.timeoutMs = std::max(1, o.timeoutSec) * 1000;

    if (setRun_) setRun_(o.oneDir ? L"构建目录" : L"构建 EXE",
                         L"PyInstaller 运行中");
    log_(L"[*] 引擎: " + c.exe, Lv::Dim);
    log_(L"[*] 命令: " + c.Display(), Lv::Dim);
    log_(L"[*] 超时上限 " + std::to_wstring(o.timeoutSec) + L"s", Lv::Dim);

    RunResult r = runner_.Run(c, [&](const std::wstring& line) {
        log_(line, ClassifyPyInstallerLine(line));
    });
    packText = r.out + L"\n" + r.err;

    if (aborted_)
    {
        log_(L"[*] 已终止", Lv::Warn);
        return false;
    }
    if (r.timedOut)
    {
        log_(L"[-] 已超时，进程已终止", Lv::Err);
        return false;
    }
    if (r.exitCode != 0)
    {
        log_(L"[-] PyInstaller 返回码 " + std::to_wstring(r.exitCode)
             + L"，打包失败", Lv::Err);
        return false;
    }

    // ── 4) 产物校验与搬运 ──
    // PyInstaller 把结果放在 dist 下：单文件是 dist\name.exe，
    // 目录形态是 dist\name\name.exe。用户要的是「输出目录里有一个 exe」，
    // 所以这里先确认原始产物存在，再搬到输出目录根。
    std::wstring produced = o.oneDir
        ? Join(Join(distPath, o.outName), o.outName + L".exe")
        : Join(distPath, o.outName + L".exe");

    if (!FileExists(produced))
    {
        log_(L"[-] 未找到构建产物: " + produced, Lv::Err);
        return false;
    }

    {
        LARGE_INTEGER sz{};
        HANDLE h = CreateFileW(produced.c_str(), GENERIC_READ,
                               FILE_SHARE_READ, nullptr, OPEN_EXISTING,
                               FILE_ATTRIBUTE_NORMAL, nullptr);
        if (h != INVALID_HANDLE_VALUE)
        {
            GetFileSizeEx(h, &sz);
            CloseHandle(h);
        }
        log_(L"[+] 输出: " + productPath + L"   "
             + std::to_wstring(sz.QuadPart / 1024) + L" KB", Lv::Ok);
    }

    // 目录形态直接搬整个目录；单文件搬 exe
    bool moved = false;
    if (o.oneDir)
    {
        std::wstring srcDir = Join(distPath, o.outName);
        if (DirExists(srcDir))
        {
            RemoveDir(productPath);
            moved = MoveFileW(srcDir.c_str(), productPath.c_str()) != FALSE;
        }
    }
    else
    {
        if (_wcsicmp(produced.c_str(), productPath.c_str()) != 0)
        {
            DeleteFileW(productPath.c_str());
            moved = MoveFileW(produced.c_str(), productPath.c_str()) != FALSE;
            if (!moved)
                moved = CopyFileW(produced.c_str(), productPath.c_str(),
                                  FALSE) != FALSE;
        }
        else moved = true;
    }
    if (!moved)
        log_(L"[!] 搬运产物失败，构建结果仍在: " + produced, Lv::Warn);

    if (o.clean)
    {
        if (!buildExisted) RemoveDir(buildPath);
        if (!distExisted) RemoveDir(distPath);
    }

    // 清掉 PyInstaller 生成的 .spec（输出名确定了就没用了）
    DeleteFileW(Join(o.outDir, o.outName + L".spec").c_str());

    // ── 5) 产物自检 ──
    // PyInstaller 返回 0 不代表产物能跑，这一步才是真正的验收
    if (o.testRun) SelfTest(o, productPath);

    return true;
}

void Packer::SelfTest(const BuildOptions& o, const std::wstring& exe)
{
    std::wstring note = o.testArgs.empty()
        ? L"" : L"（带 " + std::to_wstring(o.testArgs.size()) + L" 个自检参数）";
    if (setRun_) setRun_(L"产物自检", FileNameOf(exe));
    log_(L"[*] 产物自检：启动 " + FileNameOf(exe) + note
         + L"（最多等 6 秒）…", Lv::Dim);

    Cmd c;
    c.exe = exe;
    c.rest = o.testArgs;
    c.workDir = o.outDir;
    c.timeoutMs = 6000;

    RunResult r = runner_.Run(c);
    selftestText_ = r.out.empty() ? r.Combined() : r.out;
    selftestCode_ = r.exitCode;

    // 6 秒不退出：常驻 GUI 程序的正常表现
    if (r.timedOut || r.exitCode == 0)
    {
        log_(std::wstring(L"[+] 产物自检通过：")
             + (r.timedOut ? L"运行 6 秒未退出" : L"正常退出"), Lv::Ok);
        if (!r.out.empty())
            log_(L"[*] 产物输出: " + Flatten(r.out), Lv::Dim);
        return;
    }

    // 用法错误不算产物有问题：脚本本来就要收参数。
    // 打的是需要命令行参数的脚本时，裸启动必然走到这里，
    // 把它当失败报出去就是误报 —— 产物本身是好的。
    if (LooksLikeUsageError(selftestText_))
    {
        selftestFailed_ = false;
        log_(L"[+] 产物能启动，但它要求命令行参数才继续运行", Lv::Ok);
        log_(L"[!] 自检按「通过」处理。若要真正跑完整流程，"
             L"在「自检参数」里填上参数后重打。", Lv::Warn);
        if (!r.out.empty())
            log_(L"[*] 产物输出: " + Flatten(r.out), Lv::Dim);
        return;
    }

    selftestFailed_ = true;
    log_(L"[-] 产物自检失败：以退出码 " + std::to_wstring(r.exitCode)
         + L" 退出", Lv::Err);
    if (!selftestText_.empty())
        log_(L"[*] 产物输出: " + Flatten(selftestText_), Lv::Err);
}

} // namespace dainric
