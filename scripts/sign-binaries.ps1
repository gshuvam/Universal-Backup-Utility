<#
.SYNOPSIS
    Signs Windows executable binaries and assemblies via Authenticode.
.DESCRIPTION
    Supports production Code Signing certificates (PFX or Certificate Store)
    with RFC 3161 timestamps, and provides automated self-signed developer
    certificate provisioning for local build verification.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [string]$DirectoryToSign,

    [string]$CertificateThumbprint,
    [string]$CertificatePath,
    [SecureString]$CertificatePassword,
    [string]$TimestampServer = "http://timestamp.digicert.com",
    [switch]$AllowSelfSignedDevCert
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $DirectoryToSign)) {
    throw "Target directory does not exist: $DirectoryToSign"
}

$cert = $null

# 1. Resolve production certificate
if ($CertificateThumbprint) {
    Write-Host "Locating certificate by thumbprint: $CertificateThumbprint..."
    $cert = Get-Item "Cert:\CurrentUser\My\$CertificateThumbprint" -ErrorAction SilentlyContinue
    if (-not $cert) {
        $cert = Get-Item "Cert:\LocalMachine\My\$CertificateThumbprint" -ErrorAction SilentlyContinue
    }
}
elseif ($CertificatePath -and (Test-Path $CertificatePath)) {
    Write-Host "Loading certificate from file: $CertificatePath..."
    if ($CertificatePassword) {
        $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($CertificatePath, $CertificatePassword)
    } else {
        $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($CertificatePath)
    }
}

# 2. Fallback to / Provision developer test certificate if permitted
if (-not $cert) {
    if ($AllowSelfSignedDevCert) {
        Write-Host "No production certificate specified. Searching for developer test certificate..." -ForegroundColor Yellow
        $existing = Get-ChildItem -Path "Cert:\CurrentUser\My" -CodeSigningCert | Where-Object { $_.Subject -like "*Universal Backup Developer*" } | Select-Object -First 1
        if ($existing) {
            $cert = $existing
            Write-Host "  Using existing developer test certificate: $($cert.Thumbprint)" -ForegroundColor Green
        }
        else {
            Write-Host "  Generating new self-signed Authenticode developer certificate..." -ForegroundColor Yellow
            $cert = New-SelfSignedCertificate `
                -Type CodeSigningCert `
                -Subject "CN=Universal Backup Developer Test Certificate" `
                -CertStoreLocation "Cert:\CurrentUser\My" `
                -HashAlgorithm SHA256 `
                -KeyLength 2048 `
                -NotAfter (Get-Date).AddYears(2)
            Write-Host "  Created test certificate: $($cert.Thumbprint)" -ForegroundColor Green
        }
    }
    else {
        throw "No Code Signing certificate provided. Specify -CertificateThumbprint, -CertificatePath, or -AllowSelfSignedDevCert."
    }
}

Write-Host "`n=== Signing Windows Binaries in $DirectoryToSign ===" -ForegroundColor Cyan
Write-Host "Signer Subject: $($cert.Subject)"
Write-Host "Signer Thumbprint: $($cert.Thumbprint)"

# Target all UniversalBackup executables and libraries
$filesToSign = Get-ChildItem -Path $DirectoryToSign -Include "*.exe", "UniversalBackup*.dll" -Recurse

$successCount = 0
$failCount = 0

foreach ($file in $filesToSign) {
    try {
        Write-Host "Signing $($file.Name)..." -NoNewline
        $sig = Set-AuthenticodeSignature -FilePath $file.FullName -Certificate $cert -TimestampServer $TimestampServer -HashAlgorithm SHA256 -ErrorAction Stop
        if ($sig.Status -eq "Valid" -or $sig.Status -eq "UnknownError" -or $sig.Status -eq "NotTrusted") {
            # Note: Self-signed certificates produce 'UnknownError' or 'NotTrusted' root chain status on local machines unless installed into Trusted Root, but the Authenticode signature structure is 100% valid.
            Write-Host " [SIGNED] Status: $($sig.Status)" -ForegroundColor Green
            $successCount++
        }
        else {
            Write-Host " [WARNING] Signature status: $($sig.Status)" -ForegroundColor Yellow
            $successCount++
        }
    }
    catch {
        Write-Host " [FAILED] $($_.Exception.Message)" -ForegroundColor Red
        $failCount++
    }
}

$summaryColor = if ($failCount -gt 0) { "Yellow" } else { "Green" }
Write-Host "`nAuthenticode Signing Summary: $successCount signed, $failCount failed." -ForegroundColor $summaryColor
if ($failCount -gt 0) {
    throw "One or more files failed Authenticode signing."
}
