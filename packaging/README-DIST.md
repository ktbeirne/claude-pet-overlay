# Claude Pet Overlay

Claude Code の動作状況をデスクトップペットとして可視化する Windows アプリです。
セッションの開始・作業中・入力待ち・完了・失敗にあわせてキャラクターが動き、
完了時には吹き出しでタスク名・所要時間・応答の要約を報告します。

## 必要環境

- Windows 10 / 11
- Claude Code (CLI)
- Python 3.8 以降 (Claude Code との連携フックに使用。PATH に `python` が通っていること)
- .NET ランタイムは不要 (自己完結型 exe)

## インストール

zip を展開したフォルダで PowerShell を開き:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

- サインイン時に自動起動させる場合は `-Startup` を付ける
- `%LOCALAPPDATA%\ClaudePetOverlay` へ配置され、`~/.claude/settings.json` に
  連携フックが登録されます (既存設定は変更せず、バックアップ
  `settings.json.bak` を作成)
- **Claude Code への反応は、インストール後に起動したセッションから有効**

## 使い方

| Claude Code の状態 | ペット |
|---|---|
| セッション開始 | 手を振る |
| 作業中 | 作業モーション |
| 許可待ち | 入力待ちモーション + 吹き出し |
| 応答完了 | 完了モーション + 報告吹き出し (9秒) |
| 複数セッション並走 | 吹き出しに「同時実行 N 件」 |

- ドラッグで移動 / ダブルクリックで最新の報告を再表示
- 右クリックメニュー: 表示FPS・倍率・吹き出し・クリック透過・通知音・終了
- トレイアイコン: 直近5件の完了報告

## アニメの差し替え

右クリック →「設定フォルダを開く」→ `CustomFrames\` に素材を置き、
「素材を再読み込み」で反映されます。状態単位で差し替え可能。

1. **スプライトシート形式**: `idle.png` (フレームがグリッド状に並んだ 1 枚) +
   任意の `idle.json`:
   ```json
   { "columns": 8, "rows": 1, "fps": 16 }
   ```
   フレームごとの表示時間を変える場合は `"durationsMs": [800, 120, ...]`。
2. **フレームフォルダ形式**: `idle\frame_000.png, frame_001.png, ...`
   (+ 任意の `fps.txt` または `durations.txt`)

状態名: `idle` `running` (作業中) `running-right` `running-left` (ドラッグ中)
`waving` `jumping` (完了報告) `failed` `waiting` `review`。
セルの縦横比は 192:208 (推奨 576x624)。透過 PNG を使用してください。

## 通知音

「設定フォルダを開く」→ `Sounds\` に状態名のファイルを置くと、その状態の
イベント時に再生されます (.wav / .mp3 / .wma)。

- 例: `jumping.wav` = 完了音、`failed.wav` = 失敗音、`waiting.wav` = 入力待ち音
- 右クリックメニュー「通知音を鳴らす」でオン/オフ
- 同じ種類の連続イベントは 3 秒間隔で間引かれます

## アンインストール

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
```

フック登録も自動で解除されます。個人設定 (カスタム素材・音) も消す場合は
`-Purge` を付けてください。

## トラブルシュート

- **ペットが Claude Code に反応しない**: インストール後に新しく起動した
  セッションか確認。`python --version` が通るか確認。
  `~/.claude/settings.json` に `claude_pet_hook.py` のエントリがあるか確認。
- **完了音が二重に鳴る**: 自前の Stop hook で音を鳴らしている場合は、
  `Sounds\jumping.wav` とどちらかに寄せてください。
- **表示が乱れる / 動かない**: タスクトレイのアイコンから「終了」して
  再起動してください。
