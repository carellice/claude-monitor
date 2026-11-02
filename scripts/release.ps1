# Bumps VERSION, builds the Windows exe and the Android APK, commits, tags and publishes a GitHub release.
# Usage: release.bat [patch|minor|major] [-NoBump] [-Notes "text"]
param(
    [ValidateSet('patch', 'minor', 'major')][string]$Bump = 'patch',
    [switch]$NoBump,
    [string]$Notes
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo

function Step($text) { Write-Host "`n==> $text" -ForegroundColor DarkYellow }

if (-not (Test-Path "$env:USERPROFILE\.claude-monitor\signing.properties")) {
    throw "Chiave di firma Android mancante in $env:USERPROFILE\.claude-monitor (vedi README)."
}
cmd /c "gh auth status >nul 2>&1"
if ($LASTEXITCODE -ne 0) { throw "gh non autenticato: esegui 'gh auth login'." }

$old = [version](Get-Content VERSION -Raw).Trim()
$new = switch ($Bump) {
    'major' { [version]::new($old.Major + 1, 0, 0) }
    'minor' { [version]::new($old.Major, $old.Minor + 1, 0) }
    default { [version]::new($old.Major, $old.Minor, $old.Build + 1) }
}
if ($NoBump) { $new = $old }
$tag = "v$new"
if (git tag --list $tag) { throw "Il tag $tag esiste già." }
Set-Content VERSION "$new" -NoNewline -Encoding ascii
Write-Host "Versione: $old -> $new"

try {
    Step "Build Windows"
    & "$repo\windows\build.ps1"

    Step "Build Android"
    Push-Location "$repo\android"
    try {
        & .\gradlew.bat assembleRelease --console=plain -q
        if ($LASTEXITCODE -ne 0) { throw "Build Android fallita" }
    }
    finally { Pop-Location }
    Copy-Item "$repo\android\app\build\outputs\apk\release\app-release.apk" "$repo\dist\ClaudeMonitor.apk" -Force
}
catch {
    Set-Content VERSION "$old" -NoNewline -Encoding ascii
    throw
}

Step "Commit e tag $tag"
git add -A
git commit -q -m "Release $tag"
git tag $tag
cmd /c "git push -q origin HEAD 2>&1"
if ($LASTEXITCODE -ne 0) { throw "git push fallito" }
cmd /c "git push -q origin $tag 2>&1"
if ($LASTEXITCODE -ne 0) { throw "git push del tag fallito" }

Step "Release GitHub"
$ghArgs = @($tag, "$repo\dist\ClaudeMonitor.exe", "$repo\dist\ClaudeMonitor.apk", '--title', "Claude Monitor $tag")
if ($Notes) { $ghArgs += @('--notes', $Notes) } else { $ghArgs += '--generate-notes' }
gh release create @ghArgs
if ($LASTEXITCODE -ne 0) { throw "gh release create fallito" }
Write-Host "`nPubblicata $tag" -ForegroundColor Green
