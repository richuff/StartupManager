<#
.SYNOPSIS
    一键发布：验证构建 → 打包到 D:\StartupManager → 提交 → 推送。

.DESCRIPTION
    把重复的手工步骤收敛成一条命令：
      1. 以 Release 编译整个解决方案（失败即中止，不会提交坏代码）。
      2. 调用 publish.ps1 自包含发布到目标目录，并冒烟启动验证。
      3. git add -A + 提交（工作区干净则跳过提交）。
      4. 推送当前分支到 origin。
    任一步失败立即中止，后面的步骤不会执行。

.PARAMETER Message
    提交信息。省略时自动生成，如 "chore: 发布 2026-11-11 14:03"。

.PARAMETER OutputDir
    发布目标目录，默认 D:\StartupManager。

.PARAMETER SkipPublish
    只提交推送，不编译不打包。

.PARAMETER SkipPush
    只提交，不推送。

.PARAMETER SkipTests
    跳过发布后的冒烟启动验证。

.PARAMETER AllowEmpty
    即使没有代码改动也创建一个空提交（默认自动跳过提交）。

.EXAMPLE
    .\release.ps1 -Message "feat: MCP 记录改为弹窗编辑"

.EXAMPLE
    .\release.ps1 -SkipPush

.NOTES
    受限环境可用环境变量追加 MSBuild 参数：
        $env:SM_EXTRA_BUILD_ARGS = '-m:1 -nodeReuse:false'
#>
[CmdletBinding()]
param(
    [string]$Message,
    [string]$OutputDir = 'D:\StartupManager',
    [switch]$SkipPublish,
    [switch]$SkipPush,
    [switch]$SkipTests,
    [switch]$AllowEmpty
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = $PSScriptRoot
$solution = Join-Path $repoRoot 'StartupManager.slnx'
$publishScript = Join-Path $repoRoot 'publish.ps1'

# ---------------------------------------------------------------- 环境兼容
# 部分机器上 SDK 缺少 workload locator 目录（MSB4276），并限制子进程复用。
$env:MSBuildEnableWorkloadResolver = 'false'
$env:MSBuildDisableNodeReuse = '1'
if (-not $env:SM_EXTRA_BUILD_ARGS) {
    $env:SM_EXTRA_BUILD_ARGS = '-m:1 -nodeReuse:false'
}
$extraBuildArgs = @($env:SM_EXTRA_BUILD_ARGS -split '\s+' | Where-Object { $_ })

function Write-Step([string]$Text) {
    Write-Host ''
    Write-Host "==> $Text" -ForegroundColor Cyan
}

function Write-Ok([string]$Text) {
    Write-Host "  [OK] $Text" -ForegroundColor Green
}

function Write-Skip([string]$Text) {
    Write-Host "  [--] $Text" -ForegroundColor DarkGray
}

function Assert-Path([string]$Path, [string]$What) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "$What 不存在：$Path" }
}

# git 会把换行规范化之类的提示写到 stderr。默认的 Stop 策略下这些提示
# 会被当成终止错误，所以调用 git 时降级为 Continue 并把输出收进对象里。
function Invoke-Git([string[]]$GitArgs) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & git @GitArgs 2>&1
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    return [pscustomobject]@{
        Code   = $code
        Output = @($output | ForEach-Object { "$_" })
    }
}

# ---------------------------------------------------------------- 0. 预检
Write-Step '检查仓库状态'

Assert-Path $solution '解决方案'
Assert-Path $publishScript 'publish.ps1'

