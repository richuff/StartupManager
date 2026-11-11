<#
.SYNOPSIS
    打包 StartupManager 并发布到 D:\StartupManager。

.DESCRIPTION
    以 Release 自包含方式发布 GUI（连同 MCP 服务端），输出到 D:\StartupManager。
    目标机器无需预装 .NET 运行时。

.PARAMETER OutputDir
    发布目标目录，默认 D:\StartupManager。

.PARAMETER SingleFile
    是否发布为单文件 exe。默认 \(false\)：多文件更利于 MCP 子进程与图标资源正常工作。

.PARAMETER NoClean
    保留目标目录中的旧文件（默认会先清空，避免混入过期程序集）。

.PARAMETER SkipTests
    跳过发布后的冒烟验证（默认会启动一次程序确认无崩溃）。

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -OutputDir 'D:\StartupManager' -SingleFile

.NOTES
    受限环境可用环境变量追加 MSBuild 参数：
        $env:SM_EXTRA_BUILD_ARGS = '-m:1 -nodeReuse:false'
#>
[CmdletBinding()]
param(
    [string]$OutputDir = 'D:\StartupManager',
    [switch]$SingleFile,
    [switch]$NoClean,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot   = $PSScriptRoot
$guiProject = Join-Path $repoRoot 'src\StartupManager.Gui\StartupManager.Gui.csproj'
$solution   = Join-Path $repoRoot 'StartupManager.slnx'
$appExeName = 'StartupManager.Gui.exe'

# 额外 MSBuild 参数，供受限环境（单节点构建、禁用节点复用）复用本脚本。
# 例：$env:SM_EXTRA_BUILD_ARGS = '-m:1 -nodeReuse:false'
$extraBuildArgs = @()
if ($env:SM_EXTRA_BUILD_ARGS) {
    $extraBuildArgs = @($env:SM_EXTRA_BUILD_ARGS -split '\s+' | Where-Object { $_ })
}

function Write-Step([string]$Message) {
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Assert-Path([string]$Path, [string]$What) {
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$What 不存在：$Path"
    }
}

# ---------------------------------------------------------------- preflight
Write-Step '检查环境'

Assert-Path $guiProject 'GUI 项目'
Assert-Path $solution '解决方案'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '未找到 dotnet SDK，请先安装 .NET 10 SDK。'
}

$sdkVersion = (& dotnet --version).Trim()
Write-Host "  dotnet SDK : $sdkVersion"
Write-Host "  仓库根目录 : $repoRoot"
Write-Host "  输出目录   : $OutputDir"

# A running instance would lock the output files and make the copy fail.
$running = Get-Process -Name 'StartupManager.Gui' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host ''
    Write-Host "  检测到正在运行的实例（PID: $($running.Id -join ', ')），正在关闭…" -ForegroundColor Yellow
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
}

# ---------------------------------------------------------------- build
Write-Step '编译整个解决方案（Release）'

& dotnet build $solution -c Release --nologo -v minimal @extraBuildArgs
if ($LASTEXITCODE -ne 0) { throw "编译失败（退出码 $LASTEXITCODE）。" }

# ---------------------------------------------------------------- publish
Write-Step '发布自包含程序'

$singleFileValue = if ($SingleFile.IsPresent) { 'true' } else { 'false' }

$publishArgs = @(
    'publish', $guiProject,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    "-p:PublishSingleFile=$singleFileValue",
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=false',
    '-p:DebugType=none',
    '-p:DebugSymbols=false',
    '-o', $OutputDir,
    '--nologo',
    '-v', 'minimal'
) + $extraBuildArgs

Write-Host "  dotnet $($publishArgs -join ' ')"

# Clean the GUI project too so no stale assembly from an older build survives.
& dotnet clean $guiProject -c Release -v quiet @extraBuildArgs 2>&1 | Out-Null

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "发布失败（退出码 $LASTEXITCODE）。" }

# ---------------------------------------------------------------- tidy
Write-Step '整理输出目录'

if (-not $NoClean) {
    # PDBs are useless in a release drop.
    Get-ChildItem -LiteralPath $OutputDir -Filter '*.pdb' -File -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

$appExe = Join-Path $OutputDir $appExeName
Assert-Path $appExe '发布后的主程序'

# The MCP server ships as its own publish so we know all of its runtime files land.
# Always re-publish: skipping when the file already exists would silently ship a stale
# binary that no longer matches the current sources.
$mcpProject = Join-Path $repoRoot 'src\StartupManager.Mcp\StartupManager.Mcp.csproj'
$mcpExe = Join-Path $OutputDir 'StartupManager.Mcp.exe'

Write-Step '发布 MCP 服务端'

$mcpTmp = Join-Path $env:TEMP ('sm-mcp-' + [Guid]::NewGuid().ToString('N'))
try {
    # Clean first: incremental publish can keep a stale MCP binary that no longer
    # matches the sources, silently shipping fixes that never took effect.
    & dotnet clean $mcpProject -c Release -v quiet @extraBuildArgs 2>&1 | Out-Null
    & dotnet publish $mcpProject -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -p:DebugType=none -p:DebugSymbols=false `
        -o $mcpTmp --nologo -v minimal @extraBuildArgs
    if ($LASTEXITCODE -ne 0) { throw 'MCP 服务端发布失败。' }

    # Copy only the MCP payload; its runtime .dlls are already in the GUI drop.
    $mcpFiles = Get-ChildItem -LiteralPath $mcpTmp -Filter 'StartupManager.Mcp.*' -File
    if ($mcpFiles.Count -eq 0) { throw 'MCP 输出中没有找到 StartupManager.Mcp.* 文件。' }

    $mcpFiles | Copy-Item -Destination $OutputDir -Force
    foreach ($f in $mcpFiles) { Write-Host "  + $($f.Name)" -ForegroundColor DarkGray }
}
finally {
    Remove-Item -LiteralPath $mcpTmp -Recurse -Force -ErrorAction SilentlyContinue
}

if (-not (Test-Path -LiteralPath $mcpExe)) {
    throw "MCP 服务端未能放入输出目录：$mcpExe"
}

# ---------------------------------------------------------------- verify
if (-not $SkipTests) {
    Write-Step '冒烟验证（启动 5 秒确认不崩溃）'

    $proc = Start-Process -FilePath $appExe -PassThru
    $alive = $true
    for ($i = 0; $i -lt 5; $i++) {
        Start-Sleep -Seconds 1
        if ($proc.HasExited) { $alive = $false; break }
    }

    if (-not $alive) {
        throw "程序启动后立即退出（退出码 $($proc.ExitCode)），发布产物可能有问题。"
    }

    Write-Host '  程序可正常启动。'
    $proc | Stop-Process -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------- report
Write-Step '完成'

$files  = Get-ChildItem -LiteralPath $OutputDir -File -Recurse
$totalMb = [math]::Round((($files | Measure-Object -Property Length -Sum).Sum / 1MB), 1)

Write-Host "  主程序 : $appExe"
Write-Host "  MCP    : $mcpExe"
Write-Host "  文件数 : $($files.Count)"
Write-Host "  总大小 : $totalMb MB"
Write-Host ''
Write-Host '  直接双击 StartupManager.Gui.exe 即可运行（已自带运行时，无需安装 .NET）。' -ForegroundColor DarkGray
Write-Host '  若提示「禁止运行脚本」，请使用：powershell -ExecutionPolicy Bypass -File .\publish.ps1' -ForegroundColor DarkGray
Write-Host '  提示：把 D:\StartupManager 加入 PATH 后，可直接在终端运行 StartupManager.Gui。' -ForegroundColor DarkGray
