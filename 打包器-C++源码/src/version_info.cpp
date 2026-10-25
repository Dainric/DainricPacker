// Dainric 打包器 · 版本信息文件与打包历史
// 作者: Dainric
#include "core.h"
#include <regex>
#include <sstream>
#include <shlobj.h>

namespace dainric
{

namespace
{

std::wstring HomeDir()
{
    wchar_t buf[MAX_PATH * 2] = { 0 };
    if (FAILED(SHGetFolderPathW(nullptr, CSIDL_LOCAL_APPDATA, nullptr, 0, buf)))
        return L"";
    return std::wstring(buf) + L"\\Dainric";
}

bool WriteUtf8(const std::wstring& path, const std::wstring& text)
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

std::wstring ReadUtf8(const std::wstring& path)
{
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ,
                           nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL,
                           nullptr);
    if (h == INVALID_HANDLE_VALUE) return L"";
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
        if (need <= 0) return L"";
    }
    std::wstring w(need, L'\0');
    MultiByteToWideChar(cp, 0, bytes.data(), (int)bytes.size(), &w[0], need);
    return w;
}

/// Python 字符串字面量转义：单引号与反斜杠
std::wstring PyStr(const std::wstring& s)
{
    std::wstring r;
    for (wchar_t c : s)
    {
        if (c == L'\'') r += L"\\'";
        else if (c == L'\\') r += L"\\\\";
        else if (c == L'\n' || c == L'\r') r += L' ';
        else r += c;
    }
    return r;
}

/// 从 "1.2.3.4" 取出 4 个整数（不足补 0，超出截断）
void VersionTuple(const std::wstring& v, int out[4])
{
    for (int i = 0; i < 4; ++i) out[i] = 0;
    int idx = 0;
    size_t start = 0;
    while (start <= v.size() && idx < 4)
    {
        size_t dot = v.find(L'.', start);
        std::wstring part = (dot == std::wstring::npos)
            ? v.substr(start) : v.substr(start, dot - start);
        try { out[idx] = std::stoi(part); } catch (...) { out[idx] = 0; }
        if (out[idx] < 0) out[idx] = 0;
        if (out[idx] > 65535) out[idx] = 65535;
        ++idx;
        if (dot == std::wstring::npos) break;
        start = dot + 1;
    }
}

} // namespace

bool IsValidVersion(const std::wstring& v)
{
    if (v.empty()) return false;
    if (v.size() > 32) return false;
    if (!std::regex_match(v, std::wregex(L"^\\d+(\\.\\d+){0,3}$"))) return false;
    // 每段不能超过 65535（Windows 版本资源是 16 位字段）
    size_t start = 0;
    while (start <= v.size())
    {
        size_t dot = v.find(L'.', start);
        std::wstring part = (dot == std::wstring::npos)
            ? v.substr(start) : v.substr(start, dot - start);
        if (part.empty() || part.size() > 5) return false;
        int n = 0;
        try { n = std::stoi(part); } catch (...) { return false; }
        if (n < 0 || n > 65535) return false;
        if (dot == std::wstring::npos) break;
        start = dot + 1;
    }
    return true;
}

