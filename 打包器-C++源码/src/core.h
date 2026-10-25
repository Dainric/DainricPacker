// Dainric 打包器 · C++ 原生版
// 作者: Dainric
//
// 设计约束（来自实际需求，不是偏好）
//   1. 单文件 EXE，目标机不需要任何运行时
//      -> /MT 静态链接 CRT，动态加载 d2d1/dwrite/dwmapi，绝不静态 import
//   2. 界面要能做出 iOS 那种观感
//      -> GDI 画不出圆角与柔和层次，用 Direct2D + DirectWrite 自绘
//   3. 逻辑必须与已验证的 C# 版一致
//      -> RuntimeLocator / BuildDoctor / DependencyCheck 的行为逐条对齐，
//         那些是被 66 个测试和真实失败工程钉过的结论，不要凭感觉重写
#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif

#include <windows.h>
#include <string>
#include <vector>
#include <functional>
#include <memory>

namespace dainric
{

// ── 进程执行 ──

/// 一次命令的完整描述。exe 与参数分开存，绝不拼成一整行字符串
/// —— C# 版踩过「exe 同时出现在 exe 位置和参数列表」导致 PyInstaller
/// 报 unrecognized arguments 的坑，这里从结构上就不给这个机会。
struct Cmd
{
    std::wstring exe;
    std::vector<std::wstring> args;   // exe 自己的参数（如 -m PyInstaller）
    std::vector<std::wstring> rest;   // 业务参数
    std::wstring workDir;
    int timeoutMs = 600000;

    /// 拼成一条完整参数列表（仅用于展示与调试）
    std::wstring Display() const;
};

struct RunResult
{
    int exitCode = -3;
    std::wstring out;
    std::wstring err;
    double seconds = 0;
    bool timedOut = false;
    bool started = false;

    std::wstring Combined() const { return out + L"\n" + err; }
};

/// 子进程执行器。逐行抓取输出，带超时，可中止。
class Runner
{
public:
    using LineFn = std::function<void(const std::wstring&)>;

    RunResult Run(const Cmd& c, const LineFn& onLine = nullptr);
    void Abort();
    bool Running() const { return proc_ != nullptr; }

private:
    HANDLE proc_ = nullptr;      // 受 mutex 保护不合适，这里只在 UI 线程用
};

// ── 日志 ──

enum class Lv { Dim, Acc, Ok, Warn, Err };

struct LogLine
{
    std::wstring time;
    std::wstring text;
    Lv level = Lv::Dim;
};

// ── 诊断 ──

struct Diagnosis
{
    std::wstring title;
    std::vector<std::wstring> reasons;
    std::vector<std::wstring> fixes;

    /// 日志里提到的缺失模块名，用于自动补 --hidden-import
    std::vector<std::wstring> missingModules;

    /// 需要整包收集的库（对应 --collect-data / -binaries / -submodules）
    std::vector<std::wstring> collectData;
    std::vector<std::wstring> collectBinaries;
    std::vector<std::wstring> collectSubmodules;

    std::wstring rawOutput;

    bool HasAutoFix() const
    {
        return !missingModules.empty() || !collectData.empty()
            || !collectBinaries.empty() || !collectSubmodules.empty();
    }
    bool HasContent() const { return !reasons.empty() || !fixes.empty(); }
    std::wstring Render() const;
};

/// 把 PyInstaller 的输出翻译成用户能看懂的结论
Diagnosis Diagnose(int exitCode, const std::wstring& output);

/// 判断输出是否属于「程序在抱怨用法」而不是「程序坏了」。
/// 打的是需要命令行参数的脚本时，裸启动必然走到这里；
/// 把它当失败报出去就是误报 —— 产物本身是好的。
bool LooksLikeUsageError(const std::wstring& text);

// ── 依赖检查 ──

struct ImportEntry
{
    std::wstring module;
    std::wstring kind;      // 标准库 / 本地 / 已安装 / 缺失
    bool Missing() const { return kind == L"缺失"; }
};

struct DepReport
{
    bool ran = false;
    std::wstring skipReason;
    std::vector<ImportEntry> entries;

    std::vector<std::wstring> Missing() const;
    int StdlibCount() const;
    int LocalCount() const;
    int InstalledCount() const;
    bool Clean() const { return ran && Missing().empty(); }

    std::wstring RenderDetail(const std::wstring& scriptName) const;
    std::wstring RenderBrief() const;
};

/// 扫 import -> 去掉标准库/本地 -> 在目标解释器里验可用性
DepReport CheckDependencies(const std::wstring& scriptPath,
                            const std::wstring& pythonExe,
                            Runner& runner);

/// 不启动进程时，用正则粗扫脚本的顶层 import
std::vector<std::wstring> ScanImports(const std::wstring& scriptPath);

// ── 运行时定位 ──

struct RuntimeSlot
{
    std::wstring key;          // py312 / default
    std::wstring title;
    std::wstring note;
    std::wstring dir;
    std::wstring pythonExe;
    std::wstring version;
    std::wstring pyiVersion;
    std::wstring foundBy;      // 来源说明，显示给用户看
    bool present = false;

    bool Usable() const { return present && !pyiVersion.empty(); }
};

struct RtLocation
{
    std::wstring dir;
    std::wstring origin;
    bool Exists() const;
};

/// 运行时定位。目标：目录不必和 exe 同层，放任意位置都能被找到。
class RuntimeLocator
{
public:
    static const wchar_t* Dir312;
    static const wchar_t* Dir314;
    static const wchar_t* DirGeneric;

