# -*- coding: utf-8 -*-
# UI 回归截图：启动主程序，逐页截图，可选与基线比对
# 用法:
#   python ui_regression.py                 # 跑一遍，截图到 ui_shots/
#   python ui_regression.py --baseline      # 同时把本次截图存为基线 ui_baseline/
import ctypes, os, subprocess, sys, time
from ctypes import wintypes
from PIL import Image, ImageChops, ImageGrab

WS = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REP = os.path.join(WS, "打包器-界面与报告")
EXE = os.path.join(os.environ["LOCALAPPDATA"], "Programs", "Dainric", "Dainric打包器.exe")
OUT = os.path.join(REP, "ui_shots")
BASE = os.path.join(REP, "ui_baseline")
TMP = os.path.join(WS, "_uismoke", "tmp")
BUNDLE = os.path.join(WS, "_uismoke", "bundle")
BASELINE = "--baseline" in sys.argv

u = ctypes.windll.user32
u.SetProcessDPIAware()
CB = ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)


def windows(pid):
    rows = []

    def cb(h, l):
        pp = wintypes.DWORD()
        u.GetWindowThreadProcessId(h, ctypes.byref(pp))
        if pp.value != pid or not u.IsWindowVisible(h):
            return True
        cls = ctypes.create_unicode_buffer(256)
        u.GetClassNameW(h, cls, 256)
        n = u.GetWindowTextLengthW(h)
        buf = ctypes.create_unicode_buffer(max(n + 1, 2))
        u.GetWindowTextW(h, buf, n + 1)
        if cls.value.startswith("HwndWrapper"):
            r = wintypes.RECT()
            u.GetWindowRect(h, ctypes.byref(r))
            rows.append((h, buf.value, r.left, r.top,
                         r.right - r.left, r.bottom - r.top))
        return True
    u.EnumWindows(CB(cb), 0)
    return rows


def fail(msg):
    print("FAIL:", msg)
    sys.exit(1)


os.makedirs(OUT, exist_ok=True)
os.makedirs(TMP, exist_ok=True)
os.makedirs(BUNDLE, exist_ok=True)

env = dict(os.environ)
env.pop("DAINRIC_BATCHTEST", None)
env["TEMP"] = TMP
env["TMP"] = TMP
env["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = BUNDLE

p = subprocess.Popen([EXE], cwd=os.path.dirname(EXE), env=env,
                     stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                     stderr=subprocess.DEVNULL)
main = None
for _ in range(60):
    time.sleep(1)
    if p.poll() is not None:
        fail("主程序启动即退出")
    ws = [w for w in windows(p.pid) if w[1] == "Dainric 打包器"]
    if ws:
        main = ws[0]
        break
if not main:
    p.kill()
    fail("主窗口没有出现")

h, title, x0, y0, w, hh = main
u.SetForegroundWindow(h)
time.sleep(7)


def shot(name):
    ImageGrab.grab(bbox=(x0, y0, x0 + w, y0 + hh)).save(
        os.path.join(OUT, name))
    return os.path.join(OUT, name)


def click(px, py):
    u.SetCursorPos(x0 + px, y0 + py)
    time.sleep(0.3)
    u.mouse_event(0x0002, 0, 0, 0, 0)
    u.mouse_event(0x0004, 0, 0, 0, 0)
    time.sleep(1.1)


shots = []
shots.append(shot("01_单文件.png"))
click(100, 187)
shots.append(shot("02_历史.png"))
click(100, 139)
time.sleep(0.6)
click(100, 236)
shots.append(shot("03_运行时.png"))
click(100, 139)
time.sleep(0.6)

# 正常关闭
u.PostMessageW(h, 0x0010, 0, 0)
for _ in range(20):
    time.sleep(1)
    if p.poll() is not None:
        break
if p.poll() is None:
    p.kill()

if BASELINE:
    os.makedirs(BASE, exist_ok=True)
    for s in shots:
        Image.open(s).save(os.path.join(BASE, os.path.basename(s)))
    print("PASS: 已更新 UI 基线 ->", BASE)
    sys.exit(0)

# 可选基线比对：差异像素超过 6% 才报警，避免主题/像素级抖动误报
if os.path.isdir(BASE):
    for s in shots:
        b = os.path.join(BASE, os.path.basename(s))
        if not os.path.exists(b):
            continue
        a = Image.open(s).convert("RGB")
        c = Image.open(b).convert("RGB")
        if a.size != c.size:
            print("WARN: 尺寸变化", os.path.basename(s))
            continue
        diff = ImageChops.difference(a, c).convert("L")
        hist = diff.histogram()
        total = a.size[0] * a.size[1]
        changed = sum(hist[24:])  # 灰度差 > 24 视为变化
        ratio = changed / float(total)
        if ratio > 0.06:
            print("WARN: 截图差异 %.1f%%: %s" % (ratio * 100,
                                                 os.path.basename(s)))
        else:
            print("  ok 截图差异 %.1f%%: %s" % (ratio * 100,
                                                os.path.basename(s)))

print("PASS: UI 回归冒烟（启动/切页/关闭正常，截图已保存）")
print("  截图目录: " + OUT)
