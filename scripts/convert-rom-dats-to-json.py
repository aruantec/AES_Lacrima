#!/usr/bin/env python3
"""Convert ClrMamePro / No-Intro / Redump DAT files to AES hash title JSON databases."""

from __future__ import annotations

import argparse
import json
import re
import sys
import urllib.request
from pathlib import Path

NAME_RE = re.compile(r'^name\s+"(.*)"\s*$')
ROM_MD5_RE = re.compile(r'\bmd5\s+"?([0-9A-Fa-f]{32})"?', re.IGNORECASE)
ROM_SHA1_RE = re.compile(r'\bsha1\s+"?([0-9A-Fa-f]{40})"?', re.IGNORECASE)
ROM_CRC_RE = re.compile(r'\bcrc\s+"?([0-9A-Fa-f]{1,8})"?', re.IGNORECASE)
SERIAL_RE = re.compile(r'^serial\s+"(.*)"\s*$')

LIBRETRO_BASE = "https://raw.githubusercontent.com/libretro/libretro-database/master/metadat"

PLATFORM_SPECS: dict[str, dict[str, object]] = {
    "genesis": {
        "no_intro": f"{LIBRETRO_BASE}/no-intro/Sega%20-%20Mega%20Drive%20-%20Genesis.dat",
        "redump": [f"{LIBRETRO_BASE}/redump/Sega%20-%20Mega-CD%20-%20Sega%20CD.dat"],
    },
    "nes": {
        "no_intro": f"{LIBRETRO_BASE}/no-intro/Nintendo%20-%20Nintendo%20Entertainment%20System.dat",
    },
    "gba": {
        "no_intro": f"{LIBRETRO_BASE}/no-intro/Nintendo%20-%20Game%20Boy%20Advance.dat",
    },
    "psp": {
        "no_intro": f"{LIBRETRO_BASE}/no-intro/Sony%20-%20PlayStation%20Portable.dat",
        "redump": [f"{LIBRETRO_BASE}/redump/Sony%20-%20PlayStation%20Portable.dat"],
    },
}


def parse_dat(text: str) -> list[dict[str, str]]:
    entries: list[dict[str, str]] = []
    current_title: str | None = None
    current_serial: str | None = None

    for raw_line in text.splitlines():
        line = raw_line.strip()
        if not line or line.startswith("//"):
            continue

        if line.startswith("game ("):
            current_title = None
            current_serial = None
            continue

        if current_title is None:
            name_match = NAME_RE.match(line)
            if name_match:
                current_title = name_match.group(1).strip()
            continue

        serial_match = SERIAL_RE.match(line)
        if serial_match:
            current_serial = serial_match.group(1).strip()
            continue

        if line.startswith("rom ("):
            attrs = line[line.find("(") + 1 : line.rfind(")")].strip()
            md5_match = ROM_MD5_RE.search(attrs)
            sha1_match = ROM_SHA1_RE.search(attrs)
            crc_match = ROM_CRC_RE.search(attrs)
            md5 = md5_match.group(1).lower() if md5_match else None
            sha1 = sha1_match.group(1).lower() if sha1_match else None
            crc = crc_match.group(1).upper() if crc_match else None

            if md5 or sha1 or crc:
                entry: dict[str, str] = {"title": current_title}
                if current_serial:
                    entry["serial"] = current_serial
                if md5:
                    entry["md5"] = md5
                if sha1:
                    entry["sha1"] = sha1
                if crc:
                    entry["crc"] = crc
                entries.append(entry)

            if line.endswith(")"):
                current_title = None
                current_serial = None

    return entries


def dedupe(entries: list[dict[str, str]]) -> list[dict[str, str]]:
    seen: set[tuple[str, str, str]] = set()
    unique: list[dict[str, str]] = []
    for entry in entries:
        key = (
            entry.get("md5", "").lower(),
            entry.get("sha1", "").lower(),
            entry.get("crc", "").upper(),
        )
        if key == ("", "", ""):
            continue
        if key in seen:
            continue
        seen.add(key)
        unique.append(entry)
    return unique


def load_dat(path: Path) -> str:
    return path.read_text(encoding="utf-8", errors="replace")


def download(url: str) -> str:
    with urllib.request.urlopen(url, timeout=120) as response:
        return response.read().decode("utf-8", errors="replace")


def write_json(path: Path, entries: list[dict[str, str]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(entries, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def build_platform(platform: str, output_dir: Path, no_intro_dat: Path | None, redump_dats: list[Path], use_download: bool) -> None:
    spec = PLATFORM_SPECS[platform]
    no_intro_entries: list[dict[str, str]] = []
    redump_entries: list[dict[str, str]] = []

    if no_intro_dat:
        no_intro_entries.extend(parse_dat(load_dat(no_intro_dat)))
    elif use_download and spec.get("no_intro"):
        url = str(spec["no_intro"])
        print(f"Downloading No-Intro DAT for {platform} from {url}", file=sys.stderr)
        no_intro_entries.extend(parse_dat(download(url)))

    if redump_dats:
        for path in redump_dats:
            redump_entries.extend(parse_dat(load_dat(path)))
    elif use_download:
        for url in spec.get("redump") or []:
            print(f"Downloading Redump DAT for {platform} from {url}", file=sys.stderr)
            redump_entries.extend(parse_dat(download(str(url))))

    no_intro_entries = dedupe(no_intro_entries)
    redump_entries = dedupe(redump_entries)

    if no_intro_entries:
        out = output_dir / f"{platform}.json"
        write_json(out, no_intro_entries)
        print(f"Wrote {len(no_intro_entries)} No-Intro rows to {out}")

    if redump_entries:
        out = output_dir / f"{platform}_redump.json"
        write_json(out, redump_entries)
        print(f"Wrote {len(redump_entries)} Redump rows to {out}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--output-dir",
        type=Path,
        default=Path(__file__).resolve().parents[1] / "AES_Lacrima" / "Database",
    )
    parser.add_argument(
        "--platform",
        action="append",
        choices=sorted(PLATFORM_SPECS.keys()),
        help="Platform to convert (repeatable). Defaults to all when --download is used.",
    )
    parser.add_argument("--no-intro-dat", type=Path, help="Local No-Intro DAT path (single-platform mode)")
    parser.add_argument("--redump-dat", action="append", type=Path, default=[], help="Local Redump DAT path")
    parser.add_argument("--download", action="store_true", help="Download libretro mirror DATs")
    args = parser.parse_args()

    platforms = args.platform or (sorted(PLATFORM_SPECS.keys()) if args.download else [])
    if not platforms and args.no_intro_dat:
        platforms = ["genesis"]

    if not platforms:
        print("Specify --platform and/or --download, or pass --no-intro-dat.", file=sys.stderr)
        return 1

    for platform in platforms:
        no_intro = args.no_intro_dat if len(platforms) == 1 else None
        redump = args.redump_dat if len(platforms) == 1 else []
        build_platform(platform, args.output_dir, no_intro, redump, args.download)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
