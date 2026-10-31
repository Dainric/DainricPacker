; Dainric 打包器 · 安装脚本
; 作者: Dainric
;
; 设计要点
;   1. 每用户安装（PrivilegesRequired=lowest）—— 不需要管理员权限，也不需要 UAC
;   2. 运行时装到 %LOCALAPPDATA%\Dainric\runtime-py*
;      这是程序内置的「集中目录」搜索路径，主程序会自动找到
;   3. 主程序装到 %LOCALAPPDATA%\Programs\Dainric
;   4. 卸载时把运行时一并清掉

#define AppName        "Dainric 打包器"
; 版本号与源目录可由命令行覆盖（构建脚本会从 csproj 读版本后传入）：
;   ISCC /DAppVersion=3.2.1 /DSrcDir=..\打包器-绿色版-v3.2.1 setup.iss
#ifndef AppVersion
  #define AppVersion     "3.2.0"
#endif
#define AppPublisher   "Dainric"
#define AppExeName     "Dainric打包器.exe"
#ifndef SrcDir
  #define SrcDir         "..\打包器-绿色版-v3.2.0"
#endif

[Setup]
AppId={{8F3A2C71-5B4E-4D9A-9E27-1C6D4A8B3F52}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\Dainric
DefaultGroupName=Dainric
DisableProgramGroupPage=yes
; 免管理员：装到用户目录，不触发 UAC
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
; 用 SourcePath 保证产物落在脚本旁边，不受当前工作目录影响
OutputDir={#SourcePath}
OutputBaseFilename=Dainric打包器-安装程序-v{#AppVersion}
SetupIconFile=..\打包器-C#源码\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
; 运行时是压缩过的二进制，再用 lzma2/max 收益很低却很慢，用 normal 平衡
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableWelcomePage=no
DisableReadyPage=no
; 说明：不使用 [Files] 的 restartreplace 等，避免多余提示

[Languages]
Name: "chinese"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinese.RuntimeTask=安装 Python 运行时（3.12 + 3.14，约 187 MB，必装）
chinese.CreateDesktopIcon=创建桌面快捷方式
chinese.LaunchAfter=安装完成后启动 {#AppName}
english.RuntimeTask=Install Python runtimes (3.12 + 3.14, about 187 MB, required)
english.CreateDesktopIcon=Create a desktop shortcut
english.LaunchAfter=Launch {#AppName} after installation

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; \
    GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; 主程序
Source: "{#SrcDir}\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SrcDir}\Loader.html";  DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#SrcDir}\使用说明.txt";   DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

; 运行时 —— 装到程序默认会搜索的集中目录，这样用户看不到也无需关心
; 两个版本都内置：3.12 体积小、兼容面广；3.14 用于较新的语法特性
Source: "{#SrcDir}\runtime-py312\*"; DestDir: "{localappdata}\Dainric\runtime-py312"; \
    Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcDir}\runtime-py314\*"; DestDir: "{localappdata}\Dainric\runtime-py314"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

[Registry]
; 右键菜单：用 Dainric 打包（.py / .pyw，按用户注册，不需要管理员）
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.py\shell\DainricPacker"; ValueType: string; ValueName: ""; ValueData: "用 Dainric 打包"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.py\shell\DainricPacker"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExeName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.py\shell\DainricPacker\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.pyw\shell\DainricPacker"; ValueType: string; ValueName: ""; ValueData: "用 Dainric 打包"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.pyw\shell\DainricPacker"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExeName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.pyw\shell\DainricPacker\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""; Flags: uninsdeletekey

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\使用说明"; Filename: "{app}\使用说明.txt"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchAfter}"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 清理运行时和用户数据
Type: filesandordirs; Name: "{localappdata}\Dainric"

