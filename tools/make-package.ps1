# 配布パッケージ (zip) を生成する。
# 使い方:
#   powershell -ExecutionPolicy Bypass -File tools\make-package.ps1                      # プレースホルダ素材で生成
#   powershell -ExecutionPolicy Bypass -File tools\make-package.ps1 -FramesDir <dir>     # 指定素材で生成
#   powershell -ExecutionPolicy Bypass -File tools\make-package.ps1 -Version 1.1.0
#
# 注意: 開発中の Assets\Frames (キャラ素材) は配布物へ「含めない」。
#       -FramesDir を指定しない限りニュートラルなプレースホルダ素材を同梱する。
param(
    [string]$Version = "1.0.0",
    [string]$FramesDir
)

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $project 'dist'
$stage = Join-Path $dist "stage\ClaudePetOverlay-$Version"

if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# 1. self-contained publish (.NET ランタイム不要)
# 単一 exe (PublishSingleFile) は使わない: WPF は初回起動時にネイティブ DLL を
# %TEMP% へ自己展開するため、企業 AV に全量スキャン/ブロックされて
# 「プロセスはあるのに窓が出ない・起動が異常に遅い」事故になる (実報告あり)。
dotnet publish (Join-Path $project 'ClaudePetOverlay.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:Version=$Version `
    -o (Join-Path $stage 'app') --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# 2. 素材差し替え: publish に含まれた開発用素材は必ず取り除く
$framesDest = Join-Path $stage 'app\Assets\Frames'
if (Test-Path $framesDest) { Remove-Item -Recurse -Force $framesDest }
if ($FramesDir) {
    if (-not (Test-Path $FramesDir)) { throw "FramesDir not found: $FramesDir" }
    Copy-Item -Recurse -Force $FramesDir $framesDest
    Write-Host "frames: $FramesDir"
} else {
    # コミット済みの既定スプライト (ラフポメ) を使う
    Copy-Item -Recurse -Force (Join-Path $project 'Assets\Frames.default') $framesDest
    Write-Host 'frames: default (rough-pomeranian)'
}

# 3. フックとインストーラ
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'hooks') | Out-Null
Copy-Item (Join-Path $project 'claude_pet_hook.py') (Join-Path $stage 'hooks\')
Copy-Item (Join-Path $project 'packaging\install_hooks.py') (Join-Path $stage 'hooks\')
Copy-Item (Join-Path $project 'packaging\install.ps1') $stage
Copy-Item (Join-Path $project 'packaging\uninstall.ps1') $stage
Copy-Item (Join-Path $project 'packaging\README-DIST.md') (Join-Path $stage 'README.md')

# 4. zip
$zip = Join-Path $dist "ClaudePetOverlay-$Version.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path $stage -DestinationPath $zip
$zipMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host ''
Write-Host "package: $zip (${zipMb} MB)"
Write-Host "stage:   $stage"
