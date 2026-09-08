param(
    [string]$Version = ""  # leave blank to auto-bump patch
)

$ErrorActionPreference = "Stop"
Set-Location "c:\Users\AY ADVANCE TECH\Documents\VIBE_CODER\kevwe"

$csprojPath    = "src\Quire.UI\Quire.UI.csproj"
$slnxPath      = "Quire.slnx"
$offlineConfig = "NuGet.Offline.Config"
$publishedExe  = "publish\win-x64\Quire.exe"
$issPath       = "installer\Quire.iss"
$setupExe      = "installer\Output\Quire-Setup.exe"
$iscc          = "C:\Users\AY ADVANCE TECH\AppData\Local\Programs\Inno Setup 6\ISCC.exe"

$env:NUGET_PACKAGES = "$env:USERPROFILE\.nuget\packages"

# ── 1. Determine version ──────────────────────────────────────────────────────
if ($Version -eq "") {
    $content = Get-Content $csprojPath -Raw
    if ($content -match '<Version>(\d+)\.(\d+)\.(\d+)</Version>') {
        $Version = "$([int]$Matches[1]).$([int]$Matches[2]).$([int]$Matches[3] + 1)"
    } else {
        Write-Host "ERROR: Cannot find <Version> tag. Pass -Version explicitly." -ForegroundColor Red; exit 1
    }
}

Write-Host "=== Releasing Quire v$Version ===" -ForegroundColor Cyan

# ── 2. Bump all version fields in the csproj ─────────────────────────────────
$content = Get-Content $csprojPath -Raw
$content = $content -replace '<Version>.*?</Version>',                           "<Version>$Version</Version>"
$content = $content -replace '<AssemblyVersion>.*?</AssemblyVersion>',           "<AssemblyVersion>$Version.0</AssemblyVersion>"
$content = $content -replace '<FileVersion>.*?</FileVersion>',                   "<FileVersion>$Version.0</FileVersion>"
$content = $content -replace '<InformationalVersion>.*?</InformationalVersion>', "<InformationalVersion>$Version</InformationalVersion>"
Set-Content $csprojPath $content
Write-Host "Version bumped to $Version in csproj"

# ── 3. Bump version in Quire.iss ──────────────────────────────────────────────
$iss = Get-Content $issPath -Raw
$iss = $iss -replace '#define AppVersion ".*?"', "#define AppVersion ""$Version"""
Set-Content $issPath $iss
Write-Host "Version bumped to $Version in Quire.iss"

# ── 4. Restore (offline-safe) ─────────────────────────────────────────────────
Write-Host "Restoring packages (offline)..."
dotnet restore $slnxPath --configfile $offlineConfig
if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: Restore failed." -ForegroundColor Red; exit 1 }

# ── 5. Tests ──────────────────────────────────────────────────────────────────
Write-Host "Running tests..."
dotnet test tests\Quire.Tests\Quire.Tests.csproj --no-restore --configuration Debug
if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: Tests failed." -ForegroundColor Red; exit 1 }

# ── 6. Publish ────────────────────────────────────────────────────────────────
Write-Host "Publishing..."
dotnet publish $csprojPath --no-restore -c Release -p:PublishProfile=win-x64-release
if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: Publish failed." -ForegroundColor Red; exit 1 }
if (-not (Test-Path $publishedExe)) {
    Write-Host "ERROR: '$publishedExe' not found after publish." -ForegroundColor Red; exit 1
}
Write-Host "Published: $publishedExe ($([math]::Round((Get-Item $publishedExe).Length/1MB,1)) MB)"

# ── 7. Compile installer ──────────────────────────────────────────────────────
Write-Host "Compiling installer..."
if (-not (Test-Path $iscc)) { Write-Host "ERROR: Inno Setup not found at '$iscc'." -ForegroundColor Red; exit 1 }
& $iscc $issPath
if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: Installer compile failed." -ForegroundColor Red; exit 1 }
if (-not (Test-Path $setupExe)) { Write-Host "ERROR: '$setupExe' missing after ISCC." -ForegroundColor Red; exit 1 }
Write-Host "Installer: $setupExe ($([math]::Round((Get-Item $setupExe).Length/1MB,1)) MB)"

# ── 8. Commit version bump only ───────────────────────────────────────────────
Write-Host "Committing version bump..."
git add $csprojPath $issPath
git commit -m "Release v$Version"
git push
if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: git push failed." -ForegroundColor Red; exit 1 }

# ── 9. GitHub release ─────────────────────────────────────────────────────────
Write-Host "Creating GitHub release v$Version..."
gh release create "v$Version" $setupExe --title "v$Version" --notes "Release v$Version"
if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: GitHub release creation failed." -ForegroundColor Red; exit 1 }

Write-Host ""
Write-Host "=== Release v$Version complete ===" -ForegroundColor Green
