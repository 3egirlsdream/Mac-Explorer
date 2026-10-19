#!/usr/bin/env python3
"""Compile Languages/*.json into lazy C# catalogs; never needed by the running app."""
import argparse
import json
import re
from pathlib import Path


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"Duplicate JSON key: {key}")
        result[key] = value
    return result


def placeholders(value):
    # Compare argument indices and multiplicity while accepting escaped braces.
    arguments = []
    position = 0
    while position < len(value):
        if value.startswith(("{{", "}}"), position):
            position += 2
        elif value[position] == "{":
            match = re.match(r"\{(\d+)(?:, *-?\d+ *)?(?::[^{}]*)?\}", value[position:])
            if not match:
                raise ValueError(f"Invalid composite format: {value}")
            arguments.append(match.group(1))
            position += match.end()
        elif value[position] == "}":
            raise ValueError(f"Invalid composite format: {value}")
        else:
            position += 1
    return sorted(arguments)


def load_languages(directory):
    packs = {}
    cultures = set()
    for path in sorted(directory.glob("*.json")):
        try:
            pack = json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=unique_object)
            if not isinstance(pack, dict):
                raise ValueError("language file must contain an object")
            code = pack["id"]
            if not isinstance(code, str) or not re.fullmatch(r"[a-zA-Z]{2,3}(?:-[a-zA-Z0-9]{2,8})*", code):
                raise ValueError("id must be a language tag")
            if path.stem != code:
                raise ValueError("filename must match id")
            if code.lower() in packs:
                raise ValueError(f"Duplicate language id: {code}")
            if not isinstance(pack["nativeName"], str) or not pack["nativeName"].strip():
                raise ValueError("nativeName is required")
            if not isinstance(pack["culture"], str) or not re.fullmatch(r"[a-zA-Z]{2,3}(?:-[a-zA-Z0-9]{2,8})*", pack["culture"]):
                raise ValueError("culture must be a culture tag")
            if pack["culture"].lower() in cultures:
                raise ValueError("culture must be unique")
            cultures.add(pack["culture"].lower())
            aliases = pack.get("aliases", [])
            if not isinstance(aliases, list) or any(not isinstance(tag, str) or not re.fullmatch(r"[a-zA-Z]{2,3}(?:-[a-zA-Z0-9]{2,8})*", tag) for tag in aliases):
                raise ValueError("aliases must be a list of language tags")
            if not isinstance(pack.get("order", 100), int):
                raise ValueError("order must be an integer")
            if not isinstance(pack["strings"], dict):
                raise ValueError("strings must be an object")
            for key, value in pack["strings"].items():
                if not key or not isinstance(value, str) or not value:
                    raise ValueError(f"Empty or invalid translation: {key}")
                placeholders(value)
            if code.lower() != "zh-cn":
                pack.setdefault("fallback", "en" if code.lower() != "en" else "zh-CN")
            packs[code.lower()] = pack
        except (ValueError, KeyError, TypeError) as error:
            raise ValueError(f"{path}: {error}") from error
    if "zh-cn" not in packs or "en" not in packs:
        raise ValueError("The zh-CN and en base languages are required")
    if packs["zh-cn"]["id"] != "zh-CN" or packs["en"]["id"] != "en":
        raise ValueError("Base language ids must use canonical zh-CN and en spelling")
    tags = {}
    for pack in packs.values():
        for tag in [pack["id"], pack["culture"], *pack.get("aliases", [])]:
            if tag.lower() in tags and tags[tag.lower()] != pack["id"]:
                raise ValueError(f"Ambiguous language tag: {tag}")
            tags[tag.lower()] = pack["id"]
    baseline = packs["zh-cn"]["strings"]
    for pack in packs.values():
        fallback = pack.get("fallback")
        if fallback is not None and (not isinstance(fallback, str) or fallback.lower() not in packs):
            raise ValueError(f"{pack['id']}: unknown fallback {fallback}")
        visited = {pack["id"].lower()}
        while fallback:
            if fallback.lower() in visited:
                raise ValueError(f"{pack['id']}: cyclic fallback")
            visited.add(fallback.lower())
            fallback = packs[fallback.lower()].get("fallback")
        for key, value in pack["strings"].items():
            if key not in baseline:
                raise ValueError(f"{pack['id']}: unknown translation key {key}")
            if placeholders(value) != placeholders(baseline[key]):
                raise ValueError(f"{pack['id']}: placeholder mismatch for {key}")
    if packs["en"]["strings"].keys() != baseline.keys():
        raise ValueError("English and Chinese must contain the same keys")
    literals = packs["zh-cn"].get("literalKeys", [])
    if not isinstance(literals, list) or any(not isinstance(key, str) or key not in baseline for key in literals):
        raise ValueError("literalKeys must list existing Chinese keys")
    return sorted(packs.values(), key=lambda pack: (pack.get("order", 100), pack["id"])), literals


def generate(directory):
    packs, literals = load_languages(directory)
    def quote(value):
        return json.dumps(value, ensure_ascii=False).replace("\u0085", "\\u0085").replace("\u2028", "\\u2028").replace("\u2029", "\\u2029")
    lines = ["// <auto-generated />", "#nullable enable", "namespace MacExplorer.Services.Impl;", "", "internal static partial class LocalizationCatalog", "{", "    private static class CatalogData", "    {", "    internal static readonly LanguagePack[] Packs =", "    ["]
    for index, pack in enumerate(packs):
        fallback = quote(pack["fallback"]) if pack.get("fallback") else "null"
        aliases = ", [" + ", ".join(quote(tag) for tag in pack["aliases"]) + "]" if pack.get("aliases") else ""
        lines.append(f'        new({quote(pack["id"])}, {quote(pack["nativeName"])}, {quote(pack["culture"])}, {fallback}, () => Language{index}.Strings{aliases}),')
    lines.extend(["    ];", "    }", ""])
    for index, pack in enumerate(packs):
        lines.extend([f"    private static class Language{index}", "    {", f"        static Language{index}() {{ }}", "        internal static readonly System.Collections.Generic.IReadOnlyDictionary<string, string> Strings =", "            new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.Ordinal)", "            {"])
        for key, value in pack["strings"].items():
            lines.append(f"                [{quote(key)}] = {quote(value)},")
        lines.extend(["            };", "    }", ""])
    lines.extend(["    private static class Literals", "    {", "        internal static readonly System.Collections.Generic.IReadOnlyDictionary<string, string> Keys =", "            new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.Ordinal)", "            {"])
    seen = set()
    baseline = next(pack for pack in packs if pack["id"] == "zh-CN")["strings"]
    for key in literals:
        text = baseline[key]
        if text not in seen:
            seen.add(text)
            lines.append(f"                [{quote(text)}] = {quote(key)},")
    lines.extend(["            };", "    }", "}", ""])
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    try:
        source = generate(args.directory)
    except ValueError as error:
        parser.exit(1, f"Localization error: {error}\n")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    if not args.output.exists() or args.output.read_text(encoding="utf-8") != source:
        args.output.write_text(source, encoding="utf-8")


if __name__ == "__main__":
    main()
