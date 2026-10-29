# Publishes a new version: sets the version in FriendlyFire.cs, builds, commits, pushes and creates a GitHub release
# with FriendlyFire.exe. Running apps pick it up within ~3 hours (or right away with "ตรวจหาอัปเดต").
#   .\release.ps1 1.0.1 "what changed"
#   .\release.ps1 1.0.1 -StampOnly [-Date 2026-10-30]   (only print the update log with its date lines; nothing is changed)
param([Parameter(Mandatory = $true)][string]$Version, [string]$Notes = '', [datetime]$Date = (Get-Date), [switch]$StampOnly)

$ErrorActionPreference = 'Stop'
$cs = "$PSScriptRoot\FriendlyFire.cs"
$utf8 = New-Object Text.UTF8Encoding $false
$src = [IO.File]::ReadAllText($cs, $utf8) -replace 'public const string Version = "[^"]*";', "public const string Version = `"$Version`";"

# Update log: a "-----dd/MM/yyyy-----" line (Thai year) above each day's newest version.
# The new version gets today's line; a date line written for it on another day (preview) moves down to the previous version.
$nl = if ($src.Contains("`r`n")) { "`r`n" } else { "`n" }
$lines = New-Object Collections.Generic.List[string]; $lines.AddRange([string[]]($src -split '\r?\n'))
$isDay = { param($k) $k -ge 0 -and $k -lt $lines.Count -and $lines[$k] -match '^\s*"-----[0-9/]+-----\\n" \+$' }
$isVer = { param($k) $lines[$k] -match '^\s*"v\d[^"]*\\n" \+$' }
$day = '-----{0:dd/MM}/{1}-----' -f $Date, ($Date.Year + 543)
$i = -1; for ($k = 0; $k -lt $lines.Count; $k++) { if ($lines[$k] -match ('^\s*"v' + [regex]::Escape($Version) + '\\n" \+$')) { $i = $k; break } }
if ($i -ge 0) {
    $indent = ($lines[$i] -replace '^(\s*).*', '$1')
    $old = $null
    if (& $isDay ($i - 1)) { $old = $lines[$i - 1].Trim(); $lines.RemoveAt($i - 1); $i-- }
    $j = -1; for ($k = $i + 1; $k -lt $lines.Count; $k++) { if (& $isVer $k) { $j = $k; break } }
    if ($j -ge 0) {
        if ($old -and -not $old.Contains($day) -and -not (& $isDay ($j - 1))) { $lines.Insert($j, $indent + $old) }
        elseif ((& $isDay ($j - 1)) -and $lines[$j - 1].Contains($day)) { $lines.RemoveAt($j - 1) }   # same day: one line above the newest only
    }
    $lines.Insert($i, $indent + '"' + $day + '\n" +')
}
$src = [string]::Join($nl, $lines)
if ($StampOnly) { ($lines | ? { $_ -match '^\s*"(v\d|-----)' }) -join "`n"; return }
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
