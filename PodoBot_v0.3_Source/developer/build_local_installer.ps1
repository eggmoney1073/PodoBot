$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot -Parent)

Write-Host "This script is for the developer only."
Write-Host "Normal broadcaster users should receive PodoBotSetup.exe."
Write-Host ""

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET 8 SDK is required for a local developer build."
}

dotnet restore PodoBot.sln

if (Test-Path publish) {
    Remove-Item publish -Recurse -Force
}

dotnet publish src/PodoBot/PodoBot.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o publish

$iscc = Get-ChildItem "C:\Program Files (x86)\Inno Setup *\ISCC.exe" `
    -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending |
    Select-Object -First 1

if (-not $iscc) {
    throw "Inno Setup 6 is required for a local developer build."
}

Push-Location installer
& $iscc.FullName "PodoBot.iss"
Pop-Location

Write-Host ""
Write-Host "Done: installer\output\PodoBotSetup.exe"
