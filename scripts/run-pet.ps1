param(
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\ClaudePetOverlay.csproj'

# キャラ素材が無いクローン直後はプレースホルダ素材で起動できるようにする
$frames = Join-Path $PSScriptRoot '..\Assets\Frames'
$placeholder = Join-Path $PSScriptRoot '..\Assets\Frames.placeholder'
if (-not (Test-Path (Join-Path $frames 'idle')) -and (Test-Path $placeholder)) {
    Copy-Item -Recurse -Force $placeholder $frames
    Write-Host 'Assets\Frames が無いためプレースホルダ素材をコピーしました。'
}

if (-not $NoBuild) {
    dotnet build $project -c Release
}

$exe = Join-Path $PSScriptRoot '..\bin\Release\net8.0-windows\ClaudePetOverlay.exe'
Start-Process -FilePath $exe
