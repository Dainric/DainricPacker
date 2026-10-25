# -*- coding: utf-8 -*-
# 打包冒烟测试：用安装版跑一次 DAINRIC_BATCHTEST，断言 3 成功 1 拦下
# 用法: python pack_smoke.py [exe路径]
import json, os, re, subprocess, sys, time

WS = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
    os.environ["LOCALAPPDATA"], "Programs", "Dainric", "Dainric打包器.exe")
WORK = os.path.join(WS, "_smoke")
TMP = os.path.join(WORK, "tmp")
BUNDLE = os.path.join(WORK, "bundle")
DATA = os.path.join(os.environ["LOCALAPPDATA"], "Dainric")


def fail(msg):
    print("FAIL:", msg)
    sys.exit(1)


if not os.path.exists(EXE):
    fail("找不到主程序: " + EXE)

os.makedirs(WORK, exist_ok=True)
os.makedirs(TMP, exist_ok=True)
os.makedirs(BUNDLE, exist_ok=True)

env = dict(os.environ)
env["DAINRIC_BATCHTEST"] = WORK
env["TEMP"] = TMP
env["TMP"] = TMP
env["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = BUNDLE

t0 = time.time()
p = subprocess.Popen([EXE], cwd=os.path.dirname(EXE), env=env,
                     stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                     stderr=subprocess.DEVNULL)
try:
    p.wait(timeout=300)
except subprocess.TimeoutExpired:
    p.kill()
    fail("批量自检 300 秒未结束")

report_file = os.path.join(WORK, "batchtest.txt")
if not os.path.exists(report_file):
    fail("没有生成 batchtest.txt")
report = open(report_file, encoding="utf-8", errors="replace").read()

ok = len(re.findall(r"-> ok", report))
bad = len(re.findall(r"-> fail", report))
if ok != 3 or bad != 1:
    fail("自检结果不符合预期: ok=%d fail=%d\n%s" % (ok, bad, report[:2000]))

dist = os.path.join(WORK, "dist")
exes = [f for f in os.listdir(dist) if f.endswith(".exe")] if os.path.isdir(dist) else []
if len(exes) < 3:
    fail("产物数量不足: %s" % exes)

hist = os.path.join(DATA, "history.json")
logs = os.path.join(DATA, "logs")
if not os.path.exists(hist):
    fail("没有写 history.json")
if not os.path.isdir(logs) or not any(f.endswith(".log") for f in os.listdir(logs)):
    fail("没有自动保存日志")

print("PASS: 打包冒烟测试")
print("  耗时: %.1fs" % (time.time() - t0))
print("  结果: 成功 %d / 拦下 %d" % (ok, bad))
print("  产物: " + ", ".join(sorted(exes)))
print("  历史: " + hist)