std::wstring WriteVersionFile(const VersionInfo& vi,
                              const std::wstring& outName,
                              const std::wstring& tempDir)
{
    if (!IsValidVersion(vi.version)) return L"";

    int t[4];
    VersionTuple(vi.version, t);
    std::wstring tuple = std::to_wstring(t[0]) + L", " + std::to_wstring(t[1])
                       + L", " + std::to_wstring(t[2]) + L", "
                       + std::to_wstring(t[3]);

    std::wstring product = vi.product.empty() ? outName : vi.product;
    std::wstring desc = vi.description.empty() ? product : vi.description;
    std::wstring copyright = vi.copyright.empty() ? vi.company : vi.copyright;

    // 结构照抄 PyInstaller 的 VSVersionInfo.__str__ 输出。
    // 首行 # UTF-8 不能少：文件里有中文时，PyInstaller 靠它判定编码。
    std::wstring s;
    s += L"# UTF-8\n";
    s += L"#\n";
    s += L"# 由 Dainric 打包器生成\n";
    s += L"VSVersionInfo(\n";
    s += L"  ffi=FixedFileInfo(\n";
    s += L"    filevers=(" + tuple + L"),\n";
    s += L"    prodvers=(" + tuple + L"),\n";
    s += L"    mask=0x3f,\n";
    s += L"    flags=0x0,\n";
    s += L"    OS=0x40004,\n";
    s += L"    fileType=0x1,\n";
    s += L"    subtype=0x0,\n";
    s += L"    date=(0, 0)\n";
    s += L"    ),\n";
    s += L"  kids=[\n";
    s += L"    StringFileInfo(\n";
    s += L"      [\n";
    s += L"      StringTable(\n";
    s += L"        u'040904B0',\n";
    s += L"        [StringStruct(u'CompanyName', u'" + PyStr(vi.company) + L"'),\n";
    s += L"        StringStruct(u'FileDescription', u'" + PyStr(desc) + L"'),\n";
    s += L"        StringStruct(u'FileVersion', u'" + PyStr(vi.version) + L"'),\n";
    s += L"        StringStruct(u'InternalName', u'" + PyStr(outName) + L"'),\n";
    s += L"        StringStruct(u'LegalCopyright', u'" + PyStr(copyright) + L"'),\n";
    s += L"        StringStruct(u'OriginalFilename', u'" + PyStr(outName)
       + L".exe'),\n";
    s += L"        StringStruct(u'ProductName', u'" + PyStr(product) + L"'),\n";
    s += L"        StringStruct(u'ProductVersion', u'" + PyStr(vi.version) + L"')])\n";
    s += L"      ]),\n";
    s += L"    VarFileInfo([VarStruct(u'Translation', [1033, 1200])])\n";
    s += L"  ]\n";
    s += L")\n";

    std::wstring dir = tempDir.empty() ? HomeDir() : tempDir;
    if (dir.empty()) return L"";
    CreateDirectoryW(dir.c_str(), nullptr);

    // 文件名带随机后缀，避免并发打包时互相覆盖
    wchar_t name[128];
    swprintf_s(name, L"\\dainric_ver_%08X.txt", GetTickCount());
    std::wstring path = dir + name;

    if (!WriteUtf8(path, s)) return L"";
    return path;
}

// ══════════════ 打包历史 ══════════════

std::wstring HistoryPath()
{
    std::wstring h = HomeDir();
    return h.empty() ? L"" : h + L"\\history.txt";
}

std::vector<HistoryItem> LoadHistory()
{
    std::vector<HistoryItem> list;
    std::wstring p = HistoryPath();
    if (p.empty()) return list;

    std::wstring txt = ReadUtf8(p);
    if (txt.empty()) return list;

    // 每行: 时间 \t 名称 \t 结果 \t 耗时秒
    std::wstringstream ss(txt);
    std::wstring line;
    while (std::getline(ss, line))
    {
        while (!line.empty() && (line.back() == L'\r' || line.back() == L'\n'))
            line.pop_back();
        if (line.empty()) continue;

        std::vector<std::wstring> f;
        size_t start = 0;
        while (start <= line.size())
        {
            size_t tab = line.find(L'\t', start);
            f.push_back((tab == std::wstring::npos)
                        ? line.substr(start) : line.substr(start, tab - start));
            if (tab == std::wstring::npos) break;
            start = tab + 1;
        }
        if (f.size() < 4) continue;

        HistoryItem it;
        it.time = f[0];
        it.name = f[1];
        it.result = f[2];
        try { it.seconds = std::stod(f[3]); } catch (...) { it.seconds = 0; }
        list.push_back(it);
    }

    // 最新在前
    std::reverse(list.begin(), list.end());
    if (list.size() > 300) list.resize(300);
    return list;
}

void AppendHistory(const HistoryItem& it)
{
    std::wstring p = HistoryPath();
    if (p.empty()) return;

    std::wstring home = HomeDir();
    if (!home.empty()) CreateDirectoryW(home.c_str(), nullptr);

    std::wstring line = it.time + L"\t" + it.name + L"\t" + it.result + L"\t";
    wchar_t buf[32];
    swprintf_s(buf, L"%.1f", it.seconds);
    line += buf;
    line += L"\r\n";

    // 追加写。UTF-8 无 BOM：ReadUtf8 对两种都能读，但无 BOM 更干净。
    std::string bytes;
    int need = WideCharToMultiByte(CP_UTF8, 0, line.c_str(),
                                   (int)line.size(), nullptr, 0, nullptr,
                                   nullptr);
    if (need > 0)
    {
        bytes.resize(need);
        WideCharToMultiByte(CP_UTF8, 0, line.c_str(), (int)line.size(),
                            &bytes[0], need, nullptr, nullptr);
    }

    HANDLE h = CreateFileW(p.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ,
                           nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL,
                           nullptr);
    if (h == INVALID_HANDLE_VALUE) return;
    DWORD wr = 0;
    WriteFile(h, bytes.data(), (DWORD)bytes.size(), &wr, nullptr);
    CloseHandle(h);
}

} // namespace dainric
