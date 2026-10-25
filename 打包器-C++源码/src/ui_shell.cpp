// Dainric 打包器 · C++ 原生版 · 界面层
// 作者: Dainric
//
// UI 方案 A（分区折叠）：
//   左侧固定导航，中间一列可滚动卡片，右侧日志，底部常驻动作条。
//   常用项留在主视野，UAC / 超时 / 自检参数收进默认折叠的「高级选项」。
//
// 为什么自绘而不是用现成控件：
//   Windows 原生控件画不出 iOS 那种圆角与柔和层次；
//   引 Qt 之类又会把单文件体积从 1 MB 推到 10 MB 以上。
//   Direct2D + DirectWrite 是系统自带的，代价只是自己算布局。
#include "ui.h"

#include <windowsx.h>
#include <d2d1.h>
#include <d2d1helper.h>
#include <dwrite.h>
#include <dwmapi.h>
#include <commdlg.h>
#include <shellapi.h>
#include <shlobj.h>
#include <algorithm>
#include <map>
#include <thread>
#include <mutex>

namespace dainric
{

namespace
{

// ══════════════════════ 设计令牌（与 Theme.xaml 对齐）══════════════════════

struct Col
{
    D2D1_COLOR_F bg     = { 0.000f, 0.000f, 0.000f, 1 };  // #000000
    D2D1_COLOR_F s1     = { 0.110f, 0.110f, 0.118f, 1 };  // #1C1C1E
    D2D1_COLOR_F s2     = { 0.173f, 0.173f, 0.180f, 1 };  // #2C2C2E
    D2D1_COLOR_F s3     = { 0.227f, 0.227f, 0.235f, 1 };  // #3A3A3C
    D2D1_COLOR_F hair   = { 0.165f, 0.165f, 0.173f, 1 };  // #2A2A2C
    D2D1_COLOR_F label  = { 0.949f, 0.949f, 0.969f, 1 };  // #F2F2F7
    D2D1_COLOR_F label2 = { 0.647f, 0.647f, 0.675f, 1 };  // #A5A5AC
    D2D1_COLOR_F label3 = { 0.486f, 0.529f, 0.596f, 1 };  // #7C8798
    D2D1_COLOR_F blue   = { 0.039f, 0.518f, 1.000f, 1 };  // #0A84FF
    D2D1_COLOR_F green  = { 0.188f, 0.820f, 0.345f, 1 };  // #30D158
    D2D1_COLOR_F orange = { 1.000f, 0.624f, 0.039f, 1 };  // #FF9F0A
    D2D1_COLOR_F pink   = { 1.000f, 0.216f, 0.373f, 1 };  // #FF375F
    D2D1_COLOR_F logBg  = { 0.051f, 0.051f, 0.055f, 1 };  // #0D0D0E
} C;

const float kRadCard = 14.0f;
const float kRadBtn  = 9.0f;
const float kRadIn   = 9.0f;
// 行高按「一屏装下所有常用项」定，不是随手取的：
// 主列可视高度约 654px，脚本卡 + 输出卡 + 构建选项卡必须都在其中，
// 否则开关被挤到可视区之外，用户会以为功能不存在。
const float kRowH    = 38.0f;
const float kHeadH   = 40.0f;
const float kSideW   = 196.0f;
const float kLogW    = 330.0f;
const float kActH    = 62.0f;
const float kTitleH  = 44.0f;
const float kPadX    = 18.0f;

struct Rect
{
    float x = 0, y = 0, w = 0, h = 0;
    bool Hit(float px, float py) const
    { return px >= x && px <= x + w && py >= y && py <= y + h; }
    D2D1_RECT_F D2() const { return D2D1::RectF(x, y, x + w, y + h); }
};

// ══════════════════════ 界面状态 ══════════════════════

/// 一个可编辑的文本输入。
/// 之所以自己实现而不是嵌 Edit 控件：整个界面是 Direct2D 自绘的，
/// 混入原生控件会出现字体、圆角、焦点框全都不一致的割裂感。
struct Input
{
    int id = 0;
    std::wstring text;
    const wchar_t* placeholder = L"";
    Rect box;
    bool mono = false;
};

struct App
{
    HWND hwnd = nullptr;
    ID2D1Factory* d2d = nullptr;
    IDWriteFactory* dw = nullptr;
    ID2D1HwndRenderTarget* rt = nullptr;

    IDWriteTextFormat* fT = nullptr;    // 标题
    IDWriteTextFormat* fH = nullptr;    // 卡片标题
    IDWriteTextFormat* fB = nullptr;    // 正文
    IDWriteTextFormat* fS = nullptr;    // 次要
    IDWriteTextFormat* fC = nullptr;    // 说明
    IDWriteTextFormat* fM = nullptr;    // 等宽

    std::vector<std::pair<Rect, int>> navHits;
    std::vector<std::pair<Rect, int>> cardHits;
    std::vector<std::pair<Rect, int>> btnHits;
    std::vector<std::pair<Rect, int>> swHits;
    Rect dropZone; bool hasDrop = false;

    // ── 文本输入 ──
    std::vector<Input> inputs;      // 每帧重建
    std::vector<Rect> inputHits;    // 只放矩形，命中测试用
    int focusInput = -1;            // 按 id 索引，-1 表示无焦点
    size_t caret = 0;
    size_t selStart = 0;            // == caret 表示无选区
    bool caretOn = true;
    ULONGLONG caretTick = 0;
    int mouseDownInput = -1;

    int page = 0;
    bool swSelfTest = true, swCheckDeps = true, swUac = false, swClean = true;
    bool advOpen = false;
    float scroll = 0, contentH = 0;
    int hotBtn = -1, hotNav = -1;

    std::wstring script, outDir, iconPath;
    std::wstring statusMain = L"就绪", statusSub = L"等待开始";
    std::wstring rtSummary = L"正在读取运行时信息…";
    std::vector<ImportEntry> depEntries;
    std::wstring depNote =
        L"在「单文件打包」里选好脚本后，这里显示依赖检查结果。";
    std::vector<HistoryItem> history;
    VersionInfo ver;
    bool verDlgOpen = false;

    std::vector<LogLine> logs;
    std::mutex logMx;
    bool busy = false;

    Runner runner;
    std::unique_ptr<Packer> packer;
    std::vector<std::wstring> enginePrefix;
    std::wstring engineDesc = L"未检测";

    // 输入框当前值（以 id 为键，跨帧保留）
    std::map<int, std::wstring> values;

