param(
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\ClaudePetOverlay.csproj'

# キャラ素材が無いクローン直後は既定スプライト (ラフポメ) で起動できるようにする
$frames = Join-Path $PSScriptRoot '..\Assets\Frames'
$default = Join-Path $PSScriptRoot '..\Assets\Frames.default'
if (-not (Test-Path (Join-Path $frames 'idle')) -and (Test-Path $default)) {
    Copy-Item -Recurse -Force $default $frames
    Write-Host 'Assets\Frames が無いため既定スプライトをコピーしました。'
}

if (-not $NoBuild) {
    dotnet build $project -c Release
}

$exe = Join-Path $PSScriptRoot '..\bin\Release\net8.0-windows\ClaudePetOverlay.exe'
Start-Process -FilePath $exe
