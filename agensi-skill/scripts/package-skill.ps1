<#
.SYNOPSIS
    Package the StartupManager MCP skill as an Agensi-ready ZIP.
.DESCRIPTION
    Agensi forbids shipping any compiled binary (.exe/.dll/.pdb etc.) and requires
    the ZIP to be < 50 MB with < 1000 files. This script therefore packages
    SOURCE ONLY:

      - the entire src/ tree (.cs/.csproj/.xaml/icons), excluding bin/ and obj/
      - repo-level non-binary files (StartupManager.slnx, README.md, etc.)
      - SKILL.md

    Build/install steps live in SKILL.md; the user compiles the exe locally via
    `dotnet publish`. No exe/dll/pdb/zip is ever included.
#>
[CmdletBinding()]
param(
    [string]$SkillDir  = (Split-Path $PSScriptRoot -Parent),
    [string]$RepoRoot  = (Split-Path $SkillDir -Parent),
    [string]$OutputZip = (Join-Path $SkillDir 'dist\startup-manager-skill.zip')
)

$ErrorActionPreference = 'Stop'

$skillMd = Join-Path $SkillDir 'SKILL.md'
if (-not (Test-Path $skillMd)) { throw "Missing SKILL.md: $skillMd" }
if (-not (Test-Path (Join-Path $RepoRoot 'StartupManager.slnx'))) {
    throw "Missing solution: $(Join-Path $RepoRoot 'StartupManager.slnx')"
}

$tmp = Join-Path $env:TEMP ('sm-skill-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
try {
    # 1) source tree, excluding build output (bin/obj)
    $srcDest = Join-Path $tmp 'src'
    New-Item -ItemType Directory -Path $srcDest -Force | Out-Null
    $srcRoot = Join-Path $RepoRoot 'src'
    Get-ChildItem $srcRoot -Recurse -File |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        ForEach-Object {
            $rel = $_.FullName.Substring($srcRoot.Length).TrimStart('\','/')
            $target = Join-Path $srcDest $rel
            New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
            Copy-Item $_.FullName $target -Force
        }

    # 2) repo-level non-binary files (solution, readme; exclude exe/dll/pdb/zip/log)
    @('StartupManager.slnx','README.md','DEPLOY.md','.gitignore') | ForEach-Object {
        $f = Join-Path $RepoRoot $_
        if (Test-Path $f) { Copy-Item $f $tmp -Force }
    }

    # 3) SKILL.md
    Copy-Item $skillMd (Join-Path $tmp 'SKILL.md') -Force

    # 4) safety: never ship a forbidden binary
    $banned = Get-ChildItem $tmp -Recurse -File | Where-Object {
        $_.Extension -match '\.(exe|dll|pdb|so|dylib|wasm|jar|zip)$'
    }
    if ($banned) {
        $list = ($banned | ForEach-Object { $_.FullName.Replace($tmp,'') }) -join "`n"
        throw "Forbidden binary found in package, aborting:`n$list"
    }

    $dist = Split-Path $OutputZip
    New-Item -ItemType Directory -Path $dist -Force | Out-Null
    if (Test-Path $OutputZip) { Remove-Item $OutputZip -Force }

    Compress-Archive -Path (Join-Path $tmp '*') -DestinationPath $OutputZip -Force

    $files  = Get-ChildItem $tmp -Recurse -File
    $totalMb = [math]::Round((($files | Measure-Object -Property Length -Sum).Sum / 1MB), 2)
    Write-Host "Created: $OutputZip" -ForegroundColor Green
    Write-Host ("  Contents: src/ + StartupManager.slnx/README.md + SKILL.md ({0} files, {1} MB)" -f $files.Count, $totalMb)
    Write-Host "  No .exe/.dll/.pdb - satisfies Agensi binary ban and size limit." -ForegroundColor DarkGray
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