    std::wstring& Val(int id, const std::wstring& init = L"")
    {
        auto it = values.find(id);
        if (it == values.end())
            it = values.emplace(id, init).first;
        return it->second;
    }
};

App* g = nullptr;

// ══════════════════════ 小工具 ══════════════════════

std::wstring Now()
{
    SYSTEMTIME st; GetLocalTime(&st);
    wchar_t b[16];
    swprintf_s(b, L"%02d:%02d:%02d", st.wHour, st.wMinute, st.wSecond);
    return b;
}

std::wstring FileNameOf(const std::wstring& p)
{
    size_t s = p.find_last_of(L"\\/");
    return (s == std::wstring::npos) ? p : p.substr(s + 1);
}

std::wstring BaseNameOf(const std::wstring& p)
{
    std::wstring n = FileNameOf(p);
    size_t s = n.find_last_of(L'.');
    return (s == std::wstring::npos) ? n : n.substr(0, s);
}

std::wstring DesktopOutDir()
{
    wchar_t buf[MAX_PATH * 2] = { 0 };
    if (SUCCEEDED(SHGetFolderPathW(nullptr, CSIDL_DESKTOPDIRECTORY, nullptr,
                                   0, buf)))
    {
        std::wstring d = std::wstring(buf) + L"\\Dainric产物";
        CreateDirectoryW(d.c_str(), nullptr);
        return d;
    }
    return L"";
}

D2D1_COLOR_F A(const D2D1_COLOR_F& c, float a)
{
    D2D1_COLOR_F r = c;
    r.a = a;
    return r;
}

// CreateSolidColorBrush 在 Win32 下要求显式给 out 参数
// （ID2D1RenderTarget 同时有 1 参和 2 参重载，MSVC 不选 1 参那个）。
// 这里包一层，避免每处都写两行。
ID2D1SolidColorBrush* Brush(const D2D1_COLOR_F& c)
{
    ID2D1SolidColorBrush* br = nullptr;
    if (FAILED(g->rt->CreateSolidColorBrush(c, &br))) return nullptr;
    return br;
}

/// 把「自检参数」输入框拆成参数列表。
/// 支持引号包裹（含空格的参数），没有引号就按空白切分。
std::vector<std::wstring> SplitArgs(const std::wstring& text)
{
    std::vector<std::wstring> list;
    if (text.empty()) return list;
    std::wstring cur;
    wchar_t quote = 0;
    for (wchar_t c : text)
    {
        if (quote)
        {
            if (c == quote) quote = 0;
            else cur += c;
        }
        else if (c == L'"' || c == L'\'') quote = c;
        else if (c == L' ' || c == L'\t')
        {
            if (!cur.empty()) { list.push_back(cur); cur.clear(); }
        }
        else cur += c;
    }
    if (!cur.empty()) list.push_back(cur);
    return list;
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

void LogAdd(const std::wstring& text, Lv level)
{
    if (!g) return;
    LogLine l;
    l.time = Now();
    l.text = text;
    l.level = level;
    std::lock_guard<std::mutex> lk(g->logMx);
    g->logs.push_back(l);
    if (g->logs.size() > 3000)
        g->logs.erase(g->logs.begin(), g->logs.begin() + 800);
}

void SetStatus(const std::wstring& main, const std::wstring& sub)
{
    if (!g) return;
    g->statusMain = main;
    g->statusSub = sub;
}

void Invalidate()
{
    if (g && g->hwnd) InvalidateRect(g->hwnd, nullptr, FALSE);
}

// ══════════════════════ 绘制原语 ══════════════════════

void FillRect(const Rect& r, const D2D1_COLOR_F& c, float radius = 0)
{
    auto* br = Brush(c);
    if (!br) return;
    if (radius <= 0) g->rt->FillRectangle(r.D2(), br);
    else
    {
        auto rr = D2D1::RoundedRect(r.D2(), radius, radius);
        g->rt->FillRoundedRectangle(rr, br);
    }
    br->Release();
}

void StrokeRect(const Rect& r, const D2D1_COLOR_F& c, float radius,
                float width = 1.0f)
{
    auto* br = Brush(c);
    if (!br) return;
    if (radius <= 0) g->rt->DrawRectangle(r.D2(), br, width);
    else
    {
        auto rr = D2D1::RoundedRect(r.D2(), radius, radius);
        g->rt->DrawRoundedRectangle(rr, br, width);
    }
    br->Release();
}

void HLine(float x, float y, float w, const D2D1_COLOR_F& c)
{
    auto* br = Brush(c);
    if (!br) return;
    g->rt->DrawLine(D2D1::Point2F(x, y), D2D1::Point2F(x + w, y), br, 1.0f);
    br->Release();
}

void Text(const std::wstring& s, IDWriteTextFormat* fmt, const Rect& box,
          const D2D1_COLOR_F& c, bool centerV = true, bool centerH = false)
{
    if (s.empty() || !fmt) return;
    IDWriteTextLayout* lay = nullptr;
    if (FAILED(g->dw->CreateTextLayout(s.c_str(), (UINT32)s.size(), fmt,
                                       box.w, box.h, &lay)) || !lay)
        return;
    if (centerV)
        lay->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
    if (centerH)
        lay->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_CENTER);
    auto* br = Brush(c);
    if (br)
    {
        g->rt->DrawTextLayout(D2D1::Point2F(box.x, box.y), lay, br,
                              D2D1_DRAW_TEXT_OPTIONS_CLIP);
        br->Release();
    }
    lay->Release();
}

float TextH(const std::wstring& s, IDWriteTextFormat* fmt, float w)
{
    if (s.empty() || !fmt) return 0;
    IDWriteTextLayout* l = nullptr;
    if (FAILED(g->dw->CreateTextLayout(s.c_str(), (UINT32)s.size(), fmt,
                                       w, 8000, &l)) || !l)
        return 0;
    DWRITE_TEXT_METRICS m{};
    l->GetMetrics(&m);
    l->Release();
    return m.height;
}

// ══════════════════════ 文本输入 ══════════════════════

/// 画一个可编辑输入框，并把它登记进本帧的命中表。
/// 文本超宽时按光标位置横向滚动，否则长路径看不到尾部。
void InputBox(int id, float x, float y, float w, std::wstring& value,
              const wchar_t* placeholder, bool mono = false)
{
    Rect box{ x, y + 6, w, kRowH - 12 };
    Rect hit{ x, y, w, kRowH };

    Input in;
    in.id = id;
    in.text = value;
    in.placeholder = placeholder;
    in.box = box;
    in.mono = mono;
    g->inputs.push_back(in);
    g->inputHits.push_back(hit);

    bool focused = (g->focusInput == id);
    FillRect(box, C.s2, kRadIn);
    if (focused) StrokeRect(box, C.blue, kRadIn, 1.5f);

    auto* fmt = mono ? g->fM : g->fS;
    std::wstring shown = value.empty()
        ? (placeholder ? placeholder : L"") : value;
    if (shown.empty()) return;

    IDWriteTextLayout* lay = nullptr;
    if (FAILED(g->dw->CreateTextLayout(shown.c_str(), (UINT32)shown.size(),
                                       fmt, 4000, box.h, &lay)) || !lay)
        return;
    lay->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
    lay->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);

    DWRITE_TEXT_METRICS tm{};
    lay->GetMetrics(&tm);
    float pad = 10.0f;
    float avail = box.w - pad * 2;

    float sc = 0;
    if (focused && tm.width > avail)
    {
        // 让光标始终留在可视区内
        FLOAT cx = 0, cy = 0;
        DWRITE_HIT_TEST_METRICS hm{};
        lay->HitTestTextPosition((UINT32)g->caret, FALSE, &cx, &cy, &hm);
        if (cx > avail) sc = cx - avail + 8;
    }

    // 选区底色
    if (focused && g->selStart != g->caret)
    {
        UINT32 s = (UINT32)(std::min)(g->selStart, g->caret);
        UINT32 e = (UINT32)(std::max)(g->selStart, g->caret);
        UINT32 cnt = 0;
        lay->HitTestTextRange(s, e - s, 0, 0, nullptr, 0, &cnt);
        if (cnt > 0)
        {
            std::vector<DWRITE_HIT_TEST_METRICS> ms(cnt);
            lay->HitTestTextRange(s, e - s, 0, 0, ms.data(), cnt, &cnt);
            for (UINT32 i = 0; i < cnt; ++i)
                FillRect({ box.x + pad + ms[i].left - sc, box.y + ms[i].top,
                           ms[i].width, ms[i].height },
                         A(C.blue, 0.35f), 3);
        }
    }

    auto* br = Brush(value.empty() ? C.label3 : C.label);
    if (br)
    {
        g->rt->DrawTextLayout(D2D1::Point2F(box.x + pad - sc, box.y), lay, br,
                              D2D1_DRAW_TEXT_OPTIONS_CLIP);
        br->Release();
    }

    // 光标
    if (focused && g->caretOn)
    {
        FLOAT cx = 0, cy = 0;
        DWRITE_HIT_TEST_METRICS hm{};
        lay->HitTestTextPosition((UINT32)g->caret, FALSE, &cx, &cy, &hm);
        float cxp = box.x + pad + cx - sc;
        if (cxp >= box.x + 2 && cxp <= box.x + box.w - 2)
        {
            auto* cbr = Brush(C.blue);
            if (cbr)
            {
                g->rt->DrawLine(D2D1::Point2F(cxp, box.y + 5),
                                D2D1::Point2F(cxp, box.y + box.h - 5),
                                cbr, 1.6f);
                cbr->Release();
            }
        }
    }
    lay->Release();
}

/// 命中测试：返回被点到的输入框 id，未命中返回 -1
int InputAt(float x, float y)
{
    for (size_t i = 0; i < g->inputHits.size() && i < g->inputs.size(); ++i)
        if (g->inputHits[i].Hit(x, y)) return g->inputs[i].id;
    return -1;
}

const Input* FindInput(int id)
{
    for (const auto& in : g->inputs)
        if (in.id == id) return &in;
    return nullptr;
}

/// 把点击横坐标换算成字符索引
size_t CaretFromPoint(const Input& in, float px)
{
    if (!g->dw) return in.text.size();
    auto* fmt = in.mono ? g->fM : g->fS;
    IDWriteTextLayout* lay = nullptr;
    if (FAILED(g->dw->CreateTextLayout(in.text.c_str(),
                                       (UINT32)in.text.size(), fmt,
                                       4000, in.box.h, &lay)) || !lay)
        return in.text.size();
    lay->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
    lay->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);

    BOOL trailing = FALSE, inside = FALSE;
    DWRITE_HIT_TEST_METRICS hm{};
    lay->HitTestPoint(px - in.box.x - 10, in.box.h / 2,
                      &trailing, &inside, &hm);
    size_t idx = hm.textPosition + (trailing ? hm.length : 0);
    lay->Release();
    if (idx > in.text.size()) idx = in.text.size();
    return idx;
}

void InputDeleteSel()
{
    if (g->selStart == g->caret) return;
    size_t s = (std::min)(g->selStart, g->caret);
    size_t e = (std::max)(g->selStart, g->caret);
    std::wstring& v = g->Val(g->focusInput);
    if (e > v.size()) e = v.size();
    v.erase(s, e - s);
    g->caret = s;
    g->selStart = s;
}

/// 处理按键。返回 true 表示消费了该消息。
bool InputKey(WPARAM key, bool ctrl)
{
    if (g->focusInput < 0) return false;
    std::wstring& v = g->Val(g->focusInput);
    if (g->caret > v.size()) g->caret = v.size();

    auto reset = [&] {
        g->selStart = g->caret;
        g->caretTick = GetTickCount();
        g->caretOn = true;
    };

    switch (key)
    {
    case VK_LEFT:
        if (g->caret > 0) --g->caret;
        reset();
        return true;
    case VK_RIGHT:
        if (g->caret < v.size()) ++g->caret;
        reset();
        return true;
    case VK_HOME:
        g->caret = 0;
        reset();
        return true;
    case VK_END:
        g->caret = v.size();
        reset();
        return true;
    case VK_BACK:
        if (g->selStart != g->caret) InputDeleteSel();
        else if (g->caret > 0)
        {
            v.erase(g->caret - 1, 1);
            --g->caret;
            g->selStart = g->caret;
        }
        reset();
        return true;
    case VK_DELETE:
        if (g->selStart != g->caret) InputDeleteSel();
        else if (g->caret < v.size()) v.erase(g->caret, 1);
        reset();
        return true;
    }
    if (ctrl && (key == 'A' || key == 'a'))
    {
        g->selStart = 0;
        g->caret = v.size();
        return true;
    }
    return false;
}

