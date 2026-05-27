#!/usr/bin/env bash
set -euo pipefail

VIDEO_PATH="${1:-demo.mp4}"
BASE_URL="${BASE_URL:-http://localhost:5000}"

curl -X POST "$BASE_URL/ingest/video" \
  -F "file=@${VIDEO_PATH}" \
  -F "source=sample-video" \
  -F "secondsPerFrame=5" \
  -F "maxFrames=24" \
  -F "useVision=true"
