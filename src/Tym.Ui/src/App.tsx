import { useCallback, useEffect, useMemo, useState, type ReactNode } from "react";
import {
  Activity,
  AlertCircle,
  CheckCircle2,
  Database,
  FileText,
  Film,
  ImageIcon,
  Loader2,
  RefreshCw,
  Search,
  Settings2,
  Trash2,
  UploadCloud
} from "lucide-react";
import { TymApi } from "./api";
import type { EventDto, HealthResponse, MediaAssetDto, QueryResponse } from "./types";

type View = "operate" | "timeline" | "media";
type OperationStatus = "idle" | "loading" | "ok" | "error";

const sampleTimeline =
  "March 1, 2026: The team planned to launch the beta on March 20. March 10, 2026: QA found payment bugs that blocked release testing. March 15, 2026: The beta launch was moved to April 5 because of the payment bugs. April 2, 2026: The payment bugs were fixed. April 4, 2026: The beta was approved for release.";

const defaultApiBase = import.meta.env.VITE_TYM_API_URL ?? "http://localhost:5000";

export function App() {
  const [apiBase, setApiBase] = useState(() => localStorage.getItem("tym.apiBase") ?? defaultApiBase);
  const api = useMemo(() => new TymApi(apiBase), [apiBase]);

  const [view, setView] = useState<View>("operate");
  const [health, setHealth] = useState<HealthResponse | null>(null);
  const [events, setEvents] = useState<EventDto[]>([]);
  const [mediaAssets, setMediaAssets] = useState<MediaAssetDto[]>([]);
  const [assetEvents, setAssetEvents] = useState<EventDto[]>([]);
  const [selectedAssetId, setSelectedAssetId] = useState<string | null>(null);
  const [queryResponse, setQueryResponse] = useState<QueryResponse | null>(null);
  const [status, setStatus] = useState<OperationStatus>("idle");
  const [message, setMessage] = useState("Ready");

  const [textSource, setTextSource] = useState("ui-release-notes");
  const [referenceTime, setReferenceTime] = useState("2026-05-11T12:00:00Z");
  const [timelineText, setTimelineText] = useState(sampleTimeline);
  const [question, setQuestion] = useState("Why did the beta launch move and what is the current status?");
  const [maxEvents, setMaxEvents] = useState(8);

  const [videoFile, setVideoFile] = useState<File | null>(null);
  const [videoSource, setVideoSource] = useState("ui-video");
  const [capturedAt, setCapturedAt] = useState("");
  const [secondsPerFrame, setSecondsPerFrame] = useState(5);
  const [maxFrames, setMaxFrames] = useState(24);
  const [useVision, setUseVision] = useState(true);
  const [useSceneDetection, setUseSceneDetection] = useState(false);

  const run = useCallback(async (label: string, task: () => Promise<void>) => {
    setStatus("loading");
    setMessage(label);
    try {
      await task();
      setStatus("ok");
    } catch (error) {
      setStatus("error");
      setMessage(error instanceof Error ? error.message : "Request failed");
    }
  }, []);

  const refresh = useCallback(async () => {
    await run("Refreshing", async () => {
      const [healthResult, eventResult, mediaResult] = await Promise.all([
        api.health(),
        api.events(),
        api.mediaAssets()
      ]);
      setHealth(healthResult);
      setEvents(eventResult);
      setMediaAssets(mediaResult);
      setMessage(`Loaded ${eventResult.length} events and ${mediaResult.length} media assets`);
    });
  }, [api, run]);

  useEffect(() => {
    void refresh();
  }, []);

  const saveApiBase = () => {
    localStorage.setItem("tym.apiBase", apiBase);
    void refresh();
  };

  const ingestText = () =>
    run("Ingesting text", async () => {
      const result = await api.ingestText({
        source: textSource,
        referenceTime: referenceTime || undefined,
        text: timelineText
      });
      setEvents((current) => mergeEvents(current, result.events));
      setMessage(`Created ${result.eventsCreated} timeline events`);
      await refresh();
    });

  const ingestVideo = () =>
    run("Uploading video", async () => {
      if (!videoFile) {
        throw new Error("Select a video file first");
      }

      const form = new FormData();
      form.append("file", videoFile);
      form.append("source", videoSource);
      if (capturedAt) form.append("capturedAt", capturedAt);
      form.append("secondsPerFrame", String(secondsPerFrame));
      form.append("maxFrames", String(maxFrames));
      form.append("useVision", String(useVision));
      form.append("useSceneDetection", String(useSceneDetection));

      const result = await api.ingestVideo(form);
      setEvents((current) => mergeEvents(current, result.events));
      setMediaAssets((current) => [result.asset, ...current.filter((asset) => asset.id !== result.asset.id)]);
      setMessage(`Created ${result.eventsCreated} video events`);
      await refresh();
    });

  const ask = () =>
    run("Querying timeline", async () => {
      const result = await api.query({
        question,
        referenceTime: referenceTime || undefined,
        maxEvents
      });
      setQueryResponse(result);
      setMessage(`Retrieved ${result.evidence.length} evidence events`);
    });

  const reset = () =>
    run("Resetting memory", async () => {
      await api.reset();
      setEvents([]);
      setMediaAssets([]);
      setAssetEvents([]);
      setSelectedAssetId(null);
      setQueryResponse(null);
      setMessage("Memory reset");
    });

  const openAsset = (assetId: string) =>
    run("Loading media events", async () => {
      const result = await api.mediaAssetEvents(assetId);
      setSelectedAssetId(assetId);
      setAssetEvents(result);
      setView("media");
      setMessage(`Loaded ${result.length} media events`);
    });

  const activeEvents = events.filter((event) => !event.isSuperseded).length;
  const videoEvents = events.filter((event) => event.modality.startsWith("video")).length;

  return (
    <main className="app-shell">
      <header className="topbar">
        <div>
          <p className="eyebrow">TYM Console</p>
          <h1>Timeline Operations</h1>
        </div>
        <div className="api-controls">
          <label className="api-field">
            <Settings2 size={16} aria-hidden="true" />
            <input value={apiBase} onChange={(event) => setApiBase(event.target.value)} aria-label="API base URL" />
          </label>
          <button type="button" className="icon-button" onClick={saveApiBase} title="Save API URL" aria-label="Save API URL">
            <CheckCircle2 size={18} />
          </button>
          <button type="button" className="icon-button" onClick={refresh} title="Refresh" aria-label="Refresh">
            <RefreshCw size={18} />
          </button>
        </div>
      </header>

      <section className="status-strip">
        <Metric icon={<Activity size={18} />} label="API" value={health?.ok ? "online" : "unchecked"} />
        <Metric icon={<Database size={18} />} label="Events" value={String(events.length)} />
        <Metric icon={<CheckCircle2 size={18} />} label="Active" value={String(activeEvents)} />
        <Metric icon={<Film size={18} />} label="Video" value={String(videoEvents)} />
        <div className={`message ${status}`}>
          {status === "loading" ? <Loader2 className="spin" size={18} /> : status === "error" ? <AlertCircle size={18} /> : <CheckCircle2 size={18} />}
          <span>{message}</span>
        </div>
      </section>

      <nav className="tabs" aria-label="Primary views">
        <button className={view === "operate" ? "active" : ""} onClick={() => setView("operate")} type="button">
          <UploadCloud size={17} /> Operate
        </button>
        <button className={view === "timeline" ? "active" : ""} onClick={() => setView("timeline")} type="button">
          <FileText size={17} /> Timeline
        </button>
        <button className={view === "media" ? "active" : ""} onClick={() => setView("media")} type="button">
          <Film size={17} /> Media
        </button>
      </nav>

      {view === "operate" && (
        <section className="workspace">
          <form className="panel" onSubmit={(event) => { event.preventDefault(); ingestText(); }}>
            <div className="panel-heading">
              <FileText size={20} />
              <h2>Text Ingest</h2>
            </div>
            <div className="field-grid">
              <label>
                Source
                <input value={textSource} onChange={(event) => setTextSource(event.target.value)} />
              </label>
              <label>
                Reference time
                <input value={referenceTime} onChange={(event) => setReferenceTime(event.target.value)} />
              </label>
            </div>
            <label>
              Timeline text
              <textarea value={timelineText} onChange={(event) => setTimelineText(event.target.value)} rows={8} />
            </label>
            <button type="submit" className="primary-action">
              <UploadCloud size={18} /> Ingest Text
            </button>
          </form>

          <form className="panel" onSubmit={(event) => { event.preventDefault(); ingestVideo(); }}>
            <div className="panel-heading">
              <Film size={20} />
              <h2>Video Ingest</h2>
            </div>
            <label className="file-input">
              <input
                type="file"
                accept="video/mp4,video/quicktime,video/webm,video/x-matroska,.mp4,.mov,.m4v,.webm,.mkv"
                onChange={(event) => setVideoFile(event.target.files?.[0] ?? null)}
              />
              <UploadCloud size={18} />
              <span>{videoFile?.name ?? "Select video"}</span>
            </label>
            <div className="field-grid">
              <label>
                Source
                <input value={videoSource} onChange={(event) => setVideoSource(event.target.value)} />
              </label>
              <label>
                Captured at
                <input value={capturedAt} onChange={(event) => setCapturedAt(event.target.value)} placeholder="2026-05-12T10:00:00Z" />
              </label>
              <label>
                Seconds per frame
                <input type="number" min={1} max={600} value={secondsPerFrame} onChange={(event) => setSecondsPerFrame(Number(event.target.value))} />
              </label>
              <label>
                Max frames
                <input type="number" min={1} max={48} value={maxFrames} onChange={(event) => setMaxFrames(Number(event.target.value))} />
              </label>
            </div>
            <div className="toggle-row">
              <label>
                <input type="checkbox" checked={useVision} onChange={(event) => setUseVision(event.target.checked)} />
                Vision
              </label>
              <label>
                <input type="checkbox" checked={useSceneDetection} onChange={(event) => setUseSceneDetection(event.target.checked)} />
                Scene detection
              </label>
            </div>
            <button type="submit" className="primary-action">
              <UploadCloud size={18} /> Upload Video
            </button>
          </form>

          <form className="panel query-panel" onSubmit={(event) => { event.preventDefault(); ask(); }}>
            <div className="panel-heading">
              <Search size={20} />
              <h2>Query</h2>
            </div>
            <label>
              Question
              <textarea value={question} onChange={(event) => setQuestion(event.target.value)} rows={4} />
            </label>
            <label>
              Max events
              <input type="number" min={1} max={20} value={maxEvents} onChange={(event) => setMaxEvents(Number(event.target.value))} />
            </label>
            <div className="action-row">
              <button type="submit" className="primary-action">
                <Search size={18} /> Ask
              </button>
              <button type="button" className="danger-action" onClick={reset}>
                <Trash2 size={18} /> Reset
              </button>
            </div>
            {queryResponse && <QueryResult response={queryResponse} api={api} />}
          </form>
        </section>
      )}

      {view === "timeline" && (
        <section className="list-view">
          <div className="section-heading">
            <h2>Timeline</h2>
            <button type="button" className="secondary-action" onClick={refresh}>
              <RefreshCw size={17} /> Refresh
            </button>
          </div>
          <div className="event-list">
            {events.map((event) => (
              <EventRow key={event.id} event={event} api={api} onOpenAsset={event.mediaAssetId ? () => openAsset(event.mediaAssetId!) : undefined} />
            ))}
          </div>
        </section>
      )}

      {view === "media" && (
        <section className="list-view">
          <div className="section-heading">
            <h2>Media Assets</h2>
            <button type="button" className="secondary-action" onClick={refresh}>
              <RefreshCw size={17} /> Refresh
            </button>
          </div>
          <div className="media-grid">
            {mediaAssets.map((asset) => (
              <article key={asset.id} className={`media-card ${selectedAssetId === asset.id ? "selected" : ""}`}>
                <div>
                  <p className="item-title">{asset.originalFileName}</p>
                  <p className="item-subtitle">{asset.status} · {asset.framesExtracted} frames · {asset.eventsCreated} events</p>
                </div>
                <button type="button" className="icon-button" onClick={() => openAsset(asset.id)} title="Load asset events" aria-label="Load asset events">
                  <ImageIcon size={18} />
                </button>
              </article>
            ))}
          </div>
          {assetEvents.length > 0 && (
            <div className="event-list media-events">
              {assetEvents.map((event) => (
                <EventRow key={event.id} event={event} api={api} />
              ))}
            </div>
          )}
        </section>
      )}
    </main>
  );
}

