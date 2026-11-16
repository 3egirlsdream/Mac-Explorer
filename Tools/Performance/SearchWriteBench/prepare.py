#!/usr/bin/env python3
"""Create a synthetic index fixture; run in a separate process from the indexer."""
import argparse
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("root", type=Path)
args = parser.parse_args()
args.root.mkdir(parents=True, exist_ok=False)
flat = args.root / "flat"
flat.mkdir()
for index in range(10000):
    (flat / f"report-{index:05d}.txt").write_text("fixture\n")
for index in range(1000):
    directory = args.root / "tree" / f"branch-{index // 100}" / f"directory-{index:04d}"
    directory.mkdir(parents=True)
    for number in range(90):
        name = ("长文件名" * 12 + ".txt") if index == 0 and number == 1 else (f"合同-{number:03d}.txt" if number else "Report.txt")
        (directory / name).write_text("fixture\n")
special = args.root / "特殊路径_%_" / ("长文件夹" * 15)
special.mkdir(parents=True)
(special / "report.txt").symlink_to(flat / "report-00000.txt")
(args.root / "linked-directory").symlink_to(flat, target_is_directory=True)
(args.root / ".hidden").mkdir()
(args.root / "sample.app").mkdir()
print(args.root)
