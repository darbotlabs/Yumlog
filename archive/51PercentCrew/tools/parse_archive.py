"""Parse 51PercentCrew archive: info.json + VTT captions -> transcripts + index."""
import json
import re
import sys
from pathlib import Path

ROOT = Path(r"S:\Yumlog\archive\51PercentCrew")
META = ROOT / "meta"
TRANSCRIPTS = ROOT / "transcripts"
TRANSCRIPTS.mkdir(exist_ok=True)

TS_RE = re.compile(r"^\d{2}:\d{2}:\d{2}\.\d{3} --> ")
TAG_RE = re.compile(r"<[^>]+>")


def parse_vtt(path: Path) -> str:
    """Flatten a (possibly rolling-caption) VTT into deduplicated prose."""
    lines_out = []
    prev = ""
    for raw in path.read_text(encoding="utf-8", errors="replace").splitlines():
        line = TAG_RE.sub("", raw).strip()
        if (not line or line == "WEBVTT" or TS_RE.match(line)
                or line.startswith(("Kind:", "Language:", "NOTE", "align:", "position:"))):
            continue
        if line == prev:
            continue
        # rolling captions repeat the previous line as a prefix
        if prev and line.startswith(prev):
            line_new = line[len(prev):].strip()
            if line_new:
                lines_out.append(line_new)
        else:
            lines_out.append(line)
        prev = line
    text = " ".join(lines_out)
    return re.sub(r"\s+", " ", text).strip()


ALL_VTTS = None


def best_vtt(stem: str) -> Path | None:
    """Prefer plain 'en' captions over 'en-orig' / other variants."""
    global ALL_VTTS
    if ALL_VTTS is None:
        ALL_VTTS = list(META.iterdir())
    # avoid Path.glob: titles contain [] which are glob character classes
    candidates = sorted(
        (p for p in ALL_VTTS
         if p.name.startswith(stem + ".") and p.name.endswith(".vtt")),
        key=lambda p: len(p.name))
    for pref in (".en.vtt",):
        for c in candidates:
            if c.name.endswith(pref):
                return c
    return candidates[0] if candidates else None


def main() -> None:
    index = []
    for ij in sorted(META.glob("*.info.json")):
        data = json.loads(ij.read_text(encoding="utf-8"))
        if data.get("_type") == "playlist" or data.get("id", "").startswith("UC"):
            continue
        stem = ij.name[: -len(".info.json")]
        vtt = best_vtt(stem)
        transcript_file = None
        words = 0
        if vtt:
            text = parse_vtt(vtt)
            words = len(text.split())
            transcript_file = stem + ".txt"
            (TRANSCRIPTS / transcript_file).write_text(text, encoding="utf-8")
        index.append({
            "id": data.get("id"),
            "title": data.get("title"),
            "upload_date": data.get("upload_date"),
            "duration_s": data.get("duration"),
            "view_count": data.get("view_count"),
            "like_count": data.get("like_count"),
            "description": (data.get("description") or "")[:500],
            "tags": data.get("tags") or [],
            "url": data.get("webpage_url"),
            "transcript": transcript_file,
            "transcript_words": words,
        })

    index.sort(key=lambda v: v["upload_date"] or "")
    (ROOT / "index.json").write_text(
        json.dumps(index, indent=2, ensure_ascii=False), encoding="utf-8")

    md = ["# 51 Percent Crew — Channel Archive Index", "",
          f"{len(index)} videos | generated from yt-dlp metadata", "",
          "| # | Date | Title | Length | Views | Transcript words |",
          "|---|------|-------|--------|-------|------------------|"]
    for i, v in enumerate(index, 1):
        d = v["upload_date"]
        date = f"{d[:4]}-{d[4:6]}-{d[6:]}" if d else "?"
        mins = f"{(v['duration_s'] or 0) // 60}m{(v['duration_s'] or 0) % 60:02d}s"
        md.append(f"| {i} | {date} | [{v['title']}]({v['url']}) | {mins} "
                  f"| {v['view_count']} | {v['transcript_words']} |")
    (ROOT / "INDEX.md").write_text("\n".join(md) + "\n", encoding="utf-8")

    total_words = sum(v["transcript_words"] for v in index)
    have = sum(1 for v in index if v["transcript"])
    print(f"{len(index)} videos indexed; {have} transcripts; {total_words} total words")


if __name__ == "__main__":
    sys.exit(main())