function Metric({ icon, label, value }: { icon: ReactNode; label: string; value: string }) {
  return (
    <div className="metric">
      {icon}
      <span>{label}</span>
      <strong>{value}</strong>
    </div>
  );
}

function QueryResult({ response, api }: { response: QueryResponse; api: TymApi }) {
  return (
    <div className="query-result">
      <pre>{response.answer}</pre>
      <div className="evidence-list">
        {response.evidence.map((item) => (
          <EventRow key={item.event.id} event={item.event} api={api} score={item.score} />
        ))}
      </div>
    </div>
  );
}

function EventRow({
  event,
  api,
  score,
  onOpenAsset
}: {
  event: EventDto;
  api: TymApi;
  score?: number;
  onOpenAsset?: () => void;
}) {
  return (
    <article className={`event-row ${event.isSuperseded ? "superseded" : ""}`}>
      {event.thumbnailUri ? (
        <img className="event-thumb" src={api.assetUrl(event.thumbnailUri)} alt="" />
      ) : (
        <div className="event-thumb placeholder">
          {event.modality.startsWith("video") ? <Film size={22} /> : <FileText size={22} />}
        </div>
      )}
      <div className="event-body">
        <div className="event-line">
          <p className="item-title">{event.subject}</p>
          <div className="chips">
            <span>{event.modality}</span>
            {event.statusAfter && <span>{event.statusAfter}</span>}
            {typeof score === "number" && <span>{score.toFixed(2)}</span>}
          </div>
        </div>
        <p className="description">{event.description}</p>
        <p className="item-subtitle">
          {formatDate(event.timestampStart)} · seq {event.sequenceYards} · freshness {event.freshnessYards.toFixed(1)}
          {event.mediaStartSeconds !== null ? ` · ${formatMediaTime(event.mediaStartSeconds)}` : ""}
        </p>
      </div>
      {onOpenAsset && (
        <button type="button" className="icon-button" onClick={onOpenAsset} title="Open media asset" aria-label="Open media asset">
          <ImageIcon size={18} />
        </button>
      )}
    </article>
  );
}

function mergeEvents(current: EventDto[], incoming: EventDto[]) {
  const byId = new Map(current.map((event) => [event.id, event]));
  for (const event of incoming) {
    byId.set(event.id, event);
  }
  return Array.from(byId.values()).sort((a, b) => {
    const left = a.timestampStart ?? "";
    const right = b.timestampStart ?? "";
    return left.localeCompare(right);
  });
}

function formatDate(value: string | null) {
  if (!value) return "unknown";
  return new Intl.DateTimeFormat(undefined, {
    dateStyle: "medium",
    timeStyle: "short"
  }).format(new Date(value));
}

function formatMediaTime(seconds: number) {
  const minutes = Math.floor(seconds / 60);
  const remainder = Math.floor(seconds % 60);
  return `${String(minutes).padStart(2, "0")}:${String(remainder).padStart(2, "0")}`;
}
