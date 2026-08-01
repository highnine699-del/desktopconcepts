param(
    [string]$Version = ""  # optional — leave blank to auto-bump patch version
)

$ErrorActionPreference = "Stop"
Set-Location "c:\Users\AY ADVANCE TECH\Documents\VIBE_CODER\kevwe"

$csprojPath = "src\DesktopConcepts.UI\DesktopConcepts.UI.csproj"

# 1. Determine the version — auto-bump patch if not explicitly given
if ($Version -eq "") {
    $content = Get-Content $csprojPath -Raw
    if ($content -match '<Version>(\d+)\.(\d+)\.(\d+)</Version>') {
        $major = [int]$Matches[1]
        $minor = [int]$Matches[2]
        $patch = [int]$Matches[3] + 1
        $Version = "$major.$minor.$patch"
    } else {
        Write-Host "Could not find a <Version> tag to auto-bump - pass -Version explicitly." -ForegroundColor Red
        exit 1
    }
}

Write-Host "=== Releasing DesktopConcepts v$Version ===" -ForegroundColor Cyan

# 2. Write the new version into the csproj
$content = Get-Content $csprojPath -Raw
$content = $content -replace '<Version>.*?</Version>', "<Version>$Version</Version>"
Set-Content $csprojPath $content
Write-Host "Version bumped to $Version"

# 3. Tests must pass before anything gets released
Write-Host "Running tests..."
dotnet test tests\DesktopConcepts.Tests\DesktopConcepts.Tests.csproj
if ($LASTEXITCODE -ne 0) { Write-Host "TESTS FAILED - aborting." -ForegroundColor Red; exit 1 }

# 4. Publish
Write-Host "Publishing..."
dotnet publish src\DesktopConcepts.UI\DesktopConcepts.UI.csproj -c Release -p:PublishProfile=win-x64-release
if ($LASTEXITCODE -ne 0) { Write-Host "PUBLISH FAILED - aborting." -ForegroundColor Red; exit 1 }

# 5. Compile the installer
Write-Host "Compiling installer..."
& "C:\Users\AY ADVANCE TECH\AppData\Local\Programs\Inno Setup 6\ISCC.exe" installer\DesktopConcepts.iss
if ($LASTEXITCODE -ne 0) { Write-Host "INSTALLER COMPILE FAILED - aborting." -ForegroundColor Red; exit 1 }

$setupPath = "installer\Output\DesktopConcepts-Setup.exe"
if (-not (Test-Path $setupPath)) { Write-Host "Setup.exe missing - aborting." -ForegroundColor Red; exit 1 }

# 6. Commit and push the version bump
git add -A
git commit -m "Release v$Version"
git push

# 7. Create the GitHub release with the installer attached
Write-Host "Creating GitHub release v$Version..."
gh release create "v$Version" $setupPath --title "v$Version" --notes "Release v$Version"

Write-Host "=== Release v$Version complete ===" -ForegroundColor Green
