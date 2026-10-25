// Dainric 打包器 · 运行时定位与槽位管理
// 作者: Dainric
//
// 目标：运行时目录不必和 exe 同层，放在任意位置都能被找到。
//
// 为什么不内置进 exe：
//   两个运行时合计 187 MB，内置后 exe 会涨到 250 MB，
//   且每次启动都要解压到 %TEMP%（慢，受限环境还可能没写权限）。
//   改成「多位置搜索 + 记住选择」，效果更好也更省事。
#include "core.h"
#include <regex>
#include <sstream>
#include <algorithm>
#include <shlobj.h>

namespace dainric
{

const wchar_t* RuntimeLocator::Dir312 = L"runtime-py312";
const wchar_t* RuntimeLocator::Dir314 = L"runtime-py314";
const wchar_t* RuntimeLocator::DirGeneric = L"runtime";

const wchar_t* RuntimeManager::Key312 = L"py312";
const wchar_t* RuntimeManager::KeyDefault = L"default";

namespace
{

bool DirExists(const std::wstring& p)
{
    if (p.empty()) return false;
    DWORD a = GetFileAttributesW(p.c_str());
    return a != INVALID_FILE_ATTRIBUTES && (a & FILE_ATTRIBUTE_DIRECTORY);
}

bool FileExists(const std::wstring& p)
{
    if (p.empty()) return false;
    DWORD a = GetFileAttributesW(p.c_str());
    return a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY);
}

/// 本进程 exe 所在目录
std::wstring AppDir()
{
    wchar_t buf[MAX_PATH * 2] = { 0 };
    DWORD n = GetModuleFileNameW(nullptr, buf, MAX_PATH * 2);
    if (n == 0) return L"";
    std::wstring p(buf, n);
    size_t s = p.find_last_of(L"\\/");
    return (s == std::wstring::npos) ? L"" : p.substr(0, s);
}

std::wstring ParentOf(const std::wstring& dir)
{
    if (dir.empty()) return L"";
    std::wstring d = dir;
    while (!d.empty() && (d.back() == L'\\' || d.back() == L'/')) d.pop_back();
    size_t s = d.find_last_of(L"\\/");
    if (s == std::wstring::npos) return L"";
    if (s == 2 && d[1] == L':') return d.substr(0, 3);   // 盘根
    return d.substr(0, s);
}

std::wstring Join(const std::wstring& a, const std::wstring& b)
{
    if (a.empty()) return b;
    std::wstring r = a;
    if (r.back() != L'\\' && r.back() != L'/') r += L'\\';
    return r + b;
}

std::wstring SpecialFolder(int csidl)
{
    wchar_t buf[MAX_PATH * 2] = { 0 };
    if (SUCCEEDED(SHGetFolderPathW(nullptr, csidl, nullptr, 0, buf)))
        return buf;
    return L"";
}

std::wstring ReadAllText(const std::wstring& path)
{
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ,
                           nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL,
                           nullptr);
    if (h == INVALID_HANDLE_VALUE) return L"";
    std::string bytes;
    char buf[4096];
    DWORD n = 0;
    while (ReadFile(h, buf, sizeof(buf), &n, nullptr) && n > 0)
        bytes.append(buf, n);
    CloseHandle(h);

    if (bytes.size() >= 3 && (unsigned char)bytes[0] == 0xEF
        && (unsigned char)bytes[1] == 0xBB && (unsigned char)bytes[2] == 0xBF)
        bytes.erase(0, 3);

    int need = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS,
                                   bytes.data(), (int)bytes.size(), nullptr, 0);
    if (need <= 0)
    {
        need = MultiByteToWideChar(CP_ACP, 0, bytes.data(), (int)bytes.size(),
                                   nullptr, 0);
        if (need <= 0) return L"";
    }
    std::wstring w(need, L'\0');
    MultiByteToWideChar(need > 0 ? CP_UTF8 : CP_ACP, 0, bytes.data(),
                        (int)bytes.size(), &w[0], need);
    return w;
}

bool WriteAllTextUtf8(const std::wstring& path, const std::wstring& text)
{
    std::string bytes;
    int need = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), (int)text.size(),
                                   nullptr, 0, nullptr, nullptr);
    if (need > 0)
    {
        bytes.resize(need);
        WideCharToMultiByte(CP_UTF8, 0, text.c_str(), (int)text.size(),
                            &bytes[0], need, nullptr, nullptr);
    }
    HANDLE h = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr,
                           CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    DWORD wr = 0;
    BOOL ok = WriteFile(h, bytes.data(), (DWORD)bytes.size(), &wr, nullptr);
    CloseHandle(h);
    return ok != FALSE;
}

