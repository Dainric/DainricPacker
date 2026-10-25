// Dainric 打包器 · C++ 版后端自检
// 作者: Dainric
//
// 不开窗，直接驱动 Packer 跑真实打包，把结论打到控制台。
// 用途：验证移植后的引擎行为与 C# 版一致（那些行为是被 66 个测试和
// 真实失败工程钉过的）。
//
// 用法: dtest.exe <python.exe> <工作目录>
#include "../src/core.h"
#include <cstdio>
#include <sstream>
#include <string>
#include <vector>

using namespace dainric;

static int g_fail = 0;

static void Check(bool ok, const char* what)
{
    std::printf("  [%s] %s\n", ok ? "OK" : "!!", what);
    if (!ok) ++g_fail;
}

static std::wstring W(const char* s)
{
    if (!s) return L"";
    int n = MultiByteToWideChar(CP_UTF8, 0, s, -1, nullptr, 0);
    std::wstring w(n > 0 ? n - 1 : 0, L'\0');
    if (n > 1) MultiByteToWideChar(CP_UTF8, 0, s, -1, &w[0], n);
    return w;
}

static std::string U(const std::wstring& w)
{
    if (w.empty()) return "";
    int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(),
                                nullptr, 0, nullptr, nullptr);
    std::string s(n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), &s[0], n,
                        nullptr, nullptr);
    return s;
}

static void WriteFileUtf8(const std::wstring& path, const std::string& body)
{
    HANDLE h = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr,
                           CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return;
    DWORD wr = 0;
    WriteFile(h, body.data(), (DWORD)body.size(), &wr, nullptr);
    CloseHandle(h);
}

static bool Exists(const std::wstring& p)
{
    return GetFileAttributesW(p.c_str()) != INVALID_FILE_ATTRIBUTES;
}

