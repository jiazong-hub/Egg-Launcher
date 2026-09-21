[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [switch]$SelfContained,
    [switch]$Archive,
    [switch]$SkipDependencyAudit,
    [switch]$RequireSignature,
    [switch]$AllowUntrustedSelfSignedCertificate,
    [string]$SigningCertificateThumbprint,
    [string]$SignToolPath,
    [string]$TimestampUrl
)

$ErrorActionPreference = 'Stop'

function Copy-DotNetRuntimeNotices {
    param(
        [Parameter(Mandatory = $true)] [string]$PackagePath,
        [Parameter(Mandatory = $true)] [string]$RepositoryRoot
    )

    $depsPath = Join-Path $PackagePath 'Launcher.App.deps.json'
    if (-not (Test-Path -LiteralPath $depsPath -PathType Leaf)) {
        throw "自包含发布缺少依赖清单：$depsPath"
    }

    $deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json -Depth 100
    $runtimePackPattern = '^runtimepack\.(Microsoft\.(?:NETCore|AspNetCore|WindowsDesktop)\.App\.Runtime\.win-x64)/([^/]+)$'
    $runtimePacks = @(
        $deps.libraries.PSObject.Properties.Name |
            Where-Object { $_ -match $runtimePackPattern } |
            Sort-Object -Unique
    )
    if ($runtimePacks.Count -eq 0) {
        throw '自包含发布的依赖清单中未发现受支持的 .NET Runtime Pack。'
    }

    $noticeRequirements = @{
        'Microsoft.NETCore.App.Runtime.win-x64' = @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')
        'Microsoft.AspNetCore.App.Runtime.win-x64' = @('LICENSE.txt', 'THIRD-PARTY-NOTICES.TXT')
        'Microsoft.WindowsDesktop.App.Runtime.win-x64' = @('LICENSE')
    }
    $licensesPath = Join-Path $PackagePath 'licenses\dotnet'
    New-Item -ItemType Directory -Path $licensesPath -Force | Out-Null

    $manifestLines = @(
        'Bundled Microsoft .NET runtime components',
        '',
        'The following Runtime Packs were resolved from Launcher.App.deps.json.',
        'The accompanying license and third-party notice files are copied from the exact NuGet packages used for this release.',
        ''
    )
    $resolvedRuntimePacks = @()
    foreach ($runtimePack in $runtimePacks) {
        if ($runtimePack -notmatch $runtimePackPattern) {
            throw "无法解析 Runtime Pack：$runtimePack"
        }

        $packageId = $Matches[1]
        $version = $Matches[2]
        if (-not $noticeRequirements.ContainsKey($packageId)) {
            throw "缺少 Runtime Pack 许可规则：$packageId"
        }

        $packageRoot = Join-Path `
            (Join-Path $RepositoryRoot '.tools\nuget-packages') `
            (Join-Path $packageId.ToLowerInvariant() $version)
        if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) {
            throw "找不到 Runtime Pack 缓存目录：$packageRoot"
        }

        $copiedFiles = @()
        foreach ($noticeName in $noticeRequirements[$packageId]) {
            $sourcePath = Join-Path $packageRoot $noticeName
            if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
                throw "Runtime Pack 缺少必须随包分发的许可文件：$sourcePath"
            }

            $noticeKind = if ($noticeName.StartsWith('LICENSE', [StringComparison]::OrdinalIgnoreCase)) {
                'LICENSE.txt'
            }
            else {
                'THIRD-PARTY-NOTICES.txt'
            }
            $destinationName = "$packageId-$version-$noticeKind"
            Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $licensesPath $destinationName)
            $copiedFiles += $destinationName
        }

        $resolvedRuntimePack = "$packageId/$version"
        $resolvedRuntimePacks += $resolvedRuntimePack
        $manifestLines += "- $resolvedRuntimePack"
        foreach ($copiedFile in $copiedFiles) {
            $manifestLines += "  - $copiedFile"
        }
    }

    [System.IO.File]::WriteAllLines(
        (Join-Path $licensesPath 'RUNTIME-COMPONENTS.txt'),
        $manifestLines,
        [System.Text.UTF8Encoding]::new($false))
    return $resolvedRuntimePacks
}

