[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BackupDirectory
)

$ErrorActionPreference = 'Stop'
$developerName = ([char]0x7532).ToString() + [char]0x603B + [char]0x4E0D + [char]0x662F + [char]0x8D3E + [char]0x603B
$subject = "CN=$developerName"
$friendlyName = 'ChatGPT Local Launcher Open Source Code Signing'
$pfxFileName = 'ChatGPT-Local-Launcher-Code-Signing.pfx'
$cerFileName = 'ChatGPT-Local-Launcher-Code-Signing.cer'
$infoFileName = 'ChatGPT-Local-Launcher-Code-Signing.txt'
$errorFileName = 'ChatGPT-Local-Launcher-Code-Signing.last-error.txt'

trap {
    $errorText = $_ | Out-String
    Write-Host ''
    Write-Host 'Certificate initialization failed:' -ForegroundColor Red
    Write-Host $errorText -ForegroundColor Red
    try {
        $errorDirectory = [IO.Path]::GetFullPath($BackupDirectory)
        if (Test-Path -LiteralPath $errorDirectory -PathType Container) {
            [IO.File]::WriteAllText(
                (Join-Path $errorDirectory $errorFileName),
                $errorText,
                [Text.UTF8Encoding]::new($false))
        }
    }
    catch {
    }
    Read-Host 'Press Enter to close this window'
    exit 1
}

function ConvertTo-PlainText {
    param([Parameter(Mandatory = $true)] [SecureString]$SecureValue)

    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureValue)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}

$resolvedBackup = [IO.Path]::GetFullPath($BackupDirectory)
if (-not (Test-Path -LiteralPath $resolvedBackup -PathType Container)) {
    throw "Backup directory does not exist: $resolvedBackup"
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repositoryPrefix = $repositoryRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if ($resolvedBackup.Equals($repositoryRoot, [StringComparison]::OrdinalIgnoreCase) `
    -or $resolvedBackup.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The PFX private-key backup must not be stored in the source repository.'
}

$pfxPath = Join-Path $resolvedBackup $pfxFileName
$cerPath = Join-Path $resolvedBackup $cerFileName
$infoPath = Join-Path $resolvedBackup $infoFileName
$errorPath = Join-Path $resolvedBackup $errorFileName
if (Test-Path -LiteralPath $errorPath) {
    Remove-Item -LiteralPath $errorPath -Force
}
foreach ($path in @($pfxPath, $cerPath, $infoPath)) {
    if (Test-Path -LiteralPath $path) {
        throw "A target file already exists; refusing to overwrite it: $path"
    }
}

Write-Host 'Creating a self-signed code-signing certificate for ChatGPT Local Launcher.' -ForegroundColor Cyan
Write-Host 'Set a password for the PFX private-key backup. It will not be displayed or logged.'
$password = Read-Host 'Enter backup password' -AsSecureString
$confirmation = Read-Host 'Enter the same password again' -AsSecureString
$plainPassword = ConvertTo-PlainText $password
$plainConfirmation = ConvertTo-PlainText $confirmation
try {
    if ([string]::IsNullOrWhiteSpace($plainPassword)) {
        throw 'The backup password must not be empty.'
    }
    if ($plainPassword -cne $plainConfirmation) {
        throw 'The two passwords do not match.'
    }
}
finally {
    $plainConfirmation = $null
}

$certificate = $null
$certificateStore = $null
$pfxBytes = $null
try {
    $rsa = [Security.Cryptography.RSA]::Create(3072)
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
        $subject,
        $rsa,
        [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $enhancedKeyUsages = [Security.Cryptography.OidCollection]::new()
    $null = $enhancedKeyUsages.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3', 'Code Signing'))
    $request.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($enhancedKeyUsages, $true))
    $request.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature,
            $true))
    $request.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))

    $temporaryCertificate = $request.CreateSelfSigned(
        [DateTimeOffset]::Now.AddMinutes(-5),
        [DateTimeOffset]::Now.AddYears(5))
    $pfxBytes = $temporaryCertificate.Export(
        [Security.Cryptography.X509Certificates.X509ContentType]::Pfx,
        $plainPassword)
    [IO.File]::WriteAllBytes($pfxPath, $pfxBytes)
    [IO.File]::WriteAllBytes(
        $cerPath,
        $temporaryCertificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))

    $keyFlags = [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::UserKeySet `
        -bor [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::PersistKeySet `
        -bor [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $pfxBytes,
        $plainPassword,
        $keyFlags)
    $certificate.FriendlyName = $friendlyName
    $certificateStore = [Security.Cryptography.X509Certificates.X509Store]::new(
        [Security.Cryptography.X509Certificates.StoreName]::My,
        [Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
    $certificateStore.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    $certificateStore.Add($certificate)
    $certificateStore.Close()
    $certificateStore = $null
    $temporaryCertificate.Dispose()
    $rsa.Dispose()

    $sha256Fingerprint = $certificate.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256)
    $details = @(
        'ChatGPT Local Launcher self-signed code-signing certificate',
        "Subject=$($certificate.Subject)",
        "FriendlyName=$friendlyName",
        "SHA1Thumbprint=$($certificate.Thumbprint)",
        "SHA256Fingerprint=$sha256Fingerprint",
        "NotBefore=$($certificate.NotBefore.ToString('O'))",
        "NotAfter=$($certificate.NotAfter.ToString('O'))",
        'PrivateKeyBackup=ChatGPT-Local-Launcher-Code-Signing.pfx',
        'PublicCertificate=ChatGPT-Local-Launcher-Code-Signing.cer',
        'Warning=PFX contains the publisher private key. Never publish or share it.'
    ) -join [Environment]::NewLine
    [IO.File]::WriteAllText($infoPath, $details + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))

    Write-Host ''
    Write-Host 'Certificate creation and backup succeeded.' -ForegroundColor Green
    Write-Host "Certificate thumbprint: $($certificate.Thumbprint)"
    Write-Host "PFX private-key backup: $pfxPath"
    Write-Host "CER public certificate: $cerPath"
    Write-Host "Certificate information: $infoPath"
    Write-Host ''
    Write-Host 'Keep both the PFX and its password safe. Both are required to sign future releases with the same identity.' -ForegroundColor Yellow
    Read-Host 'Press Enter to close this window'
}
catch {
    if ($null -ne $certificate) {
        try {
            $cleanupStore = [Security.Cryptography.X509Certificates.X509Store]::new(
                [Security.Cryptography.X509Certificates.StoreName]::My,
                [Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
            $cleanupStore.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
            $cleanupStore.Remove($certificate)
            $cleanupStore.Close()
        }
        catch {
        }
    }
    foreach ($path in @($pfxPath, $cerPath, $infoPath)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
        }
    }
    throw
}
finally {
    if ($null -ne $certificateStore) {
        $certificateStore.Close()
    }
    if ($null -ne $pfxBytes) {
        [Array]::Clear($pfxBytes, 0, $pfxBytes.Length)
    }
    $plainPassword = $null
    $password.Dispose()
    $confirmation.Dispose()
}
