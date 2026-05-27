# TYM LLM .NET Prototype

TYM means **Time Yards Model**. This repo contains an ASP.NET Core Minimal API that turns text and videos into timeline events, stores them in SQLite, computes yard distances, and answers questions using timeline-aware retrieval.

The project is now a **multimodal TYM engine**:

- Text becomes structured timeline events.
- Video becomes timestamped frame events.
- Both modalities are placed into the same yard graph.
- Queries can ask about current state, causes, history, or what appears at a video timestamp.

## What this MVP does

### Text timeline ingestion

- `POST /ingest`: accepts raw notes, meeting text, incident logs, or project updates.
- Extracts events using OpenAI structured output when `OPENAI_API_KEY` is set.
- Falls back to a local heuristic extractor when no API key is present.

### Video analysis

- `POST /ingest/video`: accepts a video upload using `multipart/form-data`.
- Stores the video in a dedicated media directory.
- Uses FFmpeg to extract frames at configurable intervals.
- Optionally sends each frame to an OpenAI vision-capable model using the Responses API.
- Converts each analyzed frame into a `TimelineEvent` with:
  - `modality = video_frame`
  - `mediaStartSeconds`
  - `mediaEndSeconds`
  - frame URI
  - visual description
  - visible entities
- Falls back to timestamp-marker events when no vision provider is configured.

### Yard graph

The engine rebuilds yard metadata after ingestion:

- `sequence_yards`: event position in reconstructed timeline
- `freshness_yards`: distance in days from the reference time
- `media_chronological`: links between consecutive video frames
- `chronological`: links between ordered events
- `semantic`: links between related events
- `causal`: links from blocker/cause-like events
- `supersedes`: links from stale status events to newer status events

### Querying

- `POST /query`: scores evidence using semantic overlap, freshness, status, causal signals, and video/timestamp proximity.
- Generates an LLM answer when OpenAI is configured.
- Returns a deterministic timeline answer when no API key is present.

## Requirements

- .NET 8 SDK or newer
- FFmpeg and FFprobe available on `PATH`
- Optional: `OPENAI_API_KEY`
- Optional: `OPENAI_MODEL` for text extraction/answers
- Optional: `OPENAI_VISION_MODEL` for frame analysis

The included Dockerfile installs FFmpeg in the runtime image.

## Run locally

```bash
cd src/Tym.Api
export OPENAI_API_KEY="your_key_here"       # optional
export OPENAI_MODEL="gpt-4o-mini"           # optional
export OPENAI_VISION_MODEL="gpt-4.1-mini"   # optional

dotnet restore
dotnet run
```

The API listens on the URL printed by `dotnet run`, commonly `http://localhost:5000` or `https://localhost:5001`.

## Run the React UI

Start the API first, then run the UI from a second terminal:

```bash
cd src/Tym.Ui
npm install
npm run dev
```

Open the Vite URL, commonly `http://127.0.0.1:5173`.

The UI connects to `http://localhost:5000` by default. Change the API URL in the top-right control if your API is running on another port.

## Local deployment scripts

From PowerShell at the repository root:

```powershell
.\scripts\local-run.ps1
```

This starts the API on `http://localhost:5000` and the React UI on `http://127.0.0.1:5173`.

To build a local deployment bundle:

```powershell
.\scripts\local-deploy.ps1
```

The published API and built UI are written under `artifacts/local-deploy`.

## Azure DevOps cloud deployment

An Azure DevOps pipeline file is included at `azure-pipelines.yml`.

The pipeline:

- Builds and tests the API (`tests/Tym.Api.Tests`)
- Builds the React UI (`src/Tym.Ui`)
- Copies UI `dist` into API `wwwroot`
- Publishes one ZIP package
- Deploys to Azure App Service on pushes to `main`

### Prerequisites

1. Create an Azure Resource Group and an **App Service (Linux)** Web App.
2. In Azure DevOps, create a service connection to Azure (Project Settings -> Service connections).
3. In Azure Pipelines, create these variables:
   - `AZURE_SERVICE_CONNECTION` = service connection name
   - `AZURE_WEBAPP_NAME` = Azure Web App name

### Run

1. Create a pipeline in Azure DevOps pointing to this repo and `azure-pipelines.yml`.
2. Run it once manually.
3. Subsequent pushes to `main` trigger build + deploy automatically.

## Health check

```bash
curl http://localhost:5000/health
```

## Examples

