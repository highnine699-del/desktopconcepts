; Quire Installer Script
; Requires Inno Setup Compiler (http://jrsoftware.org/isdl.php)

#define AppName "Quire"
#define AppPublisher "Kevwe"
#define AppVersion "1.0.9"
#define AppExeName "Quire.exe"
#define AppPublisherURL "https://github.com/highnine699-del/quire-app"
#define AppSupportURL "https://github.com/highnine699-del/quire-app/issues"

[Setup]
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppPublisherURL}
AppSupportURL={#AppSupportURL}

; Per-user install to %LocalAppData%\Programs\Quire — no UAC prompt, no admin required.
; This is correct for a desktop widget. Admin installs to Program Files were causing
; silent crashes because the app needs to write to %AppData% on first run.
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

[Files]
; Source from the correct publish output directory (repo root publish\win-x64\)
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExeName}"""; \
    Flags: uninsdeletevalue; Tasks: autostart

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked
Name: "autostart"; Description: "Start {#AppName} automatically when Windows starts"; GroupDescription: "Startup:"

[Icons]
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; %AppData%\Quire is NOT deleted on uninstall — user data is preserved.

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c taskkill /F /IM {#AppExeName}"; Flags: runhidden
