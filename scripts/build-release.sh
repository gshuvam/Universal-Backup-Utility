#!/usr/bin/env bash
# Master release packaging pipeline for Linux
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
PUBLISH_DIR="${ROOT_DIR}/artifacts/publish/linux-x64"
DIST_DIR="${ROOT_DIR}/dist"
VERSION="1.0.1"

mkdir -p "${PUBLISH_DIR}"
mkdir -p "${DIST_DIR}"

echo "=== Universal Backup Utility - Linux Packaging Pipeline ==="

# 1. Run Tests
echo "[1/4] Running automated tests..."
dotnet test "${ROOT_DIR}/UniversalBackup.sln" -c Release --verbosity quiet

# 2. Stage runtimes
echo "[2/4] Staging pinned runtimes..."
bash "${SCRIPT_DIR}/bundle-runtimes.sh"

# 3. Publish assemblies
echo "[3/4] Publishing .NET 10 assemblies..."
dotnet publish "${ROOT_DIR}/src/UniversalBackup.Desktop/UniversalBackup.Desktop.csproj" -c Release -r linux-x64 --self-contained false -o "${PUBLISH_DIR}" /p:Version="${VERSION}"
dotnet publish "${ROOT_DIR}/src/UniversalBackup.Cli/UniversalBackup.Cli.csproj" -c Release -r linux-x64 --self-contained false -o "${PUBLISH_DIR}" /p:Version="${VERSION}"
dotnet publish "${ROOT_DIR}/src/UniversalBackup.PrivilegedHelper/UniversalBackup.PrivilegedHelper.csproj" -c Release -r linux-x64 --self-contained false -o "${PUBLISH_DIR}" /p:Version="${VERSION}"

# Copy runtimes and desktop integration
cp -r "${ROOT_DIR}/runtimes/linux-x64/native/"* "${PUBLISH_DIR}/" || true
cp "${ROOT_DIR}/packaging/linux/org.universalbackup.UniversalBackup.desktop" "${PUBLISH_DIR}/"
cp "${ROOT_DIR}/LICENSE" "${PUBLISH_DIR}/"

# 4. Package .tar.gz
echo "[4/4] Creating distribution archive..."
TAR_FILE="${DIST_DIR}/UniversalBackup-${VERSION}-linux-x64.tar.gz"
tar -czf "${TAR_FILE}" -C "${PUBLISH_DIR}" .
echo "Created: ${TAR_FILE}"

# SHA256
cd "${DIST_DIR}"
sha256sum "UniversalBackup-${VERSION}-linux-x64.tar.gz" >> "${DIST_DIR}/SHA256SUMS.txt"
echo "[SUCCESS] Packaging completed."
