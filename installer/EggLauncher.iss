; Built by scripts/Publish-Installer.ps1. Do not compile without PackageDir, OutputDir and AppVersion.
#ifndef PackageDir
  #error PackageDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif
#ifndef AppVersion
  #error AppVersion is required
#endif

[Setup]
AppId={{D29145D2-2A92-46BA-BAAA-4F034F497BD5}
AppName=Egg Launcher
AppVersion={#AppVersion}
AppPublisher=甲总不是贾总
DefaultDirName={userpf}\Egg Launcher
DefaultGroupName=Egg Launcher
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
MinVersion=10.0.19045
AppMutex=Local\ChatGPTLocalLauncher.App,Local\ChatGPTLocalLauncher.Agent
CloseApplications=no
RestartApplications=no
UninstallDisplayIcon={app}\Launcher.App.exe
SetupIconFile=..\src\Launcher.App\Assets\egg-launcher.ico
OutputDir={#OutputDir}
OutputBaseFilename=Egg-Launcher-{#AppVersion}-Setup-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
#ifdef SignInstaller
SignTool=eggsign
SignedUninstaller=yes
#else
SignedUninstaller=no
#endif

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "zh"; MessagesFile: "ChineseSimplified.isl"

[CustomMessages]
en.DesktopShortcut=Create a desktop shortcut
zh.DesktopShortcut=创建桌面快捷方式
en.LaunchProgram=Launch Egg Launcher
zh.LaunchProgram=启动 Egg Launcher
en.CloseBeforeInstall=Close Egg Launcher and its Agent before installing or updating, then retry.
zh.CloseBeforeInstall=请先正常退出 Egg Launcher 和后台 Agent，再重试安装或升级。
en.UninstallPreparationFailed=Uninstall preparation failed. Close ChatGPT Desktop, Egg Launcher and its Agent, then retry. If Local mode is active, restore OpenAI mode in Egg Launcher first. No program files were removed. Details are in ChatGPTLocalLauncher\Launcher.Agent.fatal.log under the Windows temp folder.
zh.UninstallPreparationFailed=卸载准备失败。请先关闭 ChatGPT Desktop、Egg Launcher 和后台 Agent 后重试；若当前为 Local 模式，请先在启动器中恢复 OpenAI 模式。程序文件尚未删除。详情见 Windows 临时目录下的 ChatGPTLocalLauncher\Launcher.Agent.fatal.log。

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PackageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Egg Launcher"; Filename: "{app}\Launcher.App.exe"
Name: "{autodesktop}\Egg Launcher"; Filename: "{app}\Launcher.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Launcher.App.exe"; Description: "{cm:LaunchProgram}"; Flags: nowait postinstall skipifsilent

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if CheckForMutexes('Local\ChatGPTLocalLauncher.App,Local\ChatGPTLocalLauncher.Agent') then
    Result := ExpandConstant('{cm:CloseBeforeInstall}');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ExitCode: Integer;
  AgentPath: String;
begin
  if CurUninstallStep <> usUninstall then
    Exit;

  AgentPath := ExpandConstant('{app}\Launcher.Agent.exe');
  if not FileExists(AgentPath) then
  begin
    MsgBox(ExpandConstant('{cm:UninstallPreparationFailed}'), mbError, MB_OK);
    Abort;
  end;

  if not Exec(AgentPath, '--prepare-uninstall', ExpandConstant('{app}'),
    SW_HIDE, ewWaitUntilTerminated, ExitCode) or (ExitCode <> 0) then
  begin
    MsgBox(ExpandConstant('{cm:UninstallPreparationFailed}'), mbError, MB_OK);
    Abort;
  end;
end;
