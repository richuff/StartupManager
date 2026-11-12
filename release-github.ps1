<#
.SYNOPSIS
    打 tag 并把自包含产物作为 zip 附件发布到 GitHub Releases。

.DESCRIPTION
    在 release.ps1（编译 → 打包 → 提交 → 推送）之后运行，负责 GitHub 侧：

      1. 从 StartupManager.Gui.csproj 读出 <Version>，推导 tag（v1.1.0）。
      2. 前置检查：git 干净、本地与 origin 一致、tag 与 Release 不冲突。
      3. 把 D:\StartupManager 打成 StartupManager-v1.1.0-win-x64.zip。
      4. 创建并推送 tag（annotated），或复用已存在的同名 tag。
      5. gh release create 上传 zip，正文自动按提交区间生成。
    任一步失败立即中止，不会留下半成品 Release。

.PARAMETER Version
    覆盖自动读取的版本号，如 1.1.0。默认取 csproj 的 <Version>。

.PARAMETER OutputDir
    已打包好的产物目录，默认 D:\StartupManager。

.PARAMETER ZipPath
    zip 输出路径，默认放到系统临时目录，避免污染仓库。

.PARAMETER Notes
    Release 正文。省略时自动按版本区间生成提交列表。

.PARAMETER Title
    Release 标题，默认 "StartupManager v<版本>"。

.PARAMETER Draft
    创建为草稿，不立即公开。

.PARAMETER Prerelease
    标记为预发布。

.PARAMETER SkipPublish
    跳过重新打包，直接使用 OutputDir 里已有的产物（默认会先跑 publish.ps1）。

.PARAMETER KeepZip
    保留下载用的临时 zip 及其解压目录（默认发布后删除）。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\release-github.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\release-github.ps1 -Draft -Notes "修了若干 UI 细节"

.NOTES
    需要 GitHub 凭据。脚本按以下顺序查找：
      1. 环境变量 GH_TOKEN / GITHUB_TOKEN
      2. 当前用户 .git-credentials 里的 github.com 条目
    git push 复用仓库已有的凭据配置。
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDir = 'D:\StartupManager',
    [string]$ZipPath,
    [string]$Notes,
    [string]$Title,
    [switch]$Draft,
    [switch]$Prerelease,
    [switch]$SkipPublish,
    [switch]$KeepZip
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot   = $PSScriptRoot
$guiProject = Join-Path $repoRoot 'src\StartupManager.Gui\StartupManager.Gui.csproj'
$publishScript = Join-Path $repoRoot 'publish.ps1'

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

# git / gh 会把正常提示写到 stderr；Stop 策略下会被当成终止错误，
# 所以统一降级为 Continue 并把输出收进对象里。
function Invoke-Tool([string]$Exe, [string[]]$ToolArgs) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $Exe @ToolArgs 2>&1
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

function Invoke-Git([string[]]$GitArgs) { Invoke-Tool 'git' $GitArgs }
function Invoke-Gh([string[]]$GhArgs) { Invoke-Tool 'gh' $GhArgs }

