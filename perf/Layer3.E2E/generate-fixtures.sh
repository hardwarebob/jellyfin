#!/usr/bin/env bash
# Generates tiny, valid, deterministic media files for Layer 3 to seed a real library with — no
# real media checked into git. Requires ffmpeg. Idempotent: skips generation if fixtures/ already
# has the expected file count.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
FIXTURES_DIR="$SCRIPT_DIR/fixtures"
mkdir -p "$FIXTURES_DIR"

TITLES=(
  "Perf Test Alpha (2020)"
  "Perf Test Bravo (2021)"
  "Perf Test Charlie (2022)"
  "Perf Test Delta (2023)"
  "Perf Test Echo (2024)"
  "Perf Test Foxtrot (2025)"
)
COLORS=(red green blue orange purple teal)

echo "${#TITLES[@]}" > "$FIXTURES_DIR/.expected-count"

for i in "${!TITLES[@]}"; do
  title="${TITLES[$i]}"
  color="${COLORS[$i]}"
  video="$FIXTURES_DIR/$title.mp4"
  poster="$FIXTURES_DIR/$title-poster.jpg"

  if [[ -f "$video" && -f "$poster" ]]; then
    continue
  fi

  # 2s, 64x64, solid color (one per title, from a fixed palette) — enough for ffprobe to resolve
  # real duration/codec metadata without shipping/downloading any real media.
  ffmpeg -y -loglevel error -f lavfi -i "color=c=${color}:s=64x64:d=2" \
    -c:v libx264 -pix_fmt yuv420p -metadata title="$title" "$video"

  ffmpeg -y -loglevel error -i "$video" -frames:v 1 "$poster"
done

echo "Generated ${#TITLES[@]} fixture(s) in $FIXTURES_DIR"
