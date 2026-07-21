# -*- coding: utf-8 -*-
"""Codex v2 ペットアトラスを ClaudePetOverlay のフレームフォルダ形式へ変換する。

入力パッケージの想定 (Codex v2 atlas):
  - spritesheet.png (または .webp): columns x rows のセルグリッド
  - animation-*.json: {"fps", "atlas": {columns, rows, cell_width, cell_height},
                       "states": {"<state>": {"row": n, "frames": [..]}}}

9 状態それぞれを <出力>/<state>/frame_XXX.png + fps.txt に展開する。
look_directions 等のペット未使用行は無視する。

使い方: python import_atlas.py <パッケージdir> <出力framesdir> [--fps state=N ...]
  --fps idle=4 のように状態別に再生レートを上書きできる (既定はアトラス json の fps)。
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np
from PIL import Image

REQUIRED_STATES = {
    "idle", "running", "running-right", "running-left", "waving",
    "jumping", "failed", "waiting", "review",
}


def load_animation(package: Path) -> dict:
    candidates = sorted(package.glob("animation-*.json"))
    if not candidates:
        raise FileNotFoundError(f"animation-*.json not found in {package}")
    return json.loads(candidates[0].read_text(encoding="utf-8"))


def load_sheet(package: Path) -> Image.Image:
    for name in ("spritesheet.png", "spritesheet.webp"):
        path = package / name
        if path.exists():
            return Image.open(path).convert("RGBA")
    raise FileNotFoundError(f"spritesheet.png/.webp not found in {package}")


def clean_rgba(image: Image.Image) -> Image.Image:
    array = np.asarray(image).copy()
    array[array[..., 3] == 0, :3] = 0
    return Image.fromarray(array, "RGBA")


def main(
    package: Path,
    output: Path,
    fps_overrides: dict[str, float] | None = None,
    duration_overrides: dict[str, list[float]] | None = None,
    frame_overrides: dict[str, list[int]] | None = None,
) -> None:
    animation = load_animation(package)
    sheet = load_sheet(package)
    atlas = animation["atlas"]
    cell_w, cell_h = atlas["cell_width"], atlas["cell_height"]
    if sheet.size != (atlas["width"], atlas["height"]):
        raise RuntimeError(f"sheet size {sheet.size} != atlas {atlas['width']}x{atlas['height']}")

    fps = animation.get("fps", 8)
    states = animation["states"]
    missing = REQUIRED_STATES - set(states)
    if missing:
        raise RuntimeError(f"states missing from animation json: {sorted(missing)}")

    for state, spec in states.items():
        if state not in REQUIRED_STATES:
            continue
        row = spec["row"]
        frame_columns = list(spec["frames"])
        selection = (frame_overrides or {}).get(state)
        if selection is not None:
            invalid = [index for index in selection if not 0 <= index < len(frame_columns)]
            if invalid:
                raise RuntimeError(f"{state}: frame index out of range 0-{len(frame_columns) - 1}: {invalid}")
            frame_columns = [frame_columns[index] for index in selection]
        state_dir = output / state
        state_dir.mkdir(parents=True, exist_ok=True)
        for stale in state_dir.glob("*"):
            stale.unlink()
        for position, column in enumerate(frame_columns):
            cell = sheet.crop((
                column * cell_w,
                row * cell_h,
                (column + 1) * cell_w,
                (row + 1) * cell_h,
            ))
            if not np.asarray(cell)[..., 3].any():
                raise RuntimeError(f"{state}: cell row={row} col={column} is empty")
            clean_rgba(cell).save(state_dir / f"frame_{position:03d}.png", optimize=True)
        durations = (duration_overrides or {}).get(state)
        if durations is not None:
            if len(durations) != len(frame_columns):
                raise RuntimeError(
                    f"{state}: durations count {len(durations)} != frame count {len(frame_columns)}")
            (state_dir / "durations.txt").write_text(
                ",".join(f"{value:g}" for value in durations) + "\n", encoding="ascii")
            print(f"{state}: {len(frame_columns)} frames, durations {durations}ms", flush=True)
        else:
            state_fps = (fps_overrides or {}).get(state, fps)
            (state_dir / "fps.txt").write_text(f"{state_fps:g}\n", encoding="ascii")
            print(f"{state}: {len(frame_columns)} frames @ {state_fps:g}fps", flush=True)
    print(f"imported -> {output}", flush=True)


def parse_frames_override(token: str) -> tuple[str, list[int]]:
    state, _, value = token.partition("=")
    if not state or not value:
        raise argparse.ArgumentTypeError(f"expected state=i,i,..., got: {token}")
    return state, [int(item) for item in value.split(",") if item]


def parse_durations_override(token: str) -> tuple[str, list[float]]:
    state, _, value = token.partition("=")
    if not state or not value:
        raise argparse.ArgumentTypeError(f"expected state=ms,ms,..., got: {token}")
    durations = [float(item) for item in value.split(",") if item]
    if not durations or any(not 10 <= item <= 60000 for item in durations):
        raise argparse.ArgumentTypeError(f"durations out of range 10-60000ms: {token}")
    return state, durations


def parse_fps_override(token: str) -> tuple[str, float]:
    state, _, value = token.partition("=")
    if not state or not value:
        raise argparse.ArgumentTypeError(f"expected state=fps, got: {token}")
    fps = float(value)
    if not 1 <= fps <= 240:
        raise argparse.ArgumentTypeError(f"fps out of range 1-240: {token}")
    return state, fps


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("package", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--fps", type=parse_fps_override, action="append", default=[],
                        metavar="STATE=N", help="状態別の再生レート上書き (例: --fps idle=4)")
    parser.add_argument("--durations", type=parse_durations_override, action="append", default=[],
                        metavar="STATE=MS,MS,...",
                        help="状態別のフレーム表示ミリ秒列 (fps より優先。例: --durations idle=3000,350,350,350,350,350)")
    parser.add_argument("--frames", type=parse_frames_override, action="append", default=[],
                        metavar="STATE=I,I,...",
                        help="状態別に使用するコマ番号 (0始まり、間引き・並べ替え。例: --frames idle=0,3)")
    arguments = parser.parse_args()
    main(arguments.package, arguments.output,
         dict(arguments.fps), dict(arguments.durations), dict(arguments.frames))