function Invoke-CodeSigning {
    param(
        [Parameter(Mandatory = $true)] [string]$PackagePath,
        [Parameter(Mandatory = $true)] [string]$CertificateThumbprint,
        [string]$ToolPath,
        [string]$TimestampServer,
        [switch]$AllowUntrustedSelfSigned
    )

    $normalizedThumbprint = $CertificateThumbprint.Replace(' ', '').ToUpperInvariant()
    if ($normalizedThumbprint -notmatch '^[0-9A-F]{40}$') {
        throw 'SigningCertificateThumbprint 必须是 40 位十六进制证书指纹。'
    }
    if (-not [string]::IsNullOrWhiteSpace($ToolPath) `
        -and -not (Test-Path -LiteralPath $ToolPath -PathType Leaf)) {
        throw "找不到 SignTool：$ToolPath"
    }
    if (-not [string]::IsNullOrWhiteSpace($TimestampServer)) {
        $parsedTimestamp = $null
        if (-not [Uri]::TryCreate($TimestampServer, [UriKind]::Absolute, [ref]$parsedTimestamp) `
            -or $parsedTimestamp.Scheme -notin @('http', 'https')) {
            throw 'TimestampUrl 必须是 HTTP 或 HTTPS 绝对 URL。'
        }
    }

    $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$normalizedThumbprint" -ErrorAction SilentlyContinue
    if ($null -eq $certificate -or -not $certificate.HasPrivateKey) {
        throw '当前用户证书存储中找不到对应的、带私钥的代码签名证书。'
    }
    $isSelfSigned = $certificate.Subject -eq $certificate.Issuer
    if ($isSelfSigned -and -not $AllowUntrustedSelfSigned) {
        throw '检测到自签名证书；必须显式使用 -AllowUntrustedSelfSignedCertificate。'
    }

    $signingTargets = @(
        (Join-Path $PackagePath 'Launcher.App.exe'),
        (Join-Path $PackagePath 'Launcher.Agent.exe')
    ) + @(Get-ChildItem -LiteralPath $PackagePath -Filter 'Launcher.*.dll' -File | Select-Object -ExpandProperty FullName)
    $signingTargets = $signingTargets | Sort-Object -Unique
    foreach ($target in $signingTargets) {
        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
            throw "缺少待签名文件：$target"
        }
        if ([string]::IsNullOrWhiteSpace($ToolPath)) {
            $signingParameters = @{
                FilePath     = $target
                Certificate  = $certificate
                HashAlgorithm = 'SHA256'
                IncludeChain = 'NotRoot'
            }
            if (-not [string]::IsNullOrWhiteSpace($TimestampServer)) {
                $signingParameters.TimestampServer = $TimestampServer
            }
            $result = Set-AuthenticodeSignature @signingParameters
            $verified = Get-AuthenticodeSignature -FilePath $target
            if ($null -eq $result.SignerCertificate `
                -or $result.SignerCertificate.Thumbprint -ne $normalizedThumbprint `
                -or $null -eq $verified.SignerCertificate `
                -or $verified.SignerCertificate.Thumbprint -ne $normalizedThumbprint `
                -or (-not [string]::IsNullOrWhiteSpace($TimestampServer) -and $null -eq $verified.TimeStamperCertificate) `
                -or $verified.Status -in @('NotSigned', 'HashMismatch', 'NotSupported')) {
                throw "PowerShell Authenticode 签名或时间戳验证失败：$target；状态=$($verified.Status)"
            }
            if (-not $AllowUntrustedSelfSigned -and $verified.Status -ne 'Valid') {
                throw "Authenticode 信任验证失败：$target；状态=$($verified.Status)"
            }
        }
        else {
            $signToolArguments = @('sign', '/sha1', $normalizedThumbprint, '/fd', 'SHA256')
            if (-not [string]::IsNullOrWhiteSpace($TimestampServer)) {
                $signToolArguments += @('/td', 'SHA256', '/tr', $TimestampServer)
            }
            $signToolArguments += $target
            & $ToolPath @signToolArguments
            if ($LASTEXITCODE -ne 0) {
                throw "Authenticode 签名失败：$target"
            }
            & $ToolPath verify /pa /all $target
            if ($LASTEXITCODE -ne 0 -and -not $AllowUntrustedSelfSigned) {
                throw "Authenticode 签名验证失败：$target"
            }
            $verified = Get-AuthenticodeSignature -FilePath $target
            if ($null -eq $verified.SignerCertificate `
                -or $verified.SignerCertificate.Thumbprint -ne $normalizedThumbprint `
                -or (-not [string]::IsNullOrWhiteSpace($TimestampServer) -and $null -eq $verified.TimeStamperCertificate) `
                -or $verified.Status -in @('NotSigned', 'HashMismatch', 'NotSupported')) {
                throw "Authenticode 签名或时间戳读取验证失败：$target；状态=$($verified.Status)"
            }
        }
    }
}

