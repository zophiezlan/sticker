; Inno Setup 6 script for Sticker — per-user install, no admin required.
;
; Build locally:
;   dotnet publish StickerApp -c Release -r win-x64 --self-contained
;   ISCC installer\Sticker.iss
;
; CI overrides:
;   ISCC /DAppVersion=1.2.3 /DPublishDir=..\path\to\publish installer\Sticker.iss

#ifndef AppVersion
  #define AppVersion "1.0.0"
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
Name: "startup"; Description: "Start Sticker with Windows (restores your stickers at login)"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{autoprograms}\Sticker"; Filename: "{app}\Sticker.exe"

[Registry]
; Classic context menu: right-click any image -> (Show more options on Win11) -> Open as sticker
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\image\shell\OpenAsSticker"; ValueType: string; ValueData: "Open as sticker"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\image\shell\OpenAsSticker"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\Sticker.exe"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\image\shell\OpenAsSticker\command"; ValueType: string; ValueData: """{app}\Sticker.exe"" ""%1"""; Flags: uninsdeletekey
; Same value the in-app "Start with Windows" toggle writes, so the two stay in sync
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Sticker"; ValueData: """{app}\Sticker.exe"" --restore"; Tasks: startup

[Run]
Filename: "{app}\Sticker.exe"; Description: "Launch Sticker (parks in the tray)"; Flags: postinstall nowait skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // Remove the autostart entry whether it was set by the installer task
    // or by the in-app tray toggle.
    RegDeleteValue(HKEY_CURRENT_USER,
      'Software\Microsoft\Windows\CurrentVersion\Run', 'Sticker');
    // Note: ~/.sticker_cache (models, mattes, session) is left in place so a
    // reinstall doesn't re-download models. Mention manual cleanup in README.
  end;
end;
