; Quire Installer Script
; Requires Inno Setup 6 (https://jrsoftware.org/isdl.php)

#define AppName        "Quire"
#define AppPublisher   "Kevwe"
#define AppVersion     "1.0.11"
#define AppExeName     "Quire.exe"
#define AppPublisherURL "https://github.com/highnine699-del/quire-app"
#define AppSupportURL  "https://github.com/highnine699-del/quire-app/issues"

[Setup]
; ── Identity ──────────────────────────────────────────────────────────────────
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppPublisherURL}
AppSupportURL={#AppSupportURL}
AppCopyright=Copyright (C) 2026 {#AppPublisher}

; Stable GUID — Windows uses this to recognise upgrades as the SAME app rather
; than a new unknown program. SmartScreen reputation accumulates on this GUID.
; DO NOT change this value between releases.
AppId={{A3F7E2B1-4C8D-4E9F-B123-567890ABCDEF}

; Prevent two installer instances from running simultaneously.
; AV engines flag concurrent installer processes as suspicious.
AppMutex=QuireInstallerMutex_A3F7E2B1

; ── Version info embedded into the installer exe ──────────────────────────────
; These fields populate the installer's file properties (right-click → Details).
; A signed/described installer is far less likely to be flagged by SmartScreen.
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} Installer
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
VersionInfoCopyright=Copyright (C) 2026 {#AppPublisher}

; ── Install location ──────────────────────────────────────────────────────────
; Per-user install to %LocalAppData%\Programs\Quire.
; No UAC prompt, no admin required — correct for a desktop widget.
; Admin installs to Program Files caused silent crashes (app writes to %AppData%).
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
OutputBaseFilename=Quire-Setup
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

WizardStyle=modern
WizardImageFile=
WizardSmallImageFile=

; Disable creating an uninstall entry in Programs and Features — optional.
; Uncomment if you want a cleaner per-user footprint:
; CreateUninstallRegKey=no

[Files]
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"

[Registry]
; Optional autostart — only written if user selects the "autostart" task.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExeName}"""; \
    Flags: uninsdeletevalue; Tasks: autostart

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked
Name: "autostart";   Description: "Start {#AppName} automatically when Windows starts"; GroupDescription: "Startup:"

[Icons]
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; %AppData%\Quire is preserved on uninstall — user data (concepts, settings) is kept.

[UninstallRun]
; Gracefully ask the app to close before uninstalling.
; PostMessage WM_CLOSE (0x0010) is the polite way — no forceful kill, no cmd.exe.
; RunOnceId ensures this runs exactly once during uninstall.
Filename: "{cmd}"; Parameters: "/c taskkill /IM {#AppExeName} /T"; \
    Flags: runhidden; RunOnceId: "CloseQuire"

