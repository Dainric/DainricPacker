// Dainric 打包器 · C++ 原生版 · 入口
// 作者: Dainric
//
// 为什么不用 .NET：
//   自包含单文件要 60 MB，而这里做的事（拉子进程、解析文本、画界面）
//   用 Win32 + Direct2D 一两 MB 就够，目标机同样零依赖。
//
// 为什么不用 WebView2：
//   它依赖目标机装有 Edge WebView2 运行时，与「解压即用」冲突。
//   界面用 Direct2D + DirectWrite 自绘，观感可控且无外部依赖。
#include "core.h"
#include "ui.h"

int main()
{
    // 进程级 DPI 感知：不设的话高分屏下整个界面会被系统拉伸变糊
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

    return dainric::RunApp();
}
