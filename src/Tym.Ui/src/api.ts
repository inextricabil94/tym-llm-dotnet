import type {
  EventDto,
  HealthResponse,
  IngestResponse,
  MediaAssetDto,
  QueryResponse,
  VideoIngestResponse
} from "./types";

type RequestOptions = RequestInit & {
  body?: BodyInit | null;
};

export class TymApi {
  constructor(private readonly baseUrl: string) {}

  health() {
    return this.request<HealthResponse>("/health");
  }

  events() {
    return this.request<EventDto[]>("/events");
  }

  mediaAssets() {
    return this.request<MediaAssetDto[]>("/media-assets");
  }

  mediaAssetEvents(assetId: string) {
    return this.request<EventDto[]>(`/media-assets/${assetId}/events`);
  }

  ingestText(payload: { source?: string; referenceTime?: string; text: string }) {
    return this.request<IngestResponse>("/ingest", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload)
    });
  }

  ingestVideo(form: FormData) {
    return this.request<VideoIngestResponse>("/ingest/video", {
      method: "POST",
      body: form
    });
  }

  query(payload: { question: string; referenceTime?: string; maxEvents: number }) {
    return this.request<QueryResponse>("/query", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload)
    });
  }

  reset() {
    return this.request<{ deleted: boolean }>("/events", { method: "DELETE" });
  }

  assetUrl(path: string) {
    return `${this.baseUrl.replace(/\/$/, "")}${path}`;
  }

  private async request<T>(path: string, options: RequestOptions = {}): Promise<T> {
    const response = await fetch(`${this.baseUrl.replace(/\/$/, "")}${path}`, options);
    const text = await response.text();
    const data = text ? JSON.parse(text) : null;

    if (!response.ok) {
      const message = data?.error ?? data?.title ?? response.statusText;
      throw new Error(message);
    }

    return data as T;
  }
}
