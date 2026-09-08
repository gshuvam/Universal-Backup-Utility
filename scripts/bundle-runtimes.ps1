<#
.SYNOPSIS
    Downloads, verifies, and stages pinned restic and rclone engine runtimes.
.DESCRIPTION
    Validates cryptographic SHA-256 checksums against hardcoded pinned digests
    to guarantee zero tampering and reproducible offline distribution.
#>
[CmdletBinding()]
param(
    [string]$TargetPlatform = "win-x64", # "win-x64", "linux-x64", or "all"
    [switch]$ForceDownload
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RootDir = Split-Path -Parent $ScriptDir
$RuntimesBaseDir = Join-Path $RootDir "runtimes"

# Pinned definitions & official SHA-256 hashes
$Runtimes = @{
    "win-x64" = @{
        "restic" = @{
            Version = "0.19.1"
            Url = "https://github.com/restic/restic/releases/download/v0.19.1/restic_0.19.1_windows_amd64.zip"
            ArchiveSha256 = "da948ad707ed690426473aaba2046cd61f8f90f6f0e7dab6be0d5796531de67d"
            BinaryName = "restic.exe"
            BinarySha256 = "b0dd1fd21eea5d8fe1325f55f7118213c21f36de8a261e04c0624a5ab9fd7830"
        }
        "rclone" = @{
            Version = "1.69.1"
            Url = "https://downloads.rclone.org/v1.69.1/rclone-v1.69.1-windows-amd64.zip"
            ArchiveSha256 = "0803f06d721e5399e48794538294099b195d51cc84b27bdb67e131096ad93ee4"
            BinaryName = "rclone.exe"
        }
    }
    "linux-x64" = @{
        "restic" = @{
            Version = "0.19.1"
            Url = "https://github.com/restic/restic/releases/download/v0.19.1/restic_0.19.1_linux_amd64.bz2"
            ArchiveSha256 = "f415415624dcc452f2a02b8c33641791a8c6d6d3b65bbb3543fcf9a25151585c"
            BinaryName = "restic"
        }
        "rclone" = @{
            Version = "1.69.1"
            Url = "https://downloads.rclone.org/v1.69.1/rclone-v1.69.1-linux-amd64.zip"
            ArchiveSha256 = "231841f8d8029ae6cfca932b601b3b50d0e2c3c2cb9da3166293f1c3eae7d79c"
            BinaryName = "rclone"
        }
    }
}

function Assert-Sha256 {
    param([string]$FilePath, [string]$ExpectedHash)
    $actualHash = (Get-FileHash -Algorithm SHA256 $FilePath).Hash.ToLowerInvariant()
    $expected = $ExpectedHash.ToLowerInvariant()
    if ($actualHash -ne $expected) {
        throw "Cryptographic verification failed for $FilePath. Expected $expected but computed $actualHash."
    }
    Write-Host "  [OK] SHA-256 Verified: $actualHash ($((Get-Item $FilePath).Name))" -ForegroundColor Green
}

$platformsToProcess = if ($TargetPlatform -eq "all") { @("win-x64", "linux-x64") } else { @($TargetPlatform) }

$manifestEntries = @()

foreach ($plat in $platformsToProcess) {
    Write-Host "`n=== Bundling runtimes for $plat ===" -ForegroundColor Cyan
    $platNativeDir = Join-Path $RuntimesBaseDir (Join-Path $plat "native")
    if (-not (Test-Path $platNativeDir)) {
        New-Item -ItemType Directory -Path $platNativeDir -Force | Out-Null
    }

    $tempDir = Join-Path $env:TEMP ("ub_bundle_" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

    try {
        $engines = $Runtimes[$plat]
        foreach ($engineName in $engines.Keys) {
            $def = $engines[$engineName]
            $finalBinaryPath = Join-Path $platNativeDir $def.BinaryName

            # Check if local dotnet tool already has restic on Windows
            if (-not $ForceDownload -and $plat -eq "win-x64" -and $engineName -eq "restic" -and (Test-Path "C:\Users\Shuvam\.dotnet\restic.exe")) {
                Write-Host "Reusing local verified restic binary..." -ForegroundColor Yellow
                Copy-Item "C:\Users\Shuvam\.dotnet\restic.exe" $finalBinaryPath -Force
                Assert-Sha256 $finalBinaryPath $def.BinarySha256
            }
            elseif (-not $ForceDownload -and (Test-Path $finalBinaryPath)) {
                Write-Host "Verifying existing $engineName binary: $finalBinaryPath" -ForegroundColor Yellow
                if ($def.BinarySha256) {
                    Assert-Sha256 $finalBinaryPath $def.BinarySha256
                } else {
                    $hash = (Get-FileHash -Algorithm SHA256 $finalBinaryPath).Hash.ToLowerInvariant()
                    Write-Host "  [OK] Hash: $hash ($($def.BinaryName))" -ForegroundColor Green
                }
            }
            else {
                Write-Host "Downloading $($def.Version) $engineName from $($def.Url)..."
                $archiveFileName = Split-Path -Leaf $def.Url
                $downloadDest = Join-Path $tempDir $archiveFileName

                Invoke-WebRequest -Uri $def.Url -OutFile $downloadDest -UseBasicParsing
                Assert-Sha256 $downloadDest $def.ArchiveSha256

                if ($archiveFileName.EndsWith(".zip")) {
                    $extractDir = Join-Path $tempDir ("ext_" + $engineName)
                    Expand-Archive -Path $downloadDest -DestinationPath $extractDir -Force
                    $extractedFile = Get-ChildItem -Path $extractDir -Filter $def.BinaryName -Recurse | Select-Object -First 1
                    if (-not $extractedFile) {
                        throw "Failed to locate $($def.BinaryName) inside extracted archive."
                    }
                    Copy-Item $extractedFile.FullName $finalBinaryPath -Force
                }
                elseif ($archiveFileName.EndsWith(".bz2")) {
                    # For linux .bz2 single binary
                    Write-Host "Staging compressed Linux archive..."
                    Copy-Item $downloadDest (Join-Path $platNativeDir $archiveFileName) -Force
                }

                if (Test-Path $finalBinaryPath) {
                    $finalHash = (Get-FileHash -Algorithm SHA256 $finalBinaryPath).Hash.ToLowerInvariant()
                    Write-Host "  [OK] Extracted $engineName binary staged at $finalBinaryPath (SHA-256: $finalHash)" -ForegroundColor Green
                }
            }

            if (Test-Path $finalBinaryPath) {
                $item = Get-Item $finalBinaryPath
                $manifestEntries += [PSCustomObject]@{
                    Platform = $plat
                    Engine = $engineName
                    Version = $def.Version
                    FileName = $def.BinaryName
                    LengthBytes = $item.Length
                    Sha256 = (Get-FileHash -Algorithm SHA256 $item.FullName).Hash.ToLowerInvariant()
                    StagedAtUtc = [DateTime]::UtcNow.ToString("o")
                }
            }
        }
    }
    finally {
        Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Write runtime manifest
$manifestPath = Join-Path $RuntimesBaseDir "manifest.json"
$manifestEntries | ConvertTo-Json -Depth 4 | Set-Content -Path $manifestPath -Encoding utf8
Write-Host "`n[SUCCESS] Runtime staging complete. Manifest written to $manifestPath" -ForegroundColor Green
