Set-Location (Split-Path -Parent $PSCommandPath)

Write-Host "Building YtDownloader.exe..." -ForegroundColor Cyan

dotnet publish YtDownloader -c Release -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o "$PSScriptRoot\dist"

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build FAILED." -ForegroundColor Red
    exit 1
}

$exe = Join-Path "$PSScriptRoot\dist" "YtDownloader.exe"
Write-Host ""
Write-Host "Done:" -ForegroundColor Green
Write-Host "  $exe"
Write-Host ""
Write-Host "Double-click it, pin it, or copy to any Windows 10/11 x64 PC."
