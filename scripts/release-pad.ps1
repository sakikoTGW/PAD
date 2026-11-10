# release-pad.ps1 — cut a PAD release in one go.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/release-pad.ps1 -Version 0.5.1 -NotesFile notes.md
#
# Steps: sync from dev tree (optional) -> bump Pad.csproj -> publish single-file exe
#        -> latest.json (sha256/size) -> commit + push -> gh release create + upload
#        -> verify the public feed.
#
# NOTE: Windows PowerShell 5.1 reads .ps1 as ANSI. This file MUST stay UTF-8 with BOM,
#       otherwise the Chinese text below turns into mojibake when executed by powershell.exe.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = "",
    [string]$NotesFile = "",
    [string]$SyncFrom = "",
    [string]$Repo = "sakikoTGW/PAD",
    [string]$RepoDir = "E:\pack-agent-pad",
    [string]$Work = "E:\tmp\pad-release",
    [switch]$SkipPublish,
    [switch]$SkipPush,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Fail($text) { Write-Host "!!  $text" -ForegroundColor Red; exit 1 }
function Run([scriptblock]$block) { & $block; if ($LASTEXITCODE -ne 0) { Fail "command failed (exit $LASTEXITCODE)" } }

if ($Version -notmatch '^\d+\.\d+\.\d+$') { Fail "version must look like 1.2.3 (got '$Version')" }
$tag = "v$Version"
$exeName = "pack-agent-for-DSH.exe"

if (-not (Test-Path (Join-Path $RepoDir 'pad\Pad.csproj'))) { Fail "repo not found: $RepoDir" }

# 1. tag must not exist yet (release is append-only; re-running should be an explicit act)
$remoteTags = & git -C $RepoDir ls-remote --tags origin "refs/tags/$tag" 2>$null
if ($remoteTags) { Fail "tag $tag already exists on origin — bump the version instead" }

# 2. optional: mirror the dev tree into the release repo (source of truth = dev monorepo)
if ($SyncFrom) {
    if (-not (Test-Path $SyncFrom)) { Fail "sync source not found: $SyncFrom" }
    Step "sync $SyncFrom -> $RepoDir\pad"
    if (-not $DryRun) {
        & robocopy $SyncFrom (Join-Path $RepoDir 'pad') /MIR /XD bin obj .vs /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { Fail "robocopy failed ($LASTEXITCODE)" }
    }
}

# 3. bump the app version (About page / cli version read it from the assembly)
$csproj = Join-Path $RepoDir 'pad\Pad.csproj'
$raw = [IO.File]::ReadAllText($csproj)
$bumped = [regex]::Replace($raw, '<Version>[^<]+</Version>', "<Version>$Version</Version>")
if ($bumped -eq $raw -and $raw -notmatch "<Version>$([regex]::Escape($Version))</Version>") {
    Fail "could not find <Version> in $csproj"
}
Step "version -> $Version"
if (-not $DryRun) { [IO.File]::WriteAllText($csproj, $bumped, (New-Object System.Text.UTF8Encoding($false))) }

# 4. release notes: notes file wins, inline notes second, CHANGELOG last section otherwise
$notesText = ""
if ($NotesFile -and (Test-Path $NotesFile)) { $notesText = [IO.File]::ReadAllText($NotesFile) }
elseif ($Notes) { $notesText = $Notes }
if ([string]::IsNullOrWhiteSpace($notesText)) { $notesText = "PAD $Version" }

