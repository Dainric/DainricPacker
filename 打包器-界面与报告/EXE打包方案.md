# 关于把打包器做成 EXE 软件 —— 实测结论与方案

作者 Dainric ｜ 基于 `打包_v2.9.0.py` 的实测，不是纸上推演

---

## 一、先说结论

打包器本身已经能做成 EXE，并且**实测通过**：冻结后的 EXE 成功调用系统 PyInstaller
完成了 2 个脚本的批量打包（各产出 9.36 MB EXE）。

但过程中挖出 **4 个必须处理的坑**，其中 2 个是 v2.8.2 就存在的隐患。

最终产物（本机实测）：

| 形态 | EXE 体积 | 整包体积 | 文件数 | 启动 |
|---|---|---|---|---|
| onedir + 窗口 | 3.5 MB | 42.2 MB | 1007 | 快，不写临时目录 |
| onedir + 控制台 | 7.0 MB | 72.5 MB | 1060 | 快 |
| onefile | 32.3 MB | — | 1 | **本沙箱内启动失败** |

---

## 二、四个真实的坑

### 坑 1：冻结后 `sys.executable` 指向自己，pip 安装会无限自启

PyInstaller 打包后，`sys.executable` **不再是 python.exe，而是 EXE 自身**。
而 v2.8.2 的 `ensure_pyinstaller` 里写的是：

```python
cmd = [sys.executable, "-m", "pip", "install", "pyinstaller"]
```

冻结后这行等于**再启动一个打包器实例**。结果不是报错，而是：
一层套一层弹窗、命令永远不返回、超时后报“pip安装失败”。

**实测记录**：
```
实际执行的命令: [['...\python.exe', '-m', 'pip', 'install', 'pyinstaller']]
  -> 冻结后这里会是 Dainric打包器.exe，被当成又一个打包器实例启动
```

**修复**：新增运行形态探测，冻结时只做探测、绝不自我安装。

```python
IS_FROZEN = bool(getattr(sys, "frozen", False))

def find_pyinstaller():
    exe = shutil.which("pyinstaller")      # 官方 CLI 入口，冻结后照常可用
    if exe:
        return [exe], "CLI: " + exe
    py = interpreter_path()                # 冻结后返回 None
    ...
```

### 坑 2：产物“自己搬自己”，把刚做好的 EXE 删掉

`do_pack` 的搬运逻辑是「先删目标，再 move」。当**输出目录正好就是 PyInstaller 的
`dist` 目录**时，源和目标是同一个文件 —— `os.remove(target)` 先把产物删了。

这是我构建自己的 EXE 时踩到的，实测日志：
```
[!] 文件被占用，等待重试 (1/3)...
[-] 移动失败: [WinError 2] 系统找不到指定的文件
[-] 复制也失败: [WinError 2] 系统找不到指定的文件
```
**32.3 MB 的 EXE 就这么没了。**

**修复**：源与目标同路径时直接判定完成；复制回退路径也加了「源仍存在才删」。

```python
if os.path.abspath(src) == os.path.abspath(target_path):
    moved = True
    log_cb("[*] 输出目录即构建目录，无需搬运")
```

> 这条对普通用户同样有意义：很多人习惯把「输出目录」填成项目下的 `dist`，
> 一填就正好踩中。

### 坑 3：onefile 依赖临时目录，受限环境直接启动失败

这是**环境相关**的坑，必须知道。onefile 每次启动都要把自身解压到 `%TEMP%\_MEIxxxx`。
在以下环境会直接崩：

- 沙箱 / 受策略限制的临时目录
- 加密盘、某些企业管控环境
- `%TEMP%` 不可写或已被占用

**实测**：最小的 `print("hello")` 打成 onefile 后同样无法运行 —— 说明与代码无关。

```
[PYI-20260:ERROR] Failed to extract VCRUNTIME140.dll: failed to open target file!
fopen: Permission denied
```

而 **onedir 完全不写临时目录**，同环境下正常：

```
hello from frozen True
```

