<#
.SYNOPSIS
    把 StartupManager 注册到「开机启动」，或将其移除。

.DESCRIPTION
    写入 HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run，
    无需管理员权限，只对当前用户生效。

    同时会把延迟启动器（StartupManagerDelayed，--delayed-launch）的路径
    修正为当前发布位置，避免它仍指向旧的 Debug 输出目录。

.PARAMETER ExePath
    主程序路径，默认 D:\StartupManager\StartupManager.Gui.exe。

.PARAMETER Remove
    移除启动项，而不是添加。

.PARAMETER Minimized
    开机时以最小化方式启动（加 --minimized 参数，程序会自行处理）。

.EXAMPLE
    .\register-startup.ps1
    .\register-startup.ps1 -Remove
#>
[CmdletBinding()]
param(
    [string]$ExePath = 'D:\StartupManager\StartupManager.Gui.exe',
    [switch]$Remove,
    [switch]$Minimized
)

$ErrorActionPreference = 'Stop'

$RunKey      = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$AppValue    = 'StartupManager'
$DelayedValue = 'StartupManagerDelayed'

function Write-Step([string]$Message) {
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Get-RunValue([string]$Name) {
    (Get-ItemProperty -Path $RunKey -Name $Name -ErrorAction SilentlyContinue).$Name
}

Write-Step '当前状态'

$existingApp     = Get-RunValue $AppValue
$existingDelayed = Get-RunValue $DelayedValue

Write-Host "  启动项 ($AppValue)      : $(if ($existingApp) { $existingApp } else { '(未注册)' })"
Write-Host "  延迟器 ($DelayedValue) : $(if ($existingDelayed) { $existingDelayed } else { '(未注册)' })"

# ---------------------------------------------------------------- remove
if ($Remove) {
    Write-Step '移除启动项'

    foreach ($name in @($AppValue, $DelayedValue)) {
        if (Get-RunValue $name) {
            Remove-ItemProperty -Path $RunKey -Name $name -ErrorAction SilentlyContinue
            Write-Host "  已移除 $name" -ForegroundColor Yellow
        }
    }

    Write-Step '完成'
    Write-Host '  下次开机本程序不会再自动运行。'
    return
}

# ---------------------------------------------------------------- add
Write-Step '校验程序路径'

if (-not (Test-Path -LiteralPath $ExePath)) {
    throw "找不到主程序：$ExePath`n请先运行 publish.ps1 打包，或用 -ExePath 指定路径。"
}

$exeItem = Get-Item -LiteralPath $ExePath
Write-Host "  $($exeItem.FullName)"
Write-Host "  版本 $($exeItem.VersionInfo.FileVersion) · $([math]::Round($exeItem.Length/1KB)) KB"

Write-Step '注册「开机启动」'

$command = "`"$($exeItem.FullName)`""
if ($Minimized) { $command += ' --minimized' }

New-ItemProperty -Path $RunKey -Name $AppValue -Value $command `
    -PropertyType String -Force | Out-Null

$written = Get-RunValue $AppValue
if ($written -ne $command) {
    throw "写入校验失败：期望 '$command'，实际 '$written'。"
}
Write-Host "  [OK] $AppValue = $written" -ForegroundColor Green

# ---------------------------------------------------------------- fix delayed launcher
Write-Step '校正延迟启动器路径'

if ($existingDelayed) {
    $newDelayed = "`"$($exeItem.FullName)`" --delayed-launch"

    # Only rewrite when it points somewhere else (e.g. an old Debug output folder).
    $oldExe = ($existingDelayed -replace '^"([^"]+)".*$', '$1')
    $oldExists = Test-Path -LiteralPath $oldExe

    if ($existingDelayed -eq $newDelayed) {
        Write-Host '  已指向当前发布路径，无需修改。'
    }
    elseif ($oldExe -eq $exeItem.FullName) {
        Write-Host '  路径一致，仅参数不同；保持原样。'
    }
    else {
        New-ItemProperty -Path $RunKey -Name $DelayedValue -Value $newDelayed `
            -PropertyType String -Force | Out-Null

        $check = Get-RunValue $DelayedValue
        if ($check -ne $newDelayed) { throw '延迟启动器路径校正失败。' }

        Write-Host "  [已校正] $DelayedValue" -ForegroundColor Yellow
        Write-Host "    旧: $existingDelayed" -ForegroundColor DarkGray
        Write-Host "    新: $newDelayed"     -ForegroundColor DarkGray
        if (-not $oldExists) {
            Write-Host '    (旧路径已不存在，原来会导致延迟启动失效)' -ForegroundColor DarkGray
        }
    }
}
else {
    Write-Host '  未注册延迟器，跳过（可在程序「延迟启动」页开启）。'
}

# ---------------------------------------------------------------- report
Write-Step '完成'

Write-Host '  注册表位置 : HKCU\Software\Microsoft\Windows\CurrentVersion\Run'
Write-Host "  启动命令   : $(Get-RunValue $AppValue)"
Write-Host ''
Write-Host '  下次登录 Windows 时本程序会自动启动。' -ForegroundColor Green
Write-Host '  可在「任务管理器 → 启动应用」中查看/禁用。' -ForegroundColor DarkGray
Write-Host '  如需撤销：.\register-startup.ps1 -Remove' -ForegroundColor DarkGray
