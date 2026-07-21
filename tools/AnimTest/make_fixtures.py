# -*- coding: utf-8 -*-
"""AnimTest 用フィクスチャ生成。使い方: python make_fixtures.py <出力dir>

生成内容 (Program.cs の前提と一致させること):
  frames/
    idle:          4 枚 + fps.txt "8"
    running:       4 枚 + fps.txt "16"
    waving:        4 枚 (タイミング宣言なし -> 既定 60fps)
    failed:        4 枚 + fps.txt "abc" (壊れた値 -> 既定 60fps)
    waiting:       4 枚 + durations.txt "100,200,300,400"
    jumping:       4 枚 + durations.txt "100,200" (個数不一致 -> fps 経路)
    running-right: 4 枚 + timing.yaml durations_ms ブロック形式 [100,200,300,400]
    running-left:  4 枚 + timing.yaml "fps: 4" と fps.txt "16" の両方 (yaml 優先)
    review:        4 枚 (プレーン)
  custom/
    review.png (4 フレーム横並び 384x104) + review.json {"columns":4,"fps":8}
    waving/frame_*.png 2 枚 + fps.txt "10"
"""

import json
import sys
from pathlib import Path

from PIL import Image

CELL = (96, 104)  # 192:208 比


def frames(directory: Path, count: int, hue: int) -> None:
    directory.mkdir(parents=True, exist_ok=True)
    for index in range(count):
        color = ((hue * 37 + index * 53) % 256, (index * 90 + 30) % 256, (hue * 11 + 200) % 256, 255)
        Image.new("RGBA", CELL, color).save(directory / f"frame_{index:03d}.png")


def main(root: Path) -> None:
    frames_root = root / "frames"
    specs = {
        "idle": ("fps.txt", "8\n"),
        "running": ("fps.txt", "16\n"),
        "waving": None,
        "failed": ("fps.txt", "abc\n"),
        "waiting": ("durations.txt", "100,200,300,400\n"),
        "jumping": ("durations.txt", "100,200\n"),
        "running-right": ("timing.yaml", "durations_ms:\n  - 100\n  - 200\n  - 300\n  - 400\n"),
        "running-left": ("timing.yaml", "fps: 4\n"),
        "review": None,
    }
    for index, (state, meta) in enumerate(specs.items()):
        directory = frames_root / state
        frames(directory, 4, index)
        if meta is not None:
            directory.joinpath(meta[0]).write_text(meta[1], encoding="ascii")
    # running-left は fps.txt との競合ケース (yaml が勝つこと)
    (frames_root / "running-left" / "fps.txt").write_text("16\n", encoding="ascii")

    custom = root / "custom"
    custom.mkdir(parents=True, exist_ok=True)
    sheet = Image.new("RGBA", (CELL[0] * 4, CELL[1]))
    for index in range(4):
        color = ((index * 61) % 256, 120, (index * 41 + 90) % 256, 255)
        sheet.paste(Image.new("RGBA", CELL, color), (index * CELL[0], 0))
    sheet.save(custom / "review.png")
    (custom / "review.json").write_text(json.dumps({"columns": 4, "fps": 8}), encoding="ascii")
    frames(custom / "waving", 2, 7)
    (custom / "waving" / "fps.txt").write_text("10\n", encoding="ascii")
    print(f"fixtures -> {root}", flush=True)


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("usage: make_fixtures.py <output-dir>")
    main(Path(sys.argv[1]))