**建议**：默认发 onedir。onefile 只在确实需要“单文件分发”时用，并在文档里注明
对临时目录的要求。

### 坑 4：`BatchProgress.feed` 收到 float 崩溃（v2.8.2 遗留）

批量打包在 v2.8.2 里**从来没成功过**：`do_pack` 会 `tracker.feed("")` 预热，
批量模式下 `progress_cb` 是 `BatchProgress`，其 `_relay` 又把数值回传给同一个
`feed`，于是 `if key in line` 抛 `TypeError`。

```
TypeError: argument of type 'float' is not a container or iterable
```
修复后批量打包 2/2 成功。详见 v2.9.0 发布说明。

---

## 三、架构上要想清楚的一件事

我们的打包器是「GUI 外壳 + 外部 PyInstaller」的结构：

```
Dainric打包器.exe  ──spawn──>  pyinstaller.exe  ──>  目标.exe
     (冻结, 不含 Python 运行时可用性)      (系统 Python 环境)
```

**关键点**：打包器 EXE **自身不需要 Python 运行时**去构建，它只是调用外部命令。
所以：

- 打包器可以安全地冻结成 EXE
- 但 `pyinstaller` 命令必须在目标机器上存在（`PATH` 里，或装在系统 Python 里）
- EXE 冻结后**不能**自己 pip install（坑 1），因此要么随包附带说明，
  要么做成“绿色版 + 检测提示”

**实测确认**：冻结 EXE 里的日志显示
```
[*] PyInstaller: CLI: ...\Python314\Scripts\pyinstaller.EXE
[*] [1/2] 正在打包: alpha
[+] [1/2] alpha -> 成功 (7.8s)
```

---

## 四、分发策略选择

| 方案 | 优点 | 缺点 | 适用 |
|---|---|---|---|
| **onedir**（推荐） | 启动快、不写临时目录、杀软误报少 | 需整个文件夹分发 | 内部使用、U 盘/共享盘分发 |
| onefile | 单个文件，好传 | 启动慢（每次解压）、临时目录受限则崩、杀软误报多 | 确实只要一个文件时 |
| 源码运行 | 体积最小、可改 | 需要 Python 环境 | 自己和同事用 |

补充建议：

1. **杀软误报**：PyInstaller 产物常被误报，尤其 onefile。可考虑代码签名证书，
   或直接发 onedir。
2. **压缩分发**：onedir 整包 42 MB，压成 zip 约 18–20 MB。用 7z 固实压缩更小。
3. **别用 UPX**：`--upx-dir` 会进一步压小，但**显著提高误报率**，不建议。
4. **图标**：已用内嵌图标，`--icon` 直接复用，不需要外部文件。

---

## 五、交付物

| 文件 | 说明 |
|---|---|
| `打包_v2.9.0.py` | 源码，含全部冻结感知修复 |
| `构建EXE.py` | 构建脚本，一行命令出包 |
| `_build/onedir/Dainric打包器/` | 已构建好的窗口版发行包（42.2 MB） |

### 构建命令

```bash
python 构建EXE.py                # 默认 onedir + 窗口模式（推荐）
python 构建EXE.py --console      # 保留控制台，方便看报错
python 构建EXE.py --onefile      # 单文件版
python 构建EXE.py --both         # 两种都出
```

脚本会自动从源码里解出内嵌 ICO 当 `--icon`，并排除 numpy/pandas/matplotlib
等无关重型模块。

---

## 六、下一步可选项

1. **附带 Python 运行时**：把便携版 Python + PyInstaller 一起打进包里，
   做成「解压即用、目标机无需装 Python」的完整绿色版（体积约 +40 MB，
   但真正零依赖）。
2. **打包器自举**：用 v2.9.0 自己打包自己，做成「一键更新」。
3. **代码签名**：消除杀软误报，需要证书。
4. **安装包**：用 Inno Setup 做带开始菜单/卸载的安装程序。

第 1 项最有价值 —— 现在的 EXE 仍然要求目标机有 `pyinstaller` 命令；
做成自带运行时才是真正的单机可用。
