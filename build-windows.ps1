# Builds the portable single-file Windows executable.
# Output: publish\win-x64\TokenBurnRate.exe (self-contained, no .NET install needed).
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $root
try {
    dotnet publish Token-Burn-Rate.csproj -c Release -r win-x64 -o publish\win-x64
    if ($LASTEXITCODE -ne 0) { throw "publish failed with exit code $LASTEXITCODE" }

    $exe = Join-Path $root 'publish\win-x64\TokenBurnRate.exe'
    $mb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host ""
    Write-Host "Built: $exe ($mb MB)" -ForegroundColor Green
    Write-Host "Copy that single file anywhere - it needs nothing else."
}
finally {
    Pop-Location
}
