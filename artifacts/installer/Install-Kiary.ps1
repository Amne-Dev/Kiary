param(
    [switch]$Force = $false,
    [switch]$ElevatedInstall = $false
)

$ErrorActionPreference = "Stop"

function Test-IsAdmin {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object System.Security.Principal.WindowsPrincipal($id)
    return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Add-CertificateToStore {
    param(
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [string]$StoreLocation,
        [string]$StoreName
    )

    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($StoreName, $StoreLocation)
    $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    try {
        $existing = $store.Certificates | Where-Object { $_.Thumbprint -eq $Certificate.Thumbprint } | Select-Object -First 1
        if (-not $existing) {
            Write-Host "Installing certificate into $StoreLocation\\$StoreName..."
            $store.Add($Certificate)
        }
        else {
            Write-Host "Certificate already present in $StoreLocation\\$StoreName."
        }
    }
    finally {
        $store.Close()
    }
}

function Get-ArchFolderName {
    $arch = $env:PROCESSOR_ARCHITECTURE
    switch -Regex ($arch) {
        "AMD64|x64" { return "x64" }
        "ARM64" { return "arm64" }
        "x86|X86" { return "x86" }
        default { return "x64" }
    }
}

$msixRoot = Join-Path $PSScriptRoot "msix"
$msix = Get-ChildItem -Path $msixRoot -Recurse -Filter *.msix |
    Sort-Object -Property LastWriteTimeUtc -Descending |
    Select-Object -First 1
if (-not $msix) {
    throw "No .msix package found under: $msixRoot"
}

$certPath = [System.IO.Path]::ChangeExtension($msix.FullName, ".cer")
if (-not (Test-Path $certPath)) {
    Write-Host "Matching .cer was not found. Inspecting package signature..."
    $signature = Get-AuthenticodeSignature -FilePath $msix.FullName
    if ($signature.Status -ne "Valid" -or -not $signature.SignerCertificate) {
        throw ("Package is not signed. Build a signed MSIX first. " +
               "Example: dotnet build EncryptedDiary.WinUI.csproj -c Release " +
               "-p:Platform=x64 -p:WindowsPackageType=MSIX -p:GenerateAppxPackageOnBuild=true " +
               "-p:AppxPackageDir=artifacts\\installer\\msix\\ -p:AppxBundle=Never " +
               "-p:UapAppxPackageBuildMode=SideloadOnly -p:AppxSymbolPackageEnabled=false " +
               "-p:AppxPackageSigningEnabled=true -p:PackageCertificateThumbprint=<thumbprint>")
    }

    [System.IO.File]::WriteAllBytes(
        $certPath,
        $signature.SignerCertificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    Write-Host "Exported signer certificate to: $certPath"
}

Write-Host "Found package: $($msix.FullName)"
Write-Host "Package timestamp: $($msix.LastWriteTime.ToString('u'))"
Write-Host "Found certificate: $certPath"

$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($certPath)

if (-not (Test-IsAdmin)) {
    if (-not $ElevatedInstall) {
        Write-Host "This installation requires administrator rights to trust the MSIX certificate machine-wide."
        Write-Host "Requesting elevation..."
        $args = @(
            "-ExecutionPolicy", "Bypass",
            "-File", "`"$PSCommandPath`"",
            "-ElevatedInstall"
        )
        if ($Force) { $args += "-Force" }
        Start-Process -FilePath "powershell.exe" -ArgumentList $args -Verb RunAs | Out-Null
        return
    }

    throw "Failed to acquire administrator rights."
}

Add-CertificateToStore -Certificate $cert -StoreLocation "CurrentUser" -StoreName "TrustedPeople"
Add-CertificateToStore -Certificate $cert -StoreLocation "CurrentUser" -StoreName "Root"
Add-CertificateToStore -Certificate $cert -StoreLocation "LocalMachine" -StoreName "TrustedPeople"
Add-CertificateToStore -Certificate $cert -StoreLocation "LocalMachine" -StoreName "Root"

$packageFolder = Split-Path -Parent $msix.FullName
$depFolder = Join-Path $packageFolder ("Dependencies\" + (Get-ArchFolderName))
$depPackages = @()
if (Test-Path $depFolder) {
    $depPackages = Get-ChildItem -Path $depFolder -File | Where-Object { $_.Extension -in ".appx", ".msix" } | Select-Object -ExpandProperty FullName
}

if ($depPackages.Count -gt 0) {
    Write-Host "Installing with dependencies from: $depFolder"
}
else {
    Write-Host "No dependency packages found for this architecture."
}

try {
    if ($depPackages.Count -gt 0) {
        Add-AppxPackage -Path $msix.FullName -DependencyPath $depPackages -ForceApplicationShutdown -ForceUpdateFromAnyVersion -ErrorAction Stop
    }
    else {
        Add-AppxPackage -Path $msix.FullName -ForceApplicationShutdown -ForceUpdateFromAnyVersion -ErrorAction Stop
    }

    Write-Host "Kiary installed successfully."
}
catch {
    Write-Host ("Install failed: " + $_.Exception.Message)
    throw
}
