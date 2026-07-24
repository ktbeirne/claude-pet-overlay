# Claude Pet Overlay インストーラ
# 使い方 (PowerShell):
#   powershell -ExecutionPolicy Bypass -File .\install.ps1              # 通常インストール (サインイン時の自動起動あり)
#   powershell -ExecutionPolicy Bypass -File .\install.ps1 -NoStartup   # 自動起動を登録しない
#   powershell -ExecutionPolicy Bypass -File .\install.ps1 -NoHooks     # フック登録をスキップ
param(
    [switch]$NoStartup,
    [switch]$NoHooks
)

$ErrorActionPreference = 'Stop'
$packageRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$installRoot = Join-Path $env:LOCALAPPDATA 'ClaudePetOverlay'
$appDir = Join-Path $installRoot 'app'
$hooksDir = Join-Path $installRoot 'hooks'

# 1. フック用の Python を確認 (フック本体が Python スクリプトのため)
if (-not $NoHooks) {
    $python = Get-Command python -ErrorAction SilentlyContinue
    if (-not $python) {
        Write-Host 'ERROR: python が見つかりません。Python 3.8 以降をインストールして PATH に通すか、-NoHooks でアプリのみインストールしてください。' -ForegroundColor Red
        exit 1
    }
}

# 2. 実行中のオーバーレイを停止
Stop-Process -Name ClaudePetOverlay -Force -Confirm:$false -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

# 3. アプリとフックを配置
New-Item -ItemType Directory -Force -Path $installRoot | Out-Null
if (Test-Path $appDir) { Remove-Item -Recurse -Force $appDir }
Copy-Item -Recurse -Force (Join-Path $packageRoot 'app') $appDir
if (Test-Path (Join-Path $packageRoot 'hooks')) {
    if (Test-Path $hooksDir) { Remove-Item -Recurse -Force $hooksDir }
    Copy-Item -Recurse -Force (Join-Path $packageRoot 'hooks') $hooksDir
}
Write-Host "アプリを配置しました: $appDir"

# 4. Claude Code の settings.json へフックを登録 (冪等・既存設定は保持)
if (-not $NoHooks) {
    python (Join-Path $hooksDir 'install_hooks.py') --hook-script (Join-Path $hooksDir 'claude_pet_hook.py')
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'ERROR: フック登録に失敗しました。' -ForegroundColor Red
        exit 1
    }
}

# 5. サインイン時の自動起動 (既定で登録。-NoStartup で登録しない)
$exePath = Join-Path $appDir 'ClaudePetOverlay.exe'
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Startup')) 'ClaudePetOverlay.lnk'
if ($NoStartup) {
    if (Test-Path $shortcutPath) { Remove-Item -Force $shortcutPath }
} else {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $exePath
    $shortcut.WorkingDirectory = $appDir
    $shortcut.Save()
    Write-Host "自動起動を登録しました: $shortcutPath"
}

# 6. 起動
# インストーラを実行したシェル (ターミナル/エディタ内蔵ターミナル等) の子プロセスとして
# 起動すると、その親アプリを終了・再起動したときにペットも道連れで落ちる。
# シェル (explorer) に起動させてプロセスツリーから切り離す。
$launchTarget = if (Test-Path $shortcutPath) { $shortcutPath } else { $exePath }
$started = $false
try {
    Start-Process explorer.exe -ArgumentList "`"$launchTarget`"" -ErrorAction Stop
    for ($i = 0; $i -lt 24 -and -not $started; $i++) {
        Start-Sleep -Milliseconds 250
        $started = [bool](Get-Process ClaudePetOverlay -ErrorAction SilentlyContinue)
    }
} catch {
    # explorer 経由が使えない環境では下のフォールバックで起動する
}
if (-not $started) {
    Start-Process -FilePath $exePath -WorkingDirectory $appDir
}

Write-Host ''
Write-Host 'インストール完了。デスクトップ右下にペットが表示されます。'
Write-Host 'Claude Code への反応は、次に起動するセッションから有効になります。'
Write-Host 'アニメの差し替えと通知音は、ペットを右クリック →「設定フォルダを開く」から。'
