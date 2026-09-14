"""Generate update notes from every complete commit message in a release range."""

import argparse
import subprocess
from pathlib import Path


def generate_memo(version, tag, previous_tag="", cwd=None):
    revision = f"{previous_tag}..{tag}" if previous_tag else tag
    # %B includes both subject and body; NUL separates commits without touching
    # paragraph breaks, indentation, Unicode or shell-looking text in the message.
    raw = subprocess.check_output(
        ["git", "log", "--no-color", "--encoding=UTF-8", "--format=%B%x00", revision, "--"],
        cwd=cwd,
    ).decode("utf-8")
    messages = [message.strip("\n") for message in raw.split("\0") if message.strip("\n")]
    if not messages:
        raise ValueError("Release range contains no commit messages")
    return f"Mac Explorer {version}\n\n" + "\n\n---\n\n".join(messages)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True)
    parser.add_argument("--tag", required=True)
    parser.add_argument("--previous-tag", default="")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    args.output.write_text(generate_memo(args.version, args.tag, args.previous_tag), encoding="utf-8")
