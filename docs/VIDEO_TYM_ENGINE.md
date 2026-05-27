# Video TYM Engine Design

The video TYM engine converts video into the same event graph used by text ingestion.

## Pipeline

```text
Video upload
  -> safe media storage
  -> FFprobe duration read
  -> FFmpeg frame extraction
  -> optional vision model captioning
  -> TimelineEvent creation
  -> yard graph rebuild
  -> query-time retrieval
```

## Why frames become events

TYM answers questions by comparing events across several yard dimensions. A video frame can be represented as an event because it has:

- a subject
- a description
- a relative timestamp inside the media file
- visible entities
- confidence
- a frame URI for inspection

Example event:

```json
{
  "modality": "video_frame",
  "mediaStartSeconds": 15,
  "eventType": "visual_observation",
  "description": "At 00:15: a person is presenting a dashboard on a conference-room screen.",
  "relatedEntities": ["person", "dashboard", "screen", "conference room"]
}
```

## Time yards and media yards

Text events primarily use chronological yards in days. Video events use two time systems:

1. `timestampStart` / `timestampEnd`: an approximate absolute time for ordering in the global timeline.
2. `mediaStartSeconds` / `mediaEndSeconds`: the exact relative position in the video.

This lets the engine answer both:

- "What happened after the incident review?"
- "What is visible around 00:35 in the video?"

## Link types

- `media_chronological`: consecutive frames in the same video.
- `chronological`: event order across all modalities.
- `semantic`: related text and video events.
- `causal`: blocker/cause-like text events.
- `supersedes`: stale status events replaced by newer status events.

## Current limitations

- Frame timestamps are approximate because fixed-interval extraction is used.
- Vision captions are only as accurate as the configured model.
- The MVP processes video synchronously.
- Audio is not transcribed yet.
- The MVP does not yet do scene-change detection.

## Recommended production evolution

1. Add a background queue with job status endpoints.
2. Add scene detection and keyframe selection.
3. Add audio transcription and align transcript sentences to visual frames.
4. Use embeddings for cross-modal retrieval.
5. Store image hashes and frame provenance for auditability.
6. Add human review tools for low-confidence visual events.
