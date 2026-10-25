// Dainric 打包器 · 平台层实现
// 作者: Dainric
#include "core.h"
#include <chrono>
#include <thread>
#include <algorithm>

namespace dainric
{

// ══════════════ 命令行展示 ══════════════

static std::wstring Quote(const std::wstring& s)
{
    if (s.find(L' ') == std::wstring::npos) return s;
    return L"\"" + s + L"\"";
}

std::wstring Cmd::Display() const
{
    std::wstring r = Quote(exe);
    for (const auto& a : args) r += L" " + Quote(a);
    for (const auto& a : rest) r += L" " + Quote(a);
    return r;
}

// ══════════════ 进程执行 ══════════════

namespace
{

/// 控制台输出多为 ANSI/UTF-8 混合。先试 UTF-8，失败按系统 ANSI 码页解
/// —— 中文 Windows 上是 GBK。
std::wstring Utf8OrAnsi(const std::string& s)
{
    if (s.empty()) return L"";
    int need = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS,
                                   s.data(), (int)s.size(), nullptr, 0);
    if (need > 0)
    {
        std::wstring w(need, L'\0');
        MultiByteToWideChar(CP_UTF8, 0, s.data(), (int)s.size(), &w[0], need);
        return w;
    }
    need = MultiByteToWideChar(CP_ACP, 0, s.data(), (int)s.size(), nullptr, 0);
    if (need > 0)
    {
        std::wstring w(need, L'\0');
        MultiByteToWideChar(CP_ACP, 0, s.data(), (int)s.size(), &w[0], need);
        return w;
    }
    return L"";
}

/// 读一个管道，边读边按行回调，最后把全文写进 sink。
///
/// 关键：必须一直读到 EOF，不能「进程退出就收工」。
/// C# 版在这里栽过 —— WaitForExit(超时) 只等进程退出、不等重定向流读完，
/// 短命进程（几十毫秒）的输出会整段丢掉，依赖检查因此间歇性拿到空结果。
/// Win32 下天然没有这个歧义：ReadFile 返回 0 才算完。
class PipeReader
{
public:
    void Start(HANDLE h, std::function<void(const std::wstring&)> onLine)
    {
        h_ = h;
        onLine_ = std::move(onLine);
        th_ = std::thread([this] { Loop(); });
    }

    void Join()
    {
        if (th_.joinable()) th_.join();
    }

    const std::wstring& Text() const { return text_; }

private:
    void Loop()
    {
        std::string buf;
        buf.reserve(16384);
        char chunk[4096];
        DWORD n = 0;

        while (ReadFile(h_, chunk, sizeof(chunk), &n, nullptr) && n > 0)
        {
            buf.append(chunk, n);
            size_t start = 0;
            for (size_t i = 0; i < buf.size(); ++i)
            {
                if (buf[i] != '\n') continue;
                std::string line = buf.substr(start, i - start);
                while (!line.empty() &&
                       (line.back() == '\r' || line.back() == '\n'))
                    line.pop_back();
                Emit(line);
                start = i + 1;
            }
            buf.erase(0, start);
        }
        if (!buf.empty()) Emit(buf);
        CloseHandle(h_);
        h_ = nullptr;
    }

    void Emit(const std::string& line)
    {
        std::wstring w = Utf8OrAnsi(line);
        if (!text_.empty()) text_ += L"\n";
        text_ += w;
        if (onLine_) onLine_(w);
    }

    HANDLE h_ = nullptr;
    std::wstring text_;
    std::function<void(const std::wstring&)> onLine_;
    std::thread th_;
};

std::wstring BuildCommandLine(const Cmd& c)
{
    // Windows 路径不允许含引号，所以「有空格就加引号」足够
    std::wstring cl = Quote(c.exe);
    for (const auto& a : c.args) cl += L" " + Quote(a);
    for (const auto& a : c.rest) cl += L" " + Quote(a);
    return cl;
}

} // namespace

RunResult Runner::Run(const Cmd& c, const LineFn& onLine)
{
    RunResult res;
    auto t0 = std::chrono::steady_clock::now();

    SECURITY_ATTRIBUTES sa{};
    sa.nLength = sizeof(sa);
    sa.bInheritHandle = TRUE;

    HANDLE rdOut = nullptr, wrOut = nullptr, rdErr = nullptr, wrErr = nullptr;
    if (!CreatePipe(&rdOut, &wrOut, &sa, 0) ||
        !CreatePipe(&rdErr, &wrErr, &sa, 0))
    {
        res.err = L"无法创建管道";
        res.exitCode = -2;
        res.started = false;
        return res;
    }
    // 读端不继承：否则子进程握着读端不放，ReadFile 永远等不到 EOF
    SetHandleInformation(rdOut, HANDLE_FLAG_INHERIT, 0);
    SetHandleInformation(rdErr, HANDLE_FLAG_INHERIT, 0);

    STARTUPINFOW si{};
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW;
    si.wShowWindow = SW_HIDE;
    si.hStdOutput = wrOut;
    si.hStdError = wrErr;
    si.hStdInput = GetStdHandle(STD_INPUT_HANDLE);

    PROCESS_INFORMATION pi{};
    std::wstring cl = BuildCommandLine(c);
    std::wstring workDir = c.workDir;

    // CREATE_NO_WINDOW 与 STARTF_USESHOWWINDOW 同时用会让 CreateProcess 直接失败，
    // 这里只保留 CREATE_NO_WINDOW（本来就不需要窗口）
    DWORD flags = CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT;

    BOOL ok = CreateProcessW(nullptr, cl.empty() ? nullptr : &cl[0],
                             nullptr, nullptr, TRUE, flags, nullptr,
                             workDir.empty() ? nullptr : workDir.c_str(),
                             &si, &pi);

    CloseHandle(wrOut);
    CloseHandle(wrErr);

    if (!ok)
    {
        DWORD e = GetLastError();
        CloseHandle(rdOut);
        CloseHandle(rdErr);
        res.err = L"启动失败 (错误码 " + std::to_wstring(e) + L")";
        res.exitCode = -2;
        res.started = false;
        return res;
    }

    proc_ = pi.hProcess;
    res.started = true;

    PipeReader outRd, errRd;
    outRd.Start(rdOut, onLine);
    errRd.Start(rdErr, nullptr);

    DWORD wr = WaitForSingleObject(pi.hProcess,
                                   (DWORD)std::max(1, c.timeoutMs));
    if (wr == WAIT_TIMEOUT)
    {
        res.timedOut = true;
        res.exitCode = -1;
        TerminateProcess(pi.hProcess, (UINT)-1);
    }

    // 等两个管道都读到 EOF，输出才算收全
    outRd.Join();
    errRd.Join();
    res.out = outRd.Text();
    res.err = errRd.Text();

    if (!res.timedOut)
    {
        DWORD code = 0;
        if (GetExitCodeProcess(pi.hProcess, &code)) res.exitCode = (int)code;
        else res.exitCode = -3;
    }

    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    proc_ = nullptr;

    auto t1 = std::chrono::steady_clock::now();
    res.seconds = std::chrono::duration<double>(t1 - t0).count();
    return res;
}

void Runner::Abort()
{
    if (proc_) TerminateProcess(proc_, (UINT)-1);
}

// ══════════════ 临时产物 ══════════════

BuildArtifacts::~BuildArtifacts() { Cleanup(); }

void BuildArtifacts::Cleanup()
{
    for (const auto& f : tempFiles)
        if (!f.empty()) DeleteFileW(f.c_str());
    tempFiles.clear();
    icoPath.clear();
    verPath.clear();
}

} // namespace dainric