    /// 集中存放目录 %LOCALAPPDATA%\Dainric
    static std::wstring HomeDir();
    static std::wstring ConfigPath();

    static std::wstring LoadConfigured(const std::wstring& key);
    static void SaveConfigured(const std::wstring& key,
                               const std::wstring& value);

    /// 所有可能存放 runtimes 的根目录，按优先级排列
    static std::vector<std::pair<std::wstring, std::wstring>> SearchRoots();

    static RtLocation Locate(const std::wstring& key);
    static void Remember(const std::wstring& key, const std::wstring& dir);
};

/// 运行时槽位管理
class RuntimeManager
{
public:
    static const wchar_t* Key312;
    static const wchar_t* KeyDefault;

    static std::vector<RuntimeSlot> Discover();
    static void FillDetails(RuntimeSlot& s, Runner& runner);
    static RuntimeSlot* PickUsable(std::vector<RuntimeSlot>& slots);
    static bool LooksLikeRuntime(const std::wstring& dir);
    static std::wstring DescribeLocations();
};

// ── 构建 ──

struct BuildOptions
{
    std::wstring script;
    std::wstring outName;
    std::wstring outDir;
    bool oneDir = false;
    std::wstring windowMode;       // auto / console / noconsole
    bool uac = false;
    bool clean = true;
    bool testRun = true;
    bool checkDeps = true;
    int timeoutSec = 1800;
    std::wstring iconPath;
    std::wstring version;
    std::vector<std::wstring> enginePrefix;   // exe 自带参数部分
    std::vector<std::wstring> testArgs;       // 自检参数
    std::wstring versionFile;                 // --version-file 指向的文件
    std::vector<std::wstring> tempFiles;      // 打包结束后要删的临时文件

    /// 追加额外的 PyInstaller 参数（用于自动修复重试）
    BuildOptions WithExtraArgs(const std::vector<std::wstring>& extra) const;
};

/// 版本信息文件 / 默认图标之类的临时产物
struct BuildArtifacts
{
    std::wstring icoPath;
    std::wstring verPath;
    std::vector<std::wstring> tempFiles;

    ~BuildArtifacts();
    void Cleanup();
};

/// 产物路径：单文件是 outDir\name.exe，目录形态是 outDir\name\name.exe
std::wstring ProductPathOf(const BuildOptions& o);

/// 脚本里是否出现 GUI 库关键字，决定「自动」窗口模式选哪种
bool ScriptHasGui(const std::wstring& scriptPath);

/// 把 PyInstaller 输出行映射到日志级别
Lv ClassifyPyInstallerLine(const std::wstring& line);

// ── 版本信息文件 ──

struct VersionInfo
{
    std::wstring version = L"1.0.0";   // 1.0 / 1.0.0 / 1.0.0.0
    std::wstring product;              // 留空则用产物名
    std::wstring company = L"Dainric";
    std::wstring description;
    std::wstring copyright;
};

/// 校验版本号：1 到 4 段数字，每段 0-65535
bool IsValidVersion(const std::wstring& v);

/// 生成 PyInstaller 的 --version-file。
/// 格式见 PyInstaller/utils/win32/versioninfo.py 的 VSVersionInfo.__str__，
/// 首行必须是 # UTF-8，否则中文描述会被按系统码页解析。
/// 返回空串表示生成失败。
std::wstring WriteVersionFile(const VersionInfo& vi,
                              const std::wstring& outName,
                              const std::wstring& tempDir);

// ── 打包历史 ──

struct HistoryItem
{
    std::wstring time;
    std::wstring name;
    std::wstring result;    // 成功 / 失败 / 已终止
    double seconds = 0;
};

std::vector<HistoryItem> LoadHistory();
void AppendHistory(const HistoryItem& it);
std::wstring HistoryPath();

/// 打包引擎：把 BuildOptions 变成一条 PyInstaller 命令并执行，含自动修复重试。
///
/// 两条来自实测的结论，别改：
///   1) PyInstaller 对「import 一个不存在的模块」是哑的 —— 照样返回 0、
///      照样产出 exe，坏的只是产物。所以缺模块只能靠产物自检抓到，
///      重试判断必须同时看「打包输出」和「自检输出」两条路。
///   2) 补 hidden-import 救不了「模块根本没装」。那种情况要在打包前
///      用依赖检查拦住，不能指望重试。
class Packer
{
public:
    using LogFn = std::function<void(const std::wstring&, Lv)>;
    using RunFn = std::function<void(const std::wstring&, const std::wstring&)>;

    Packer(Runner& runner, LogFn log, RunFn setRun);

    /// 带自动修复的打包。返回是否成功。
    bool Build(const BuildOptions& o);

    /// 单次打包，不重试。回填完整输出与产物路径。
    bool BuildOnce(const BuildOptions& o, std::wstring& packText,
                   std::wstring& productPath);

    void Abort();
    bool Aborted() const { return aborted_; }

    /// 最近一次产物自检的结论
    bool SelfTestFailed() const { return selftestFailed_; }
    const std::wstring& SelfTestText() const { return selftestText_; }

private:
    void SelfTest(const BuildOptions& o, const std::wstring& exe);
    std::wstring FindPython();

    Runner& runner_;
    LogFn log_;
    RunFn setRun_;
    bool aborted_ = false;
    bool selftestFailed_ = false;
    int selftestCode_ = 0;
    std::wstring selftestText_;
    std::wstring cachedPython_;
};

} // namespace dainric
