#!/usr/bin/env python3
"""
Small CLI parser/downloader for Hitmo-style search pages.

Use only for audio you are allowed to download.
"""

from __future__ import annotations

import argparse
import html
import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass
from html.parser import HTMLParser
from pathlib import Path
from typing import Iterable


BASE_URL = "https://rus.hitmoz.org"
SEARCH_URL = BASE_URL + "/search"
USER_AGENT = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
    "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36"
)


@dataclass
class Track:
    index: int
    track_id: str
    artist: str
    title: str
    duration: str
    download_path: str
    preview_url: str

    @property
    def display_name(self) -> str:
        if self.artist and self.title:
            return f"{self.artist} - {self.title}"
        return self.title or self.artist or self.download_path.rsplit("/", 1)[-1]


class TrackParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.tracks: list[Track] = []
        self._current: dict[str, str] | None = None
        self._capture: str | None = None
        self._capture_text: list[str] = []

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        attrs_dict = {name: value or "" for name, value in attrs}
        classes = set(attrs_dict.get("class", "").split())

        if tag == "li" and {"tracks__item", "track"}.issubset(classes):
            self._current = {
                "track_id": "",
                "artist": "",
                "title": "",
                "duration": "",
                "download_path": "",
                "preview_url": "",
            }
            self._load_metadata(attrs_dict.get("data-musmeta", ""))
            return

        if self._current is None:
            return

        if tag == "a" and "track__download-btn" in classes:
            href = attrs_dict.get("href", "")
            if href:
                self._current["download_path"] = html.unescape(href)

        if tag == "span" and "track__like-btn" in classes:
            track_id = attrs_dict.get("data-track-id", "")
            if track_id:
                self._current["track_id"] = track_id

        if tag == "div":
            if "track__title" in classes:
                self._begin_capture("title")
            elif "track__desc" in classes:
                self._begin_capture("artist")
            elif "track__fulltime" in classes:
                self._begin_capture("duration")

    def handle_data(self, data: str) -> None:
        if self._current is not None and self._capture is not None:
            self._capture_text.append(data)

    def handle_endtag(self, tag: str) -> None:
        if self._current is None:
            return

        if tag == "div" and self._capture is not None:
            text = " ".join("".join(self._capture_text).split())
            if text:
                self._current[self._capture] = html.unescape(text)
            self._capture = None
            self._capture_text = []
            return

        if tag == "li":
            if self._current.get("download_path"):
                index = len(self.tracks) + 1
                self.tracks.append(Track(index=index, **self._current))
            self._current = None
            self._capture = None
            self._capture_text = []

    def _begin_capture(self, field: str) -> None:
        self._capture = field
        self._capture_text = []

    def _load_metadata(self, raw: str) -> None:
        if self._current is None or not raw:
            return
        try:
            meta = json.loads(html.unescape(raw))
        except json.JSONDecodeError:
            return
        self._current["artist"] = str(meta.get("artist") or "")
        self._current["title"] = str(meta.get("title") or "")
        self._current["preview_url"] = str(meta.get("url") or "")
        meta_id = str(meta.get("id") or "")
        if meta_id.startswith("track-id-"):
            self._current["track_id"] = meta_id[len("track-id-") :]


def parse_tracks(page_html: str) -> list[Track]:
    parser = TrackParser()
    parser.feed(page_html)
    return parser.tracks


def fetch_search_page(query: str, timeout: int) -> str:
    params = urllib.parse.urlencode({"q": query})
    request = urllib.request.Request(
        f"{SEARCH_URL}?{params}",
        headers={
            "Accept": "*/*",
            "Accept-Language": "ru,en;q=0.9",
            "Referer": BASE_URL + "/",
            "User-Agent": USER_AGENT,
            "X-PJAX": "true",
            "X-Requested-With": "XMLHttpRequest",
        },
    )
    with urllib.request.urlopen(request, timeout=timeout) as response:
        charset = response.headers.get_content_charset() or "utf-8"
        return response.read().decode(charset, errors="replace")