/// 极简 JSON 取值：避免为一个字段引入依赖，和 C# 版做法一致
std::wstring Extract(const std::wstring& json, const std::wstring& key)
{
    try
    {
        std::wregex re(L"\"" + key + L"\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        std::wsmatch m;
        if (!std::regex_search(json, m, re)) return L"";
        std::wstring v = m[1].str();
        // 反转义：把成对的反斜杠还原成单个（注释别以反斜杠结尾，会吃下一行）
        std::wstring out;
        for (size_t i = 0; i < v.size(); ++i)
        {
            if (v[i] == L'\\' && i + 1 < v.size()) { out += v[++i]; }
            else out += v[i];
        }
        return out;
    }
    catch (...) { return L""; }
}

} // namespace

bool RtLocation::Exists() const { return DirExists(dir); }

std::wstring RuntimeLocator::HomeDir()
{
    std::wstring la = SpecialFolder(CSIDL_LOCAL_APPDATA);
    if (la.empty()) return L"";
    return Join(la, L"Dainric");
}

std::wstring RuntimeLocator::ConfigPath()
{
    std::wstring h = HomeDir();
    return h.empty() ? L"" : Join(h, L"config.json");
}

std::wstring RuntimeLocator::LoadConfigured(const std::wstring& key)
{
    std::wstring p = ConfigPath();
    if (p.empty() || !FileExists(p)) return L"";
    return Extract(ReadAllText(p), key);
}

void RuntimeLocator::SaveConfigured(const std::wstring& key,
                                    const std::wstring& value)
{
    std::wstring home = HomeDir();
    if (home.empty()) return;
    CreateDirectoryW(home.c_str(), nullptr);

    std::wstring p = ConfigPath();
    // 保留已有的其它键
    std::vector<std::pair<std::wstring, std::wstring>> kv;
    if (FileExists(p))
    {
        std::wstring txt = ReadAllText(p);
        for (const wchar_t* k : { Dir312, Dir314, L"lastUsed" })
        {
            std::wstring v = Extract(txt, k);
            if (!v.empty()) kv.push_back({ k, v });
        }
    }
    bool replaced = false;
    for (auto& e : kv)
        if (e.first == key) { e.second = value; replaced = true; }
    if (!replaced) kv.push_back({ key, value });

    auto esc = [](const std::wstring& s) {
        std::wstring r;
        for (wchar_t c : s) { if (c == L'\\') r += L"\\\\"; else r += c; }
        return r;
    };

    std::wstring json = L"{\n";
    for (size_t i = 0; i < kv.size(); ++i)
    {
        json += L"  \"" + kv[i].first + L"\": \"" + esc(kv[i].second) + L"\"";
        json += (i + 1 < kv.size()) ? L",\n" : L"\n";
    }
    json += L"}\n";
    WriteAllTextUtf8(p, json);
}

std::vector<std::pair<std::wstring, std::wstring>>
RuntimeLocator::SearchRoots()
{
    std::vector<std::pair<std::wstring, std::wstring>> roots;
    auto add = [&](const std::wstring& d, const wchar_t* why) {
        if (!d.empty()) roots.push_back({ d, why });
    };

    std::wstring app = AppDir();
    add(app, L"程序所在目录");
    add(Join(app, L"runtime"), L"程序目录下的 runtime");
    std::wstring up = ParentOf(app);
    add(up, L"上一级目录");
    add(Join(up, L"runtime"), L"上一级目录下的 runtime");
    add(ParentOf(up), L"上两级目录");

    std::wstring home = HomeDir();
    add(home, L"集中目录 %LOCALAPPDATA%\\Dainric");
    add(Join(home, L"runtime"), L"集中目录下的 runtime");

    struct { int id; const wchar_t* why; } specials[] = {
        { CSIDL_DESKTOPDIRECTORY, L"系统目录 桌面" },
        { CSIDL_PERSONAL,         L"系统目录 文档" },
        { CSIDL_PROFILE,          L"系统目录 用户目录" },
        { CSIDL_PROGRAM_FILES,    L"系统目录 Program Files" },
        { CSIDL_LOCAL_APPDATA,    L"系统目录 LocalAppData" },
    };
    for (auto& s : specials)
    {
        std::wstring p = SpecialFolder(s.id);
        add(p, s.why);
        add(Join(p, L"Dainric"), L"系统目录下的 Dainric");
    }

    wchar_t envBuf[MAX_PATH * 2] = { 0 };
    DWORD n = GetEnvironmentVariableW(L"DAINRIC_RUNTIME_HOME", envBuf,
                                      MAX_PATH * 2);
    if (n > 0 && n < MAX_PATH * 2)
        add(std::wstring(envBuf, n), L"环境变量 DAINRIC_RUNTIME_HOME");

    return roots;
}

namespace
{
std::wstring Probe(const std::wstring& root,
                   const std::vector<std::wstring>& names)
{
    for (const auto& n : names)
    {
        std::wstring d = Join(root, n);
        if (FileExists(Join(d, L"python.exe"))) return d;
    }
    return L"";
}
} // namespace

