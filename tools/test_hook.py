#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""claude_pet_hook.py の単体テスト。

CLAUDE_PET_ACTIVITY_DIR を一時ディレクトリへ向けてサブプロセスとして本番同様に
実行し (stdin JSON -> pet-events.jsonl 追記)、実バスとオーバーレイを汚さずに
全イベントの入出力を検証する。

実行: python tools/test_hook.py
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
HOOK = ROOT / "claude_pet_hook.py"


def run_hook(activity_dir: Path, payload: dict) -> subprocess.CompletedProcess:
    env = dict(os.environ)
    env["CLAUDE_PET_ACTIVITY_DIR"] = str(activity_dir)
    env["PYTHONUTF8"] = "1"
    return subprocess.run(
        [sys.executable, str(HOOK)],
        input=json.dumps(payload, ensure_ascii=False).encode("utf-8"),
        capture_output=True,
        env=env,
        timeout=30,
    )


def read_bus(activity_dir: Path) -> list[dict]:
    bus = activity_dir / "claude-pet-events.jsonl"
    if not bus.exists():
        return []
    lines = bus.read_text(encoding="utf-8").splitlines()
    return [json.loads(line) for line in lines if line.strip()]


def main() -> int:
    failures: list[str] = []

    def check(name: str, condition: bool, detail: str = "") -> None:
        if condition:
            print(f"  ok: {name}")
        else:
            failures.append(name)
            print(f"FAIL: {name} {detail}")

    with tempfile.TemporaryDirectory(prefix="pet-hook-test-") as tmp:
        activity = Path(tmp)
        sessions = activity / "claude-sessions"
        base = {"cwd": "D:/AI/project/pet", "transcript_path": ""}

        # 実 transcript 形式のダミー (Stop fallback 用)
        transcript = activity / "transcript.jsonl"
        transcript.write_text(
            "\n".join(
                [
                    json.dumps({"type": "user", "message": {"role": "user", "content": "hi"}}),
                    json.dumps(
                        {
                            "type": "assistant",
                            "message": {
                                "role": "assistant",
                                "content": [
                                    {"type": "tool_use", "id": "t1", "name": "Bash", "input": {}},
                                ],
                            },
                        }
                    ),
                    json.dumps(
                        {
                            "type": "assistant",
                            "message": {
                                "role": "assistant",
                                "content": [{"type": "text", "text": "テストを直しました。全件パスです。"}],
                            },
                        }
                    ),
                ]
            )
            + "\n",
            encoding="utf-8",
        )

        print("[1] SessionStart(startup) -> waving")
        proc = run_hook(activity, {**base, "hook_event_name": "SessionStart", "session_id": "sess-a", "source": "startup"})
        events = read_bus(activity)
        check("exit 0 / stdout 空", proc.returncode == 0 and proc.stdout == b"", str(proc))
        check("waving 送信", len(events) == 1 and events[0]["state"] == "waving", str(events))

        print("[2] SessionStart(resume) -> 送信なし")
        run_hook(activity, {**base, "hook_event_name": "SessionStart", "session_id": "sess-a", "source": "resume"})
        check("イベント増えない", len(read_bus(activity)) == 1)

        print("[3] UserPromptSubmit -> running + 状態ファイル + タスク名正規化")
        run_hook(
            activity,
            {**base, "hook_event_name": "UserPromptSubmit", "session_id": "sess-a", "prompt": "認証処理を直して。あとテストもお願い。"},
        )
        events = read_bus(activity)
        last = events[-1]
        state_a = json.loads((sessions / "sess-a.json").read_text(encoding="utf-8"))
        check("running 送信", last["state"] == "running", str(last))
        check("タスク名正規化", last["taskName"] == "認証処理を修正", repr(last["taskName"]))
        check("吹き出しなし", last["showInSpeechBubble"] is False)
        check("activeTaskCount=1", last["activeTaskCount"] == 1, str(last))
        check("状態ファイル running", state_a["state"] == "running", str(state_a))

        print("[4] Notification(permission_prompt) -> waiting")
        run_hook(
            activity,
            {
                **base,
                "hook_event_name": "Notification",
                "session_id": "sess-a",
                "notification_type": "permission_prompt",
                "message": "Bash の実行許可を待っています",
            },
        )
        events = read_bus(activity)
        last = events[-1]
        state_a = json.loads((sessions / "sess-a.json").read_text(encoding="utf-8"))
        check("waiting 送信", last["state"] == "waiting" and "許可" in last["message"], str(last))
        check("状態ファイル waiting", state_a["state"] == "waiting", str(state_a))

        print("[5] Notification(idle_prompt) -> 送信なし")
        before = len(read_bus(activity))
        run_hook(
            activity,
            {**base, "hook_event_name": "Notification", "session_id": "sess-a", "notification_type": "idle_prompt", "message": "入力待ち"},
        )
        check("イベント増えない", len(read_bus(activity)) == before)

        print("[6] 並走セッション B -> activeTaskCount=2")
        run_hook(
            activity,
            {**base, "hook_event_name": "UserPromptSubmit", "session_id": "sess-b", "prompt": "README を作って"},
        )
        last = read_bus(activity)[-1]
        check("activeTaskCount=2", last["activeTaskCount"] == 2, str(last))
        check("B タスク名", last["taskName"] == "README を作成", repr(last["taskName"]))

        print("[7] Stop(A, last_assistant_message あり) -> jumping / 残 1 件")
        run_hook(
            activity,
            {
                **base,
                "hook_event_name": "Stop",
                "session_id": "sess-a",
                "last_assistant_message": "認証処理を修正しました。テストは 12 件すべてパスしています。",
            },
        )
        last = read_bus(activity)[-1]
        check("jumping 送信", last["state"] == "jumping", str(last))
        check("プレビュー引用", "12 件" in last["message"], str(last))
        check("taskName 維持", last["taskName"] == "認証処理を修正", repr(last["taskName"]))
        check("startedAt 維持", bool(last["startedAt"]))
        check("endedAt 付与", bool(last.get("endedAt")))
        check("残 activeTaskCount=1", last["activeTaskCount"] == 1, str(last))
        check("状態ファイル削除", not (sessions / "sess-a.json").exists())

        print("[8] Stop(B, transcript fallback) -> プレビューを transcript から取得")
        run_hook(
            activity,
            {
                **base,
                "hook_event_name": "Stop",
                "session_id": "sess-b",
                "transcript_path": str(transcript),
            },
        )
        last = read_bus(activity)[-1]
        check("fallback プレビュー", "テストを直しました" in last["message"], str(last))
        check("残 activeTaskCount=0", last["activeTaskCount"] == 0, str(last))

        print("[9] SessionEnd -> idle (吹き出しなし)")
        run_hook(activity, {**base, "hook_event_name": "SessionEnd", "session_id": "sess-b", "reason": "prompt_input_exit"})
        last = read_bus(activity)[-1]
        check("idle 送信", last["state"] == "idle" and last["showInSpeechBubble"] is False, str(last))

        print("[10] サブエージェント (agent_id) -> 送信なし")
        before = len(read_bus(activity))
        run_hook(
            activity,
            {**base, "hook_event_name": "Stop", "session_id": "sess-c", "agent_id": "agent-1", "last_assistant_message": "sub"},
        )
        check("イベント増えない", len(read_bus(activity)) == before)

        print("[11] 壊れた stdin -> exit 0")
        env = dict(os.environ)
        env["CLAUDE_PET_ACTIVITY_DIR"] = str(activity)
        proc = subprocess.run(
            [sys.executable, str(HOOK)], input=b"not json", capture_output=True, env=env, timeout=30
        )
        check("exit 0", proc.returncode == 0, str(proc))

        print("[12] BOM 付き stdin (PowerShell パイプ相当、多重 BOM 含む) -> 正常処理")
        for bom_count in (1, 2):
            before = len(read_bus(activity))
            payload = {**base, "hook_event_name": "UserPromptSubmit", "session_id": "sess-bom", "prompt": "BOM 検証"}
            raw = b"\xef\xbb\xbf" * bom_count + json.dumps(payload, ensure_ascii=False).encode("utf-8") + b"\r\n"
            proc = subprocess.run([sys.executable, str(HOOK)], input=raw, capture_output=True, env=env, timeout=30)
            check(f"BOM x{bom_count}: exit 0", proc.returncode == 0, str(proc))
            check(f"BOM x{bom_count}: イベント処理される", len(read_bus(activity)) == before + 1, str(read_bus(activity)[-3:]))

    print()
    if failures:
        print(f"NG: {len(failures)} 件失敗: {failures}")
        return 1
    print("全件パス")
    return 0


if __name__ == "__main__":
    sys.exit(main())
