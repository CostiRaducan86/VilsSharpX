# Run once per clone/workspace to enable repo hooks + helpful local aliases.
# Usage: powershell -ExecutionPolicy Bypass -File .\scripts\git-setup.ps1

$ErrorActionPreference = 'Stop'

Write-Host "Configuring git hooksPath to .githooks" -ForegroundColor Cyan
git config core.hooksPath .githooks

Write-Host "Adding useful local git aliases" -ForegroundColor Cyan
# Keep aliases simple (no shell/date dependencies).
git config alias.st "status -sb"
git config alias.br "branch -vv"
git config alias.lg "log --oneline --decorate --graph --all"
git config alias.sw "switch"

Write-Host "Done." -ForegroundColor Green
Write-Host "Tip: Use 'git st' / 'git lg'" -ForegroundColor DarkGray
