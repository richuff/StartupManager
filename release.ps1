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

# ---------------------------------------------------------------- 0. 预检
Write-Step '检查仓库状态'

Assert-Path $solution '解决方案'
Assert-Path $publishScript 'publish.ps1'

Push-Location $repoRoot
try {
    $branch = (& git rev-parse --abbrev-ref HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw '不是有效的 git 仓库。' }
    Write-Host "  分支     : $branch"
    Write-Host "  发布目录 : $OutputDir"

    $dirty = (& git status --porcelain)
    $hasChanges = [bool]($dirty -and $dirty.Count -gt 0)
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
        & dotnet build $solution -c Release --nologo -v minimal @($env:SM_EXTRA_BUILD_ARGS -split '\s+' | Where-Object { $_ })
        if ($LASTEXITCODE -ne 0) { throw "编译失败（退出码 $LASTEXITCODE），已中止，未提交。" }
        Write-Ok '编译通过'

        # -------------------------------------------------------- 2. 打包
        Write-Step '打包并发布'
        $publishArgs = @{ OutputDir = $OutputDir }
        if ($SkipTests) { $publishArgs['SkipTests'] = $true }
        & $publishScript @publishArgs
        if (-not $?) { throw '打包失败，已中止，未提交。' }

        $exe = Join-Path $OutputDir 'StartupManager.Gui.exe'
        $mcp = Join-Path $OutputDir 'StartupManager.Mcp.exe'
        Assert-Path $exe '发布后的主程序'
        Assert-Path $mcp '发布后的 MCP 服务端'
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

        & git add -A
        if ($LASTEXITCODE -ne 0) { throw 'git add 失败。' }

        if ($AllowEmpty) {
            & git commit --allow-empty -m $Message | Out-Null
        }
        else {
            & git commit -m $Message | Out-Null
        }
        if ($LASTEXITCODE -ne 0) { throw 'git commit 失败（可能没有实际改动）。' }

        $head = (& git log -1 --format='%h %s').Trim()
        Write-Ok "已提交 $head"
    }

    # ------------------------------------------------------------ 4. 推送
    if ($SkipPush) {
        Write-Step '跳过推送（-SkipPush）'
    }
    else {
        Write-Step "推送到 origin/$branch"

        $env:GIT_TERMINAL_PROMPT = '0'

        # 优先直连；失败时用凭据文件里的 token 走显式请求头（绕过 credential helper）。
        & git push origin $branch
        if ($LASTEXITCODE -ne 0) {
            Write-Host '  直连推送失败，改用 token 显式鉴权重试…' -ForegroundColor Yellow

            $token = $null
            $credFile = Join-Path $env:USERPROFILE '.git-credentials'
            if (Test-Path -LiteralPath $credFile) {
                foreach ($line in Get-Content -LiteralPath $credFile) {
                    if ($line -match '^https://([^:]+):([^@]+)@github\.com') {
                        $token = $Matches[2]
                        break
                    }
                }
            }
            if (-not $token) { throw '推送失败，且未在 .git-credentials 中找到 GitHub token。' }

            $pair = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("x-access-token:$token"))
            & git -c credential.helper= -c "http.extraHeader=AUTHORIZATION: basic $pair" push origin $branch
            if ($LASTEXITCODE -ne 0) { throw '推送失败。' }
        }
        Write-Ok "已推送到 origin/$branch"
    }

    # ------------------------------------------------------------ 汇总
    Write-Step '完成'
    Write-Host ("  HEAD   : " + (& git log -1 --format='%h %s').Trim())
    if (& git status --porcelain) {
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
