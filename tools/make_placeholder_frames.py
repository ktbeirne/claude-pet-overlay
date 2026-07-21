# -*- coding: utf-8 -*-
"""配布用プレースホルダ素材を生成する。

キャラクター素材を含めない配布パッケージ向けに、丸いゴースト風の図形キャラで
9 状態のループアニメ (各 8 フレーム、576x624、白フチ付き、fps 8) を描く。
受領者は CustomFrames で自分の素材に差し替える前提のニュートラルな見た目。

使い方: python make_placeholder_frames.py <出力先ディレクトリ>
"""

from __future__ import annotations

import math
import sys
from pathlib import Path

from PIL import Image, ImageDraw

CANVAS = (576, 624)
SS = 2  # supersample
FRAMES = 8
FPS = 8
BODY_RADIUS = 150
OUTLINE = 12
BASELINE = 600  # キャラ底辺の目標 y (本物素材の整列に合わせる)

STATES = {
    "idle": {"color": (128, 142, 166), "anim": "breathe"},
    "running": {"color": (86, 140, 210), "anim": "work"},
    "running-right": {"color": (128, 142, 166), "anim": "hop_right"},
    "running-left": {"color": (128, 142, 166), "anim": "hop_left"},
    "waving": {"color": (104, 182, 122), "anim": "wave"},
    "jumping": {"color": (232, 192, 92), "anim": "jump"},
    "failed": {"color": (206, 110, 134), "anim": "sad"},
    "waiting": {"color": (160, 132, 214), "anim": "look_up"},
    "review": {"color": (88, 188, 194), "anim": "scan"},
}


def draw_frame(state: str, config: dict, index: int) -> Image.Image:
    width, height = CANVAS[0] * SS, CANVAS[1] * SS
    image = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    t = index / FRAMES
    wave_phase = math.sin(2 * math.pi * t)
    anim = config["anim"]
    color = config["color"]

    center_x = width / 2
    radius = BODY_RADIUS * SS
    squash = 0.0
    dx = 0.0
    dy = 0.0
    eye_dx = 0.0
    eye_dy = 0.0
    eyes_closed = anim == "sad"

    if anim == "breathe":
        dy = -8 * SS * wave_phase
        squash = 0.03 * wave_phase
    elif anim == "work":
        dx = 6 * SS * math.sin(4 * math.pi * t)
        eye_dy = 12 * SS
        squash = 0.02 * math.sin(4 * math.pi * t)
    elif anim in ("hop_right", "hop_left"):
        hop = abs(math.sin(2 * math.pi * t))
        dy = -34 * SS * hop
        direction = 1 if anim == "hop_right" else -1
        dx = direction * 10 * SS * math.sin(2 * math.pi * t)
        eye_dx = direction * 22 * SS
        squash = -0.10 * (1 - hop)
    elif anim == "wave":
        dy = -6 * SS * wave_phase
    elif anim == "jump":
        dy = -70 * SS * abs(math.sin(math.pi * t))
        squash = -0.12 * (1 - abs(math.sin(math.pi * t)))
    elif anim == "sad":
        dy = 10 * SS + 4 * SS * wave_phase
        squash = -0.08
    elif anim == "look_up":
        eye_dy = -16 * SS
        dx = 8 * SS * wave_phase
        dy = -4 * SS * abs(wave_phase)
    elif anim == "scan":
        eye_dx = 30 * SS * math.sin(2 * math.pi * t)
        dy = -4 * SS

    body_w = radius * (1 + squash)
    body_h = radius * (1 - squash)
    body_bottom = BASELINE * SS + dy
    body_cx = center_x + dx
    body_cy = body_bottom - body_h

    def blob(pad: float, fill: tuple[int, ...]) -> None:
        # 上が丸く下が広がるゴースト形: 円 + 台形裾
        draw.ellipse(
            (
                body_cx - body_w - pad,
                body_cy - body_h - pad,
                body_cx + body_w + pad,
                body_cy + body_h + pad,
            ),
            fill=fill,
        )

    blob(OUTLINE * SS, (255, 255, 255, 255))
    blob(0, (*color, 255))

    # waving: 体の右上で小さな手が振れる
    if anim == "wave":
        angle = 0.6 * math.sin(2 * math.pi * t)
        hand_cx = body_cx + (body_w + 46 * SS) * math.cos(-0.7 + angle)
        hand_cy = body_cy + (body_w + 46 * SS) * math.sin(-0.7 + angle)
        hand_r = 34 * SS
        draw.ellipse(
            (hand_cx - hand_r - OUTLINE * SS, hand_cy - hand_r - OUTLINE * SS,
             hand_cx + hand_r + OUTLINE * SS, hand_cy + hand_r + OUTLINE * SS),
            fill=(255, 255, 255, 255),
        )
        draw.ellipse(
            (hand_cx - hand_r, hand_cy - hand_r, hand_cx + hand_r, hand_cy + hand_r),
            fill=(*color, 255),
        )

    # 目
    eye_y = body_cy - body_h * 0.15 + eye_dy
    eye_r = 15 * SS
    for side in (-1, 1):
        eye_x = body_cx + side * body_w * 0.38 + eye_dx
        if eyes_closed:
            offset = eye_r
            draw.line(
                (eye_x - offset, eye_y - offset, eye_x + offset, eye_y + offset),
                fill=(30, 30, 40, 255),
                width=6 * SS,
            )
            draw.line(
                (eye_x - offset, eye_y + offset, eye_x + offset, eye_y - offset),
                fill=(30, 30, 40, 255),
                width=6 * SS,
            )
        else:
            draw.ellipse(
                (eye_x - eye_r, eye_y - eye_r, eye_x + eye_r, eye_y + eye_r),
                fill=(30, 30, 40, 255),
            )

    result = image.resize(CANVAS, Image.Resampling.LANCZOS)
    array = result.load()
    return result


def main(output_root: Path) -> None:
    for state, config in STATES.items():
        state_dir = output_root / state
        state_dir.mkdir(parents=True, exist_ok=True)
        for stale in state_dir.glob("*"):
            stale.unlink()
        for index in range(FRAMES):
            frame = draw_frame(state, config, index)
            frame.save(state_dir / f"frame_{index:03d}.png", optimize=True)
        (state_dir / "timing.yaml").write_text(f"fps: {FPS}\n", encoding="ascii")
        print(f"{state}: {FRAMES} frames", flush=True)
    print(f"placeholder frames -> {output_root}", flush=True)


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("usage: make_placeholder_frames.py <output-dir>")
    main(Path(sys.argv[1]))