int main(int argc, char** argv)
{
    SetConsoleOutputCP(CP_UTF8);

    if (argc < 3)
    {
        std::printf("用法: dtest.exe <python.exe> <工作目录>\n");
        return 2;
    }
    std::wstring py = W(argv[1]);
    std::wstring work = W(argv[2]);

    std::printf("python = %s\n", U(py).c_str());
    std::printf("工作目录 = %s\n\n", U(work).c_str());

    CreateDirectoryW(work.c_str(), nullptr);
    std::wstring srcDir = work + L"\\src";
    std::wstring outDir = work + L"\\out";
    CreateDirectoryW(srcDir.c_str(), nullptr);
    CreateDirectoryW(outDir.c_str(), nullptr);

    // ── 1) 依赖检查：标准库 / 本地 / 缺失 三类都要分对 ──
    {
        std::printf("[1] 依赖检查分类\n");
        WriteFileUtf8(srcDir + L"\\helper.py", "X = 1\n");
        std::wstring f = srcDir + L"\\dep.py";
        WriteFileUtf8(f, "import os\nimport helper\n"
                         "import dainric_absent_zz\nprint(1)\n");

        Runner r;
        DepReport rep = CheckDependencies(f, py, r);
        Check(rep.ran, "检查执行成功");
        bool hasStd = false, hasLocal = false, hasMiss = false;
        for (const auto& e : rep.entries)
        {
            if (e.module == L"os" && e.kind == L"标准库") hasStd = true;
            if (e.module == L"helper" && e.kind == L"本地") hasLocal = true;
            if (e.module == L"dainric_absent_zz" && e.kind == L"缺失")
                hasMiss = true;
        }
        Check(hasStd, "os 归类为标准库");
        Check(hasLocal, "helper 归类为本地");
        Check(hasMiss, "dainric_absent_zz 归类为缺失");
        Check(!rep.Clean(), "有缺失时不算干净");

        // 正则粗扫兜底
        auto scanned = ScanImports(f);
        Check(!scanned.empty(), "正则粗扫能扫到 import");
        std::printf("\n");
    }

    // ── 2) 依赖缺失必须在打包前被拦下 ──
    {
        std::printf("[2] 缺依赖应被拦下（不进入打包）\n");
        Runner r;
        Packer pk(r,
                  [](const std::wstring& t, Lv) { std::printf("      %s\n", U(t).c_str()); },
                  nullptr);

        BuildOptions o;
        o.script = srcDir + L"\\dep.py";
        o.outName = L"dep";
        o.outDir = outDir;
        o.checkDeps = true;
        o.enginePrefix = { py, L"-m", L"PyInstaller" };

        std::wstring packText, product;
        bool ok = pk.BuildOnce(o, packText, product);
        Check(!ok, "缺依赖时打包被拦下");
        Check(!Exists(outDir + L"\\dep.exe"), "没有产出 EXE");
        std::printf("\n");
    }

    // ── 3) 正常脚本应当打包成功且产物自检通过 ──
    {
        std::printf("[3] 正常打包 + 产物自检\n");
        std::wstring f = srcDir + L"\\good.py";
        WriteFileUtf8(f, "import json\nprint(json.dumps({'ok': 1}))\n");

        Runner r;
        Packer pk(r,
                  [](const std::wstring& t, Lv) { std::printf("      %s\n", U(t).c_str()); },
                  nullptr);

        BuildOptions o;
        o.script = f;
        o.outName = L"good";
        o.outDir = outDir;
        o.checkDeps = true;
        o.testRun = true;
        o.timeoutSec = 600;
        o.enginePrefix = { py, L"-m", L"PyInstaller" };

        std::wstring packText, product;
        bool ok = pk.BuildOnce(o, packText, product);
        Check(ok, "打包成功");
        Check(!pk.SelfTestFailed(), "产物自检通过");
        Check(Exists(product), "产物存在");
        if (Exists(product))
        {
            HANDLE h = CreateFileW(product.c_str(), GENERIC_READ,
                                   FILE_SHARE_READ, nullptr, OPEN_EXISTING,
                                   FILE_ATTRIBUTE_NORMAL, nullptr);
            if (h != INVALID_HANDLE_VALUE)
            {
                LARGE_INTEGER sz{};
                GetFileSizeEx(h, &sz);
                CloseHandle(h);
                std::printf("      产物大小 %.2f MB\n",
                            sz.QuadPart / 1048576.0);
                Check(sz.QuadPart > 1000000, "产物大小合理（>1MB）");
            }
        }
        std::printf("\n");
    }

    // ── 4) 诊断：缺模块的日志要能抽出模块名 ──
    {
        std::printf("[4] 诊断抽取\n");
        auto d1 = Diagnose(1,
            L"ModuleNotFoundError: No module named 'requests'\n");
        Check(!d1.missingModules.empty()
              && d1.missingModules[0] == L"requests", "抽出缺失模块 requests");
        Check(d1.HasAutoFix(), "给出了可自动修复项");

        auto d2 = Diagnose(1, L"547 WARNING: Hidden import \"PyQt5.sip\" "
                              L"not found!");
        bool hasPyQt = false;
        for (const auto& m : d2.collectSubmodules)
            if (m == L"PyQt5") hasPyQt = true;
        Check(hasPyQt, "hidden import 抽出顶层包 PyQt5");

        auto d3 = Diagnose(1,
            L"FileNotFoundError: [Errno 2] No such file or directory: "
            L"'C:\\py\\Lib\\site-packages\\matplotlib\\mpl-data\\fontlist.json'");
        bool hasMpl = false;
        for (const auto& m : d3.collectData)
            if (m == L"matplotlib") hasMpl = true;
        Check(hasMpl, "数据文件缺失抽出 matplotlib");

        auto d4 = Diagnose(1, L"WARNING: Hidden import \"site-packages\" "
                              L"not found!");
        Check(d4.collectSubmodules.empty(), "site-packages 不被当成库名");
        std::printf("\n");
    }

    // ── 5) 用法错误识别 ──
    {
        std::printf("[5] 用法错误识别（自检误报的防线）\n");
        Check(LooksLikeUsageError(
                  L"usage: app.py [-h] --input INPUT\n"
                  L"app.py: error: the following arguments are required: --input"),
              "argparse 缺参数被认成用法错误");
        Check(!LooksLikeUsageError(
                  L"ModuleNotFoundError: No module named 'requests'"),
              "缺模块不被认成用法错误");
        Check(!LooksLikeUsageError(L"Traceback (most recent call last):\n"
                                   L"ValueError: boom"),
              "真实崩溃不被认成用法错误");
        std::printf("\n");
    }

    // ── 6) 版本号校验 ──
    {
        std::printf("[6] 版本号校验\n");
        Check(IsValidVersion(L"1"), "1 合法");
        Check(IsValidVersion(L"1.0"), "1.0 合法");
        Check(IsValidVersion(L"3.2.0"), "3.2.0 合法");
        Check(IsValidVersion(L"1.0.0.0"), "1.0.0.0 合法");
        Check(!IsValidVersion(L""), "空串不合法");
        Check(!IsValidVersion(L"1.0.0.0.0"), "五段不合法");
        Check(!IsValidVersion(L"abc"), "非数字不合法");
        Check(!IsValidVersion(L"1..2"), "空段不合法");
        Check(!IsValidVersion(L"70000"), "超过 65535 不合法");
        Check(!IsValidVersion(L"1.0-beta"), "带后缀不合法");
        std::printf("\n");
    }

    // ── 7) 版本文件必须能被 PyInstaller 真解析 ──
    {
        std::printf("[7] 版本文件生成\n");
        VersionInfo vi;
        vi.version = L"3.2.0";
        vi.product = L"测试产品";
        vi.company = L"Dainric";
        vi.description = L"中文描述测试";
        vi.copyright = L"Dainric";

        std::wstring f = WriteVersionFile(vi, L"good", work);
        Check(!f.empty(), "生成成功");
        Check(Exists(f), "文件存在");

        if (!f.empty())
        {
            // 拿真解释器解析一遍 —— 自己看结构不算数
            std::wstring libdir = py.substr(0, py.find_last_of(L'\\'));
            Runner r;
            Cmd c;
            c.exe = py;
            c.args = { L"-c",
                L"import sys\n"
                L"sys.path.insert(0, r'" + libdir + L"')\n"
                L"from PyInstaller.utils.win32.versioninfo import "
                L"load_version_info_from_text_file as L\n"
                L"i = L(sys.argv[1])\n"
                L"d = {k.name: k.val for k in i.kids[0].kids[0].kids}\n"
                L"print('PARSED', d.get('FileVersion'), d.get('CompanyName'))" };
            c.args.push_back(f);
            c.timeoutMs = 60000;
            RunResult rr = r.Run(c);
            std::printf("      %s\n", U(rr.out).c_str());
            if (!rr.err.empty())
                std::printf("      err: %s\n", U(rr.err).c_str());
            Check(rr.exitCode == 0, "PyInstaller 能解析生成的版本文件");
            Check(rr.out.find(L"PARSED") != std::wstring::npos,
                  "解析返回了预期结构");
        }
        std::printf("\n");
    }

    // ── 8) 打包历史持久化 ──
    {
        std::printf("[8] 打包历史\n");
        std::wstring hp = HistoryPath();
        Check(!hp.empty(), "历史文件路径可用");

        auto before = LoadHistory();
        HistoryItem it;
        it.time = L"99:99:99";
        it.name = L"dtest_probe";
        it.result = L"成功";
        it.seconds = 1.5;
        AppendHistory(it);
        auto after = LoadHistory();
        Check(after.size() == before.size() + 1, "追加后条数 +1");
        Check(!after.empty() && after[0].name == L"dtest_probe",
              "最新记录在最前");
        Check(!after.empty() && after[0].result == L"成功", "结果字段正确");
        Check(!after.empty() && after[0].seconds > 1.4
              && after[0].seconds < 1.6, "耗时字段正确");

        // 清理探针记录，别污染用户历史
        {
            std::wstring kept, line, txt;
            HANDLE h = CreateFileW(hp.c_str(), GENERIC_READ, FILE_SHARE_READ,
                                   nullptr, OPEN_EXISTING,
                                   FILE_ATTRIBUTE_NORMAL, nullptr);
            if (h != INVALID_HANDLE_VALUE)
            {
                std::string bytes; char buf[4096]; DWORD n = 0;
                while (ReadFile(h, buf, sizeof(buf), &n, nullptr) && n > 0)
                    bytes.append(buf, n);
                CloseHandle(h);
                int need = MultiByteToWideChar(CP_UTF8, 0, bytes.data(),
                                               (int)bytes.size(), nullptr, 0);
                if (need > 0)
                {
                    txt.resize(need);
                    MultiByteToWideChar(CP_UTF8, 0, bytes.data(),
                                        (int)bytes.size(), &txt[0], need);
                }
            }
            std::wstringstream ss(txt);
            while (std::getline(ss, line))
            {
                while (!line.empty() && line.back() == L'\r') line.pop_back();
                if (line.find(L"dtest_probe") != std::wstring::npos) continue;
                if (line.empty()) continue;
                kept += line + L"\r\n";
            }
            std::string bytes;
            int need = WideCharToMultiByte(CP_UTF8, 0, kept.c_str(),
                                           (int)kept.size(), nullptr, 0,
                                           nullptr, nullptr);
            if (need > 0)
            {
                bytes.resize(need);
                WideCharToMultiByte(CP_UTF8, 0, kept.c_str(),
                                    (int)kept.size(), &bytes[0], need,
                                    nullptr, nullptr);
            }
            HANDLE h2 = CreateFileW(hp.c_str(), GENERIC_WRITE, 0, nullptr,
                                    CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL,
                                    nullptr);
            if (h2 != INVALID_HANDLE_VALUE)
            {
                DWORD wr = 0;
                WriteFile(h2, bytes.data(), (DWORD)bytes.size(), &wr, nullptr);
                CloseHandle(h2);
            }
            Check(LoadHistory().size() == before.size(), "探针记录已清理");
        }
        std::printf("\n");
    }

    // ── 9) 带版本资源的真实打包，并从产物读回 ──
    {
        std::printf("[9] 带版本资源的真实打包\n");
        std::wstring f = srcDir + L"\\ver.py";
        WriteFileUtf8(f, "print('ver-ok')\n");

        VersionInfo vi;
        vi.version = L"2.5.1";
        vi.product = L"版本测试";
        vi.company = L"Dainric";
        vi.description = L"带版本资源";
        std::wstring vf = WriteVersionFile(vi, L"very", work);
        Check(!vf.empty(), "版本文件已生成");

        Runner r;
        Packer pk(r,
                  [](const std::wstring& t, Lv) { std::printf("      %s\n", U(t).c_str()); },
                  nullptr);

        BuildOptions o;
        o.script = f;
        o.outName = L"very";
        o.outDir = outDir;
        o.checkDeps = true;
        o.testRun = true;
        o.timeoutSec = 600;
        o.versionFile = vf;
        o.enginePrefix = { py, L"-m", L"PyInstaller" };

        std::wstring packText, product;
        bool ok = pk.BuildOnce(o, packText, product);
        Check(ok, "打包成功");
        Check(Exists(product), "产物存在");

        if (Exists(product))
        {
            std::wstring libdir = py.substr(0, py.find_last_of(L'\\'));
            Cmd c;
            c.exe = py;
            c.args = { L"-c",
                L"import sys\n"
                L"sys.path.insert(0, r'" + libdir + L"')\n"
                L"from PyInstaller.utils.win32.versioninfo import "
                L"read_version_info_from_executable as R\n"
                L"i = R(sys.argv[1])\n"
                L"d = {k.name: k.val for k in i.kids[0].kids[0].kids}\n"
                L"print('VER', d.get('FileVersion'), d.get('ProductName'))" };
            c.args.push_back(product);
            c.timeoutMs = 60000;
            RunResult rr = r.Run(c);
            std::printf("      %s\n", U(rr.out).c_str());
            if (!rr.err.empty())
                std::printf("      err: %s\n", U(rr.err).c_str());
            Check(rr.out.find(L"2.5.1") != std::wstring::npos,
                  "从产物里读回版本号 2.5.1");
        }
        DeleteFileW(vf.c_str());
        std::printf("\n");
    }

    std::printf("====================================\n");
    if (g_fail == 0) std::printf("全部通过\n");
    else std::printf("失败 %d 项\n", g_fail);
    return g_fail == 0 ? 0 : 1;
}