$signingArgumentsProvided = -not [string]::IsNullOrWhiteSpace($SigningCertificateThumbprint) `
    -or -not [string]::IsNullOrWhiteSpace($SignToolPath) `
    -or -not [string]::IsNullOrWhiteSpace($TimestampUrl)
if ($RequireSignature -or $signingArgumentsProvided) {
    if ([string]::IsNullOrWhiteSpace($SigningCertificateThumbprint)) {
        throw '签名发布必须提供 SigningCertificateThumbprint；SignToolPath 可省略以使用系统 PowerShell 签名。'
    }
    if (-not $AllowUntrustedSelfSignedCertificate -and [string]::IsNullOrWhiteSpace($TimestampUrl)) {
        throw '公共信任签名必须提供 TimestampUrl；自签名发布可以显式省略时间戳。'
    }
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $flavor = if ($SelfContained) { 'self-contained' } else { 'framework-dependent' }
    $stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
    $OutputDirectory = Join-Path $artifactsRoot "portable-win-x64-$flavor-$stamp"
}

$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
$allowedPrefix = $artifactsRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $outputPath.StartsWith($allowedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory 必须位于仓库 artifacts 目录内：$artifactsRoot"
}
if (Test-Path -LiteralPath $outputPath) {
    throw "发布目标已存在，拒绝覆盖或混入旧文件：$outputPath"
}

$archivePath = $outputPath + '.zip'
$archiveHashPath = $archivePath + '.sha256'
if ($Archive -and ((Test-Path -LiteralPath $archivePath) -or (Test-Path -LiteralPath $archiveHashPath))) {
    throw "发布压缩包或其校验文件已存在，拒绝覆盖：$archivePath"
}

$dotnet = Join-Path $repositoryRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    throw "找不到项目内 .NET SDK：$dotnet"
}

$solution = Join-Path $repositoryRoot 'ChatGPTLocalLauncher.sln'
$stagingRoot = Join-Path $artifactsRoot ('.publish-staging-' + [Guid]::NewGuid().ToString('N'))
$stagingPackage = Join-Path $stagingRoot 'package'
$testResults = Join-Path $stagingRoot 'test-results'
$archiveVerification = Join-Path $stagingRoot 'archive-verification'
New-Item -ItemType Directory -Path $stagingPackage -Force | Out-Null
New-Item -ItemType Directory -Path $testResults -Force | Out-Null

