; StartupManager Windows installer (Inno Setup 6)
; 打包 D:\StartupManager 的完整自包含发布产物（GUI + MCP + .NET 10 运行时）。

#define MyAppName      "StartupManager"
#define MyAppVersion   "1.1.0"
#define MyAppPublisher "StartupManager"
#define MyAppExeName   "StartupManager.Gui.exe"
#define MyAppIcon      "D:\Mygit\StartupManager\src\StartupManager.Gui\Assets\app.ico"
#define SourceDir      "D:\StartupManager"

[Setup]
AppId={{8F5A7C2E-9B3D-4E6A-B1C8-2D4F6A8E0C13}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
; per-user 安装：无需管理员，双击即装，安装到当前用户目录。
DefaultDirName={userappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
PrivilegesRequired=lowest
AllowNoIcons=yes
OutputDir=D:\Mygit\StartupManager\installer\Output
OutputBaseFilename=StartupManager-{#MyAppVersion}-setup
SetupIconFile={#MyAppIcon}
UninstallDisplayIcon={app}\{#MyAppExeName},0
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
; 程序数据本就放在 %LOCALAPPDATA%\StartupManager，因此安装目录也用每用户位置，
; 全程不写 Program Files，不需要 UAC 提权。

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; 整个发布目录（含 13 个本地化子目录、MCP 服务端与运行时），递归打包。
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{#MyAppIcon}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{#MyAppIcon}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
