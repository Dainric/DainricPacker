// Dainric 打包器 · C++ 原生版 · 界面层
// 作者: Dainric
//
// UI 方案 A（分区折叠）：
//   左侧固定导航，中间一列可滚动卡片，右侧日志，
//   底部常驻动作条。常用项留在主视野，UAC / 超时 / 自检参数
//   收进默认折叠的「高级选项」，主列因此不需要滚动。
//
// 全部自绘：Direct2D 负责圆角矩形与柔和层次，DirectWrite 负责文字。
// 不用 GDI —— 它画不出 iOS 那种观感。
#pragma once
#include "core.h"

namespace dainric
{

/// 创建窗口并跑消息循环。返回进程退出码。
int RunApp();

} // namespace dainric
