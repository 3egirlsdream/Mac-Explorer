#!/usr/bin/env python3
"""Create a deterministic, non-destructive file-list benchmark directory (stdlib only).

Examples:
  python3 Tools/Performance/generate_filelist_dataset.py ~/filelist-bench --count 10000
  python3 Tools/Performance/generate_filelist_dataset.py ~/filelist-mixed --count 5000 --mixed
No pre-existing destination is modified. Large fixtures are opt-in; no files are deleted.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import struct
import zlib


def png() -> bytes:
    def chunk(kind: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)
    pixels = b"".join(b"\x00" + bytes((80, 145, 210)) * 64 for _ in range(64))
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", 64, 64, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(pixels)) + chunk(b"IEND", b""))


def create_dataset(destination: Path, count: int, mixed: bool) -> dict:
    if not 1 <= count <= 100_000:
        raise ValueError("count must be between 1 and 100000")
    destination = destination.expanduser().resolve()
    destination.mkdir(parents=True, exist_ok=False)
    image = png()
    counts: dict[str, int] = {}
    for index in range(count):
        kind = ("text", "text", "text", "text", "image", "image", "json", "csv", "folder", "binary")[index % 10] if mixed else "text"
        extensions = {"text": ".txt", "image": ".png", "json": ".json", "csv": ".csv", "folder": "", "binary": ".bin"}
        path = destination / f"item-{index:06d}{extensions[kind]}"
        if kind == "folder":
            path.mkdir()
        else:
            content = {"text": f"file-list benchmark {index}\n".encode(), "image": image,
                       "json": json.dumps({"index": index}).encode(), "csv": b"id,value\n1,example\n",
                       "binary": bytes(range(64))}[kind]
            with path.open("xb") as stream:
                stream.write(content)
        counts[kind] = counts.get(kind, 0) + 1
    return {"directory": str(destination), "entries": count, "kinds": counts,
            "notes": "Synthetic PNG/text fixtures, not a native PDF/Office/video or Git performance benchmark."}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("destination", type=Path)
    parser.add_argument("--count", type=int, default=1000)
    parser.add_argument("--mixed", action="store_true")
    args = parser.parse_args()
    try:
        result = create_dataset(args.destination, args.count, args.mixed)
    except (OSError, ValueError) as error:
        parser.exit(1, f"Could not create dataset: {error}\n")
    print(json.dumps(result, indent=2, ensure_ascii=False))


if __name__ == "__main__":
    main()
