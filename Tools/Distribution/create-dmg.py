"""Create a release DMG, retrying macOS disk-image service contention only."""

import argparse
import subprocess
import sys
import time


def create_dmg(source, output):
    command = ["hdiutil", "create", "-volname", "Mac Explorer", "-srcfolder", str(source),
               "-ov", "-format", "UDZO", "-imagekey", "zlib-level=9", str(output)]
    for attempt in range(3):
        result = subprocess.run(command, capture_output=True, text=True)
        sys.stdout.write(result.stdout)
        sys.stderr.write(result.stderr)
        if result.returncode == 0 or "resource busy" not in (result.stdout + result.stderr).lower() or attempt == 2:
            return result.returncode
        delay = 3 * (attempt + 1)
        print(f"Disk-image service busy; retrying in {delay}s ({attempt + 2}/3).", file=sys.stderr, flush=True)
        time.sleep(delay)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source")
    parser.add_argument("output")
    args = parser.parse_args()
    sys.exit(create_dmg(args.source, args.output))
