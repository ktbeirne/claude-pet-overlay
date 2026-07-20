# Claude Pet Overlay アンインストーラ
# 使い方:
#   powershell -ExecutionPolicy Bypass -File .\uninstall.ps1          # アプリ+フック解除 (個人設定は残す)
#   powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -Purge   # CustomFrames / Sounds / 表示設定も削除
param(
    [switch]$Purge
)

$ErrorActionPreference = 'Stop'
$installRoot = Join-Path $env:LOCALAPPDATA 'ClaudePetOverlay'
$hooksDir = Join-Path $installRoot 'hooks'

Stop-Process -Name ClaudePetOverlay -Force -Confirm:$false -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

# フック解除 (claude_pet_hook.py を含むエントリだけを settings.json から除去)
$installHooks = Join-Path $hooksDir 'install_hooks.py'
if (Test-Path $installHooks) {
    $python = Get-Command python -ErrorAction SilentlyContinue
    if ($python) {
        python $installHooks --uninstall
    } else {
        Write-Host '警告: python が無いためフック解除をスキップしました。~/.claude/settings.json の claude_pet_hook.py エントリを手動で削除してください。' -ForegroundColor Yellow
    }
}

# 自動起動ショートカット削除
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Startup')) 'ClaudePetOverlay.lnk'
if (Test-Path $shortcutPath) { Remove-Item -Force $shortcutPath }

# ファイル削除
foreach ($sub in 'app', 'hooks') {
    $path = Join-Path $installRoot $sub
    if (Test-Path $path) { Remove-Item -Recurse -Force $path }
}
if ($Purge -and (Test-Path $installRoot)) {
    Remove-Item -Recurse -Force $installRoot
}

Write-Host 'アンインストール完了。'
if (-not $Purge) {
    Write-Host "個人設定 (CustomFrames / Sounds / settings.json) は $installRoot に残っています。完全に消すには -Purge を付けて再実行してください。"
}
