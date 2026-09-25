[CmdletBinding()]
param(
    [string]$IsccPath,
    [switch]$SkipDependencyAudit,
    [switch]$RequireSignature,
    [switch]$AllowUntrustedSelfSignedCertificate,
    [string]$SigningCertificateThumbprint,
    [string]$SignToolPath,
    [string]$TimestampUrl
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'
$installerScript = Join-Path $repositoryRoot 'installer\EggLauncher.iss'
$signScript = Join-Path $PSScriptRoot 'Sign-InnoArtifact.ps1'

if ([string]::IsNullOrWhiteSpace($IsccPath)) {
    $candidates = @(
        (Join-Path $repositoryRoot '.tools\inno-setup\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    $IsccPath = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($IsccPath)) {
        $onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        $IsccPath = $onPath.Source
    }
}
if ([string]::IsNullOrWhiteSpace($IsccPath) -or
    -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw '找不到 Inno Setup 编译器 ISCC.exe；请安装 Inno Setup 6/7 或传入 -IsccPath。'
}
$IsccPath = [System.IO.Path]::GetFullPath($IsccPath)

$languageFile = Join-Path $repositoryRoot 'installer\ChineseSimplified.isl'
if (-not (Test-Path -LiteralPath $languageFile -PathType Leaf)) {
    throw "项目缺少简体中文安装语言文件：$languageFile"
}

$versionXml = [xml](Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') -Raw)
$version = [string]$versionXml.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "无法识别安装包版本：$version"
}

$signingArgumentsProvided = -not [string]::IsNullOrWhiteSpace($SigningCertificateThumbprint) -or
    -not [string]::IsNullOrWhiteSpace($SignToolPath) -or
    -not [string]::IsNullOrWhiteSpace($TimestampUrl) -or
    $AllowUntrustedSelfSignedCertificate
if ($RequireSignature -and [string]::IsNullOrWhiteSpace($SigningCertificateThumbprint)) {
    throw '签名安装包必须提供 -SigningCertificateThumbprint。'
}
if (-not $RequireSignature -and $signingArgumentsProvided) {
    throw '提供签名参数时必须同时使用 -RequireSignature。'
}
if ($RequireSignature) {
    $pwshCommand = Get-Command pwsh.exe -ErrorAction SilentlyContinue
    if ($null -eq $pwshCommand -or -not (Test-Path -LiteralPath $pwshCommand.Source -PathType Leaf)) {
        throw '签名安装包需要 PowerShell 7（pwsh.exe）。'
    }
    $pwshPath = $pwshCommand.Source
    if ($pwshPath.IndexOfAny(@('"', "`r", "`n")) -ge 0) {
        throw 'PowerShell 7 路径包含不支持的引号或换行符。'
    }
    $thumbprint = $SigningCertificateThumbprint.Replace(' ', '').ToUpperInvariant()
    if ($thumbprint -notmatch '^[0-9A-F]{40}$') {
        throw 'SigningCertificateThumbprint 必须是 40 位十六进制证书指纹。'
    }
    $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$thumbprint" -ErrorAction SilentlyContinue
    if ($null -eq $certificate -or -not $certificate.HasPrivateKey) {
        throw '当前用户证书存储中找不到带私钥的代码签名证书。'
    }
    if ($certificate.Subject -eq $certificate.Issuer -and -not $AllowUntrustedSelfSignedCertificate) {
        throw '检测到自签名证书；必须显式使用 -AllowUntrustedSelfSignedCertificate。'
    }
    foreach ($value in @($signScript, $SignToolPath, $TimestampUrl)) {
        if (-not [string]::IsNullOrWhiteSpace($value) -and $value.IndexOfAny(@('"', "`r", "`n")) -ge 0) {
            throw '签名参数包含不支持的引号或换行符。'
        }
    }
}

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
$stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
$runId = [Guid]::NewGuid().ToString('N').Substring(0, 8)
$payloadPath = Join-Path $artifactsRoot "installer-payload-$version-$stamp-$runId"
$outputPath = Join-Path $artifactsRoot "installer-output-$version-$stamp-$runId"
New-Item -ItemType Directory -Path $outputPath | Out-Null

$publishArguments = @{
    OutputDirectory = $payloadPath
    SelfContained   = $true
}
if ($SkipDependencyAudit) { $publishArguments.SkipDependencyAudit = $true }
if ($RequireSignature) {
    $publishArguments.RequireSignature = $true
    $publishArguments.SigningCertificateThumbprint = $thumbprint
    if ($AllowUntrustedSelfSignedCertificate) {
        $publishArguments.AllowUntrustedSelfSignedCertificate = $true
    }
    if (-not [string]::IsNullOrWhiteSpace($SignToolPath)) {
        $publishArguments.SignToolPath = $SignToolPath
    }
    if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
        $publishArguments.TimestampUrl = $TimestampUrl
    }
}

& (Join-Path $PSScriptRoot 'Publish-Portable.ps1') @publishArguments
if (-not (Test-Path -LiteralPath (Join-Path $payloadPath 'Launcher.App.exe') -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $payloadPath 'Launcher.Agent.exe') -PathType Leaf)) {
    throw '发布目录缺少启动器或 Agent 可执行文件。'
}

$compilerArguments = @(
    "/DPackageDir=$payloadPath",
    "/DOutputDir=$outputPath",
    "/DAppVersion=$version"
)
if ($RequireSignature) {
    $signCommand = '$q' + $pwshPath + '$q -NoProfile -NonInteractive -File $q' +
        $signScript + '$q -FilePath $f -SigningCertificateThumbprint ' + $thumbprint
    if ($AllowUntrustedSelfSignedCertificate) {
        $signCommand += ' -AllowUntrustedSelfSignedCertificate'
    }
    if (-not [string]::IsNullOrWhiteSpace($SignToolPath)) {
        $signCommand += ' -SignToolPath $q' + $SignToolPath + '$q'
    }
    if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
        $signCommand += ' -TimestampUrl $q' + $TimestampUrl + '$q'
    }
    $compilerArguments += '/DSignInstaller=1'
    $compilerArguments += "/seggsign=$signCommand"
}
$compilerArguments += $installerScript
& $IsccPath @compilerArguments
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup 编译失败，退出码：$LASTEXITCODE"
}

$setupPath = Join-Path $outputPath "Egg-Launcher-$version-Setup-win-x64.exe"
if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
    throw "未生成安装程序：$setupPath"
}
if ($RequireSignature) {
    $signature = Get-AuthenticodeSignature -FilePath $setupPath
    if ($null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Thumbprint -ne $thumbprint -or
        $signature.Status -in @('NotSigned', 'HashMismatch', 'NotSupported')) {
        throw "安装程序签名验证失败：$($signature.Status)"
    }
}
$hash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
$hashPath = $setupPath + '.sha256'
[System.IO.File]::WriteAllText(
    $hashPath,
    "$hash  $([System.IO.Path]::GetFileName($setupPath))$([Environment]::NewLine)",
    [System.Text.UTF8Encoding]::new($false))
Write-Output "Installer: $setupPath"
Write-Output "Installer hash: $hashPath"
Write-Output "Internal payload: $payloadPath"
