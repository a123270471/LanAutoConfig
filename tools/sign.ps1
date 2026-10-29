<#
.SYNOPSIS
    给 LanAutoConfig 的两个 exe 解除“网络文件”标记，并可选做代码签名。

.DESCRIPTION
    “打开文件 - 安全警告 / 无法验证发布者”有两个来源，本脚本分别处理：

    1) 文件带有 MOTW（从网络下载、从共享复制来的标记）
       → Unblock-File 直接去掉，这个对话框就不会再出现，默认执行。

    2) exe 没有 Authenticode 数字签名（“未知发布者”）
       → 需要证书：
         - 内网自签名：-SelfSigned（生成 CN=LanAutoConfig 的代码签名证书并签名，同时导出 .cer，
                        部署机把 .cer 装进「受信任的根证书颁发机构」后就不会再提示未知发布者）
         - 购买的正规证书：-Pfx 证书路径 -Password 密码

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\sign.ps1
    powershell -ExecutionPolicy Bypass -File tools\sign.ps1 -SelfSigned
    powershell -ExecutionPolicy Bypass -File tools\sign.ps1 -Pfx D:\cert.pfx -Password 123456
#>
param(
    [switch]$SelfSigned,
    [string]$Pfx,
    [string]$Password,
    [string]$Subject = "CN=LanAutoConfig, O=LanAutoConfig, C=CN",
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
# 自动找出 bin 下所有 exe，文件名已改成中文正式名
$exes = @(Get-ChildItem -LiteralPath (Join-Path $root 'bin') -Recurse -Filter *.exe -ErrorAction SilentlyContinue |
          Select-Object -ExpandProperty FullName)
if ($exes.Count -eq 0) { Write-Host "bin 下没有找到 exe，请先运行 build.cmd 或 dotnet publish。" -ForegroundColor Yellow; return }

Write-Host "== 1) 解除网络文件标记（消除“安全警告”对话框）==" -ForegroundColor Cyan
foreach ($e in $exes)
{
    if (-not (Test-Path -LiteralPath $e)) { Write-Host "  跳过（不存在）: $e" -ForegroundColor DarkGray; continue }
    Unblock-File -LiteralPath $e
    Write-Host "  已解除: $(Split-Path -Leaf $e)"
}

if (-not $SelfSigned -and -not $Pfx)
{
    Write-Host ""
    Write-Host "只做了 MOTW 解除。要同时消除“未知发布者”，请加 -SelfSigned（内网自签名）或 -Pfx（正规证书）。" -ForegroundColor Yellow
    return
}

Write-Host ""
Write-Host "== 2) 准备代码签名证书 ==" -ForegroundColor Cyan
$cert = $null

if ($Pfx)
{
    if (-not (Test-Path -LiteralPath $Pfx)) { throw "找不到证书文件：$Pfx" }
    $sec = ConvertTo-SecureString $Password -AsPlainText -Force
    $cert = Get-PfxCertificate -FilePath $Pfx
    Write-Host "  使用 PFX：$Pfx（Subject=$($cert.Subject)）"
}
else
{
    $cert = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey } |
        Select-Object -First 1
    if ($cert)
    {
        Write-Host "  复用已有自签名证书：$($cert.Thumbprint)"
    }
    else
    {
        $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject `
            -CertStoreLocation Cert:\CurrentUser\My -KeyUsage DigitalSignature `
            -NotAfter (Get-Date).AddYears(10)
        Write-Host "  已创建自签名证书：$($cert.Thumbprint)"
    }
}

Write-Host ""
Write-Host "== 3) 签名 ==" -ForegroundColor Cyan
foreach ($e in $exes)
{
    if (-not (Test-Path -LiteralPath $e)) { Write-Host "  跳过（不存在）: $e" -ForegroundColor DarkGray; continue }
    $sig = $null
    try
    {
        $sig = Set-AuthenticodeSignature -FilePath $e -Certificate $cert -TimestampServer $TimestampUrl
    }
    catch
    {
        # 没有外网/时间戳服务不可达时，做不带时间戳的签名
        $sig = Set-AuthenticodeSignature -FilePath $e -Certificate $cert
        Write-Host "  （时间戳服务不可用，已做不带时间戳的签名）" -ForegroundColor Yellow
    }
    $name = Split-Path -Leaf $e
    if ($sig.Status -eq 'Valid') { Write-Host "  $name -> 签名有效" -ForegroundColor Green }
    else { Write-Host "  $name -> $($sig.Status) $($sig.StatusMessage)" -ForegroundColor Yellow }
}

Write-Host ""
Write-Host "== 4) 导出公钥证书（部署机需要信任它）==" -ForegroundColor Cyan
$cerPath = Join-Path $PSScriptRoot 'LanAutoConfig-codesign.cer'
Export-Certificate -Cert $cert -FilePath $cerPath -Type CERT | Out-Null
Write-Host "  已导出: $cerPath"
Write-Host ""
Write-Host "部署机上执行（管理员 PowerShell）后，双击 exe 就不会再提示“未知发布者”："
Write-Host "  Import-Certificate -FilePath LanAutoConfig-codesign.cer -CertStoreLocation Cert:\LocalMachine\Root"
Write-Host "（只想显示为“已信任的发布者”而不进根证书，也可以装到 Cert:\LocalMachine\TrustedPublisher）"
