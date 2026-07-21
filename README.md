# Claude Pet Overlay

Claude Code の動作状況をデスクトップペットとして可視化する Windows オーバーレイアプリ。
セッションの開始・作業中・入力待ち・完了・失敗にあわせてキャラクターが動き、完了時には
吹き出しでタスク名・所要時間・応答の要約を報告します。複数セッションの並走にも対応。

```
Claude Code (全セッション)
  └─ hooks (~/.claude/settings.json)
       └─ python claude_pet_hook.py
            └─ ~/.agent-activity/claude-pet-events.jsonl  (イベントバス, 1 行 JSON)
                 └─ ClaudePetOverlay (WPF)  … モーション + 吹き出し + 通知音
```

## インストール (配布 zip)

[Releases](../../releases) から zip をダウンロードして展開し:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1            # 通常
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Startup   # + サインイン時自動起動
```

要件: Windows 10/11、Claude Code、Python 3.8+ (フック用)。.NET ランタイムは不要。
詳細は zip 内の README (packaging/README-DIST.md) を参照。

## 機能

- 状態モーション: idle / 作業中 / 入力待ち / 完了報告 / 失敗 / 挨拶 / 確認 / ドラッグ移動
- 完了報告の吹き出し: タスク名 (プロンプトから自動生成)・所要時間・応答プレビュー・残タスク数
- 複数セッション並走の集計表示 (「同時実行 N 件」)
- **アニメ差し替え**: `%LOCALAPPDATA%\ClaudePetOverlay\CustomFrames\` にスプライトシート
  (`<state>.png` + 任意の `<state>.json`) かフレームフォルダを置くと状態単位で上書き。
  トレイの「素材を再読み込み」で再起動なしに反映
- **通知音**: `%LOCALAPPDATA%\ClaudePetOverlay\Sounds\<state>.wav|.mp3` を置くと状態
  イベント時に再生 (トレイでオン/オフ、同一種別 3 秒クールダウン)
- トレイメニュー: 表示FPS (8〜120) / 表示倍率 / 吹き出し / クリック透過 / 直近5件の完了報告

## ソースからのビルド・実行

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run-pet.ps1
```

初回は既定スプライト (`Assets\Frames.default`、ラフポメ 8fps) が `Assets\Frames` へ
コピーされ、Release ビルド後に起動します。別のキャラ素材を使う場合は
`Assets\Frames\<state>\frame_*.png` (+ `fps.txt` / `durations.txt`) を差し替えてください。
Codex v2 アトラス形式のスプライトシートは `python tools\import_atlas.py <package> <出力>`
でフレームフォルダへ変換できます (`--fps idle=4` で状態別レート上書き)。

フックの登録 (開発環境):

```powershell
python packaging\install_hooks.py --hook-script <絶対パス>\claude_pet_hook.py
```

## 配布パッケージの作成

```powershell
powershell -ExecutionPolicy Bypass -File tools\make-package.ps1 -Version 1.0.0
powershell -ExecutionPolicy Bypass -File tools\make-package.ps1 -FramesDir <素材dir>   # キャラ素材を同梱
```

`dist\ClaudePetOverlay-<ver>.zip` (self-contained 単一 exe) が生成されます。
`-FramesDir` を指定しない場合は既定スプライト (ラフポメ) が同梱されます。

## 素材仕様

- 状態: `idle` `running` (作業中) `running-right` `running-left` (ドラッグ) `waving`
  `jumping` (完了報告) `failed` `waiting` `review`
- フレーム: 透過 PNG、セル比 192:208 (推奨 576x624)。`fps.txt` (等間隔) または
  `durations.txt` (フレームごとの ms) で再生タイミングを宣言 (既定 60fps)
- 検証ハーネス: `dotnet run --project tools\AnimTest -- <framesRoot> [customRoot]`

## アーキテクチャ補足

- フック (claude_pet_hook.py) は SessionStart / UserPromptSubmit / Notification / Stop /
  SessionEnd を受けてイベントバスへ 1 行 JSON を追記。常に exit 0・stdout なしで、
  失敗しても Claude Code を妨げない。並走カウントは ~/.agent-activity/claude-sessions/ で管理
- オーバーレイはバスを tail するだけで Claude Code 本体には依存しない。
  `.\scripts\send-pet-state.ps1 -State jumping -Message 'テスト'` で手動送信も可能
- 設定 UI 由来の値は `%LOCALAPPDATA%\ClaudePetOverlay\settings.json`