RtLocation RuntimeLocator::Locate(const std::wstring& key)
{
    std::vector<std::wstring> names;
    if (key == RuntimeManager::Key312)
        names = { Dir312, L"python312", L"py312" };
    else
        names = { Dir314, L"python314", L"py314", DirGeneric };

    // 1) 用户手动指定过的位置最优先
    std::wstring saved = LoadConfigured(key);
    if (!saved.empty() && FileExists(Join(saved, L"python.exe")))
    {
        RtLocation r;
        r.dir = saved;
        r.origin = L"已记住的位置";
        return r;
    }

    // 2) 逐个候选根目录探测
    for (const auto& root : SearchRoots())
    {
        if (!DirExists(root.first)) continue;
        std::wstring d = Probe(root.first, names);
        if (!d.empty())
        {
            RtLocation r;
            r.dir = d;
            r.origin = root.second;
            return r;
        }
    }
    RtLocation none;
    none.origin = L"未找到";
    return none;
}

void RuntimeLocator::Remember(const std::wstring& key, const std::wstring& dir)
{
    if (!dir.empty()) SaveConfigured(key, dir);
}

// ══════════════ 槽位管理 ══════════════

bool RuntimeManager::LooksLikeRuntime(const std::wstring& dir)
{
    if (dir.empty()) return false;
    if (!FileExists(Join(dir, L"python.exe"))) return false;
    return DirExists(Join(dir, L"Lib"));
}

std::vector<RuntimeSlot> RuntimeManager::Discover()
{
    std::vector<RuntimeSlot> slots;

    RuntimeSlot a;
    a.key = Key312;
    a.title = L"Python 3.12 · 兼容模式";
    a.note = L"三方库生态覆盖最广，老库与科学计算包优先选它";
    slots.push_back(a);

    RuntimeSlot b;
    b.key = KeyDefault;
    b.title = L"Python 3.14 · 默认";
    b.note = L"版本较新，部分依赖尚未适配的库会装不上";
    slots.push_back(b);

    std::wstring app = AppDir();
    for (auto& s : slots)
    {
        RtLocation loc = RuntimeLocator::Locate(s.key);
        s.dir = loc.Exists() ? loc.dir
                             : Join(app, s.key == Key312 ? RuntimeLocator::Dir312
                                                         : RuntimeLocator::Dir314);
        s.foundBy = loc.origin;
        s.pythonExe = Join(s.dir, L"python.exe");
        s.present = loc.Exists();
        if (loc.Exists()) RuntimeLocator::Remember(s.key, loc.dir);
    }
    return slots;
}

void RuntimeManager::FillDetails(RuntimeSlot& s, Runner& runner)
{
    if (!s.present || !FileExists(s.pythonExe)) return;
    try
    {
        Cmd c1;
        c1.exe = s.pythonExe;
        c1.args = { L"-c", L"import sys;print(sys.version.split()[0])" };
        c1.timeoutMs = 20000;
        RunResult r1 = runner.Run(c1);
        if (r1.exitCode == 0)
        {
            std::wstring v = r1.out;
            size_t a = v.find_first_not_of(L" \t\r\n");
            size_t b = v.find_last_not_of(L" \t\r\n");
            if (a != std::wstring::npos) s.version = v.substr(a, b - a + 1);
        }

        Cmd c2;
        c2.exe = s.pythonExe;
        c2.args = { L"-c",
                    L"import PyInstaller,sys;"
                    L"sys.stdout.write(PyInstaller.__version__)" };
        c2.timeoutMs = 30000;
        RunResult r2 = runner.Run(c2);
        if (r2.exitCode == 0)
        {
            std::wstring v = r2.out;
            size_t a = v.find_first_not_of(L" \t\r\n");
            size_t b = v.find_last_not_of(L" \t\r\n");
            if (a != std::wstring::npos) s.pyiVersion = v.substr(a, b - a + 1);
        }
    }
    catch (...) { }
}

RuntimeSlot* RuntimeManager::PickUsable(std::vector<RuntimeSlot>& slots)
{
    for (auto& s : slots) if (s.Usable()) return &s;
    return nullptr;
}

std::wstring RuntimeManager::DescribeLocations()
{
    std::wstring r;
    for (auto& s : Discover())
    {
        r += s.title + L"\n";
        r += L"  目录: " + (s.dir.empty() ? L"(无)" : s.dir) + L"\n";
        r += L"  来源: " + s.foundBy + L"\n";
        r += L"  存在: " + std::wstring(s.present ? L"是" : L"否") + L"\n";
    }
    r += L"\n搜索过这些位置：\n";
    for (auto& root : RuntimeLocator::SearchRoots())
        r += L"  " + root.first + L"    (" + root.second + L")\n";
    return r;
}

} // namespace dainric
