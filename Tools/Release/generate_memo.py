"""Extract blank-line-delimited numbered changelogs from a release range."""

import argparse
import re
import subprocess
from pathlib import Path


def extract_changelog(message):
    paragraphs = re.split(r"\n(?:[ \t]*\n)+", message.replace("\r\n", "\n").strip("\n"))
    if len(paragraphs) < 2:
        return []

    entries = []
    for line in paragraphs[1].splitlines():
        match = re.fullmatch(r"([1-9][0-9]*)\.[ \t]+(\S.*)", line)
        if match:
            if int(match[1]) != len(entries) + 1:
                return []
            entries.append(match[2])
        elif entries and line.startswith((" ", "\t")):
            entries[-1] += "\n" + line
        else:
            return []
    return entries


def generate_memo(tag, previous_tag="", cwd=None):
    revision = f"{previous_tag}..{tag}" if previous_tag else tag
    # %B includes both subject and body; NUL separates commits without touching
    # paragraph breaks, indentation, Unicode or shell-looking text in the message.
    raw = subprocess.check_output(
        ["git", "log", "--no-merges", "--no-color", "--encoding=UTF-8", "--format=%B%x00", revision, "--"],
        cwd=cwd,
    ).decode("utf-8")
    messages = [message.strip("\n") for message in raw.split("\0") if message.strip("\n")]
    entries = [entry for message in messages for entry in extract_changelog(message)]
    if not entries:
        raise ValueError("Release range contains no numbered changelog blocks; see AGENTS.md")
    return "\n".join(f"{number}. {entry}" for number, entry in enumerate(entries, 1))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tag", required=True)
    parser.add_argument("--previous-tag", default="")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    args.output.write_text(generate_memo(args.tag, args.previous_tag), encoding="utf-8")
