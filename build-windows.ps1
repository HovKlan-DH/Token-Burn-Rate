# Publishes the self-contained Windows build into publish\win-x64\.
# That folder is what Velopack's `vpk pack` consumes to produce the installer users
# download (see .github\workflows\build-and-release.yml) - it is not a portable single
# file, so the whole folder is needed to run it.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $root
try {
    dotnet publish TokenBurnRate.csproj -c Release -r win-x64 -o publish\win-x64
    if ($LASTEXITCODE -ne 0) { throw "publish failed with exit code $LASTEXITCODE" }

    $dir = Join-Path $root 'publish\win-x64'
    $mb = [math]::Round((Get-ChildItem $dir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
    Write-Host ""
    Write-Host "Built: $dir ($mb MB)" -ForegroundColor Green
    Write-Host "Run TokenBurnRate.exe from that folder - it needs the files beside it."
}
finally {
    Pop-Location
}
