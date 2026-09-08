#!/usr/bin/env bash
# Downloads, verifies, and stages pinned restic and rclone engine runtimes on Linux.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
RUNTIMES_DIR="${ROOT_DIR}/runtimes/linux-x64/native"

RESTIC_VERSION="0.19.1"
RESTIC_URL="https://github.com/restic/restic/releases/download/v${RESTIC_VERSION}/restic_${RESTIC_VERSION}_linux_amd64.bz2"
RESTIC_SHA256="f415415624dcc452f2a02b8c33641791a8c6d6d3b65bbb3543fcf9a25151585c"

RCLONE_VERSION="1.69.1"
RCLONE_URL="https://downloads.rclone.org/v${RCLONE_VERSION}/rclone-v${RCLONE_VERSION}-linux-amd64.zip"
RCLONE_SHA256="231841f8d8029ae6cfca932b601b3b50d0e2c3c2cb9da3166293f1c3eae7d79c"

mkdir -p "${RUNTIMES_DIR}"
TMP_DIR="$(mktemp -d)"
trap 'rm -rf "${TMP_DIR}"' EXIT

echo "=== Staging Linux x64 Pinned Runtimes ==="

# 1. Restic
RESTIC_ARCHIVE="${TMP_DIR}/restic.bz2"
echo "Downloading restic ${RESTIC_VERSION}..."
curl -fsSL "${RESTIC_URL}" -o "${RESTIC_ARCHIVE}"
echo "${RESTIC_SHA256}  ${RESTIC_ARCHIVE}" | sha256sum -c -
bzip2 -d "${RESTIC_ARCHIVE}"
mv "${TMP_DIR}/restic" "${RUNTIMES_DIR}/restic"
chmod +x "${RUNTIMES_DIR}/restic"
echo "  [OK] restic staged at ${RUNTIMES_DIR}/restic"

# 2. Rclone
RCLONE_ARCHIVE="${TMP_DIR}/rclone.zip"
echo "Downloading rclone ${RCLONE_VERSION}..."
curl -fsSL "${RCLONE_URL}" -o "${RCLONE_ARCHIVE}"
echo "${RCLONE_SHA256}  ${RCLONE_ARCHIVE}" | sha256sum -c -
unzip -q "${RCLONE_ARCHIVE}" -d "${TMP_DIR}/rclone_extracted"
find "${TMP_DIR}/rclone_extracted" -type f -name "rclone" -exec cp {} "${RUNTIMES_DIR}/rclone" \;
chmod +x "${RUNTIMES_DIR}/rclone"
echo "  [OK] rclone staged at ${RUNTIMES_DIR}/rclone"

echo "[SUCCESS] Staging completed for linux-x64."
