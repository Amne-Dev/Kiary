param(
    [string]$CertSubject = "",
    [switch]$CreateCertificateIfMissing = $true,
    [int]$CertificateYears = 5
)

$ErrorActionPreference = "Stop"

function Get-CodeSigningCertificate {
    param([string]$Subject)

    return Get-ChildItem -Path Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
}

function Get-ManifestPublisher {
    param([string]$ManifestPath)

    [xml]$manifest = Get-Content -Path $ManifestPath
    $ns = New-Object System.Xml.XmlNamespaceManager($manifest.NameTable)
    $ns.AddNamespace("appx", "http://schemas.microsoft.com/appx/manifest/foundation/windows10")

    $identity = $manifest.SelectSingleNode("/appx:Package/appx:Identity", $ns)
    if (-not $identity -or [string]::IsNullOrWhiteSpace($identity.Publisher)) {
        throw "Could not read Package/Identity/Publisher from $ManifestPath"
    }

    return $identity.Publisher.Trim()
}

function New-CodeSigningCertificate {
    param(
        [string]$Subject,
        [int]$Years
    )

    Write-Host "Creating self-signed code-signing certificate: $Subject"
    return New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $Subject `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -HashAlgorithm "SHA256" `
        -NotAfter (Get-Date).AddYears($Years) `
        -KeyExportPolicy Exportable
}

$manifestPath = Join-Path $PSScriptRoot "..\..\Package.appxmanifest"
$manifestPath = (Resolve-Path $manifestPath).Path
if ([string]::IsNullOrWhiteSpace($CertSubject)) {
    $CertSubject = Get-ManifestPublisher -ManifestPath $manifestPath
}

$cert = Get-CodeSigningCertificate -Subject $CertSubject
if (-not $cert) {
    if (-not $CreateCertificateIfMissing) {
        throw "No signing certificate found for $CertSubject in Cert:\CurrentUser\My."
    }

    $cert = New-CodeSigningCertificate -Subject $CertSubject -Years $CertificateYears
}

Write-Host "Using certificate:"
Write-Host "  Subject   : $($cert.Subject)"
Write-Host "  Thumbprint: $($cert.Thumbprint)"
Write-Host "  Expires   : $($cert.NotAfter.ToString('u'))"

$project = Join-Path $PSScriptRoot "..\..\EncryptedDiary.WinUI.csproj"
$project = (Resolve-Path $project).Path

$buildArgs = @(
    "build", $project,
    "-c", "Release",
    "-p:Platform=x64",
    "-p:WindowsPackageType=MSIX",
    "-p:GenerateAppxPackageOnBuild=true",
    "-p:AppxPackageDir=artifacts\installer\msix\",
    "-p:AppxBundle=Never",
    "-p:UapAppxPackageBuildMode=SideloadOnly",
    "-p:AppxSymbolPackageEnabled=false",
    "-p:AppxPackageSigningEnabled=true",
    "-p:PackageCertificateThumbprint=$($cert.Thumbprint)"
)

Write-Host "Building signed MSIX..."
& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE."
}

$msixRoot = Join-Path $PSScriptRoot "msix"
$msix = Get-ChildItem -Path $msixRoot -Recurse -Filter *.msix |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1

if (-not $msix) {
    throw "No MSIX found under $msixRoot after build."
}

$signature = Get-AuthenticodeSignature -FilePath $msix.FullName
if ($signature.Status -eq "NotSigned" -or -not $signature.SignerCertificate) {
    throw "MSIX is not signed. Status: $($signature.Status) - $($signature.StatusMessage)"
}

if ($signature.SignerCertificate.Thumbprint -ne $cert.Thumbprint) {
    throw ("MSIX was signed with unexpected certificate. " +
           "Expected: $($cert.Thumbprint), Actual: $($signature.SignerCertificate.Thumbprint)")
}

if ($signature.Status -ne "Valid") {
    Write-Warning ("MSIX is signed but not fully trusted on this machine yet. " +
                   "Status: $($signature.Status) - $($signature.StatusMessage)")
}

$cerPath = [System.IO.Path]::ChangeExtension($msix.FullName, ".cer")
[System.IO.File]::WriteAllBytes(
    $cerPath,
    $signature.SignerCertificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))

Write-Host "Signed MSIX ready:"
Write-Host "  Package: $($msix.FullName)"
Write-Host "  Cert   : $cerPath"
Write-Host "Next step: powershell -ExecutionPolicy Bypass -File .\artifacts\installer\Install-Kiary.ps1"