/// 处理字符输入。返回 true 表示消费了该消息。
bool InputChar(wchar_t ch)
{
    if (g->focusInput < 0) return false;
    if (ch < 0x20 || ch == 0x7F) return false;   // 控制字符走 KEYDOWN

    std::wstring& v = g->Val(g->focusInput);
    if (g->selStart != g->caret) InputDeleteSel();
    if (g->caret > v.size()) g->caret = v.size();
    v.insert(v.begin() + g->caret, ch);
    ++g->caret;
    g->selStart = g->caret;
    g->caretTick = GetTickCount();
    g->caretOn = true;
    return true;
}

/// 光标闪烁：约 530ms 翻转一次
void TickCaret()
{
    if (g->focusInput < 0) return;
    ULONGLONG now = GetTickCount();
    if (now - g->caretTick >= 530)
    {
        g->caretTick = now;
        g->caretOn = !g->caretOn;
        Invalidate();
    }
}

// ══════════════════════ 布局 ══════════════════════

struct Layout
{
    float W = 0, H = 0;
    Rect side, form, logCol, act, title;
};

Layout MakeLayout()
{
    RECT rc;
    GetClientRect(g->hwnd, &rc);
    Layout L;
    L.W = (float)(rc.right - rc.left);
    L.H = (float)(rc.bottom - rc.top);
    L.title = { 0, 0, L.W, kTitleH };
    L.act   = { 0, L.H - kActH, L.W, kActH };
    float midY = kTitleH;
    float midH = L.H - kTitleH - kActH;
    L.side   = { 0, midY, kSideW, midH };
    float logW = (L.W > 940) ? kLogW : 0;   // 窗口窄就不显示日志列
    L.logCol = { L.W - logW, midY, logW, midH };
    L.form   = { kSideW, midY, L.W - kSideW - logW, midH };
    return L;
}

// ══════════════════════ 各区块 ══════════════════════

void DrawTitle(const Layout& L)
{
    FillRect(L.title, C.bg);

    // 品牌三角
    float cx = 22, cy = L.title.h / 2;
    auto* triBr = Brush(C.blue);
    ID2D1PathGeometry* geo = nullptr;
    if (triBr && SUCCEEDED(g->d2d->CreatePathGeometry(&geo)) && geo)
    {
        ID2D1GeometrySink* sink = nullptr;
        if (SUCCEEDED(geo->Open(&sink)) && sink)
        {
            sink->BeginFigure(D2D1::Point2F(cx - 6, cy - 8),
                              D2D1_FIGURE_BEGIN_FILLED);
            sink->AddLine(D2D1::Point2F(cx + 8, cy));
            sink->AddLine(D2D1::Point2F(cx - 6, cy + 8));
            sink->EndFigure(D2D1_FIGURE_END_CLOSED);
            sink->Close();
            g->rt->FillGeometry(geo, triBr);
            sink->Release();
        }
        geo->Release();
    }
    if (triBr) triBr->Release();

    Text(L"Dainric 打包器", g->fT, { 40, 0, 180, L.title.h }, C.label);
    Text(L"3.2.0 · 原生版", g->fC, { 156, 2, 140, L.title.h }, C.label3);
    Text(g->statusMain, g->fC, { L.W - 280, 0, 260, L.title.h }, C.label2,
         true, false);
    HLine(0, L.title.h - 1, L.W, C.hair);
}

const wchar_t* kNav[] = { L"单文件打包", L"依赖检查", L"批量打包",
                          L"打包历史", L"运行时" };
const int kNavCount = 5;

void DrawSide(const Layout& L)
{
    FillRect(L.side, C.bg);

    float y = L.side.y + 16;
    FillRect({ L.side.x + 12, y, 26, 26 }, C.blue, 7);
    Text(L"◈", g->fB, { L.side.x + 12, y, 26, 26 }, C.label, true, true);
    Text(L"Dainric", g->fB, { L.side.x + 46, y - 4, 120, 18 }, C.label);
    Text(L"打包器 3.2.0", g->fC, { L.side.x + 46, y + 11, 120, 14 },
         C.label3);
    y += 46;

    for (int i = 0; i < kNavCount; ++i)
    {
        Rect r{ L.side.x + 12, y, L.side.w - 24, 36 };
        if (i == g->page) FillRect(r, C.blue, kRadBtn);
        else if (g->hotNav == i) FillRect(r, C.s1, kRadBtn);
        Text(kNav[i], g->fB, { r.x + 12, r.y, r.w - 20, r.h },
             i == g->page ? C.label : C.label2);
        g->navHits.push_back({ r, i });
        y += 39;
    }

    // 底部引擎状态
    float by = L.side.y + L.side.h - 66;
    HLine(L.side.x + 12, by, L.side.w - 24, C.hair);
    bool ok = !g->enginePrefix.empty();
    Text(L"引擎", g->fC, { L.side.x + 14, by + 8, 60, 18 }, C.label3);
    Text(ok ? L"就绪" : L"缺失", g->fC,
         { L.side.x + L.side.w - 70, by + 8, 56, 18 },
         ok ? C.green : C.pink, true, true);
    Text(g->engineDesc, g->fC, { L.side.x + 14, by + 26, L.side.w - 30, 16 },
         C.label3);
    Text(L"运行时 3.12 · 3.14", g->fC,
         { L.side.x + 14, by + 43, L.side.w - 30, 16 }, C.label3);

    HLine(L.side.w - 1, L.side.y, 1, C.hair);
}

/// 卡片标题行
void CardHead(int id, float x, float y, float w, const wchar_t* title,
              const std::wstring& sub, bool open)
{
    Text(open ? L"▼" : L"▶", g->fC, { x + 14, y, 14, kHeadH }, C.label3);
    Text(title, g->fH, { x + 34, y, 260, kHeadH }, C.label);
    if (!sub.empty())
        Text(sub, g->fC, { x + w - 240, y, 224, kHeadH }, C.label3, true, false);
    g->cardHits.push_back({ { x, y, w, kHeadH }, id });
}

/// 一行：左标签，右侧内容由回调画。返回下一行 Y。
float Row(float x, float y, float w, const wchar_t* label,
          const std::function<void(float rx, float ry, float rw)>& right)
{
    Text(label, g->fB, { x, y, w - 20, kRowH }, C.label);
    float rx = x + w * 0.46f;
    float rw = w * 0.54f;
    if (right) right(rx, y, rw);
    HLine(x, y + kRowH - 1, w, C.hair);
    return y + kRowH;
}

struct BtnStyle
{
    D2D1_COLOR_F bg;
    D2D1_COLOR_F fg;
    bool bold = false;
};

void Btn(int id, const Rect& r, const wchar_t* text, const BtnStyle& st)
{
    FillRect(r, (g->hotBtn == id) ? A(st.bg, 0.82f) : st.bg, kRadBtn);
    Text(text, st.bold ? g->fB : g->fS, r, st.fg, true, true);
    g->btnHits.push_back({ r, id });
}

void Switch(int id, float x, float y, bool on)
{
    Rect r{ x, y, 47, 28 };
    FillRect(r, on ? C.green : C.s3, 14);
    FillRect({ x + (on ? 21.0f : 2.0f), y + 2, 24, 24 },
             D2D1::ColorF(1, 1, 1, 1), 12);
    g->swHits.push_back({ r, id });
}

// ── 单文件页 ──

void DrawScriptCard(const Layout& L, float padX, float w, float& y)
{
    Rect card{ padX, y, w, 0 };
    float subH = g->script.empty() ? 92.0f : 72.0f;
    card.h = kHeadH + subH + 14;
    FillRect(card, C.s1, kRadCard);
    CardHead(1, padX, y, w, L"脚本",
             g->script.empty() ? L"未选择" : FileNameOf(g->script), true);

    Rect drop{ padX + 16, y + kHeadH + 2, w - 32, subH };
    if (g->script.empty())
    {
        FillRect(drop, C.s2, 12);
        StrokeRect(drop, A(C.blue, 0.35f), 12, 1.5f);
        Text(L"点击选择 .py 文件", g->fB,
             { drop.x, drop.y + 16, drop.w, 22 }, C.label, true, true);
        Text(L"拖入 .py 文件即可载入，支持从资源管理器直接拖拽", g->fC,
             { drop.x, drop.y + 44, drop.w, 20 }, C.label3, true, true);
    }
    else
    {
        FillRect(drop, C.s2, 12);
        FillRect({ drop.x + 14, drop.y + 19, 34, 34 }, A(C.blue, 0.16f), 9);
        Text(L"🐍", g->fB, { drop.x + 14, drop.y + 19, 34, 34 }, C.blue,
             true, true);
        std::wstring dir = g->script;
        size_t s = dir.find_last_of(L"\\/");
        if (s != std::wstring::npos) dir = dir.substr(0, s);
        Text(FileNameOf(g->script), g->fB,
             { drop.x + 60, drop.y + 14, drop.w - 210, 20 }, C.label);
        Text(dir, g->fM, { drop.x + 60, drop.y + 34, drop.w - 210, 18 },
             C.label3);
        Btn(10, { drop.x + drop.w - 92, drop.y + 21, 78, 30 }, L"更换",
            { D2D1::ColorF(0, 0, 0, 0), C.blue, false });
    }
    g->dropZone = drop;
    g->hasDrop = true;
    y = card.y + card.h + 12;
}

