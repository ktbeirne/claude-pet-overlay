# -*- coding: utf-8 -*-
"""AnimTest 用フィクスチャ生成。使い方: python make_fixtures.py <出力dir>

生成内容 (Program.cs の前提と一致させること):
  frames/
    idle:          4 枚 + timing.yaml "fps: 8"
    running:       4 枚 + timing.yaml "fps: 16"
    waving:        4 枚 (timing.yaml なし -> 既定 60fps)
    failed:        4 枚 + timing.yaml "fps: abc" (壊れた値 -> 既定 60fps)
    waiting:       4 枚 + timing.yaml durations_ms フロー形式 [100, 200, 300, 400]
    jumping:       4 枚 + timing.yaml durations_ms [100, 200] (個数不一致 -> 既定 60fps)
    running-right: 4 枚 + timing.yaml durations_ms ブロック形式 (100..400) + コメント行
    running-left:  4 枚 + timing.yaml "fps: 4"
    review:        4 枚 (プレーン)
  custom/
    review.png (4 フレーム横並び 384x104) + review.json {"columns":4,"fps":8}
    waving/frame_*.png 2 枚 + timing.yaml "fps: 10"
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
        "idle": "fps: 8\n",
        "running": "fps: 16\n",
        "waving": None,
        "failed": "fps: abc\n",
        "waiting": "durations_ms: [100, 200, 300, 400]\n",
        "jumping": "durations_ms: [100, 200]\n",
        "running-right": "# comment\ndurations_ms:\n  - 100\n  - 200\n  - 300\n  - 400\n",
        "running-left": "fps: 4\n",
        "review": None,
    }
    for index, (state, yaml) in enumerate(specs.items()):
        directory = frames_root / state
        frames(directory, 4, index)
        if yaml is not None:
            directory.joinpath("timing.yaml").write_text(yaml, encoding="ascii")

    custom = root / "custom"
    custom.mkdir(parents=True, exist_ok=True)
    sheet = Image.new("RGBA", (CELL[0] * 4, CELL[1]))
    for index in range(4):
        color = ((index * 61) % 256, 120, (index * 41 + 90) % 256, 255)
        sheet.paste(Image.new("RGBA", CELL, color), (index * CELL[0], 0))
    sheet.save(custom / "review.png")
    (custom / "review.json").write_text(json.dumps({"columns": 4, "fps": 8}), encoding="ascii")
    frames(custom / "waving", 2, 7)
    (custom / "waving" / "timing.yaml").write_text("fps: 10\n", encoding="ascii")
    print(f"fixtures -> {root}", flush=True)


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("usage: make_fixtures.py <output-dir>")
    main(Path(sys.argv[1]))
