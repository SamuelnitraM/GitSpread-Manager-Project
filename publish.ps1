<#
.SYNOPSIS
    Lance les tests puis, seulement s'ils sont tous verts, génère dist\GitSpread.exe
    (exécutable autonome : rien à installer, double-clic pour lancer).
#>
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Write-Host "`n== Tests ==" -ForegroundColor Cyan
dotnet test tests/GitSpread.Tests -c Release --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "`nTests en échec : exécutable NON généré." -ForegroundColor Red
    exit 1
}

Write-Host "`n== Publication ==" -ForegroundColor Cyan
dotnet publish src/GitSpread.App -c Release -o dist --nologo
if ($LASTEXITCODE -ne 0) { exit 1 }

$exe = Join-Path $PSScriptRoot 'dist\GitSpread.exe'
Write-Host "`nExécutable prêt : $exe ($([math]::Round((Get-Item $exe).Length / 1MB, 1)) Mo)" -ForegroundColor Green