void DrawOutputCard(const Layout& L, float padX, float w, float& y)
{
    Rect card{ padX, y, w, kHeadH + 2 + kRowH * 4 + 12 };
    FillRect(card, C.s1, kRadCard);
    CardHead(2, padX, y, w, L"输出",
             g->ver.version.empty() ? L"未设置版本" : g->ver.version, true);

    float cy = y + kHeadH + 2;
    cy = Row(padX + 16, cy, w - 32, L"产物名称",
             [&](float rx, float ry, float rw) {
                 InputBox(100, rx, ry, rw, g->Val(100), L"与脚本同名");
             });
    cy = Row(padX + 16, cy, w - 32, L"版本",
             [&](float rx, float ry, float rw) {
                 Text(g->ver.version, g->fS, { rx, ry, rw - 74, kRowH },
                      C.label, true, false);
                 Btn(11, { rx + rw - 66, ry + 6, 66, kRowH - 12 }, L"设置",
                     { D2D1::ColorF(0, 0, 0, 0), C.blue, false });
             });
    cy = Row(padX + 16, cy, w - 32, L"输出目录",
             [&](float rx, float ry, float rw) {
                 InputBox(101, rx, ry, rw - 74, g->Val(101, g->outDir),
                          L"选择目录", true);
                 Btn(12, { rx + rw - 66, ry + 6, 66, kRowH - 12 }, L"浏览",
                     { D2D1::ColorF(0, 0, 0, 0), C.blue, false });
             });
    cy = Row(padX + 16, cy, w - 32, L"图标",
             [&](float rx, float ry, float rw) {
                 Text(g->iconPath.empty() ? L"使用内置图标"
                                          : FileNameOf(g->iconPath),
                      g->fS, { rx, ry, rw - 74, kRowH },
                      g->iconPath.empty() ? C.label3 : C.label, true, false);
                 Btn(13, { rx + rw - 66, ry + 6, 66, kRowH - 12 }, L"选择",
                     { D2D1::ColorF(0, 0, 0, 0), C.blue, false });
             });
    y = card.y + card.h + 12;
}

void DrawOptionsCard(const Layout& L, float padX, float w, float& y)
{
    std::wstring sub = g->swSelfTest ? L"打包后自检" : L"不自检";
    Rect card{ padX, y, w, kHeadH + 2 + kRowH * 2 + 12 };
    FillRect(card, C.s1, kRadCard);
    CardHead(3, padX, y, w, L"构建选项", sub, true);

    float cy = y + kHeadH + 2;
    cy = Row(padX + 16, cy, w - 32, L"打包后自检产物",
             [&](float rx, float ry, float rw) {
                 Switch(20, rx + rw - 47, ry + 7, g->swSelfTest);
             });
    cy = Row(padX + 16, cy, w - 32, L"打包前检查依赖",
             [&](float rx, float ry, float rw) {
                 Switch(21, rx + rw - 47, ry + 7, g->swCheckDeps);
             });
    y = card.y + card.h + 12;
}

void DrawAdvancedCard(const Layout& L, float padX, float w, float& y)
{
    std::wstring sub = g->advOpen ? L"点击收起" : L"默认折叠 · 5 项";
    int rows = g->advOpen ? 5 : 0;
    Rect card{ padX, y, w, kHeadH + 2 + kRowH * rows + (g->advOpen ? 12.0f : 0.0f) };
    FillRect(card, C.s1, kRadCard);
    CardHead(4, padX, y, w, L"高级选项", sub, g->advOpen);
    if (!g->advOpen) { y = card.y + card.h; return; }

    float cy = y + kHeadH + 2;
    cy = Row(padX + 16, cy, w - 32, L"管理员提权 (UAC)",
             [&](float rx, float ry, float rw) {
                 Switch(22, rx + rw - 47, ry + 7, g->swUac);
             });
    cy = Row(padX + 16, cy, w - 32, L"清理临时文件",
             [&](float rx, float ry, float rw) {
                 Switch(23, rx + rw - 47, ry + 7, g->swClean);
             });
    cy = Row(padX + 16, cy, w - 32, L"自检参数",
             [&](float rx, float ry, float rw) {
                 InputBox(102, rx, ry, rw, g->Val(102),
                          L"留空即可，产物需参数时才填");
             });
    cy = Row(padX + 16, cy, w - 32, L"打包超时",
             [&](float rx, float ry, float rw) {
                 InputBox(103, rx + rw - 96, ry, 96, g->Val(103, L"1800"), L"");
                 Text(L"秒", g->fS, { rx + rw - 58, ry, 60, kRowH },
                      C.label3, true, false);
             });
    cy = Row(padX + 16, cy, w - 32, L"依赖",
             [&](float rx, float ry, float rw) {
                 Btn(30, { rx + rw - 200, ry + 7, 92, kRowH - 14 },
                     L"检查依赖",
                     { D2D1::ColorF(0, 0, 0, 0), C.blue, false });
                 Btn(31, { rx + rw - 100, ry + 7, 100, kRowH - 14 },
                     L"安装缺失依赖", { C.s2, C.label, false });
             });
    y = card.y + card.h;
}

void DrawSingle(const Layout& L)
{
    float padX = L.form.x + kPadX;
    float w = L.form.w - kPadX * 2;
    float y = L.form.y + 18 - g->scroll;
    float startY = y;

    DrawScriptCard(L, padX, w, y);
    DrawOutputCard(L, padX, w, y);
    DrawOptionsCard(L, padX, w, y);
    DrawAdvancedCard(L, padX, w, y);

    g->contentH = (y + g->scroll) - startY + 18;
}

// ── 依赖页 ──

/// 状态徽章：小圆角标签
void Badge(float x, float y, const wchar_t* text, const D2D1_COLOR_F& bg,
           const D2D1_COLOR_F& fg)
{
    float w = 64, h = 20;
    FillRect({ x, y, w, h }, bg, 10);
    Text(text, g->fC, { x, y, w, h }, fg, true, true);
}

void DrawDeps(const Layout& L)
{
    float padX = L.form.x + kPadX;
    float w = L.form.w - kPadX * 2;
    float y = L.form.y + 18 - g->scroll;
    float startY = y;

    // 有检查结果就画表格，否则画说明
    bool hasTable = !g->depEntries.empty();
    int rows = hasTable ? (int)g->depEntries.size() : 0;
    float bodyH = hasTable ? (float)rows * 34.0f + 12
                           : TextH(g->depNote, g->fM, w - 64) + 28;
    Rect card{ padX, y, w, kHeadH + 2 + bodyH + 12 };
    FillRect(card, C.s1, kRadCard);

    std::wstring sub = g->script.empty() ? L"未选择脚本"
                                         : FileNameOf(g->script);
    if (hasTable)
    {
        int miss = 0;
        for (const auto& e : g->depEntries) if (e.Missing()) ++miss;
        sub += miss ? L" · " + std::to_wstring(miss) + L" 个缺失"
                    : L" · 全部可用";
    }
    CardHead(-1, padX, y, w, L"依赖检查", sub, true);

    if (!hasTable)
    {
        Rect box{ padX + 16, y + kHeadH + 2, w - 32, bodyH };
        FillRect(box, C.s2, 10);
        Text(g->depNote, g->fM, { box.x + 14, box.y, box.w - 28, box.h },
             C.label2, false, false);
        g->contentH = card.h + 18;
        return;
    }

    float ry = y + kHeadH + 8;
    for (const auto& e : g->depEntries)
    {
        // 只列第三方与缺失；标准库/本地折叠成一行统计
        if (e.kind == L"标准库" || e.kind == L"本地") continue;
        Text(e.module, g->fM, { padX + 20, ry, w * 0.5f, 34 }, C.label);
        if (e.Missing())
            Badge(padX + w - 100, ry + 7, L"缺失", C.s3, C.pink);
        else
            Badge(padX + w - 100, ry + 7, L"已安装", C.green,
                  D2D1::ColorF(1, 1, 1, 1));
        HLine(padX + 16, ry + 33, w - 32, C.hair);
        ry += 34;
    }

    int stdN = 0, locN = 0;
    for (const auto& e : g->depEntries)
    {
        if (e.kind == L"标准库") ++stdN;
        if (e.kind == L"本地") ++locN;
    }
    ry += 4;
    Text(L"标准库 " + std::to_wstring(stdN) + L" 个 · 本地模块 "
         + std::to_wstring(locN) + L" 个", g->fC,
         { padX + 20, ry, w - 40, 20 }, C.label3, true, false);

    g->contentH = card.h + 18;
}

// ── 批量页 ──

void DrawBatch(const Layout& L)
{
    float padX = L.form.x + kPadX;
    float w = L.form.w - kPadX * 2;
    float y = L.form.y + 18;

    Rect card{ padX, y, w, kHeadH + 2 + 90 + 12 };
    FillRect(card, C.s1, kRadCard);
    CardHead(-1, padX, y, w, L"批量打包", L"尚未开始", true);

    Rect drop{ padX + 16, y + kHeadH + 2, w - 32, 90 };
    FillRect(drop, C.s2, 12);
    StrokeRect(drop, A(C.blue, 0.35f), 12, 1.5f);
    Text(L"把多个 .py 一起拖进来", g->fB,
         { drop.x, drop.y + 20, drop.w, 22 }, C.label, true, true);
    Text(L"每个脚本各自独立打包，一个失败不影响后面的", g->fC,
         { drop.x, drop.y + 50, drop.w, 20 }, C.label3, true, true);

    g->contentH = card.h + 18;
}

// ── 历史页 ──

void BadgeColored(float x, float y, const wchar_t* text,
                  const D2D1_COLOR_F& fg)
{
    FillRect({ x, y, 64, 20 }, C.s3, 10);
    Text(text, g->fC, { x, y, 64, 20 }, fg, true, true);
}

