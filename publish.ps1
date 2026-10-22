# Builds a self-contained, single-file Windows x64 release of CS2 Soundboard.
# No .NET installation is needed on the target PC. Output: .\publish\CS2Soundboard.exe
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Output = "publish"
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

dotnet test tests/CS2Soundboard.Tests --filter "Category!=Hardware" -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Tests failed; not publishing." }

dotnet publish src/CS2Soundboard/CS2Soundboard.csproj `
    -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $Output
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

Write-Host ""
Write-Host "Published: $(Resolve-Path "$Output\CS2Soundboard.exe")"
