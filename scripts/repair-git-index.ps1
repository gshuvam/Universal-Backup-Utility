# Universal Backup Utility - Git Index Auto-Repair
# Resolves Windows file-locking truncation: "fatal: .git/index: index file smaller than expected"

Write-Host "Checking Git repository index status..." -ForegroundColor Cyan

if (Test-Path .git/index.lock) {
    Write-Host "Removing stale .git/index.lock..." -ForegroundColor Yellow
    Remove-Item .git/index.lock -Force -ErrorAction SilentlyContinue
}

$index = Get-Item .git/index -ErrorAction SilentlyContinue
if ($null -eq $index -or $index.Length -eq 0) {
    Write-Host "Detected corrupted or 0-byte .git/index. Rebuilding from HEAD..." -ForegroundColor Yellow
    Remove-Item .git/index -Force -ErrorAction SilentlyContinue
    git reset
} else {
    Write-Host "Index is healthy ($($index.Length) bytes)." -ForegroundColor Green
}

git update-index --index-version 2 --refresh
git status