void DrawHistory(const Layout& L)
{
    float padX = L.form.x + kPadX;
    float w = L.form.w - kPadX * 2;
    float y = L.form.y + 18 - g->scroll;
    float startY = y;

    int n = (int)g->history.size();
    float bodyH = n ? (float)n * 34.0f + 12 : 60;
    Rect card{ padX, y, w, kHeadH + 2 + bodyH + 12 };
    FillRect(card, C.s1, kRadCard);
    CardHead(-1, padX, y, w, L"打包历史",
             n ? std::to_wstring(n) + L" 条记录" : L"还没有记录", true);

    if (n == 0)
    {
        Text(L"打包完成后这里会留下记录（时间 / 名称 / 结果 / 耗时）。",
             g->fC, { padX + 20, y + kHeadH + 10, w - 40, 40 }, C.label3,
             false, false);
        g->contentH = card.h + 18;
        return;
    }

    float ry = y + kHeadH + 8;
    for (const auto& it : g->history)
    {
        if (ry > y + card.h) break;
        Text(it.time, g->fM, { padX + 20, ry, 130, 34 }, C.label3);
        Text(it.name, g->fB, { padX + 156, ry, w * 0.42f, 34 }, C.label);
        wchar_t buf[32];
        swprintf_s(buf, L"%.1fs", it.seconds);
        Text(buf, g->fC, { padX + w - 200, ry, 70, 34 }, C.label3);

        D2D1_COLOR_F fg = (it.result == L"成功") ? C.green : C.pink;
        BadgeColored(padX + w - 116, ry + 7, it.result.c_str(), fg);

        HLine(padX + 16, ry + 33, w - 32, C.hair);
        ry += 34;
    }
    g->contentH = card.h + 18;
}

// ── 运行时页 ──

void DrawRuntime(const Layout& L)
{
    float padX = L.form.x + kPadX;
    float w = L.form.w - kPadX * 2;
    float y = L.form.y + 18;

    float boxH = TextH(g->rtSummary, g->fB, w - 64) + 28;
    Rect card{ padX, y, w, kHeadH + 2 + boxH + 12 };
    FillRect(card, C.s1, kRadCard);
    CardHead(-1, padX, y, w, L"打包运行时",
             L"两个运行时都已随包提供", true);

    Rect box{ padX + 16, y + kHeadH + 2, w - 32, boxH };
    FillRect(box, C.s2, 10);
    Text(g->rtSummary, g->fB, { box.x + 16, box.y, box.w - 32, box.h },
         C.label2, false, false);
    g->contentH = card.h + 18;
}

// ── 日志列 ──

void DrawLog(const Layout& L)
{
    if (L.logCol.w <= 0) return;
    FillRect(L.logCol, C.logBg);
    HLine(L.logCol.x, L.logCol.y, 1, C.hair);

    float x = L.logCol.x + 16;
    float w = L.logCol.w - 32;
    float y = L.logCol.y + 18;

    Text(L"输出日志", g->fB, { x, y, 120, 20 }, C.label);
    Rect pill{ x + w - 78, y, 78, 20 };
    FillRect(pill, C.s2, 10);
    Text(g->busy ? L"实时" : L"等待开始", g->fC, pill,
         g->busy ? C.blue : C.label2, true, true);
    y += 30;

    std::vector<LogLine> snap;
    {
        std::lock_guard<std::mutex> lk(g->logMx);
        snap = g->logs;
    }
    const float lineH = 20.0f;
    int maxLines = (int)((L.logCol.y + L.logCol.h - y - 8) / lineH);
    if (maxLines <= 0) return;
    int from = (int)snap.size() > maxLines ? (int)snap.size() - maxLines : 0;

    for (int i = from; i < (int)snap.size(); ++i)
    {
        const LogLine& l = snap[i];
        D2D1_COLOR_F c = C.label3;
        switch (l.level)
        {
        case Lv::Ok:   c = C.green;  break;
        case Lv::Warn: c = C.orange; break;
        case Lv::Err:  c = C.pink;   break;
        case Lv::Acc:  c = C.blue;   break;
        default:       c = C.label3; break;
        }
        bool showTime = (i == from) || (snap[i].time != snap[i - 1].time);
        if (showTime)
            Text(l.time, g->fM, { x, y, 46, lineH },
                 D2D1::ColorF(0.29f, 0.29f, 0.31f, 1), true, false);
        Text(l.text, g->fM, { x + 50, y, w - 50, lineH }, c, true, false);
        y += lineH;
        if (y > L.logCol.y + L.logCol.h - 8) break;
    }
}

// ── 底部动作条 ──

void DrawActions(const Layout& L)
{
    FillRect(L.act, C.logBg);
    HLine(0, L.act.y, L.W, C.hair);

    float cy = L.act.y + L.act.h / 2;
    Text(L"当前生效 " + g->engineDesc, g->fS,
         { 20, L.act.y, 360, L.act.h }, C.label2, true, false);

    Rect pb{ 396, cy - 2, 180, 4 };
    FillRect(pb, C.s2, 2);
    if (g->busy) FillRect({ pb.x, pb.y, pb.w * 0.6f, pb.h }, C.blue, 2);

    float rx = L.W - 20;
    Btn(1, { rx - 120, cy - 18, 120, 36 },
        g->busy ? L"打包中…" : L"开始打包",
        { C.blue, D2D1::ColorF(1, 1, 1, 1), true });
    rx -= 130;

    Btn(3, { rx - 130, cy - 18, 130, 36 }, L"打开产物目录",
        { C.s2, C.label, false });
    rx -= 140;

    if (g->busy)
        Btn(2, { rx - 90, cy - 18, 90, 36 }, L"停止",
            { A(C.pink, 0.15f), C.pink, false });
    else
        Text(g->statusSub, g->fC, { rx - 220, L.act.y, 220, L.act.h },
             C.label3, true, false);
}

// ══════════════════════ 版本信息对话框 ══════════════════════

/// 模态覆盖层：五栏版本信息。
/// 自己画而不是弹系统对话框：系统没有这种「多字段 + 深色 iOS 观感」的现成件，
/// 而且版本资源里的字段本来就这几项，手写更直接。
void DrawVersionDialog(const Layout& L)
{
    // 遮罩
    FillRect({ 0, 0, L.W, L.H }, D2D1::ColorF(0, 0, 0, 0.55f));

    float dw = 460, dh = 396;
    Rect dlg{ (L.W - dw) / 2, (L.H - dh) / 2, dw, dh };
    FillRect(dlg, C.s1, 16);
    StrokeRect(dlg, C.s3, 16, 1.0f);

    Text(L"版本信息", g->fT, { dlg.x + 22, dlg.y + 16, 200, 26 }, C.label);
    Text(L"写入产物属性的文件版本 / 产品名称等字段",
         g->fC, { dlg.x + 22, dlg.y + 42, dw - 44, 18 }, C.label3);
    HLine(dlg.x + 22, dlg.y + 66, dw - 44, C.hair);

    struct F { int id; const wchar_t* label; const wchar_t* ph; };
    const F fields[] = {
        { 200, L"版本号",   L"1.0.0（1 到 4 段数字）" },
        { 201, L"产品名称", L"留空则用产物名" },
        { 202, L"公司",     L"Dainric" },
        { 203, L"文件描述", L"留空则用产品名" },
        { 204, L"版权",     L"留空则用公司名" },
    };

    float fy = dlg.y + 78;
    for (const auto& f : fields)
    {
        Rect lab{ dlg.x + 22, fy, 92, 32 };
        Text(f.label, g->fB, lab, C.label2, true, false);

        Rect ib{ dlg.x + 118, fy + 3, dw - 140, 26 };
        InputBox(f.id, ib.x, fy, ib.w, g->Val(f.id), f.ph);

        // 版本号格式不对时给红框提示，而不是等点确定才报错
        if (f.id == 200 && !g->Val(200).empty()
            && !IsValidVersion(g->Val(200)))
            StrokeRect(ib, C.pink, kRadIn, 1.2f);

        fy += 36;
    }

    if (!g->Val(200).empty() && !IsValidVersion(g->Val(200)))
        Text(L"版本号格式不对：应为 1 / 1.0 / 1.0.0 / 1.0.0.0，每段不超过 65535",
             g->fC, { dlg.x + 22, fy - 2, dw - 44, 18 }, C.pink);

    float by = dlg.y + dh - 50;
    Btn(210, { dlg.x + dw - 190, by, 84, 34 }, L"取消",
        { C.s2, C.label, false });
    Btn(211, { dlg.x + dw - 98, by, 76, 34 }, L"确定",
        { C.blue, D2D1::ColorF(1, 1, 1, 1), true });
}

// ══════════════════════ 渲染 ══════════════════════

