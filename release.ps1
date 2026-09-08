param(
    [string]$Version = ""  # optional — leave blank to auto-bump patch version
)

$ErrorActionPreference = "Stop"
Set-Location "c:\Users\AY ADVANCE TECH\Documents\VIBE_CODER\kevwe"

$csprojPath = "src\DesktopConcepts.UI\DesktopConcepts.UI.csproj"

# ── 1. Determine the version ──────────────────────────────────────────────────
if ($Version -eq "") {
    $content = Get-Content $csprojPath -Raw
    if ($content -match '<Version>(\d+)\.(\d+)\.(\d+)</Version>') {
        $major = [int]$Matches[1]
        $minor = [int]$Matches[2]
        $patch = [int]$Matches[3] + 1
        $Version = "$major.$minor.$patch"
    } else {
        Write-Host "Could not find a <Version> tag to auto-bump. Pass -Version explicitly." -ForegroundColor Red
        exit 1
    }
}

Write-Host "=== Releasing DesktopConcepts v$Version ===" -ForegroundColor Cyan

# ── 2. Write all version fields into the csproj ───────────────────────────────
# Updates <Version>, <AssemblyVersion>, <FileVersion>, and <InformationalVersion>
# so GetCurrentVersion() in RefreshScheduler reads the correct 3-part string.
$content = Get-Content $csprojPath -Raw
$content = $content -replace '<Version>.*?</Version>',               "<Version>$Version</Version>"
$content = $content -replace '<AssemblyVersion>.*?</AssemblyVersion>', "<AssemblyVersion>$Version.0</AssemblyVersion>"
$content = $content -replace '<FileVersion>.*?</FileVersion>',         "<FileVersion>$Version.0</FileVersion>"
$content = $content -replace '<InformationalVersion>.*?</InformationalVersion>', "<InformationalVersion>$Version</InformationalVersion>"
Set-Content $csprojPath $content -NoNewline:$false
Write-Host "Version bumped to $Version (Version, AssemblyVersion, FileVersion, InformationalVersion all updated)"

# ── 3. Restore using local NuGet cache (offline-safe) ────────────────────────
# Uses --ignore-failed-sources so nuget.org SSL failures are non-fatal.
# The local cache at %USERPROFILE%\.nuget\packages has all required packages.
Write-Host "Restoring packages (offline-safe)..."
$env:NUGET_PACKAGES = "$env:USERPROFILE\.nuget\packages"
dotnet restore "DesktopConcepts.slnx" `
    --ignore-failed-sources `
    --source "$env:USERPROFILE\.nuget\packages"
if ($LASTEXITCODE -ne 0) {
    Write-Host "RESTORE FAILED - aborting." -ForegroundColor Red
    exit 1
}

# ── 4. Run tests (must pass before anything gets released) ────────────────────
Write-Host "Running tests..."
dotnet test tests\DesktopConcepts.Tests\DesktopConcepts.Tests.csproj `
    --no-restore `
    --configuration Debug
if ($LASTEXITCODE -ne 0) {
    Write-Host "TESTS FAILED - aborting." -ForegroundColor Red
    exit 1
}

# ── 5. Publish (offline-safe — uses already-restored packages) ───────────────
Write-Host "Publishing..."
dotnet publish src\DesktopConcepts.UI\DesktopConcepts.UI.csproj `
    --no-restore `
    -c Release `
    -p:PublishProfile=win-x64-release
if ($LASTEXITCODE -ne 0) {
    Write-Host "PUBLISH FAILED - aborting." -ForegroundColor Red
    exit 1
}

$publishedExe = "publish\win-x64\DesktopConcepts.exe"
if (-not (Test-Path $publishedExe)) {
    Write-Host "Published exe not found at '$publishedExe' - aborting." -ForegroundColor Red
    exit 1
}
Write-Host "Published: $publishedExe ($([math]::Round((Get-Item $publishedExe).Length / 1MB, 1)) MB)"

# ── 6. Compile the installer ──────────────────────────────────────────────────
Write-Host "Compiling installer..."
$iscc = "C:\Users\AY ADVANCE TECH\AppData\Local\Programs\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) {
    Write-Host "Inno Setup not found at '$iscc' - aborting." -ForegroundColor Red
    exit 1
}
& $iscc installer\DesktopConcepts.iss
if ($LASTEXITCODE -ne 0) {
    Write-Host "INSTALLER COMPILE FAILED - aborting." -ForegroundColor Red
    exit 1
}

$setupPath = "installer\Output\DesktopConcepts-Setup.exe"
if (-not (Test-Path $setupPath)) {
    Write-Host "DesktopConcepts-Setup.exe missing after ISCC run - aborting." -ForegroundColor Red
    exit 1
}
Write-Host "Installer: $setupPath ($([math]::Round((Get-Item $setupPath).Length / 1MB, 1)) MB)"

# ── 7. Commit only the version bump (not build artifacts) ────────────────────
Write-Host "Committing version bump..."
git add $csprojPath
git commit -m "Release v$Version"
git push
if ($LASTEXITCODE -ne 0) {
    Write-Host "GIT PUSH FAILED - installer was built but not released." -ForegroundColor Red
    exit 1
}

# ── 8. Create the GitHub release with the installer attached ─────────────────
Write-Host "Creating GitHub release v$Version..."
gh release create "v$Version" $setupPath `
    --title "v$Version" `
    --notes "Release v$Version"
if ($LASTEXITCODE -ne 0) {
    Write-Host "GitHub release creation failed." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "=== Release v$Version complete ===" -ForegroundColor Green
