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


def main(package: Path, output: Path, fps_overrides: dict[str, float] | None = None) -> None:
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
        state_dir = output / state
        state_dir.mkdir(parents=True, exist_ok=True)
        for stale in state_dir.glob("*"):
            stale.unlink()
        for position, column in enumerate(spec["frames"]):
            cell = sheet.crop((
                column * cell_w,
                row * cell_h,
                (column + 1) * cell_w,
                (row + 1) * cell_h,
            ))
            if not np.asarray(cell)[..., 3].any():
                raise RuntimeError(f"{state}: cell row={row} col={column} is empty")
            clean_rgba(cell).save(state_dir / f"frame_{position:03d}.png", optimize=True)
        state_fps = (fps_overrides or {}).get(state, fps)
        (state_dir / "fps.txt").write_text(f"{state_fps:g}\n", encoding="ascii")
        print(f"{state}: {len(spec['frames'])} frames @ {state_fps:g}fps", flush=True)
    print(f"imported -> {output}", flush=True)


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
    arguments = parser.parse_args()
    main(arguments.package, arguments.output, dict(arguments.fps))
