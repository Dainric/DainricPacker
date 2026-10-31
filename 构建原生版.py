# -*- coding: utf-8 -*-
# 一键重建：发布单文件 EXE -> 组装绿色版 -> 编译安装程序
# 作者: Dainric
# 版本号唯一来源：打包器-C#源码\DainricPacker.csproj 的 <Version>
import io, os, re, shutil, subprocess, sys, time

WS = os.path.dirname(os.path.abspath(__file__))
PROJ = os.path.join(WS, "打包器-C#源码", "DainricPacker.csproj")
PUB = os.path.join(WS, "_native_selfcontained")
ISS = os.path.join(WS, "打包器-安装程序", "setup.iss")
ISCC = os.path.join(os.environ["LOCALAPPDATA"],
                    "Programs", "Inno Setup 6", "ISCC.exe")
EXE_NAME = "Dainric打包器.exe"


def read_version():
    try:
        txt = io.open(PROJ, encoding="utf-8").read()
        m = re.search(r"<Version>\s*([0-9.]+)\s*</Version>", txt)
        if m:
            return m.group(1).strip()
    except Exception as ex:
        print("读取版本号失败: %s" % ex)
    return "0.0.0"


VER = read_version()
GREEN = os.path.join(WS, "打包器-绿色版-v" + VER)

env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1")


def run(cmd, cwd=None, timeout=1800):
    print("  $ " + " ".join(cmd[:4]) + (" …" if len(cmd) > 4 else ""))
    p = subprocess.run(cmd, cwd=cwd, env=env, timeout=timeout,
                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    out = p.stdout.decode("utf-8", "replace")
    return p.returncode, out


def robust_rm(path):
    if not os.path.exists(path):
        return
    for i in range(6):
        try:
            shutil.rmtree(path, ignore_errors=False)
            return
        except Exception:
            time.sleep(1.2)
    shutil.rmtree(path, ignore_errors=True)


def size_mb(path):
    if os.path.isfile(path):
        return os.path.getsize(path) / 1048576.0
    total = 0
    for r, _, fs in os.walk(path):
        for f in fs:
            try:
                total += os.path.getsize(os.path.join(r, f))
            except OSError:
                pass
    return total / 1048576.0


def write_readme(dst, ver):
    """复制 使用说明.txt 并把首行版本号换成当前版本，避免说明书版本漂移。"""
    src = os.path.join(WS, "使用说明.txt")
    try:
        txt = io.open(src, encoding="utf-8-sig").read()
        txt = re.sub(r"^Dainric 打包器 v[0-9.]+",
                     "Dainric 打包器 v" + ver, txt, count=1, flags=re.M)
        with io.open(dst, "w", encoding="utf-8-sig", newline="\r\n") as f:
            f.write(txt)
        return True
    except Exception as ex:
        print("    ! 生成使用说明失败: %s" % ex)
        return False


print("版本号: v" + VER)
print("[1/4] 发布单文件 EXE …")
rc, out = run(["dotnet", "publish", PROJ, "-c", "Release", "-r", "win-x64",
               "--self-contained", "true",
               "-p:PublishSingleFile=true",
               "-p:IncludeNativeLibrariesForSelfExtract=true",
               "-p:EnableCompressionInSingleFile=true",
               "-p:DebugType=none",
               "-o", PUB, "--nologo", "-v", "q"])
if rc != 0:
    print(out[-3000:])
    sys.exit("发布失败")

exe = os.path.join(PUB, EXE_NAME)
if not os.path.exists(exe):
    cand = [f for f in os.listdir(PUB) if f.endswith(".exe")]
    if not cand:
        sys.exit("没有产出 exe")
    exe = os.path.join(PUB, cand[0])
print("    -> %s  %.1f MB" % (os.path.basename(exe), size_mb(exe)))

# ── 2) 组装绿色版 ──
print("[2/4] 组装绿色版 …")
robust_rm(GREEN)
os.makedirs(GREEN, exist_ok=True)
shutil.copy2(exe, os.path.join(GREEN, EXE_NAME))

for extra in ("Loader.html",):
    for src in (os.path.join(WS, extra),
                os.path.join(PUB, extra),
                os.path.join(WS, "打包器-界面与报告", extra)):
        if os.path.exists(src):
            shutil.copy2(src, os.path.join(GREEN, extra))
            break
write_readme(os.path.join(GREEN, "使用说明.txt"), VER)

# 运行时放进去，绿色版解压即用
RT_HOME = os.path.join(os.environ["LOCALAPPDATA"], "Dainric")
for d in ("runtime-py312", "runtime-py314"):
    src = os.path.join(RT_HOME, d)
    if os.path.isdir(src):
        try:
            shutil.copytree(src, os.path.join(GREEN, d), dirs_exist_ok=True)
        except Exception:
            subprocess.run(["robocopy", src, os.path.join(GREEN, d),
                            "/E", "/NFL", "/NDL", "/NJH", "/NJS", "/R:2",
                            "/W:1"], stdout=subprocess.DEVNULL)
        print("    + %s  %.1f MB" % (d, size_mb(os.path.join(GREEN, d))))
    else:
        print("    ! 缺少 %s，跳过" % d)

print("    绿色版合计 %.1f MB" % size_mb(GREEN))

# ── 3) 编译安装程序 ──
print("[3/4] 编译安装程序 …")
inst = os.path.join(WS, "打包器-安装程序",
                    "Dainric打包器-安装程序-v%s.exe" % VER)
if not os.path.exists(ISCC):
    print("    ! 未找到 ISCC，跳过安装包")
else:
    for old in (os.path.join(WS, "Dainric打包器-安装程序-v%s.exe" % VER),):
        if os.path.exists(old):
            try:
                os.remove(old)
            except Exception:
                pass

    src_rel = os.path.relpath(GREEN, os.path.dirname(ISS))
    t0 = time.time()
    rc, out = run([ISCC, "/DAppVersion=" + VER, "/DSrcDir=" + src_rel, ISS],
                  cwd=os.path.dirname(ISS), timeout=3600)
    if rc != 0:
        print(out[-3000:])
        sys.exit("安装包编译失败")

    if os.path.exists(inst):
        print("    -> %s  %.1f MB  (%.0f s)" % (
            os.path.basename(inst), size_mb(inst), time.time() - t0))
        # 每个版本保留一个安装程序副本
        try:
            arch = os.path.join(WS, "归档", "旧版本")
            os.makedirs(arch, exist_ok=True)
            shutil.copy2(inst, os.path.join(arch, os.path.basename(inst)))
            print("    -> 已归档安装程序: " + os.path.join("归档", "旧版本",
                  os.path.basename(inst)))
        except Exception as ex:
            print("    ! 归档安装程序失败: %s" % ex)
    else:
        sys.exit("未找到安装包产物")

# ── 4) 汇总 ──
print("[4/4] 汇总")
for p in (exe, GREEN, inst if os.path.exists(ISCC) else None):
    if p and os.path.exists(p):
        print("    %-52s %8.1f MB" % (os.path.basename(p), size_mb(p)))
