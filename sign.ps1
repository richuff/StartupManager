<#
.SYNOPSIS
    给已发布的 exe 做数字签名（可选步骤）。

.DESCRIPTION
    脚本按以下顺序工作：

      1. 如果提供了 -PfxPath，就用该 .pfx 证书签名（正式分发应走这一步）。
      2. 否则在「当前用户\个人」证书库中查找可用的代码签名证书。
      3. 都没有时，生成一个**自签名证书**并签名。

    重要：自签名证书只在本机（证书被导入受信任根之后）才会让 Windows 认可发布者。
    把程序分发给别人时，对方仍会看到「未知的发行商」，必须使用由公共 CA
    （DigiCert / Sectigo 等）签发的代码签名证书。

.PARAMETER TargetDir
    要签名的目录，默认 D:\StartupManager。

.PARAMETER PfxPath
    正式代码签名证书（.pfx）路径。

.PARAMETER PfxPassword
    .pfx 密码。

.PARAMETER SelfSigned
    强制生成并安装自签名证书（仅本机可用）。

.EXAMPLE
    .\sign.ps1 -SelfSigned
    .\sign.ps1 -PfxPath 'D:\cert\mycert.pfx' -PfxPassword 'pwd'
#>
[CmdletBinding()]
param(
    [string]$TargetDir = 'D:\StartupManager',
    [string]$PfxPath,
    [string]$PfxPassword,
    [switch]$SelfSigned,
    [switch]$TrustCert,
    [string]$TimestampServer
)

$ErrorActionPreference = 'Stop'

function Write-Step([string]$Message) {
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Assert-Path([string]$Path, [string]$What) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "$What 不存在：$Path" }
}

Assert-Path $TargetDir '目标目录'

$targets = @(
    (Join-Path $TargetDir 'StartupManager.Gui.exe'),
    (Join-Path $TargetDir 'StartupManager.Mcp.exe')
) | Where-Object { Test-Path -LiteralPath $_ }

if ($targets.Count -eq 0) { throw "在 $TargetDir 中没有找到可签名的 exe。" }

Write-Step '签名目标'
foreach ($t in $targets) {
    $status = (Get-AuthenticodeSignature $t).Status
    Write-Host "  $([System.IO.Path]::GetFileName($t))  [$status]"
}

# ---------------------------------------------------------------- certificate
Write-Step '准备证书'

$cert = $null
$usePfx = $false

if ($PfxPath) {
    Assert-Path $PfxPath '.pfx 证书'
    $secure = if ($PfxPassword) { ConvertTo-SecureString $PfxPassword -AsPlainText -Force } else { $null }
    $cert = Get-PfxCertificate -FilePath $PfxPath -Password $secure
    $usePfx = $true
    Write-Host "  使用 .pfx 证书：$($cert.Subject)" -ForegroundColor Green
}
elseif (-not $SelfSigned) {
    $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
            Where-Object { $_.NotAfter -gt (Get-Date) } |
            Sort-Object NotAfter -Descending |
            Select-Object -First 1

    if ($cert) { Write-Host "  使用现有代码签名证书：$($cert.Subject)" -ForegroundColor Green }
}

if (-not $cert) {
    Write-Step '生成自签名证书（仅本机有效）'

    # Keep this simple: -TextExtension with complex OIDs can block for a long time.
    $cert = New-SelfSignedCertificate `
        -Subject 'CN=StartupManager' `
        -Type CodeSigningCert `
        -KeyUsage DigitalSignature `
        -FriendlyName 'StartupManager Code Signing' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -NotAfter (Get-Date).AddYears(3)

    Write-Host "  已生成：$($cert.Thumbprint)" -ForegroundColor Yellow

    # Trusting the cert can block in restricted environments, so it is opt-in.
    # Without trust the signature is still applied, but Windows keeps showing an
    # untrusted/unknown publisher.
    if (-not $TrustCert) {
        Write-Host '  跳过自动信任；如需本机认可发布者，请加 -TrustCert 参数。' -ForegroundColor DarkGray
    }
    else {
        $cerPath = Join-Path $env:TEMP ('sm-cert-' + [Guid]::NewGuid().ToString('N') + '.cer')
        try {
            Export-Certificate -Cert $cert -FilePath $cerPath -Type CERT -ErrorAction Stop | Out-Null
            $certutil = Join-Path $env:SystemRoot 'System32\certutil.exe'
            if (Test-Path -LiteralPath $certutil) {
                $proc = Start-Process -FilePath $certutil `
                    -ArgumentList @('-addstore', '-user', 'Root', "`"$cerPath`"") `
                    -NoNewWindow -PassThru -Wait
                if ($proc.ExitCode -eq 0) {
                    Write-Host '  已将证书加入「当前用户 → 受信任的根证书颁发机构」（仅本机生效）。' -ForegroundColor Yellow
                }
                else {
                    Write-Host "  certutil 返回 $($proc.ExitCode)，未能自动信任。" -ForegroundColor Yellow
                }
            }
        }
        catch {
            Write-Host "  未能加入受信任根：$($_.Exception.Message)" -ForegroundColor Yellow
        }
        finally {
            Remove-Item -LiteralPath $cerPath -Force -ErrorAction SilentlyContinue
        }
    }
}

# ---------------------------------------------------------------- sign
Write-Step '执行签名'

foreach ($file in $targets) {
    try {
        # Only timestamp when explicitly requested: reaching a TSA server can hang
        # for minutes on an offline machine and would appear as a freeze.
        $sigArgs = @{
            FilePath      = $file
            Certificate   = $cert
            HashAlgorithm = 'SHA256'
        }
        if ($TimestampServer) { $sigArgs.TimestampServer = $TimestampServer }

        $result = Set-AuthenticodeSignature @sigArgs

        if ($result.Status -eq 'Valid') {
            Write-Host "  [OK] $([System.IO.Path]::GetFileName($file))" -ForegroundColor Green
        }
        else {
            Write-Host "  [部分成功] $([System.IO.Path]::GetFileName($file)) — $($result.Status)" -ForegroundColor Yellow
            Write-Host "         原因：$($result.StatusMessage)" -ForegroundColor DarkGray
        }
    }
    catch {
        Write-Host "  [失败] $([System.IO.Path]::GetFileName($file)) — $($_.Exception.Message)" -ForegroundColor Red
    }
}

# ---------------------------------------------------------------- report
Write-Step '结果'

foreach ($file in $targets) {
    $sig = Get-AuthenticodeSignature $file
    $name = [System.IO.Path]::GetFileName($file)
    Write-Host "  $name"
    Write-Host "    状态   : $($sig.Status)"
    Write-Host "    签名者 : $(if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { '(无)' })"
}

Write-Host ''
if ($usePfx) {
    Write-Host '  已使用正式证书签名，分发到其它机器也能显示正确的发行商。' -ForegroundColor Green
}
else {
    Write-Host '  注意：自签名证书只在本机受信任。' -ForegroundColor Yellow
    Write-Host '        分发给他人时对方仍会看到「未知的发行商」，' -ForegroundColor Yellow
    Write-Host '        要让所有 Windows 机器都认可，需要向公共 CA 购买代码签名证书。' -ForegroundColor Yellow
}
