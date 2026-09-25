[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$FilePath,
    [Parameter(Mandatory = $true)] [string]$SigningCertificateThumbprint,
    [switch]$AllowUntrustedSelfSignedCertificate,
    [string]$SignToolPath,
    [string]$TimestampUrl
)

$ErrorActionPreference = 'Stop'
$thumbprint = $SigningCertificateThumbprint.Replace(' ', '').ToUpperInvariant()
if ($thumbprint -notmatch '^[0-9A-F]{40}$') {
    throw 'SigningCertificateThumbprint 必须是 40 位十六进制证书指纹。'
}
if (-not (Test-Path -LiteralPath $FilePath -PathType Leaf)) {
    throw "找不到待签名文件：$FilePath"
}
$certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$thumbprint" -ErrorAction SilentlyContinue
if ($null -eq $certificate -or -not $certificate.HasPrivateKey) {
    throw '当前用户证书存储中找不到带私钥的代码签名证书。'
}
if ($certificate.Subject -eq $certificate.Issuer -and -not $AllowUntrustedSelfSignedCertificate) {
    throw '检测到自签名证书；必须显式使用 -AllowUntrustedSelfSignedCertificate。'
}
if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
    $uri = $null
    if (-not [Uri]::TryCreate($TimestampUrl, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -notin @('http', 'https')) {
        throw 'TimestampUrl 必须是 HTTP 或 HTTPS 绝对 URL。'
    }
}

if (-not [string]::IsNullOrWhiteSpace($SignToolPath)) {
    if (-not (Test-Path -LiteralPath $SignToolPath -PathType Leaf)) {
        throw "找不到 SignTool：$SignToolPath"
    }
    $arguments = @('sign', '/sha1', $thumbprint, '/fd', 'SHA256')
    if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
        $arguments += @('/tr', $TimestampUrl, '/td', 'SHA256')
    }
    $arguments += $FilePath
    & $SignToolPath @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "SignTool 签名失败：$FilePath"
    }
}
else {
    $signingParameters = @{
        FilePath      = $FilePath
        Certificate   = $certificate
        HashAlgorithm = 'SHA256'
        IncludeChain  = 'NotRoot'
    }
    if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
        $signingParameters.TimestampServer = $TimestampUrl
    }
    $null = Set-AuthenticodeSignature @signingParameters
}

$signature = Get-AuthenticodeSignature -FilePath $FilePath
if ($null -eq $signature.SignerCertificate -or
    $signature.SignerCertificate.Thumbprint -ne $thumbprint -or
    $signature.Status -in @('NotSigned', 'HashMismatch', 'NotSupported') -or
    (-not [string]::IsNullOrWhiteSpace($TimestampUrl) -and $null -eq $signature.TimeStamperCertificate)) {
    throw "安装器 Authenticode 验证失败：$FilePath；状态=$($signature.Status)"
}
