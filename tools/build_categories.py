#!/usr/bin/env python3
"""Merges Categories/{Props,Vehicles,Peds}/*.txt into resource/data/categories.json.

The SP build read the 75 category files straight off disk. A FiveM client has to go through
LoadResourceFile, one call per file, at startup - so the files are merged into a single document at
build time instead. The .txt files stay the source of truth: edit those, then re-run this.

Usage:  python tools/build_categories.py
"""

import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE = os.path.join(ROOT, "Categories")
TARGET = os.path.join(ROOT, "resource", "mapeditor", "data", "categories.json")

# "07_Signs & Billboards.txt" browses as "Signs & Billboards"; the prefix only fixes the menu order.
ORDER_PREFIX = re.compile(r"^\d+[_\-\s]+")

GROUPS = ("Props", "Vehicles", "Peds")


def read_category(path):
    models = []
    seen = set()
    with open(path, "r", encoding="utf-8-sig") as handle:
        for raw in handle:
            name = raw.strip()
            if not name or name.startswith("#") or name in seen:
                continue
            seen.add(name)
            models.append(name)
    return models


def build_group(folder):
    directory = os.path.join(SOURCE, folder)
    if not os.path.isdir(directory):
        print("warning: {} has no category folder".format(folder), file=sys.stderr)
        return []

    categories = []
    # Sorted by filename, matching what the SP build's Directory.GetFiles(...).OrderBy did.
    for filename in sorted(os.listdir(directory), key=str.lower):
        if not filename.lower().endswith(".txt"):
            continue
        models = read_category(os.path.join(directory, filename))
        if not models:
            continue
        display = ORDER_PREFIX.sub("", os.path.splitext(filename)[0])
        categories.append({"name": display, "models": models})
    return categories


def main():
    document = {}
    total = 0
    for group in GROUPS:
        categories = build_group(group)
        document[group] = categories
        count = sum(len(c["models"]) for c in categories)
        total += count
        print("{}: {} categories, {} models".format(group, len(categories), count))

    os.makedirs(os.path.dirname(TARGET), exist_ok=True)
    with open(TARGET, "w", encoding="utf-8") as handle:
        json.dump(document, handle, ensure_ascii=False, separators=(",", ":"))

    print("wrote {} ({} models, {} bytes)".format(
        os.path.relpath(TARGET, ROOT), total, os.path.getsize(TARGET)))


if __name__ == "__main__":
    main()
