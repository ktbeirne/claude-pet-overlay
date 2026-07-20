# -*- coding: utf-8 -*-
"""Claude Code の settings.json へペット連携フックを安全に登録/解除する。

- 既存の設定 (他のフック・権限・環境変数など) は一切変更しない
- 冪等: 再実行すると自分のエントリ (claude_pet_hook.py を含む command) を
  置き換えるだけで重複しない
- 書き換え前に settings.json.bak へバックアップを保存する

使い方:
  python install_hooks.py --hook-script <claude_pet_hook.py の絶対パス>
  python install_hooks.py --uninstall
  (--settings <path> で対象ファイルを指定可能。既定 ~/.claude/settings.json)
"""

from __future__ import annotations

import argparse
import json
import shutil
from pathlib import Path

EVENTS = ["SessionStart", "UserPromptSubmit", "Notification", "Stop", "SessionEnd"]
MARKER = "claude_pet_hook.py"


def remove_pet_hooks(settings: dict) -> None:
    hooks = settings.get("hooks")
    if not isinstance(hooks, dict):
        return
    for event in list(hooks.keys()):
        groups = hooks.get(event)
        if not isinstance(groups, list):
            continue
        for group in groups:
            if isinstance(group, dict) and isinstance(group.get("hooks"), list):
                group["hooks"] = [
                    entry
                    for entry in group["hooks"]
                    if MARKER not in str(entry.get("command", ""))
                ]
        hooks[event] = [
            group
            for group in groups
            if not isinstance(group, dict) or group.get("hooks")
        ]
        if not hooks[event]:
            del hooks[event]
    if not hooks:
        settings.pop("hooks", None)


def add_pet_hooks(settings: dict, hook_script: Path) -> None:
    command = "python " + str(hook_script.resolve()).replace("\\", "/")
    hooks = settings.setdefault("hooks", {})
    for event in EVENTS:
        hooks.setdefault(event, []).append(
            {
                "matcher": "",
                "hooks": [{"type": "command", "command": command, "timeout": 10}],
            }
        )


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--hook-script", type=Path)
    parser.add_argument("--uninstall", action="store_true")
    parser.add_argument(
        "--settings",
        type=Path,
        default=Path.home() / ".claude" / "settings.json",
    )
    args = parser.parse_args()
    if not args.uninstall and args.hook_script is None:
        parser.error("--hook-script is required unless --uninstall is given")
    if not args.uninstall and not args.hook_script.exists():
        parser.error(f"hook script not found: {args.hook_script}")

    settings_path = args.settings
    if settings_path.exists():
        settings = json.loads(settings_path.read_text(encoding="utf-8"))
        shutil.copy2(settings_path, settings_path.with_name("settings.json.bak"))
    else:
        settings = {}

    remove_pet_hooks(settings)
    if not args.uninstall:
        add_pet_hooks(settings, args.hook_script)

    settings_path.parent.mkdir(parents=True, exist_ok=True)
    settings_path.write_text(
        json.dumps(settings, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
    )
    action = "removed from" if args.uninstall else "installed into"
    print(f"pet hooks {action} {settings_path}")
    print("有効になるのは次に起動する Claude Code セッションからです。")


if __name__ == "__main__":
    main()