Push-Location $repoRoot
try {
    $branchResult = Invoke-Git @('rev-parse', '--abbrev-ref', 'HEAD')
    if ($branchResult.Code -ne 0) { throw '不是有效的 git 仓库。' }
    $branch = ($branchResult.Output | Select-Object -First 1).Trim()

    Write-Host "  分支     : $branch"
    Write-Host "  发布目录 : $OutputDir"

    $statusResult = Invoke-Git @('status', '--porcelain')
    if ($statusResult.Code -ne 0) { throw 'git status 执行失败。' }
    $dirty = @($statusResult.Output | Where-Object { $_ })
    $hasChanges = $dirty.Count -gt 0

    if ($hasChanges) {
        Write-Host '  未提交改动：'
        $dirty | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
    }
    else {
        Write-Skip '工作区没有改动'
    }

    # ------------------------------------------------------------ 1. 编译闸门
    if ($SkipPublish) {
        Write-Step '跳过编译与打包（-SkipPublish）'
    }
    else {
        Write-Step '编译整个解决方案（Release）'
        & dotnet build $solution -c Release --nologo -v minimal @extraBuildArgs
        if ($LASTEXITCODE -ne 0) { throw "编译失败（退出码 $LASTEXITCODE），已中止，未提交。" }
        Write-Ok '编译通过'

        # -------------------------------------------------------- 2. 打包
        Write-Step '打包并发布'
        $publishArgs = @{ OutputDir = $OutputDir }
        if ($SkipTests) { $publishArgs['SkipTests'] = $true }
        & $publishScript @publishArgs
        if (-not $?) { throw '打包失败，已中止，未提交。' }

        Assert-Path (Join-Path $OutputDir 'StartupManager.Gui.exe') '发布后的主程序'
        Assert-Path (Join-Path $OutputDir 'StartupManager.Mcp.exe') '发布后的 MCP 服务端'
        Write-Ok "打包完成：$OutputDir"
    }

    # ------------------------------------------------------------ 3. 提交
    Write-Step '提交改动'

    $doCommit = $true
    if (-not $hasChanges) {
        if ($AllowEmpty) {
            Write-Skip '没有改动，按 -AllowEmpty 创建空提交'
        }
        else {
            Write-Skip '没有改动，跳过提交'
            $doCommit = $false
        }
    }

    if ($doCommit) {
        if (-not $Message) {
            $Message = 'chore: 发布 ' + (Get-Date -Format 'yyyy-MM-dd HH:mm')
        }

        $addResult = Invoke-Git @('add', '-A')
        if ($addResult.Code -ne 0) {
            throw "git add 失败：$($addResult.Output -join ' ')"
        }

        $commitArgs = if ($AllowEmpty) { @('commit', '--allow-empty', '-m', $Message) } else { @('commit', '-m', $Message) }
        $commitResult = Invoke-Git $commitArgs
        if ($commitResult.Code -ne 0) {
            throw "git commit 失败：$($commitResult.Output -join ' ')"
        }

        $headResult = Invoke-Git @('log', '-1', '--format=%h %s')
        Write-Ok "已提交 $(($headResult.Output | Select-Object -First 1).Trim())"
    }

    # ------------------------------------------------------------ 4. 推送
    if ($SkipPush) {
        Write-Step '跳过推送（-SkipPush）'
    }
    else {
        Write-Step "推送到 origin/$branch"

        $env:GIT_TERMINAL_PROMPT = '0'

        # 优先直连；失败时用凭据文件里的 token 走显式请求头（绕过 credential helper）。
        $pushResult = Invoke-Git @('push', 'origin', $branch)
        if ($pushResult.Code -ne 0) {
            Write-Host '  直连推送失败，改用 token 显式鉴权重试…' -ForegroundColor Yellow

            $token = $null
            $credFile = Join-Path $env:USERPROFILE '.git-credentials'
            if (Test-Path -LiteralPath $credFile) {
                $previous = $ErrorActionPreference
                $ErrorActionPreference = 'Continue'
                try {
                    foreach ($line in Get-Content -LiteralPath $credFile) {
                        if ($line -match '^https://([^:]+):([^@]+)@github\.com') {
                            $token = $Matches[2]
                            break
                        }
                    }
                }
                finally {
                    $ErrorActionPreference = $previous
                }
            }
            if (-not $token) { throw '推送失败，且未在 .git-credentials 中找到 GitHub token。' }

            $pair = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("x-access-token:$token"))
            $pushResult = Invoke-Git @('-c', 'credential.helper=', '-c', "http.extraHeader=AUTHORIZATION: basic $pair", 'push', 'origin', $branch)
            if ($pushResult.Code -ne 0) {
                throw "推送失败：$($pushResult.Output -join ' ')"
            }
        }
        Write-Ok "已推送到 origin/$branch"
    }

    # ------------------------------------------------------------ 汇总
    Write-Step '完成'
    $headResult = Invoke-Git @('log', '-1', '--format=%h %s')
    Write-Host "  HEAD   : $(($headResult.Output | Select-Object -First 1).Trim())"

    $finalStatus = Invoke-Git @('status', '--porcelain')
    if (@($finalStatus.Output | Where-Object { $_ }).Count -gt 0) {
        Write-Host '  注意   : 仍有未提交/未跟踪文件（可能被 .gitignore 排除）' -ForegroundColor Yellow
    }
    else {
        Write-Host '  工作区 : 干净'
    }
    if (-not $SkipPublish) { Write-Host "  产物   : $OutputDir" }
}
finally {
    Pop-Location
}