def choose_track(tracks: list[Track], selected: int) -> Track:
    if selected < 1 or selected > len(tracks):
        raise ValueError(f"track index must be between 1 and {len(tracks)}")
    return tracks[selected - 1]


def safe_filename(value: str) -> str:
    value = re.sub(r'[<>:"/\\|?*\x00-\x1f]', "_", value).strip(" .")
    value = re.sub(r"\s+", " ", value)
    return value[:180] or "track"


def filename_from_response(response: urllib.response.addinfourl, fallback_url: str, track: Track) -> str:
    disposition = response.headers.get("Content-Disposition", "")
    match = re.search(r'filename\*?=(?:UTF-8\'\')?"?([^";]+)', disposition, re.IGNORECASE)
    if match:
        return safe_filename(urllib.parse.unquote(match.group(1)))

    path_name = Path(urllib.parse.urlparse(response.geturl() or fallback_url).path).name
    if path_name and "." in path_name:
        return safe_filename(urllib.parse.unquote(path_name))

    return safe_filename(track.display_name) + ".mp3"


def download_track(track: Track, output_dir: Path, timeout: int) -> Path:
    output_dir.mkdir(parents=True, exist_ok=True)
    url = urllib.parse.urljoin(BASE_URL, track.download_path)
    request = urllib.request.Request(
        url,
        headers={
            "Accept": "audio/mpeg,audio/*,*/*",
            "Accept-Language": "ru,en;q=0.9",
            "Referer": BASE_URL + "/",
            "User-Agent": USER_AGENT,
        },
    )

    with urllib.request.urlopen(request, timeout=timeout) as response:
        content_type = response.headers.get("Content-Type", "")
        file_name = filename_from_response(response, url, track)
        if not file_name.lower().endswith(".mp3"):
            file_name += ".mp3"
        target = output_dir / file_name

        with target.open("wb") as file:
            while True:
                chunk = response.read(1024 * 128)
                if not chunk:
                    break
                file.write(chunk)

    if target.stat().st_size == 0:
        target.unlink(missing_ok=True)
        raise RuntimeError("download produced an empty file")
    if "html" in content_type.lower():
        raise RuntimeError(f"server returned HTML instead of audio: {target}")
    return target


def print_tracks(tracks: Iterable[Track], limit: int) -> None:
    for track in list(tracks)[:limit]:
        duration = f" [{track.duration}]" if track.duration else ""
        track_id = f" id={track.track_id}" if track.track_id else ""
        print(f"{track.index:>2}. {track.display_name}{duration}{track_id}")
        print(f"    {track.download_path}")


def build_arg_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Search and parse Hitmo-style tracks. Download only audio you may legally download."
    )
    parser.add_argument("query", nargs="?", help="search query, for example: lecture python")
    parser.add_argument("--html", type=Path, help="parse a local saved response instead of calling search")
    parser.add_argument("--limit", type=int, default=10, help="how many results to print")
    parser.add_argument("--download", type=int, metavar="N", help="download result number N")
    parser.add_argument("--out", type=Path, default=Path("downloads"), help="download directory")
    parser.add_argument("--timeout", type=int, default=30, help="network timeout in seconds")
    return parser


def main() -> int:
    args = build_arg_parser().parse_args()
    if args.html:
        page_html = args.html.read_text(encoding="utf-8", errors="replace")
    else:
        if not args.query:
            print("error: query is required unless --html is used", file=sys.stderr)
            return 2
        page_html = fetch_search_page(args.query, args.timeout)

    tracks = parse_tracks(page_html)
    if not tracks:
        print("no tracks found", file=sys.stderr)
        return 1

    print_tracks(tracks, max(0, args.limit))
    if args.download:
        track = choose_track(tracks, args.download)
        target = download_track(track, args.out, args.timeout)
        print(f"downloaded: {target}")

    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, urllib.error.URLError, ValueError, RuntimeError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        raise SystemExit(1)