### Example 1: Release timeline memory

Ingest the included release-meeting sample from the repository root:

```bash
curl -X POST http://localhost:5000/ingest \
  -H "Content-Type: application/json" \
  -d @samples/ingest-release.json
```

The sample payload contains a planned beta launch, a payment-bug blocker, the delayed release date, the bug fix, and final release approval:

```json
{
  "source": "release-meeting-notes",
  "referenceTime": "2026-05-11T12:00:00Z",
  "text": "March 1, 2026: The team planned to launch the beta on March 20. March 10, 2026: QA found payment bugs that blocked release testing. March 15, 2026: The beta launch was moved to April 5 because of the payment bugs. April 2, 2026: The payment bugs were fixed. April 4, 2026: The beta was approved for release."
}
```

Ask why the date changed and what the latest status is:

```bash
curl -X POST http://localhost:5000/query \
  -H "Content-Type: application/json" \
  -d '{
    "question": "Why did the beta launch move and what is the current status?",
    "referenceTime": "2026-05-11T12:00:00Z",
    "maxEvents": 8
  }'
```

### Example 2: Video frame memory

Use the included helper script to ingest a video with frame analysis enabled:

```bash
./samples/ingest-video.sh /path/to/demo.mp4
```

Or call the API directly:

```bash
curl -X POST http://localhost:5000/ingest/video \
  -F "file=@/path/to/demo.mp4" \
  -F "source=sample-video" \
  -F "secondsPerFrame=5" \
  -F "maxFrames=24" \
  -F "useVision=true"
```

Ask about a specific point in the video:

```bash
curl -X POST http://localhost:5000/query \
  -H "Content-Type: application/json" \
  -d '{
    "question": "What is visible in the video around 00:15?",
    "maxEvents": 6
  }'
```

## Ingest sample text timeline

From the repo root:

```bash
curl -X POST http://localhost:5000/ingest \
  -H "Content-Type: application/json" \
  -d @samples/ingest-release.json
```

If you run the command from `src/Tym.Api`, use this path instead:

```bash
curl -X POST http://localhost:5000/ingest \
  -H "Content-Type: application/json" \
  -d @../../samples/ingest-release.json
```

## Ingest a video

```bash
curl -X POST http://localhost:5000/ingest/video \
  -F "file=@/path/to/demo.mp4" \
  -F "source=demo-camera" \
  -F "capturedAt=2026-05-12T10:00:00Z" \
  -F "secondsPerFrame=5" \
  -F "maxFrames=24" \
  -F "useVision=true"
```

Use `useVision=false` to create timestamp-marker events without sending frames to OpenAI:

```bash
curl -X POST http://localhost:5000/ingest/video \
  -F "file=@/path/to/demo.mp4" \
  -F "secondsPerFrame=10" \
  -F "maxFrames=12" \
  -F "useVision=false"
```

Use `useSceneDetection=true` to let FFmpeg select frames around visual scene changes instead of only fixed intervals:

```bash
curl -X POST http://localhost:5000/ingest/video \
  -F "file=@/path/to/demo.mp4" \
  -F "secondsPerFrame=5" \
  -F "maxFrames=24" \
  -F "useVision=true" \
  -F "useSceneDetection=true"
```

## Ask text and video questions

```bash
curl -X POST http://localhost:5000/query \
  -H "Content-Type: application/json" \
  -d '{
    "question": "Why did the beta launch move and what is the current status?",
    "referenceTime": "2026-05-11T12:00:00Z",
    "maxEvents": 8
  }'
```

Video-oriented examples:

```bash
curl -X POST http://localhost:5000/query \
  -H "Content-Type: application/json" \
  -d '{
    "question": "What is visible in the video around 00:15?",
    "maxEvents": 6
  }'
```

```bash
curl -X POST http://localhost:5000/query \
  -H "Content-Type: application/json" \
  -d '{
    "question": "Summarize the main scene changes in the footage.",
    "maxEvents": 12
  }'
```

## Inspect memory

```bash
curl http://localhost:5000/events
curl http://localhost:5000/yard-links
curl http://localhost:5000/media-assets
curl http://localhost:5000/media-assets/{assetId}
curl http://localhost:5000/media-assets/{assetId}/events
```

Frame events include a `mediaUri` such as:

```text
/media-assets/{assetId}/frames/frame_00001.jpg
```

Open it in a browser or fetch it with curl.

## Reset memory