$published = $false
try {
    & $dotnet restore $solution --locked-mode -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) {
        throw '锁定依赖恢复失败。'
    }

    & $dotnet build $solution `
        --configuration Release `
        --no-restore `
        --verbosity minimal `
        -warnaserror
    if ($LASTEXITCODE -ne 0) {
        throw 'Release 构建门禁失败。'
    }

    & $dotnet format $solution --verify-no-changes --no-restore --verbosity quiet
    if ($LASTEXITCODE -ne 0) {
        throw '代码格式门禁失败。'
    }

    & $dotnet test $solution `
        --configuration Release `
        --no-restore `
        --logger 'trx;LogFileName=release.trx' `
        --results-directory $testResults `
        --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        throw '自动化测试失败。'
    }

    $trxPath = Join-Path $testResults 'release.trx'
    if (-not (Test-Path -LiteralPath $trxPath)) {
        throw "未生成测试结果：$trxPath"
    }
    [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
    $counters = $trx.SelectSingleNode("//*[local-name()='Counters']")
    if ($null -eq $counters) {
        throw '无法从 TRX 读取测试计数。'
    }
    $testsTotal = [int]$counters.total
    $testsPassed = [int]$counters.passed
    $testsFailed = [int]$counters.failed + [int]$counters.error + [int]$counters.timeout + [int]$counters.aborted
    if ($testsTotal -le 0 -or $testsPassed -ne $testsTotal -or $testsFailed -ne 0) {
        throw "测试计数不满足发布条件：total=$testsTotal passed=$testsPassed failed=$testsFailed"
    }

    $dependencyAudit = 'passed'
    if ($SkipDependencyAudit) {
        $dependencyAudit = 'skipped-by-explicit-switch'
    }
    else {
        $auditOutput = & $dotnet list $solution package `
            --vulnerable `
            --include-transitive `
            --no-restore `
            --format json `
            --output-version 1 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw '依赖漏洞审计失败；离线验收只能显式使用 -SkipDependencyAudit。'
        }
        $auditJson = $auditOutput -join [Environment]::NewLine
        try {
            $null = $auditJson | ConvertFrom-Json -Depth 100
        }
        catch {
            throw '依赖漏洞审计没有返回可解析的 JSON。'
        }
        if ([System.Text.RegularExpressions.Regex]::IsMatch(
            $auditJson,
            '"vulnerabilities"\s*:\s*\[\s*(?!\])',
            [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
            throw '依赖漏洞审计发现至少一个已知漏洞，拒绝发布。'
        }
        Write-Output 'Dependency vulnerability audit: 0 known vulnerable packages.'
    }

    $selfContainedValue = if ($SelfContained) { 'true' } else { 'false' }
    $projects = @(
        'src\Launcher.App\Launcher.App.csproj',
        'src\Launcher.Agent\Launcher.Agent.csproj'
    )
    foreach ($project in $projects) {
        & $dotnet publish (Join-Path $repositoryRoot $project) `
            --configuration Release `
            --runtime win-x64 `
            --self-contained $selfContainedValue `
            --no-restore `
            -p:NuGetAudit=false `
            --output $stagingPackage `
            --verbosity minimal
        if ($LASTEXITCODE -ne 0) {
            throw "发布失败：$project"
        }
    }

    $signatureStatus = 'unsigned'
    $signatureCertificateSha256 = 'none'
    if ($RequireSignature -or $signingArgumentsProvided) {
        Invoke-CodeSigning `
            -PackagePath $stagingPackage `
            -CertificateThumbprint $SigningCertificateThumbprint `
            -ToolPath $SignToolPath `
            -TimestampServer $TimestampUrl `
            -AllowUntrustedSelfSigned:$AllowUntrustedSelfSignedCertificate
        $signatureStatus = if ($AllowUntrustedSelfSignedCertificate) {
            'authenticode-self-signed-verified'
        }
        else {
            'authenticode-public-trust-verified'
        }
        $signedApp = Get-AuthenticodeSignature -FilePath (Join-Path $stagingPackage 'Launcher.App.exe')
        $signatureCertificateSha256 = $signedApp.SignerCertificate.GetCertHashString(
            [Security.Cryptography.HashAlgorithmName]::SHA256)
    }

    $agentExecutable = Join-Path $stagingPackage 'Launcher.Agent.exe'
    $appAssembly = Join-Path $stagingPackage 'Launcher.App.dll'
    if (-not (Test-Path -LiteralPath $agentExecutable) -or -not (Test-Path -LiteralPath $appAssembly)) {
        throw '发布目录缺少 Launcher.Agent.exe 或 Launcher.App.dll。'
    }
    $selfTestStartInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $selfTestStartInfo.FileName = $agentExecutable
    $selfTestStartInfo.UseShellExecute = $false
    $selfTestStartInfo.CreateNoWindow = $true
    $selfTestStartInfo.RedirectStandardOutput = $true
    $selfTestStartInfo.RedirectStandardError = $true
    $selfTestStartInfo.ArgumentList.Add('--self-test')
    $selfTestProcess = [System.Diagnostics.Process]::new()
    $selfTestProcess.StartInfo = $selfTestStartInfo
    try {
        if (-not $selfTestProcess.Start()) {
            throw '无法启动发布后的 Agent 自检。'
        }
        $selfTestOutputTask = $selfTestProcess.StandardOutput.ReadToEndAsync()
        $selfTestErrorTask = $selfTestProcess.StandardError.ReadToEndAsync()
        if (-not $selfTestProcess.WaitForExit(15000)) {
            $selfTestProcess.Kill($true)
            $selfTestProcess.WaitForExit()
            throw '发布后的 Agent 自检在 15 秒内没有退出。'
        }
        $selfTestOutput = $selfTestOutputTask.GetAwaiter().GetResult()
        $selfTestError = $selfTestErrorTask.GetAwaiter().GetResult()
        if ($selfTestProcess.ExitCode -ne 0 -or
            -not $selfTestOutput.Contains('Launcher.Agent self-test succeeded.', [System.StringComparison]::Ordinal)) {
            throw "发布后的 Agent 自检失败：$selfTestOutput $selfTestError"
        }
    }
    finally {
        $selfTestProcess.Dispose()
    }

    $bundledDotNetRuntimePacks = @()
    if ($SelfContained) {
        $bundledDotNetRuntimePacks = @(
            Copy-DotNetRuntimeNotices -PackagePath $stagingPackage -RepositoryRoot $repositoryRoot
        )
    }

    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'README.md') -Destination $stagingPackage
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination $stagingPackage
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'THIRD-PARTY-NOTICES.md') -Destination $stagingPackage
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'ChatGPT_Launcher_OpenSource_PRD.md') -Destination $stagingPackage
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'CHANGELOG.md') -Destination $stagingPackage
    $packageDocsPath = Join-Path $stagingPackage 'docs'
    $packageArchitecturePath = Join-Path $packageDocsPath 'architecture'
    New-Item -ItemType Directory -Path $packageArchitecturePath -Force | Out-Null
    Copy-Item `
        -LiteralPath (Join-Path $repositoryRoot 'docs\manual-acceptance-checklist.md') `
        -Destination $packageDocsPath
    Copy-Item `
        -LiteralPath (Join-Path $repositoryRoot 'docs\signing-policy.md') `
        -Destination $packageDocsPath
    Copy-Item `
        -LiteralPath (Join-Path $repositoryRoot 'docs\user-guide.md') `
        -Destination $packageDocsPath
    Copy-Item `
        -LiteralPath (Join-Path $repositoryRoot 'docs\release-notes-0.9.1.md') `
        -Destination $packageDocsPath
    Copy-Item `
        -LiteralPath (Join-Path $repositoryRoot 'docs\release-security-review-0.9.1.md') `
        -Destination $packageDocsPath
    Copy-Item `
        -LiteralPath (Join-Path $repositoryRoot 'docs\architecture\0002-thin-launcher-boundary.md') `
        -Destination $packageArchitecturePath

    $sourceFiles = Get-ChildItem -LiteralPath $repositoryRoot -Recurse -File |
        Where-Object {
            $relative = [System.IO.Path]::GetRelativePath($repositoryRoot, $_.FullName)
            -not ($relative -match '^(artifacts|\.tools)(\\|$)') -and
            -not ($relative -match '^\.(git|codex|agents)(\\|$)') -and
            -not ($relative -match '(^|\\)(bin|obj)(\\|$)')
        } |
        Sort-Object FullName
    $sourceDigestLines = $sourceFiles | ForEach-Object {
        $relative = [System.IO.Path]::GetRelativePath($repositoryRoot, $_.FullName).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $relative"
    }
    $sourceDigestBytes = [System.Text.Encoding]::UTF8.GetBytes(($sourceDigestLines -join "`n"))
    $sourceTreeSha256 = [Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData($sourceDigestBytes)).ToLowerInvariant()
    $assemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($appAssembly).Version.ToString()
    $appVersionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $stagingPackage 'Launcher.App.exe'))

    $buildInfo = @(
        'ChatGPT Local Launcher public beta',
        "ProductVersion=$($appVersionInfo.ProductVersion)",
        "Developer=$($appVersionInfo.CompanyName)",
        "BuiltUtc=$([DateTimeOffset]::UtcNow.ToString('O'))",
        "AssemblyVersion=$assemblyVersion",
        "Signature=$signatureStatus",
        "SigningCertificateSha256=$signatureCertificateSha256",
        'RuntimeIdentifier=win-x64',
        "SelfContained=$selfContainedValue",
        "BundledDotNetRuntimePacks=$(if ($bundledDotNetRuntimePacks.Count -gt 0) { $bundledDotNetRuntimePacks -join ',' } else { 'none' })",
        'MinimumWindowsApi=10.0.19041.0',
        'ValidatedWindowsTarget=Windows 10 22H2 x64',
        'LlamaRequirement=health, models, and OpenAI-compatible POST /v1/responses',
        "TestsTotal=$testsTotal",
        "TestsPassed=$testsPassed",
        "DependencyAudit=$dependencyAudit",
        "SourceTreeSha256=$sourceTreeSha256"
    ) -join [Environment]::NewLine
    [System.IO.File]::WriteAllText(
        (Join-Path $stagingPackage 'BUILD-INFO.txt'),
        $buildInfo + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false))

    $hashManifestPath = Join-Path $stagingPackage 'SHA256SUMS.txt'
    $hashLines = Get-ChildItem -LiteralPath $stagingPackage -Recurse -File |
        Sort-Object FullName |
        ForEach-Object {
            $relativePath = [System.IO.Path]::GetRelativePath($stagingPackage, $_.FullName).Replace('\', '/')
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash  $relativePath"
        }
    [System.IO.File]::WriteAllLines(
        $hashManifestPath,
        $hashLines,
        [System.Text.UTF8Encoding]::new($false))

    Move-Item -LiteralPath $stagingPackage -Destination $outputPath
    $published = $true

    if ($Archive) {
        Compress-Archive -Path (Join-Path $outputPath '*') -DestinationPath $archivePath -CompressionLevel Optimal
        New-Item -ItemType Directory -Path $archiveVerification -Force | Out-Null
        Expand-Archive -LiteralPath $archivePath -DestinationPath $archiveVerification

        $packageFiles = Get-ChildItem -LiteralPath $outputPath -Recurse -File
        $extractedFiles = Get-ChildItem -LiteralPath $archiveVerification -Recurse -File
        if ($packageFiles.Count -ne $extractedFiles.Count) {
            throw 'ZIP 内容数量与发布目录不一致。'
        }
        foreach ($file in $packageFiles) {
            $relative = [System.IO.Path]::GetRelativePath($outputPath, $file.FullName)
            $extracted = Join-Path $archiveVerification $relative
            if (-not (Test-Path -LiteralPath $extracted)) {
                throw "ZIP 缺少文件：$relative"
            }
            $sourceHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            $extractedHash = (Get-FileHash -LiteralPath $extracted -Algorithm SHA256).Hash
            if ($sourceHash -ne $extractedHash) {
                throw "ZIP 文件哈希不一致：$relative"
            }
        }

        $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
        [System.IO.File]::WriteAllText(
            $archiveHashPath,
            "$archiveHash  $([System.IO.Path]::GetFileName($archivePath))$([Environment]::NewLine)",
            [System.Text.UTF8Encoding]::new($false))
        Write-Output "Archive: $archivePath"
        Write-Output "Archive hash: $archiveHashPath"
    }

    Write-Output "Package: $outputPath"
}
catch {
    if ($published) {
        Write-Warning "发布目录已生成但后续校验失败，请勿分发：$outputPath"
    }
    throw
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        try {
            Remove-Item -LiteralPath $stagingRoot -Recurse -Force
        }
        catch {
            Write-Warning "无法清理发布暂存目录：$stagingRoot。$($_.Exception.Message)"
        }
    }
}
