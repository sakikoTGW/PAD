# Drift check: does the bundled PAD skill still match the CLI source?
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/check-skill-drift.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/check-skill-drift.ps1 -Repo <PAD repo root>
#
# Reports:
#   1. groups present in Cli.cs but never mentioned in plugin/skills/pad/references/cli.md  (skill gap)
#   2. groups present in Cli.cs but missing from `pad cli help`                            (product gap)
# Exit code: 0 = skill covers every group; 1 = skill gap.
#
# ASCII only: Windows PowerShell 5.1 reads .ps1 as ANSI and would mangle UTF-8.

param(
    [string]$Repo = (Split-Path $PSScriptRoot -Parent),
    [string]$Skill = "plugin\skills\pad"
)

$ErrorActionPreference = "Stop"

$cliPath = Join-Path $Repo "pad\Core\Cli.cs"
if (-not (Test-Path $cliPath)) { throw "not found: $cliPath (pass -Repo <PAD repo root>)" }

$src = Get-Content $cliPath -Raw

# Top-level groups are the `case "x":` lines indented by 16 spaces; subcommands sit deeper.
$groups = [regex]::Matches($src, '(?m)^\s{16}case\s+((?:"[^"]+"|\s+or\s+)+)') |
    ForEach-Object { [regex]::Matches($_.Groups[1].Value, '"([^"]+)"') } |
    ForEach-Object { $_.Groups[1].Value } |
    Sort-Object -Unique

# Global help block: Console.WriteLine(""" <body> """);
$helpMatch = [regex]::Match($src, 'Console\.WriteLine\((?:\s*)"""\r?\n(?<body>[\s\S]*?)\r?\n\s*"""\);')
$help = if ($helpMatch.Success) { $helpMatch.Groups['body'].Value } else { "" }
if (-not $helpMatch.Success) { Write-Host "warn: global help block not found" -ForegroundColor Yellow }

$mdPath = Join-Path (Join-Path $Repo $Skill) "references\cli.md"
if (-not (Test-Path $mdPath)) { throw "not found: $mdPath" }
$md = Get-Content $mdPath -Raw

$notInSkill = @($groups | Where-Object { $md -notmatch ('(?<![\w-])' + [regex]::Escape($_) + '(?![\w-])') })
$notInHelp = @($groups | Where-Object { $help -notmatch ('pad cli ' + [regex]::Escape($_) + '(?=[\s|<\r\n])') })

Write-Host ("groups in Cli.cs : {0}" -f $groups.Count)
Write-Host ("missing in skill : {0}" -f $notInSkill.Count)
Write-Host ("missing in help  : {0}" -f $notInHelp.Count)

if ($notInSkill.Count) { Write-Host ("  skill gap: " + ($notInSkill -join ", ")) -ForegroundColor Yellow }
if ($notInHelp.Count) { Write-Host ("  help gap : " + ($notInHelp -join ", ")) -ForegroundColor DarkYellow }

if ($notInSkill.Count) { exit 1 }
exit 0
