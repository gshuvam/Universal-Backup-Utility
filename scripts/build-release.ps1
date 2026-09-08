<#
.SYNOPSIS
    Master build, bundling, code-signing, and release packaging script.
.DESCRIPTION
    Builds Universal Backup projects in Release mode, bundles pinned sidecars,
    signs binaries, and produces production installer and portable archive packages.
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.1",
    [string]$TargetPlatform = "win-x64", # "win-x64", "linux-x64", or "all"
    [switch]$SkipTests,
    [switch]$SkipSigning
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RootDir = Split-Path -Parent $ScriptDir
$ArtifactsDir = Join-Path $RootDir "artifacts"
$PublishBaseDir = Join-Path $ArtifactsDir "publish"
$DistDir = Join-Path $RootDir "dist"

$env:PATH = "C:\Users\Shuvam\.dotnet;" + $env:PATH

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " Universal Backup Utility - Release Packaging Pipeline" -ForegroundColor Cyan
Write-Host " Version: $Version | Config: $Configuration | Platform: $TargetPlatform" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Clean output directories
if (-not (Test-Path $DistDir)) {
    New-Item -ItemType Directory -Path $DistDir -Force | Out-Null
}

# 2. Automated Test Suite Gate
if (-not $SkipTests) {
    Write-Host "`n[1/6] Running automated test suite gate..." -ForegroundColor Yellow
    & dotnet test (Join-Path $RootDir "UniversalBackup.sln") -c $Configuration --verbosity quiet
    if ($LASTEXITCODE -ne 0) {
        throw "Test suite execution failed with exit code $LASTEXITCODE. Release packaging aborted."
    }
    Write-Host "  [PASSED] All automated unit and integration tests passed." -ForegroundColor Green
} else {
    Write-Host "`n[1/6] Automated tests skipped by switch." -ForegroundColor Gray
}

# 3. Bundle Pinned Runtime Engines
Write-Host "`n[2/6] Staging verified pinned runtimes (restic & rclone)..." -ForegroundColor Yellow
& (Join-Path $ScriptDir "bundle-runtimes.ps1") -TargetPlatform $TargetPlatform

$platformsToBuild = if ($TargetPlatform -eq "all") { @("win-x64", "linux-x64") } else { @($TargetPlatform) }

