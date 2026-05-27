# Changelog

## v0.2.0 - Multimodal Video TYM Engine

- Added `POST /ingest/video` for multipart video uploads.
- Added FFmpeg/FFprobe frame extraction and duration probing.
- Added optional OpenAI vision-frame analysis via the Responses API.
- Added `MediaAsset` domain model and media inspection endpoints.
- Extended `TimelineEvent` with modality and media timestamp fields.
- Added `media_chronological` yard links between consecutive video frames.
- Upgraded query scoring with video modality and media timestamp proximity.
- Added frame-serving endpoint for inspecting extracted images.
- Added Docker runtime FFmpeg installation.
- Added video design documentation and sample curl script.