# Compress-Archive 用独占方式读取源文件，而发布的 MCP 服务端常驻运行会锁住
# 自己的 dll，导致打包失败。这里显式用 FileShare.ReadWrite 打开，规避锁占用。
function New-ZipFromDirectory([string]$SourceDir, [string]$DestinationZip) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $root = (Resolve-Path -LiteralPath $SourceDir).Path.TrimEnd('\')
    $stream = [System.IO.File]::Open($DestinationZip, [System.IO.FileMode]::Create,
        [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    try {
        $zip = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            $files = Get-ChildItem -LiteralPath $root -Recurse -File -Force
            foreach ($file in $files) {
                $relative = $file.FullName.Substring($root.Length).TrimStart('\').Replace('\', '/')
                $entry = $zip.CreateEntry($relative, [System.IO.Compression.CompressionLevel]::Optimal)
                $entryStream = $entry.Open()
                try {
                    $input = [System.IO.File]::Open($file.FullName, [System.IO.FileMode]::Open,
                        [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
                    try { $input.CopyTo($entryStream) }
                    finally { $input.Dispose() }
                }
                finally { $entryStream.Dispose() }
            }
        }
        finally { $zip.Dispose() }
    }
    finally { $stream.Dispose() }
}

# gh 不读 .git-credentials，这里把 token 交给环境变量（不落盘、不回显）。
function Resolve-GitHubToken {
    foreach ($name in 'GH_TOKEN', 'GITHUB_TOKEN') {
        $value = [Environment]::GetEnvironmentVariable($name)
        if ($value) { return $value }
    }
    $credFile = Join-Path $env:USERPROFILE '.git-credentials'
    if (Test-Path -LiteralPath $credFile) {
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            foreach ($line in Get-Content -LiteralPath $credFile) {
                if ($line -match '^https://([^:]+):([^@]+)@github\.com') { return $Matches[2] }
            }
        }
        finally {
            $ErrorActionPreference = $previous
        }
    }
    return $null
}

Push-Location $repoRoot
try {
    # ------------------------------------------------------------ 0. 预检
    Write-Step '检查环境'

    Assert-Path $guiProject 'GUI 项目'
    Assert-Path $OutputDir '产物目录'

    $ghVersion = Invoke-Tool 'gh' @('--version')
    if ($ghVersion.Code -ne 0) { throw '未找到 GitHub CLI（gh），请先安装：https://cli.github.com/' }

    # ------------------------------------------------------------ 1. 版本
    Write-Step '解析版本号'

    if (-not $Version) {
        $raw = Select-String -LiteralPath $guiProject -Pattern '<Version>([^<]+)</Version>' |
            Select-Object -First 1
        if (-not $raw) { throw "无法从 $guiProject 读取 <Version>。" }
        $Version = $raw.Matches[0].Groups[1].Value.Trim()
    }
    if ($Version -notmatch '^\d+\.\d+\.\d+') {
        throw "版本号格式不对：$Version（应形如 1.1.0）"
    }

    $tag = "v$Version"
    if (-not $Title) { $Title = "StartupManager v$Version" }
    if (-not $ZipPath) { $ZipPath = Join-Path $env:TEMP "StartupManager-$tag-win-x64.zip" }

    Write-Host "  版本 : $Version"
    Write-Host "  tag  : $tag"
    Write-Host "  zip  : $ZipPath"

    $repo = (Invoke-Git @('rev-parse', '--show-toplevel')).Output | Select-Object -First 1
    if ($repo) { Write-Host "  仓库 : $($repo.Trim())" }

    # 工作区必须干净：否则 zip 里的产物与 tag 指向的提交对不上。
    $status = Invoke-Git @('status', '--porcelain')
    if ($status.Code -ne 0) { throw 'git status 执行失败。' }
    $dirty = @($status.Output | Where-Object { $_ })
    if ($dirty.Count -gt 0) {
        Write-Host '  有未提交改动：' -ForegroundColor Yellow
        $dirty | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
        throw '工作区不干净。请先运行 .\release.ps1 提交并推送，再发布 GitHub Release。'
    }
    Write-Ok '工作区干净'

    $branch = ((Invoke-Git @('rev-parse', '--abbrev-ref', 'HEAD')).Output | Select-Object -First 1).Trim()
    $head   = ((Invoke-Git @('rev-parse', 'HEAD')).Output | Select-Object -First 1).Trim()

    # tag 指向的提交必须已经在远端，否则别人拉不到这个版本的源码。
    $remoteHead = Invoke-Git @('rev-parse', "origin/$branch")
    if ($remoteHead.Code -ne 0) { throw "找不到远端分支 origin/$branch。" }
    if (($remoteHead.Output | Select-Object -First 1).Trim() -ne $head) {
        throw "本地 $branch 与 origin/$branch 不一致，请先推送后再发布。"
    }
    Write-Ok "提交已同步到 origin/$branch ($($head.Substring(0,7)))"

    # 认证
    if (-not $env:GH_TOKEN -and -not $env:GITHUB_TOKEN) {
        $token = Resolve-GitHubToken
        if ($token) {
            $env:GH_TOKEN = $token
            Write-Host '  凭据 : 使用 .git-credentials 中的 token' -ForegroundColor DarkGray
        }
    }
    $auth = Invoke-Gh @('auth', 'status')
    if ($auth.Code -ne 0) {
        throw 'GitHub CLI 未认证。请设置 GH_TOKEN，或运行 gh auth login。'
    }
    Write-Ok 'GitHub 认证可用'

    # ------------------------------------------------------------ 2. 打包
    $zipDir = Split-Path -Parent $ZipPath
    if (-not (Test-Path -LiteralPath $zipDir)) { New-Item -ItemType Directory -Path $zipDir -Force | Out-Null }

    if ($SkipPublish) {
        Write-Step '跳过打包（-SkipPublish）'
        Assert-Path (Join-Path $OutputDir 'StartupManager.Gui.exe') '发布后的主程序'
        Write-Skip '复用已有产物'
    }
    else {
        Write-Step '编译并打包'
        $publishArgs = @{ OutputDir = $OutputDir }
        & $publishScript @publishArgs
        if (-not $?) { throw '打包失败，已中止。' }
        Write-Ok "打包完成：$OutputDir"
    }

    Write-Step '生成 zip 附件'
    if (Test-Path -LiteralPath $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }

    # 在产物目录内打包，保证解压后是平铺的一层，而不是多套一层目录。
    $sourceFiles = Get-ChildItem -LiteralPath $OutputDir -Force
    if ($sourceFiles.Count -eq 0) { throw "产物目录为空：$OutputDir" }
    New-ZipFromDirectory -SourceDir $OutputDir -DestinationZip $ZipPath

    $zipItem = Get-Item -LiteralPath $ZipPath
    $zipMb = [math]::Round($zipItem.Length / 1MB, 1)
    Write-Ok "zip 已生成：$zipMb MB"

    # ------------------------------------------------------------ 3. 正文
    if (-not $Notes) {
        #区间取「上一个 tag..HEAD」，首个版本则取全部历史。
        $prevTags = Invoke-Git @('tag', '--sort=-v:refname', '--list', 'v*')
        $prevTag = @($prevTags.Output | Where-Object { $_ -and $_.Trim() -ne $tag } | ForEach-Object { $_.Trim() }) |
            Select-Object -First 1

        $range = if ($prevTag) { "$prevTag..HEAD" } else { 'HEAD' }
        $log = Invoke-Git (@('log', '--no-merges', '--format=- %s', $range) + @('--'))

        $lines = @("## StartupManager $tag", '')
        if ($prevTag) {
            $lines += "自 ``$prevTag`` 以来的变更："
        }
        else {
            $lines += '首个正式版本。'
        }
        $lines += ''
        $lines += @($log.Output | Where-Object { $_ })
        $lines += ''
        $lines += '### 下载'
        $lines += ''
        $lines += "- ``StartupManager-$tag-win-x64.zip``：自包含产物，解压后双击 ``StartupManager.Gui.exe`` 即可运行，目标机器无需安装 .NET。"
        $lines += ''
        $lines += '详见仓库 [README.md](https://github.com/richuff/StartupManager#readme)。'

        $Notes = ($lines -join "`n")
    }

    # ------------------------------------------------------------ 4. Release
    Write-Step '发布 GitHub Release'

    $existingRelease = Invoke-Gh @('release', 'view', $tag, '--json', 'tagName')
    if ($existingRelease.Code -eq 0) {
        throw "Release $tag 已存在。要重新发布请先删除：gh release delete $tag --yes --cleanup-tag"
    }

    $tagExists = Invoke-Git @('rev-parse', "--verify", "--quiet", "refs/tags/$tag")
    if ($tagExists.Code -eq 0) {
        Write-Skip "tag $tag 已存在，直接复用"
    }
    else {
        $annotated = Invoke-Git @('tag', '-a', $tag, '-m', "StartupManager $tag")
        if ($annotated.Code -ne 0) { throw "创建 tag 失败：$($annotated.Output -join ' ')" }

        $pushed = Invoke-Git @('push', 'origin', "refs/tags/$tag")
        if ($pushed.Code -ne 0) { throw "推送 tag 失败：$($pushed.Output -join ' ')" }
        Write-Ok "已创建并推送 tag $tag"
    }

    $notesFile = Join-Path $env:TEMP "sm-release-notes-$Version.md"
    # 用无 BOM 的 UTF-8，避免 gh 把 BOM 当成正文的一部分。
    [System.IO.File]::WriteAllText($notesFile, $Notes, (New-Object System.Text.UTF8Encoding($false)))

    $createArgs = @(
        'release', 'create', $tag,
        $ZipPath,
        '--title', $Title,
        '--notes-file', $notesFile
    )
    if ($Draft) { $createArgs += '--draft' }
    if ($Prerelease) { $createArgs += '--prerelease' }

    $created = Invoke-Gh $createArgs
    if ($created.Code -ne 0) {
        # 失败时回滚刚建的 tag，避免下次因「tag 已存在」而卡住。
        if ($tagExists.Code -ne 0) {
            Invoke-Git @('push', 'origin', '--delete', "refs/tags/$tag") | Out-Null
            Invoke-Git @('tag', '-d', $tag) | Out-Null
            Write-Host "  已回滚 tag $tag" -ForegroundColor Yellow
        }
        throw "创建 Release 失败：$($created.Output -join ' ')"
    }
    Write-Ok "Release 已创建：$tag"

    # ------------------------------------------------------------ 5. 汇总
    Write-Step '完成'

    $view = Invoke-Gh @('release', 'view', $tag, '--json', 'url,isDraft,isPrerelease,assets')
    if ($view.Code -eq 0) {
        $info = $view.Output -join '' | ConvertFrom-Json

        $assets = @($info.assets)
        $totalMb = [math]::Round((($assets | Measure-Object -Property size -Sum).Sum / 1MB), 1)

        Write-Host "  版本   : $tag"
        Write-Host "  地址   : $($info.url)"
        Write-Host "  草稿   : $($info.isDraft)"
        Write-Host "  预发布 : $($info.isPrerelease)"
        foreach ($a in $assets) {
            Write-Host "  附件   : $($a.name) ($([math]::Round($a.size / 1MB, 1)) MB)"
        }
        Write-Host "  合计   : $totalMb MB"
    }

    if (-not $KeepZip) {
        Remove-Item -LiteralPath $ZipPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $notesFile -Force -ErrorAction SilentlyContinue
        Write-Host '  临时 zip 与正文文件已清理（-KeepZip 可保留）。' -ForegroundColor DarkGray
    }
}
finally {
    Pop-Location
}
