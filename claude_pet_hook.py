#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Claude Code hooks を ClaudePetOverlay のイベントバスへ橋渡しする。

Claude Code が settings.json の hooks から本スクリプトを起動し、stdin の hook JSON を
PetEventBusWatcher が解釈できる 1 行 JSON として
~/.agent-activity/claude-pet-events.jsonl に追記する。Codex ペットの
pet-events.jsonl とはファイルを分離しており、互いに干渉しない。

イベント対応:
  SessionStart(startup) -> waving   (挨拶)
  UserPromptSubmit      -> running  (ターン開始。セッション状態ファイル作成)
  Notification          -> waiting  (permission_prompt / elicitation_dialog のみ)
  Stop                  -> jumping  (完了報告。応答プレビューを吹き出しに表示)
  SessionEnd            -> idle     (セッション状態ファイル削除)

複数セッション並走時の activeTaskCount は ~/.agent-activity/claude-sessions/ の
状態ファイルを集計して算出する。オーバーレイ側は Source 単位でタスク数を合算する
ため、Codex と Claude Code が同時に動いても数字は破綻しない。

制約 (意図的な省略):
  - ツール実行ごとの中間更新は送らない (フックのオーバーヘッド回避)。
  - permission 許可後の running 復帰イベントはなく、次の Stop まで waiting 表示が残る
    (auto permission 運用では発生自体が稀)。
  - エラー検知イベントが hooks に無いため failed 状態は使わない。

