; Inno Setup 6 script for Sticker — per-user install, no admin required.
;
; Build locally:
;   dotnet publish StickerApp -c Release -r win-x64 --self-contained
;   ISCC installer\Sticker.iss
;
; CI overrides:
;   ISCC /DAppVersion=1.2.3 /DPublishDir=..\path\to\publish installer\Sticker.iss

#ifndef AppVersion
  #define AppVersion "1.0.4"
#endif
#ifndef PublishDir
  #define PublishDir "..\StickerApp\bin\Release\net10.0-windows\win-x64\publish"
#endif

[Setup]
AppId={{8f3c1a72-4d6b-4e9a-b2c5-1f7e9d0a3b48}
AppName=Sticker
AppVersion={#AppVersion}
AppPublisher=Zophie
AppPublisherURL=https://github.com/zophiezlan/sticker
AppSupportURL=https://github.com/zophiezlan/sticker/issues
AppUpdatesURL=https://github.com/zophiezlan/sticker/releases
DefaultDirName={autopf}\Sticker
DefaultGroupName=Sticker
DisableProgramGroupPage=yes
; Per-user: installs to %LOCALAPPDATA%\Programs\Sticker, no UAC prompt
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=StickerSetup-{#AppVersion}
SetupIconFile=..\StickerApp\app.ico
UninstallDisplayIcon={app}\Sticker.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Win11 (and late Win10) only
MinVersion=10.0.19041
; Prompts the user to close a running Sticker before install/uninstall
AppMutex=StickerApp.SingleInstance

[Tasks]
Name: "startup"; Description: "Start Sticker with Windows (reopens your stickers at login)"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{autoprograms}\Sticker"; Filename: "{app}\Sticker.exe"
; Autostart via a Startup-folder shortcut (not a Run-key write) — the same .lnk
; the in-app "Start with Windows" toggle manages, so the two stay in sync.
Name: "{userstartup}\Sticker"; Filename: "{app}\Sticker.exe"; Parameters: "--resume"; Tasks: startup

[Registry]
; Classic context menu: right-click an image -> (Show more options on Win11) -> Open as sticker.
; Registered per file extension rather than under the "image" PerceivedType, because
; PerceivedType is set inconsistently (e.g. .webp usually lacks it), which made the
; entry appear for .jpg only. Keep this list in sync with App.ImageExtensions.
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpg\shell\OpenAsSticker"; ValueType: string; ValueData: "Open as sticker"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpg\shell\OpenAsSticker"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\Sticker.exe"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpg\shell\OpenAsSticker\command"; ValueType: string; ValueData: """{app}\Sticker.exe"" ""%1"""; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpeg\shell\OpenAsSticker"; ValueType: string; ValueData: "Open as sticker"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpeg\shell\OpenAsSticker"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\Sticker.exe"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpeg\shell\OpenAsSticker\command"; ValueType: string; ValueData: """{app}\Sticker.exe"" ""%1"""; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.png\shell\OpenAsSticker"; ValueType: string; ValueData: "Open as sticker"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.png\shell\OpenAsSticker"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\Sticker.exe"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.png\shell\OpenAsSticker\command"; ValueType: string; ValueData: """{app}\Sticker.exe"" ""%1"""; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.webp\shell\OpenAsSticker"; ValueType: string; ValueData: "Open as sticker"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.webp\shell\OpenAsSticker"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\Sticker.exe"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.webp\shell\OpenAsSticker\command"; ValueType: string; ValueData: """{app}\Sticker.exe"" ""%1"""; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.bmp\shell\OpenAsSticker"; ValueType: string; ValueData: "Open as sticker"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.bmp\shell\OpenAsSticker"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\Sticker.exe"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.bmp\shell\OpenAsSticker\command"; ValueType: string; ValueData: """{app}\Sticker.exe"" ""%1"""; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.gif\shell\OpenAsSticker"; ValueType: string; ValueData: "Open as sticker"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.gif\shell\OpenAsSticker"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\Sticker.exe"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.gif\shell\OpenAsSticker\command"; ValueType: string; ValueData: """{app}\Sticker.exe"" ""%1"""; Flags: uninsdeletekey

[Run]
Filename: "{app}\Sticker.exe"; Description: "Launch Sticker (parks in the tray)"; Flags: postinstall nowait skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // Remove the autostart shortcut whether it was created by the installer
    // task or by the in-app tray toggle. (Inno auto-removes the task-created
    // [Icons] entry; this also covers a toggle-created one it doesn't track.)
    DeleteFile(ExpandConstant('{userstartup}\Sticker.lnk'));
    // Note: ~/.sticker_cache (models, mattes, session) is left in place so a
    // reinstall doesn't re-download models. Mention manual cleanup in README.
  end;
end;
