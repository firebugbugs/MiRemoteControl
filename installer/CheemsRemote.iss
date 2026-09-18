; Cheems遥控器（Cheems Remote）安装包脚本
; 用法：先按 docs/release/发布流程.md 生成 artifacts\installer-stage（docs/ 不入库），再执行：
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\CheemsRemote.iss
; 产物：artifacts\installer\CheemsRemote-Setup-0.0.1.exe
; 版本号三处联动：本文件 AppVersion / OutputBaseFilename、Desktop csproj 的 Version、app/stable.json。

#define MyAppName "Cheems遥控器"
#define MyAppNameEn "Cheems Remote"
#define MyAppVersion "0.0.1"
#define MyAppPublisher "Cheems"
#define MyAppURL "https://cheems.cn"
#define MyAppExeName "MiRemoteControl.exe"

[Setup]
AppId={{A52CD5ED-A8D4-4EA3-89C6-F9423D005B21}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
DefaultDirName={autopf}\CheemsRemote
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; 安装器自身版本信息
OutputBaseFilename=CheemsRemote-Setup-{#MyAppVersion}
OutputDir=..\artifacts\installer
SetupIconFile=..\src\MiRemoteControl.Desktop\Assets\head_portrait.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64bitMode=x64compatible
PrivilegesRequired=admin
; 语言：Inno 6 未自带简体中文（Unofficial 版可从 issrc 仓库 Languages/Unofficial/ChineseSimplified.isl
; 下载后放入 Inno 安装目录的 Languages\ 并在此追加一行）。
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\artifacts\installer-stage\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