foreach ($plat in $platformsToBuild) {
    Write-Host "`n[3/6] Publishing application assemblies for $plat..." -ForegroundColor Yellow
    $platPublishDir = Join-Path $PublishBaseDir $plat
    if (Test-Path $platPublishDir) {
        Remove-Item -Path $platPublishDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $platPublishDir -Force | Out-Null

    # Publish Desktop
    Write-Host "  Publishing UniversalBackup.Desktop ($plat)..."
    & dotnet publish (Join-Path $RootDir "src\UniversalBackup.Desktop\UniversalBackup.Desktop.csproj") `
        -c $Configuration `
        -r $plat `
        --self-contained false `
        -o $platPublishDir `
        /p:Version=$Version /p:AssemblyVersion="$Version.0" /p:FileVersion="$Version.0"

    # Publish CLI
    Write-Host "  Publishing UniversalBackup.Cli ($plat)..."
    & dotnet publish (Join-Path $RootDir "src\UniversalBackup.Cli\UniversalBackup.Cli.csproj") `
        -c $Configuration `
        -r $plat `
        --self-contained false `
        -o $platPublishDir `
        /p:Version=$Version /p:AssemblyVersion="$Version.0" /p:FileVersion="$Version.0"

    # Publish Privileged Helper
    Write-Host "  Publishing UniversalBackup.PrivilegedHelper ($plat)..."
    & dotnet publish (Join-Path $RootDir "src\UniversalBackup.PrivilegedHelper\UniversalBackup.PrivilegedHelper.csproj") `
        -c $Configuration `
        -r $plat `
        --self-contained false `
        -o $platPublishDir `
        /p:Version=$Version /p:AssemblyVersion="$Version.0" /p:FileVersion="$Version.0"

    # Copy LICENSE and docs
    if (Test-Path (Join-Path $RootDir "LICENSE")) {
        Copy-Item (Join-Path $RootDir "LICENSE") $platPublishDir -Force
    }

    # Stage native sidecars directly into publish output for zero-dependency execution
    $stagedRuntimes = Join-Path $RootDir "runtimes\$plat\native"
    if (Test-Path $stagedRuntimes) {
        Write-Host "  Copying staged engine runtimes into publish directory..."
        Copy-Item (Join-Path $stagedRuntimes "*") $platPublishDir -Force -Recurse
        # Also preserve runtimes subfolder
        $subRuntimeDir = Join-Path $platPublishDir "runtimes\$plat\native"
        New-Item -ItemType Directory -Path $subRuntimeDir -Force | Out-Null
        Copy-Item (Join-Path $stagedRuntimes "*") $subRuntimeDir -Force -Recurse
    }

    # 4. Code Signing
    if ($plat -eq "win-x64" -and (-not $SkipSigning)) {
        Write-Host "`n[4/6] Executing Authenticode code signing..." -ForegroundColor Yellow
        & (Join-Path $ScriptDir "sign-binaries.ps1") -DirectoryToSign $platPublishDir -AllowSelfSignedDevCert
    }

    # 5. Packaging Artifacts
    Write-Host "`n[5/6] Creating release distribution packages for $plat..." -ForegroundColor Yellow
    if ($plat -eq "win-x64") {
        # Portable ZIP package
        $zipName = "UniversalBackup-$Version-win-x64-Portable.zip"
        $zipPath = Join-Path $DistDir $zipName
        if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
        Write-Host "  Compressing $zipName..."
        Compress-Archive -Path (Join-Path $platPublishDir "*") -DestinationPath $zipPath -Force
        Write-Host "  [CREATED] $zipPath ($((Get-Item $zipPath).Length / 1MB | ForEach-Object { $_.ToString('N2') }) MB)" -ForegroundColor Green

        # Inno Setup Installer Check
        $iscc = Get-Command "iscc" -ErrorAction SilentlyContinue
        if ($iscc) {
            Write-Host "  Compiling Inno Setup installer..."
            $issFile = Join-Path $RootDir "packaging\windows\inno\UniversalBackupSetup.iss"
            & $iscc.Source $issFile
            Write-Host "  [CREATED] Inno Setup installer compiled successfully." -ForegroundColor Green
        } else {
            Write-Host "  [NOTICE] Inno Setup Compiler (iscc.exe) not in PATH. UniversalBackupSetup.iss is staged and ready for CI/CD." -ForegroundColor Yellow
        }
    }
    elseif ($plat -eq "linux-x64") {
        # Linux .tar.gz package
        $tarName = "UniversalBackup-$Version-linux-x64.tar.gz"
        $tarPath = Join-Path $DistDir $tarName
        if (Test-Path $tarPath) { Remove-Item $tarPath -Force }
        Write-Host "  Archiving $tarName..."
        tar -czf $tarPath -C $platPublishDir .
        Write-Host "  [CREATED] $tarPath ($((Get-Item $tarPath).Length / 1MB | ForEach-Object { $_.ToString('N2') }) MB)" -ForegroundColor Green
    }
}

# 6. Generate SHA-256 Checksums
Write-Host "`n[6/6] Generating SHA256 checksums..." -ForegroundColor Yellow
$checksumFile = Join-Path $DistDir "SHA256SUMS.txt"
$distFiles = Get-ChildItem -Path $DistDir -File | Where-Object { $_.Name -ne "SHA256SUMS.txt" }
$hashLines = @()

foreach ($f in $distFiles) {
    $hash = (Get-FileHash -Algorithm SHA256 $f.FullName).Hash.ToLowerInvariant()
    $hashLines += "$hash  $($f.Name)"
}

$hashLines | Set-Content -Path $checksumFile -Encoding utf8
Write-Host "  [CREATED] $checksumFile" -ForegroundColor Green
foreach ($line in $hashLines) {
    Write-Host "    $line" -ForegroundColor Gray
}

Write-Host "`n==========================================================" -ForegroundColor Green
Write-Host " [SUCCESS] Packaging pipeline completed successfully!" -ForegroundColor Green
Write-Host " Packages ready in: $DistDir" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
