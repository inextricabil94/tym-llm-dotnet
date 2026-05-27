# TYM LLM .NET Repository Manifest

This repository package contains the TYM LLM .NET prototype with the v0.2 multimodal video-engine additions included.

## Included repository assets

- ASP.NET Core Minimal API source under `src/Tym.Api`
- SQLite EF Core event memory
- Text timeline ingestion and timeline-aware querying
- Video upload ingestion through `POST /ingest/video`
- FFmpeg/FFprobe media probing and frame extraction
- Optional OpenAI vision-frame analysis
- Media asset/event inspection endpoints
- Yard graph links for chronological, semantic, causal, supersession, and video-frame order
- Dockerfile with FFmpeg installation
- GitHub Actions CI workflow under `.github/workflows/dotnet.yml`
- GitHub publishing helper script under `scripts/create-github-repo.sh`
- Video engine documentation under `docs/VIDEO_TYM_ENGINE.md`

## Suggested first commit message

```text
Add multimodal TYM video engine prototype
```

## Publish

```bash
gh auth login
./scripts/create-github-repo.sh tym-llm-dotnet private
```