```bash
curl -X DELETE http://localhost:5000/events
```

This clears events, yard links, and media asset rows. It does not delete the files already stored under `App_Data/tym-media`; delete that folder manually if you want to clear uploaded media from disk.

## Automated tests

Run the lightweight test harness from the repository root:

```bash
dotnet run --project tests/Tym.Api.Tests/Tym.Api.Tests.csproj
```

The current tests cover token similarity, superseded status events, freshness yards, and current-state query retrieval. GitHub Actions runs the same test harness on pushes and pull requests.

## API hardening

The API applies fixed-window rate limits to the write-heavy endpoints:

- `POST /ingest`: 30 requests per minute
- `POST /ingest/video`: 4 requests per minute
- `POST /query`: 60 requests per minute

Video uploads are checked by extension, declared content type, configured maximum size, and basic container signature before storage.

## Configuration

`src/Tym.Api/appsettings.json` includes:

```json
{
  "Tym": {
    "MediaRoot": "App_Data/tym-media",
    "MaxVideoBytes": 262144000,
    "MaxFramesPerVideo": 48,
    "DefaultSecondsPerFrame": 5,
    "AllowedVideoExtensions": [".mp4", ".mov", ".m4v", ".webm", ".mkv"],
    "AllowedVideoMimeTypes": ["video/mp4", "video/quicktime", "video/webm", "video/x-matroska", "application/octet-stream"],
    "FfmpegPath": "ffmpeg",
    "FfprobePath": "ffprobe",
    "ExtractedFrameWidth": 640,
    "UseSceneDetectionByDefault": false,
    "SceneChangeThreshold": 0.35
  }
}
```

## Core model

The main domain objects are:

```text
TimelineEvent
  text event, video frame event, future audio transcript event

MediaAsset
  uploaded video metadata and analysis status

YardLink
  chronological, media_chronological, semantic, causal, supersedes
```

The main scoring idea is:

```text
answer evidence score = semantic relevance
                      + freshness yards
                      + active/non-superseded status
                      + causal/blocker relevance
                      + video modality relevance
                      + media timestamp proximity
```

For current-state questions, freshness and non-superseded events are weighted more heavily. For why/cause questions, blocker and causal-update events receive extra weight. For video questions, frame events and media timestamp proximity receive extra weight.

## Files

```text
src/Tym.Api/
  Program.cs                       Minimal API endpoints
  Contracts/ApiContracts.cs        HTTP request/response DTOs
  Data/TymDbContext.cs             SQLite EF Core context
  Domain/TimelineEvent.cs          Event memory object
  Domain/MediaAsset.cs             Uploaded media metadata
  Domain/YardLink.cs               Yard graph edge
  Services/EventExtractor.cs       OpenAI + heuristic text extraction
  Services/VideoIngestService.cs   Video upload, FFmpeg frames, vision analysis
  Services/YardService.cs          Yard computation and supersession
  Services/QueryService.cs         Retrieval and answer generation
  Services/TokenSimilarity.cs      Local token overlap scoring
  Services/TextUtil.cs             Date, timestamp, and heuristic helpers

src/Tym.Ui/
  src/App.tsx                      React operations console
  src/api.ts                       Fetch client for the TYM API
  src/styles.css                   UI layout and controls
```

## Video design notes

See [`docs/VIDEO_TYM_ENGINE.md`](docs/VIDEO_TYM_ENGINE.md).

## Next production upgrades

1. Add durable EF Core migrations instead of `EnsureCreated`, then support PostgreSQL for research deployments.
2. Move video ingestion to a queued background worker for large files.
3. Replace token overlap with embeddings and vector search using pgvector, Qdrant, or Milvus.
4. Store exact document spans, frame checksums, and transcript offsets for citations.
5. Add audio transcription and align transcript events with visual frame events.
6. Add scene clustering on top of FFmpeg scene-change extraction so adjacent scene frames become one stronger event.
7. Add a minimal Blazor or React dashboard for ingestion, timeline inspection, and query evidence review.
8. Add user/project tenancy columns and authorization.
9. Add malware scanning for uploaded media before analysis.
10. Expand test coverage with integration tests for video timestamp retrieval, supersession, rate limits, and upload rejection paths.

## Publishing to GitHub

See [`CREATE_GITHUB_REPO.md`](CREATE_GITHUB_REPO.md) or run:

```bash
./scripts/create-github-repo.sh tym-llm-dotnet private
```
