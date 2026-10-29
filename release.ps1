# Publishes a new version: sets the version in FriendlyFire.cs, builds, commits, pushes and creates a GitHub release
# with FriendlyFire.exe. Running apps pick it up within ~3 hours (or right away with "ตรวจหาอัปเดต").
#   .\release.ps1 1.0.1 "what changed"
param([Parameter(Mandatory = $true)][string]$Version, [string]$Notes = '')

$ErrorActionPreference = 'Stop'
$cs = "$PSScriptRoot\FriendlyFire.cs"
$utf8 = New-Object Text.UTF8Encoding $false
$src = [IO.File]::ReadAllText($cs, $utf8) -replace 'public const string Version = "[^"]*";', "public const string Version = `"$Version`";"
[IO.File]::WriteAllText($cs, $src, $utf8)

& "$PSScriptRoot\build.ps1"
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

Push-Location $PSScriptRoot
try {
    git add -A
    git commit -m "v$Version" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>" | Out-Null
    git push
    if (-not $Notes) { $Notes = "Friendly Fire v$Version" }
    gh release create "v$Version" "$PSScriptRoot\FriendlyFire.exe" --title "v$Version" --notes $Notes
}
finally { Pop-Location }