void Render()
{
    if (!g || !g->rt) return;
    PAINTSTRUCT ps;
    BeginPaint(g->hwnd, &ps);

    g->rt->BeginDraw();
    g->rt->Clear(C.bg);

    Layout L = MakeLayout();
    g->navHits.clear();
    g->cardHits.clear();
    g->btnHits.clear();
    g->swHits.clear();
    g->inputs.clear();
    g->inputHits.clear();
    g->hasDrop = false;

    DrawTitle(L);
    DrawSide(L);

    g->rt->PushAxisAlignedClip(L.form.D2(),
                               D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
    switch (g->page)
    {
    case 0: DrawSingle(L);  break;
    case 1: DrawDeps(L);    break;
    case 2: DrawBatch(L);   break;
    case 3: DrawHistory(L); break;
    default: DrawRuntime(L); break;
    }
    g->rt->PopAxisAlignedClip();

    DrawLog(L);
    DrawActions(L);

    // 对话框最后画，盖在所有内容之上
    if (g->verDlgOpen) DrawVersionDialog(L);

    if (g->rt->EndDraw() == D2DERR_RECREATE_TARGET)
    {
        g->rt->Release();
        g->rt = nullptr;
    }
    EndPaint(g->hwnd, &ps);

    // 临时探针：画在窗口内部而不是改标题。
    // 改标题会让 Windows 按标题长度调整标题栏高度，客户区尺寸随之变化，
    // 布局跟着漂 —— 探针不能在观测时改变被测对象。
    // DAINRIC_UITRACE=1 时才生效，排查完可整段删除。
    if (GetEnvironmentVariableW(L"DAINRIC_UITRACE", nullptr, 0) > 0)
    {
        std::wstring btn;
        for (const auto& b : g->btnHits)
        {
            wchar_t one[64];
            swprintf_s(one, L"%d@%.0f,%.0f ", b.second, b.first.x, b.first.y);
            btn += one;
        }
        wchar_t tb[400];
        swprintf_s(tb, L"TRACE pg=%d fc=%d cr=%d dlg=%d v100=%s ver=%s btn %s",
                   g->page, g->focusInput, (int)g->caret,
                   g->verDlgOpen ? 1 : 0,
                   g->values.count(100) ? g->values[100].c_str() : L"-",
                   g->ver.version.c_str(), btn.c_str());
        // 半透明底条 + 文本，只占最底一行，不动布局
        FillRect({ 0, L.H - kActH - 20, L.W, 20 },
                 D2D1::ColorF(0, 0, 0, 0.85f));
        Text(tb, g->fC, { 6, L.H - kActH - 20, L.W - 12, 20 },
             D2D1::ColorF(0.2f, 1.0f, 0.4f, 1), true, false);
    }
}

// ══════════════════════ 操作 ══════════════════════

void InstallDepsDone();

void DoCheckDeps()
{
    if (g->script.empty())
    {
        g->depEntries.clear();
        g->depNote = L"先在「单文件打包」里选一个脚本。";
        g->page = 1;
        Invalidate();
        return;
    }
    std::wstring py;
    for (auto& s : RuntimeManager::Discover())
        if (s.present) { py = s.pythonExe; break; }
    if (py.empty())
    {
        g->depEntries.clear();
        g->depNote = L"没有可用的 Python 运行时。";
        g->page = 1;
        Invalidate();
        return;
    }
    LogAdd(L"[*] 依赖检查…", Lv::Dim);
    SetStatus(L"依赖检查中…", FileNameOf(g->script));

    DepReport rep = CheckDependencies(g->script, py, g->runner);
    g->depEntries = rep.entries;
    g->depNote = rep.ran ? L"检查完成" : (L"检查未完成：" + rep.skipReason);
    for (const auto& line : SplitLines(rep.RenderBrief()))
        if (!line.empty()) LogAdd(line, rep.Clean() ? Lv::Ok : Lv::Warn);

    SetStatus(rep.Clean() ? L"依赖齐全" : L"依赖缺失", FileNameOf(g->script));
    g->page = 1;
    g->scroll = 0;
    Invalidate();
}

/// 一键安装缺失依赖。
/// 走与打包相同的运行时，用阿里云镜像（国内可直连）。
void InstallDeps()
{
    if (g->busy) return;

    std::vector<std::wstring> missing;
    for (const auto& e : g->depEntries)
        if (e.Missing()) missing.push_back(e.module);

    if (missing.empty())
    {
        MessageBoxW(g->hwnd,
                    L"还没有检测到缺失依赖。\n先点「检查依赖」。",
                    L"Dainric", MB_OK | MB_ICONINFORMATION);
        return;
    }

    std::wstring py;
    for (auto& s : RuntimeManager::Discover())
        if (s.present && !s.pyiVersion.empty()) { py = s.pythonExe; break; }
    if (py.empty())
        for (auto& s : RuntimeManager::Discover())
            if (s.present) { py = s.pythonExe; break; }
    if (py.empty())
    {
        MessageBoxW(g->hwnd, L"没有可用的 Python 运行时，无法安装。",
                    L"Dainric", MB_OK | MB_ICONWARNING);
        return;
    }

    std::wstring names;
    for (size_t i = 0; i < missing.size(); ++i)
        names += (i ? L" " : L"") + missing[i];

    std::wstring msg = L"将使用内置运行时安装：\n\n" + names
                     + L"\n\n来源：阿里云 PyPI 镜像";
    if (MessageBoxW(g->hwnd, msg.c_str(), L"安装依赖",
                    MB_OKCANCEL | MB_ICONQUESTION) != IDOK)
        return;

    g->busy = true;
    SetStatus(L"安装依赖中…", names);
    LogAdd(L"[*] 安装依赖: " + names, Lv::Acc);
    LogAdd(L"[*] 运行时: " + py, Lv::Dim);

    std::vector<std::wstring> pkgs = missing;
    std::wstring exe = py;
    std::thread([exe, pkgs]() {
        Cmd c;
        c.exe = exe;
        c.args = { L"-m", L"pip", L"install" };
        for (const auto& p : pkgs) c.args.push_back(p);
        c.args.push_back(L"-i");
        c.args.push_back(L"https://mirrors.aliyun.com/pypi/simple/");
        c.args.push_back(L"--disable-pip-version-check");
        c.timeoutMs = 900000;

        Runner r;
        RunResult res = r.Run(c, [](const std::wstring& line) {
            LogAdd(line, (line.find(L"ERROR") != std::wstring::npos
                          || line.find(L"error") != std::wstring::npos)
                         ? Lv::Err : Lv::Dim);
        });

        if (res.exitCode == 0)
        {
            LogAdd(L"[+] 依赖安装完成", Lv::Ok);
            SetStatus(L"依赖已安装", L"");
            InstallDepsDone();
        }
        else
        {
            LogAdd(L"[-] 安装失败，退出码 " + std::to_wstring(res.exitCode),
                   Lv::Err);
            for (const auto& line : SplitLines(res.err))
                if (!line.empty()) LogAdd(line, Lv::Err);
            SetStatus(L"安装失败", L"见日志");
        }
        g->busy = false;
        Invalidate();
    }).detach();
}

/// 安装成功后复检一次，把表格刷新成最新状态
void InstallDepsDone()
{
    std::wstring py;
    for (auto& s : RuntimeManager::Discover())
        if (s.present) { py = s.pythonExe; break; }
    if (py.empty() || g->script.empty()) return;
    Runner r;
    DepReport rep = CheckDependencies(g->script, py, r);
    g->depEntries = rep.entries;
    LogAdd(rep.RenderBrief(), rep.Clean() ? Lv::Ok : Lv::Warn);
}

void PickScript()
{
    wchar_t buf[MAX_PATH * 4] = { 0 };
    OPENFILENAMEW ofn{};
    ofn.lStructSize = sizeof(ofn);
    ofn.hwndOwner = g->hwnd;
    ofn.lpstrFilter = L"Python 脚本\0*.py\0所有文件\0*.*\0";
    ofn.lpstrFile = buf;
    ofn.nMaxFile = MAX_PATH * 4;
    ofn.lpstrTitle = L"选择 Python 脚本";
    ofn.Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR;
    if (!GetOpenFileNameW(&ofn)) return;

    g->script = buf;
    g->Val(100) = BaseNameOf(g->script);
    LogAdd(L"脚本: " + g->script, Lv::Acc);
    Invalidate();
}

void PickOutDir()
{
    BROWSEINFOW bi{};
    bi.hwndOwner = g->hwnd;
    bi.lpszTitle = L"选择输出目录";
    bi.ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE;
    LPITEMIDLIST idl = SHBrowseForFolderW(&bi);
    if (!idl) return;
    wchar_t buf[MAX_PATH * 2] = { 0 };
    if (SHGetPathFromIDListW(idl, buf)) { g->outDir = buf; g->Val(101) = buf; }
    CoTaskMemFree(idl);
    Invalidate();
}

void PickIcon()
{
    wchar_t buf[MAX_PATH * 4] = { 0 };
    OPENFILENAMEW ofn{};
    ofn.lStructSize = sizeof(ofn);
    ofn.hwndOwner = g->hwnd;
    ofn.lpstrFilter = L"图标\0*.ico\0所有文件\0*.*\0";
    ofn.lpstrFile = buf;
    ofn.nMaxFile = MAX_PATH * 4;
    ofn.lpstrTitle = L"选择图标";
    ofn.Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR;
    if (GetOpenFileNameW(&ofn)) { g->iconPath = buf; Invalidate(); }
}

void OpenOutDir()
{
    std::wstring d = g->outDir.empty() ? DesktopOutDir() : g->outDir;
    if (d.empty()) return;
    CreateDirectoryW(d.c_str(), nullptr);
    ShellExecuteW(nullptr, L"open", d.c_str(), nullptr, nullptr,
                  SW_SHOWNORMAL);
}

std::vector<std::wstring> ResolveEngine(std::wstring& desc)
{
    for (auto& s : RuntimeManager::Discover())
    {
        if (!s.present) continue;
        RuntimeManager::FillDetails(s, g->runner);
        if (!s.pyiVersion.empty())
        {
            desc = s.title + L" · PyInstaller " + s.pyiVersion;
            return { s.pythonExe, L"-m", L"PyInstaller" };
        }
    }
    desc = L"未检测到可用运行时";
    return {};
}

void RunBuild()
{
    if (g->busy) return;
    if (g->script.empty())
    {
        MessageBoxW(g->hwnd, L"请先选择一个 .py 脚本。", L"Dainric",
                    MB_OK | MB_ICONINFORMATION);
        return;
    }
    if (g->enginePrefix.empty())
    {
        MessageBoxW(g->hwnd,
                    L"没有可用的打包引擎。\n\n"
                    L"请确认运行时目录存在"
                    L"（安装版在 %LOCALAPPDATA%\\Dainric）。",
                    L"Dainric", MB_OK | MB_ICONWARNING);
        return;
    }

    std::wstring outName = g->Val(100);
    if (outName.empty()) outName = BaseNameOf(g->script);
    if (outName.empty())
    {
        MessageBoxW(g->hwnd, L"产物名称为空。", L"Dainric",
                    MB_OK | MB_ICONINFORMATION);
        return;
    }
    int timeout = 1800;
    try { timeout = std::stoi(g->Val(103, L"1800")); } catch (...) { timeout = 1800; }
    if (timeout < 10 || timeout > 86400) timeout = 1800;

    std::wstring outDir = g->Val(101);
    if (outDir.empty()) outDir = g->outDir.empty() ? DesktopOutDir() : g->outDir;

    BuildOptions o;
    o.script = g->script;
    o.outName = outName;
    o.outDir = outDir;
    o.windowMode = L"auto";
    o.uac = g->swUac;
    o.clean = g->swClean;
    o.testRun = g->swSelfTest;
    o.checkDeps = g->swCheckDeps;
    o.timeoutSec = timeout;
    o.iconPath = g->iconPath;
    o.enginePrefix = g->enginePrefix;
    o.testArgs = SplitArgs(g->Val(102));

    // 版本号合法才生成版本资源文件
    if (IsValidVersion(g->ver.version))
    {
        VersionInfo vi = g->ver;
        vi.product = g->Val(201).empty() ? outName : g->Val(201);
        vi.company = g->Val(202).empty() ? L"Dainric" : g->Val(202);
        vi.description = g->Val(203);
        vi.copyright = g->Val(204);

        wchar_t tmp[MAX_PATH * 2] = { 0 };
        GetTempPathW(MAX_PATH * 2, tmp);
        std::wstring vf = WriteVersionFile(vi, outName, tmp);
        if (!vf.empty())
        {
            o.versionFile = vf;
            o.tempFiles.push_back(vf);
            LogAdd(L"[*] 版本资源: " + vi.version, Lv::Dim);
        }
    }

    CreateDirectoryW(o.outDir.c_str(), nullptr);

    g->busy = true;
    SetStatus(L"打包中…", o.outName);
    LogAdd(L"[*] 开始打包 " + o.outName, Lv::Acc);
    Invalidate();

    std::thread([o]() {
        ULONGLONG t0 = GetTickCount64();
        bool ok = g->packer->Build(o);
        double sec = (GetTickCount64() - t0) / 1000.0;

        // 清掉本次生成的临时版本文件
        for (const auto& f : o.tempFiles) DeleteFileW(f.c_str());

        HistoryItem it;
        it.time = Now();
        it.name = o.outName;
        it.result = ok ? L"成功" : L"失败";
        it.seconds = sec;
        AppendHistory(it);
        g->history = LoadHistory();

        g->busy = false;
        SetStatus(ok ? L"打包完成" : L"打包失败",
                  ok ? L"产物已生成" : L"见日志诊断");
        Invalidate();
    }).detach();
}

// ══════════════════════ 命中与事件 ══════════════════════

enum class HitKind { None, Nav, Card, Button, Switch, Drop };

struct Hit
{
    HitKind kind = HitKind::None;
    int id = -1;
};

void HitTest(float x, float y, Hit& out)
{
    out = { HitKind::None, -1 };
    for (auto& n : g->navHits)
        if (n.first.Hit(x, y)) { out = { HitKind::Nav, n.second }; return; }
    for (auto& b : g->btnHits)
        if (b.first.Hit(x, y)) { out = { HitKind::Button, b.second }; return; }
    for (auto& s : g->swHits)
        if (s.first.Hit(x, y)) { out = { HitKind::Switch, s.second }; return; }
    for (auto& c : g->cardHits)
        if (c.first.Hit(x, y)) { out = { HitKind::Card, c.second }; return; }
    if (g->hasDrop && g->dropZone.Hit(x, y))
        out = { HitKind::Drop, 0 };
}

void ClickButton(int id)
{
    switch (id)
    {
    case 1: RunBuild(); break;
    case 2:
        if (g->packer) g->packer->Abort();
        LogAdd(L"[*] 已请求停止", Lv::Warn);
        break;
    case 3: OpenOutDir(); break;
    case 10: PickScript(); break;
    case 11:
        // 打开版本对话框，字段预填当前值
        if (g->Val(200).empty()) g->Val(200) = g->ver.version;
        if (g->Val(202).empty()) g->Val(202) = L"Dainric";
        g->verDlgOpen = true;
        g->focusInput = 200;
        g->caret = g->Val(200).size();
        g->selStart = 0;                 // 全选，方便直接覆盖
        g->caretTick = GetTickCount();
        g->caretOn = true;
        Invalidate();
        break;
    case 12: PickOutDir(); break;
    case 13: PickIcon(); break;
    case 30: DoCheckDeps(); break;
    case 31: InstallDeps(); break;

    // ── 版本对话框 ──
    case 210:                        // 取消
        g->verDlgOpen = false;
        g->focusInput = -1;
        Invalidate();
        break;
    case 211:                        // 确定
    {
        std::wstring v = g->Val(200);
        // 去掉首尾空白
        size_t a = v.find_first_not_of(L" \t");
        size_t b = v.find_last_not_of(L" \t");
        v = (a == std::wstring::npos) ? L"" : v.substr(a, b - a + 1);
        if (!IsValidVersion(v))
        {
            MessageBoxW(g->hwnd,
                        L"版本号格式不对。\n\n"
                        L"应为 1 / 1.0 / 1.0.0 / 1.0.0.0，"
                        L"每段是 0 到 65535 的数字。",
                        L"Dainric", MB_OK | MB_ICONWARNING);
            return;
        }
        g->ver.version = v;
        g->verDlgOpen = false;
        g->focusInput = -1;
        LogAdd(L"[*] 版本信息已设置: " + v, Lv::Dim);
        Invalidate();
        break;
    }
    default: break;
    }
}

void ToggleSwitch(int id)
{
    switch (id)
    {
    case 20: g->swSelfTest = !g->swSelfTest; break;
    case 21: g->swCheckDeps = !g->swCheckDeps; break;
    case 22: g->swUac = !g->swUac; break;
    case 23: g->swClean = !g->swClean; break;
    default: break;
    }
}

LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    switch (msg)
    {
    case WM_CREATE:
        g->hwnd = hwnd;
        return 0;

    case WM_PAINT:
        Render();
        return 0;

    case WM_ERASEBKGND:
        return 1;

    case WM_MOUSEMOVE:
    {
        float x = (float)GET_X_LPARAM(lp), y = (float)GET_Y_LPARAM(lp);
        int oldBtn = g->hotBtn, oldNav = g->hotNav;
        g->hotBtn = -1;
        g->hotNav = -1;
        for (auto& b : g->btnHits) if (b.first.Hit(x, y)) g->hotBtn = b.second;
        for (auto& n : g->navHits) if (n.first.Hit(x, y)) g->hotNav = n.second;

        // 拖选：按下时记了输入框，移动就更新选区
        if (g->mouseDownInput >= 0 && (wp & MK_LBUTTON))
        {
            const Input* in = FindInput(g->mouseDownInput);
            if (in)
            {
                g->caret = CaretFromPoint(*in, x);
                g->caretTick = GetTickCount();
                g->caretOn = true;
                Invalidate();
            }
        }
        if (oldBtn != g->hotBtn || oldNav != g->hotNav) Invalidate();
        return 0;
    }

    case WM_LBUTTONDOWN:
    {
        float x = (float)GET_X_LPARAM(lp), y = (float)GET_Y_LPARAM(lp);

        // 对话框打开时只处理对话框内的交互
        if (g->verDlgOpen)
        {
            // 按钮先判：输入框的命中区是整行，会盖住同一行右侧的按钮
            for (auto& b : g->btnHits)
                if (b.first.Hit(x, y)) { ClickButton(b.second); return 0; }

            int iid = InputAt(x, y);
            if (iid >= 200 && iid < 300)
            {
                g->focusInput = iid;
                const Input* in = FindInput(iid);
                if (in)
                {
                    g->caret = CaretFromPoint(*in, x);
                    g->selStart = g->caret;
                }
                g->caretTick = GetTickCount();
                g->caretOn = true;
                Invalidate();
            }
            return 0;   // 点遮罩不穿透
        }

        // 按钮优先于输入框：输入框的命中区是整行（含右侧留给按钮的位置），
        // 若先判输入框，「设置」「浏览」「选择」这几个会被吃掉点不到。
        {
            Hit hb;
            HitTest(x, y, hb);
            if (hb.kind == HitKind::Button) { ClickButton(hb.id); return 0; }
            if (hb.kind == HitKind::Switch) { ToggleSwitch(hb.id); Invalidate(); return 0; }
        }

        // 点输入框：取得焦点并定位光标
        int iid = InputAt(x, y);
        if (iid >= 0)
        {
            g->focusInput = iid;
            const Input* in = FindInput(iid);
            if (in)
            {
                g->caret = CaretFromPoint(*in, x);
                g->selStart = g->caret;
            }
            g->mouseDownInput = iid;
            g->caretTick = GetTickCount();
            g->caretOn = true;
            Invalidate();
            return 0;
        }

        // 点空白处取消焦点
        g->focusInput = -1;

        Hit h;
        HitTest(x, y, h);
        switch (h.kind)
        {
        case HitKind::Nav:
            g->page = h.id;
            g->scroll = 0;
            Invalidate();
            break;
        case HitKind::Card:
            if (h.id == 4) { g->advOpen = !g->advOpen; Invalidate(); }
            break;
        case HitKind::Drop:
            if (g->page == 0) PickScript();
            break;
        default: break;   // 按钮与开关已在上面处理
        }
        return 0;
    }

    case WM_LBUTTONUP:
        g->mouseDownInput = -1;
        return 0;

    case WM_CHAR:
        if (g->verDlgOpen && InputChar((wchar_t)wp)) Invalidate();
        else if (!g->verDlgOpen && InputChar((wchar_t)wp)) Invalidate();
        return 0;

    case WM_KEYDOWN:
    {
        bool ctrl = (GetKeyState(VK_CONTROL) & 0x8000) != 0;

        if (wp == VK_ESCAPE)
        {
            if (g->verDlgOpen)
            {
                g->verDlgOpen = false;
                g->focusInput = -1;
                Invalidate();
            }
            return 0;
        }
        if (wp == VK_RETURN && g->verDlgOpen)
        {
            ClickButton(211);
            return 0;
        }
        if (wp == VK_TAB && g->verDlgOpen)
        {
            // 在五个字段间循环
            static const int ids[] = { 200, 201, 202, 203, 204 };
            int cur = 0;
            for (int i = 0; i < 5; ++i) if (ids[i] == g->focusInput) cur = i;
            int next = (cur + (ctrl ? 4 : 1)) % 5;
            g->focusInput = ids[next];
            g->caret = g->Val(ids[next]).size();
            g->selStart = 0;
            g->caretTick = GetTickCount();
            g->caretOn = true;
            Invalidate();
            return 0;
        }

        if (InputKey(wp, ctrl)) { Invalidate(); return 0; }

        // 没有焦点时用快捷键：Ctrl+O 选脚本，Ctrl+B 开始打包
        if (ctrl && (wp == 'O' || wp == 'o')) { PickScript(); return 0; }
        if (ctrl && (wp == 'B' || wp == 'b')) { RunBuild(); return 0; }
        return 0;
    }

    case WM_MOUSEWHEEL:
    {
        if (g->contentH <= 0) return 0;
        POINT pt{ GET_X_LPARAM(lp), GET_Y_LPARAM(lp) };
        ScreenToClient(hwnd, &pt);
        Layout L = MakeLayout();
        if (!L.form.Hit((float)pt.x, (float)pt.y)) return 0;

        int delta = GET_WHEEL_DELTA_WPARAM(wp);
        float visible = L.form.h - 36;
        float maxScroll = (std::max)(0.0f, g->contentH - visible);
        g->scroll -= delta / 120.0f * 60.0f;
        g->scroll = (std::max)(0.0f, (std::min)(maxScroll, g->scroll));
        Invalidate();
        return 0;
    }

    case WM_DROPFILES:
    {
        HDROP hd = (HDROP)wp;
        UINT n = DragQueryFileW(hd, 0xFFFFFFFF, nullptr, 0);
        std::vector<std::wstring> files;
        for (UINT i = 0; i < n; ++i)
        {
            wchar_t buf[MAX_PATH * 4] = { 0 };
            DragQueryFileW(hd, i, buf, MAX_PATH * 4);
            files.push_back(buf);
        }
        DragFinish(hd);

        for (const auto& f : files)
        {
            if (f.size() > 3 && _wcsicmp(f.c_str() + f.size() - 3, L".py") == 0)
            {
                g->script = f;
                g->Val(100) = BaseNameOf(f);
                LogAdd(L"脚本: " + f, Lv::Acc);
                break;
            }
        }
        Invalidate();
        return 0;
    }

    case WM_TIMER:
        TickCaret();
        return 0;

    case WM_SIZE:
        if (g->rt)
            g->rt->Resize(D2D1::SizeU(LOWORD(lp), HIWORD(lp)));
        Invalidate();
        return 0;

    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

// ══════════════════════ 初始化 ══════════════════════

bool CreateResources(HWND hwnd)
{
    RECT rc; GetClientRect(hwnd, &rc);
    D2D1_SIZE_U size = D2D1::SizeU(rc.right - rc.left, rc.bottom - rc.top);

    if (!g->rt)
    {
        if (FAILED(g->d2d->CreateHwndRenderTarget(
                D2D1::RenderTargetProperties(),
                D2D1::HwndRenderTargetProperties(hwnd, size), &g->rt)))
            return false;
        g->rt->SetAntialiasMode(D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
        g->rt->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
    }

    auto mk = [&](float sz, DWRITE_FONT_WEIGHT wt,
                  IDWriteTextFormat** out) {
        if (*out) return;
        g->dw->CreateTextFormat(L"Microsoft YaHei UI", nullptr, wt,
                                DWRITE_FONT_STYLE_NORMAL,
                                DWRITE_FONT_STRETCH_NORMAL, sz, L"zh-CN", out);
    };
    mk(15.0f, DWRITE_FONT_WEIGHT_SEMI_BOLD, &g->fT);
    mk(13.5f, DWRITE_FONT_WEIGHT_SEMI_BOLD, &g->fH);
    mk(13.0f, DWRITE_FONT_WEIGHT_NORMAL, &g->fB);
    mk(12.5f, DWRITE_FONT_WEIGHT_NORMAL, &g->fS);
    mk(11.5f, DWRITE_FONT_WEIGHT_NORMAL, &g->fC);
    if (!g->fM)
    {
        g->dw->CreateTextFormat(L"Cascadia Mono", nullptr,
                                DWRITE_FONT_WEIGHT_NORMAL,
                                DWRITE_FONT_STYLE_NORMAL,
                                DWRITE_FONT_STRETCH_NORMAL, 11.5f, L"zh-CN",
                                &g->fM);
        if (!g->fM)
            g->dw->CreateTextFormat(L"Consolas", nullptr,
                                    DWRITE_FONT_WEIGHT_NORMAL,
                                    DWRITE_FONT_STYLE_NORMAL,
                                    DWRITE_FONT_STRETCH_NORMAL, 11.5f,
                                    L"zh-CN", &g->fM);
    }
    return true;
}

} // namespace

int RunApp()
{
    // 单实例：重复启动就把已有窗口拿到前台
    HANDLE mtx = CreateMutexW(nullptr, TRUE, L"Dainric_Packer_Cpp_Single");
    if (mtx && GetLastError() == ERROR_ALREADY_EXISTS)
    {
        HWND old = FindWindowW(L"DainricPackerWnd", nullptr);
        if (old)
        {
            ShowWindow(old, SW_RESTORE);
            SetForegroundWindow(old);
            return 0;
        }
    }

    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);

    App app;
    g = &app;

    app.packer = std::make_unique<Packer>(
        app.runner,
        [](const std::wstring& t, Lv lv) { LogAdd(t, lv); },
        [](const std::wstring& a, const std::wstring& b) { SetStatus(a, b); });

    if (FAILED(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, &app.d2d)))
        return 1;
    if (FAILED(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED,
                                   __uuidof(IDWriteFactory),
                                   reinterpret_cast<IUnknown**>(&app.dw))))
        return 1;

    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.style = CS_HREDRAW | CS_VREDRAW;
    wc.lpfnWndProc = WndProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    wc.hbrBackground = nullptr;
    wc.lpszClassName = L"DainricPackerWnd";
    RegisterClassExW(&wc);

    int dpi = GetDpiForSystem();
    HWND hwnd = CreateWindowExW(
        WS_EX_ACCEPTFILES, L"DainricPackerWnd", L"Dainric 打包器",
        WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, CW_USEDEFAULT,
        MulDiv(1200, dpi, 96), MulDiv(760, dpi, 96),
        nullptr, nullptr, wc.hInstance, nullptr);
    if (!hwnd) return 1;

    // 深色标题栏（Win10 1809+；失败就保持系统默认，不影响功能）
    BOOL dark = TRUE;
    DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */,
                          &dark, sizeof(dark));

    if (!CreateResources(hwnd)) return 1;

    app.outDir = DesktopOutDir();
    app.Val(101) = app.outDir;
    app.Val(103) = L"1800";
    app.Val(200) = app.ver.version;
    app.Val(202) = L"Dainric";
    app.history = LoadHistory();
    SetTimer(hwnd, 1, 120, nullptr);
    app.enginePrefix = ResolveEngine(app.engineDesc);

    LogAdd(L"[*] Dainric 打包器 3.2.0 · C++ 原生版", Lv::Acc);
    LogAdd(L"[*] 引擎: " + app.engineDesc, Lv::Dim);

    {
        std::wstring s;
        for (auto& sl : RuntimeManager::Discover())
        {
            RuntimeManager::FillDetails(sl, app.runner);
            s += sl.title + L"\n";
            s += L"    位置: " + (sl.dir.empty() ? L"(未找到)" : sl.dir) + L"\n";
            s += L"    来源: " + sl.foundBy + L"\n";
            if (!sl.version.empty())
                s += L"    版本: Python " + sl.version + L" · PyInstaller "
                   + sl.pyiVersion + L"\n";
            s += L"\n";
        }
        if (s.empty()) s = L"未找到任何运行时。";
        app.rtSummary = s;
    }

    ShowWindow(hwnd, SW_SHOWNORMAL);
    UpdateWindow(hwnd);

    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0) > 0)
    {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    if (app.rt) app.rt->Release();
    for (auto* f : { app.fT, app.fH, app.fB, app.fS, app.fC, app.fM })
        if (f) f->Release();
    if (app.dw) app.dw->Release();
    if (app.d2d) app.d2d->Release();
    if (mtx) CloseHandle(mtx);
    g = nullptr;
    return 0;
}

} // namespace dainric