本スクリプトはいかなる場合も exit 0 / stdout 出力なしで終える。stdout は
UserPromptSubmit 等でコンテキストに注入され、exit 2 はブロック動作になるため。
"""

from __future__ import annotations

import json
import os
import re
import sys
import tempfile
from datetime import datetime, timedelta, timezone
from pathlib import Path

SOURCE = os.environ.get("CLAUDE_PET_SOURCE", "Claude Code")
ACTIVITY_DIR = Path(os.environ.get("CLAUDE_PET_ACTIVITY_DIR", str(Path.home() / ".agent-activity")))
BUS_PATH = ACTIVITY_DIR / "claude-pet-events.jsonl"
SESSIONS_DIR = ACTIVITY_DIR / "claude-sessions"

TASK_NAME_MAX = 34
PREVIEW_MAX = 180
TRANSCRIPT_TAIL_BYTES = 512 * 1024
ACTIVE_WINDOW = timedelta(hours=2)    # これより古い状態ファイルは active 集計から除外
CLEANUP_WINDOW = timedelta(hours=24)  # これより古い状態ファイルは削除

# waiting へ変換する Notification 種別。idle_prompt はターン外 (応答完了後の入力待ち)
# なので active 扱いにしない。agent_needs_input 等の background session 系も対象外。
WAITING_TYPES = {"permission_prompt", "elicitation_dialog"}

TAG_PATTERN = re.compile(r"<[^>]+>")
AMBIENT_BLOCK = re.compile(
    r"<(system-reminder|command-name|command-args|command-message)\b[^>]*>.*?</\1>",
    re.IGNORECASE | re.DOTALL,
)


def now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def emit(
    state: str,
    *,
    message: str = "",
    task_name: str = "",
    thread_id: str = "",
    started_at: str = "",
    ended_at: str = "",
    active_count: int = 0,
    show_bubble: bool = True,
) -> None:
    event = {
        "timestamp": now_iso(),
        "state": state,
        "source": SOURCE,
        "message": message or "",
        "threadId": thread_id or "",
        "taskName": task_name or "",
        "startedAt": started_at or "",
        "activeTaskCount": max(0, int(active_count)),
        "showInSpeechBubble": bool(show_bubble),
    }
    if ended_at:
        event["endedAt"] = ended_at
    ACTIVITY_DIR.mkdir(parents=True, exist_ok=True)
    with open(BUS_PATH, "a", encoding="utf-8", newline="\n") as fh:
        fh.write(json.dumps(event, ensure_ascii=False) + "\n")


# ---------------------------------------------------------------------------
# セッション状態ファイル (~/.agent-activity/claude-sessions/<session_id>.json)


def session_path(session_id: str) -> Path:
    safe = re.sub(r"[^0-9A-Za-z._-]", "_", session_id or "unknown")
    return SESSIONS_DIR / f"{safe}.json"


def read_session(session_id: str) -> dict | None:
    try:
        return json.loads(session_path(session_id).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def write_session(session_id: str, state: str, task_name: str, started_at: str, cwd: str) -> None:
    SESSIONS_DIR.mkdir(parents=True, exist_ok=True)
    payload = {
        "state": state,
        "task_name": task_name,
        "started_at": started_at,
        "updated_at": now_iso(),
        "cwd": cwd,
    }
    fd, tmp = tempfile.mkstemp(dir=str(SESSIONS_DIR), suffix=".tmp")
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as fh:
            json.dump(payload, fh, ensure_ascii=False)
        os.replace(tmp, session_path(session_id))
    except OSError:
        try:
            os.unlink(tmp)
        except OSError:
            pass


def remove_session(session_id: str) -> None:
    try:
        session_path(session_id).unlink()
    except OSError:
        pass


def count_active(exclude: str | None = None) -> int:
    if not SESSIONS_DIR.is_dir():
        return 0
    now = datetime.now(timezone.utc)
    excluded = session_path(exclude) if exclude else None
    count = 0
    for path in SESSIONS_DIR.glob("*.json"):
        try:
            age = now - datetime.fromtimestamp(path.stat().st_mtime, timezone.utc)
            if age > CLEANUP_WINDOW:
                path.unlink(missing_ok=True)
                continue
            if excluded is not None and path.name == excluded.name:
                continue
            if age > ACTIVE_WINDOW:
                continue
            data = json.loads(path.read_text(encoding="utf-8"))
            if data.get("state") in ("running", "waiting"):
                count += 1
        except (OSError, ValueError):
            continue
    return count


# ---------------------------------------------------------------------------
# テキスト整形 (タスク名は Codex 版 CreateTaskName と同じ正規化で見た目を揃える)


def make_task_name(prompt: str) -> str:
    text = AMBIENT_BLOCK.sub(" ", prompt)
    text = TAG_PATTERN.sub(" ", text)
    text = re.sub(r"\s+", " ", text).strip()
    text = re.sub(r"^[#>*・●\-\s]+", "", text).strip()
    if not text:
        return ""
    match = re.search(r"[。！？!?]", text)
    if match:
        text = text[: match.start()]
    text = re.sub(r"を?入れられない(?:かな|の)?$", "を追加", text)
    text = re.sub(r"(?:も)?(?:見せて|みせて)$", "を表示", text)
    text = re.sub(r"を?探して$", "を探す", text)
    text = re.sub(r"を?直して$", "を修正", text)
    text = re.sub(r"を?作って$", "を作成", text)
    text = re.sub(r"を?調べて$", "を調査", text)
    text = re.sub(r"(?:して)?(?:もらえますか|ください|ほしい|おいて|くれ|お願い)$", "", text)
    text = text.strip(" 。！？!?、")
    if len(text) > TASK_NAME_MAX:
        text = text[: TASK_NAME_MAX - 1] + "…"
    return text


def make_preview(text: str) -> str:
    compact = re.sub(r"\s+", " ", text).strip()
    if len(compact) <= PREVIEW_MAX:
        return compact
    return compact[: PREVIEW_MAX - 1] + "…"


def extract_assistant_text(obj: object) -> str:
    """transcript の 1 行から assistant のテキストを取り出す。

    実形式 {"type":"assistant","message":{"content":[{"type":"text","text":...}]}} と
    素朴な {"role":"assistant","content":"..."} の両方を受ける。
    """
    if not isinstance(obj, dict):
        return ""
    message = None
    if obj.get("type") == "assistant" and isinstance(obj.get("message"), dict):
        message = obj["message"]
    elif obj.get("role") == "assistant":
        message = obj
    if not isinstance(message, dict):
        return ""
    if message.get("role", "assistant") != "assistant":
        return ""
    content = message.get("content")
    if isinstance(content, str):
        return content.strip()
    parts: list[str] = []
    if isinstance(content, list):
        for item in content:
            if isinstance(item, dict) and item.get("type") == "text":
                text = item.get("text")
                if isinstance(text, str) and text.strip():
                    parts.append(text.strip())
    return " ".join(parts)


def last_assistant_from_transcript(path_str: str) -> str:
    if not path_str:
        return ""
    try:
        path = Path(path_str)
        size = path.stat().st_size
        with open(path, "rb") as fh:
            if size > TRANSCRIPT_TAIL_BYTES:
                fh.seek(size - TRANSCRIPT_TAIL_BYTES)
                fh.readline()  # 途中から読んだ部分行を捨てる
            lines = fh.read().decode("utf-8", errors="replace").splitlines()
    except OSError:
        return ""
    for line in reversed(lines):
        line = line.strip()
        if not line:
            continue
        try:
            text = extract_assistant_text(json.loads(line))
        except ValueError:
            continue
        if text:
            return text
    return ""


# ---------------------------------------------------------------------------
# イベントハンドラ


def on_session_start(data: dict, session_id: str) -> None:
    if (data.get("source") or "") != "startup":
        return  # resume / clear / compact のたびに手を振るのは騒がしい
    emit(
        "waving",
        message="こんにちは！Claude Code が起動しました",
        active_count=count_active(),
        show_bubble=True,
    )


def on_prompt(data: dict, session_id: str) -> None:
    prompt = data.get("prompt") or data.get("user_prompt") or ""
    task_name = make_task_name(str(prompt))
    started_at = now_iso()
    write_session(session_id, "running", task_name, started_at, str(data.get("cwd") or ""))
    emit(
        "running",
        message="タスクを処理しています",
        task_name=task_name,
        thread_id=session_id,
        started_at=started_at,
        active_count=count_active(),
        show_bubble=False,  # 作業開始のたびに吹き出しを出さない (Codex 版 task_started と同じ)
    )


def on_notification(data: dict, session_id: str) -> None:
    if (data.get("notification_type") or "") not in WAITING_TYPES:
        return
    sess = read_session(session_id) or {}
    task_name = sess.get("task_name") or ""
    started_at = sess.get("started_at") or ""
    if sess:
        write_session(session_id, "waiting", task_name, started_at, sess.get("cwd") or "")
    emit(
        "waiting",
        message=str(data.get("message") or "入力を待っています"),
        task_name=task_name,
        thread_id=session_id,
        started_at=started_at,
        active_count=max(1, count_active()),
        show_bubble=True,
    )


def on_stop(data: dict, session_id: str) -> None:
    if data.get("stop_hook_active"):
        return  # 他の Stop hook がブロック中の再発火で完了報告を重複させない
    sess = read_session(session_id) or {}
    remove_session(session_id)
    preview = str(data.get("last_assistant_message") or "").strip()
    if not preview:
        preview = last_assistant_from_transcript(str(data.get("transcript_path") or ""))
    emit(
        "jumping",
        message=make_preview(preview) if preview else "作業が完了しました",
        task_name=sess.get("task_name") or "",
        thread_id=session_id,
        started_at=sess.get("started_at") or "",
        ended_at=now_iso(),
        active_count=count_active(exclude=session_id),
        show_bubble=True,
    )


def on_session_end(data: dict, session_id: str) -> None:
    remove_session(session_id)
    emit("idle", active_count=count_active(), show_bubble=False)


HANDLERS = {
    "SessionStart": on_session_start,
    "UserPromptSubmit": on_prompt,
    "Notification": on_notification,
    "Stop": on_stop,
    "SessionEnd": on_session_end,
}


def main() -> int:
    raw = sys.stdin.buffer.read()
    try:
        # PowerShell のパイプは設定により BOM を付け、多重になることもある
        # (実測: $OutputEncoding 再設定で 2 重)。個数に依存せず全て剥がす。
        data = json.loads(raw.decode("utf-8", errors="replace").lstrip("﻿"))
    except ValueError:
        return 0
    if not isinstance(data, dict):
        return 0
    if data.get("agent_id"):
        return 0  # サブエージェント内の発火はペットに流さない
    handler = HANDLERS.get(str(data.get("hook_event_name") or ""))
    if handler is not None:
        handler(data, str(data.get("session_id") or ""))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception:
        sys.exit(0)  # ペット連携の失敗で Claude Code 本体を止めない
