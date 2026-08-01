; DesktopConcepts Installer Script
; Requires Inno Setup Compiler (http://jrsoftware.org/isdl.php)
;
; This script packages the published .NET 8 self-contained executable
; from src/publish/win-x64/ into a proper Windows installer.

#define AppName "DesktopConcepts"
#define AppPublisher "Kevwe"
#define AppVersion "1.0.0"
#define AppExeName "DesktopConcepts.exe"
#define AppPublisherURL "https://github.com/highnine699-del/desktopconcepts"
#define AppSupportURL "https://github.com/highnine699-del/desktopconcepts/issues"

[Setup]
; Basic installer information
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppPublisherURL}
AppSupportURL={#AppSupportURL}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
OutputBaseFilename=DesktopConcepts-Setup
Compression=lzma2
SolidCompression=yes
; Require admin rights for Program Files installation
PrivilegesRequired=admin
; Show a license page (you can replace this with your actual license file)
LicenseFile=
; Modern wizard interface
WizardStyle=modern
WizardImageFile=
WizardSmallImageFile=

[Files]
; Package everything from the publish output
Source: "..\src\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Start Menu shortcut
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"

[Registry]
; Optional: Auto-start on Windows startup (user can choose during install)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExeName}"""; \
    Flags: uninsdeletevalue; Tasks: autostart

[Tasks]
; Optional desktop shortcut (unchecked by default - this is a quiet widget)
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked
; Optional auto-start on Windows startup (checked by default - this is the main use case)
Name: "autostart"; Description: "Start {#AppName} automatically when Windows starts"; GroupDescription: "Startup:"

[Icons]
; Desktop shortcut (only if user selected the task)
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; Run the app after installation (user can uncheck)
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Note: We do NOT automatically delete %AppData%\DesktopConcepts on uninstall.
; User data (concepts, settings, history) is preserved by default.
; If you want to offer optional cleanup, add a custom page with a checkbox.

[UninstallRun]
; Stop the app before uninstalling (if it's running)
Filename: "{cmd}"; Parameters: "/c taskkill /F /IM {#AppExeName}"; Flags: runhidden

[Code]
// Optional: Add a custom uninstall page to ask about user data cleanup
// Uncomment and customize if you want this feature
//
// function ShouldDeleteAppData(): Boolean;
// begin
//   Result := MsgBox('Do you also want to delete your saved concepts, settings, and history?' + #13#10 +
//                    'This will remove everything under %AppData%\DesktopConcepts.' + #13#10 +
//                    'Click Yes to delete, No to keep your data.',
//                    mbConfirmation, MB_YESNO) = IDYES;
// end;
//
// procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
// var
//   AppDataPath: String;
// begin
//   if CurUninstallStep = usUninstall then
//   begin
//     if ShouldDeleteAppData() then
//     begin
//       AppDataPath := ExpandConstant('{userappdata}\DesktopConcepts');
//       if DirExists(AppDataPath) then
//       begin
//         if DeleteFile(AppDataPath + '\Settings.json') then;
//         if DeleteFile(AppDataPath + '\History.md') then;
//         if DeleteFile(AppDataPath + '\buffer.json') then;
//         if DeleteFile(AppDataPath + '\last_run.txt') then;
//         if DelTree(AppDataPath + '\Logs', True, True, True) then;
//         if DelTree(AppDataPath + '\Models', True, True, True) then;
//         // Remove the directory if empty
//         RemoveDir(AppDataPath);
//       end;
//     end;
//   end;
// end;