# 5. publish: self-contained single file, one exe, no pdb
$publishDir = Join-Path $Work 'publish'
$assetsDir = Join-Path $Work "release-$Version"
if (-not $DryRun) {
    Step "publish (single file, win-x64)"
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    if (Test-Path $assetsDir) { Remove-Item $assetsDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $assetsDir | Out-Null
    & dotnet publish (Join-Path $RepoDir 'pad\Pad.csproj') -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $publishDir -v q --nologo
    if ($LASTEXITCODE -ne 0) { Fail "dotnet publish failed" }

    $built = Get-ChildItem $publishDir -Filter '*.exe' | Select-Object -First 1
    if (-not $built) { Fail "no exe produced" }
    $exePath = Join-Path $assetsDir $exeName
    Copy-Item $built.FullName $exePath -Force

    # 6. latest.json — schema is fixed by Core/PadUpdate.cs
    $hash = (Get-FileHash $exePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $size = (Get-Item $exePath).Length
    $url = "https://github.com/$Repo/releases/download/$tag/$exeName"
    $feed = [ordered]@{
        schema = 'pack-agent.pad-update/v1'
        version = $Version
        url = $url
        sha256 = $hash
        size = $size
        notes = ($notesText -split "`r?`n")[0]
    } | ConvertTo-Json
    [IO.File]::WriteAllText((Join-Path $assetsDir 'latest.json'), $feed, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "    exe: $([math]::Round($size / 1MB, 1)) MB  sha256: $($hash.Substring(0, 12))..."
}

# 7. commit the version bump (code goes first, release points at a commit that exists)
if (-not $DryRun) {
    Step "commit + push"
    & git -C $RepoDir add pad/Pad.csproj CHANGELOG.md README.md
    $dirty = & git -C $RepoDir status --porcelain
    if ($dirty) {
        & git -C $RepoDir commit -q -m "release: v$Version"
        if ($LASTEXITCODE -ne 0) { Fail "git commit failed" }
    }
    if (-not $SkipPush) {
        & git -C $RepoDir push origin HEAD:main
        if ($LASTEXITCODE -ne 0) { Fail "git push failed" }
    }
}

# 8. create the release and attach both assets (old gh takes one file per upload)
if (-not $DryRun) {
    Step "create release $tag"
    $notesFile = Join-Path $assetsDir 'notes.md'
    [IO.File]::WriteAllText($notesFile, $notesText, (New-Object System.Text.UTF8Encoding($false)))
    & gh release create $tag --title "PAD $Version" --notes-file $notesFile --target main -R $Repo
    if ($LASTEXITCODE -ne 0) { Fail "gh release create failed (tag $tag)" }

    Step "upload assets"
    & gh release upload $tag (Join-Path $assetsDir $exeName) -R $Repo --clobber
    if ($LASTEXITCODE -ne 0) { Fail "upload exe failed" }
    & gh release upload $tag (Join-Path $assetsDir 'latest.json') -R $Repo --clobber
    if ($LASTEXITCODE -ne 0) { Fail "upload latest.json failed" }
}

# 9. verify: the public feed must parse and match the version + hash we just built
if (-not $DryRun) {
    Step "verify feed"
    $feedUrl = "https://github.com/$Repo/releases/latest/download/latest.json"
    $resp = Invoke-WebRequest -Uri $feedUrl -UseBasicParsing -MaximumRedirection 5 -TimeoutSec 30
    $bytes = if ($resp.Content -is [byte[]]) { $resp.Content } else { [Text.Encoding]::UTF8.GetBytes([string]$resp.Content) }
    $info = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
    if ($info.version -ne $Version) { Fail "feed says $($info.version), expected $Version" }
    $respExe = Invoke-WebRequest -Uri $info.url -Method Head -UseBasicParsing -MaximumRedirection 5 -TimeoutSec 30
    if ($respExe.StatusCode -ne 200) { Fail "exe url not reachable: $($info.url)" }

    Write-Host ""
    Write-Host "done: https://github.com/$Repo/releases/tag/$tag" -ForegroundColor Green
    Write-Host "  version $($info.version)  size $($info.size)  sha256 $($info.sha256.Substring(0, 12))..." -ForegroundColor Green
    Write-Host "  提醒：本机 pad.json 的 other.updateFeed 若是旧值（pack-agent），要手动改成 $feedUrl" -ForegroundColor Yellow
}
else { Write-Host "dry run: nothing was published" -ForegroundColor Yellow }
