export type HealthResponse = {
  ok: boolean;
  service: string;
  utc: string;
};

export type EventDto = {
  id: string;
  subject: string;
  eventType: string;
  description: string;
  timestampStart: string | null;
  timestampEnd: string | null;
  timeConfidence: number;
  source: string;
  actor: string | null;
  statusBefore: string | null;
  statusAfter: string | null;
  relatedEntities: string[];
  modality: string;
  mediaAssetId: string | null;
  mediaStartSeconds: number | null;
  mediaEndSeconds: number | null;
  segmentIndex: number | null;
  mediaUri: string | null;
  thumbnailUri: string | null;
  freshnessYards: number;
  sequenceYards: number;
  confidence: number;
  isSuperseded: boolean;
  supersededByEventId: string | null;
};

export type ScoredEventDto = {
  event: EventDto;
  score: number;
  semanticScore: number;
  freshnessScore: number;
  whySelected: string;
};

export type QueryResponse = {
  answer: string;
  evidence: ScoredEventDto[];
  notes: string[];
};

export type IngestResponse = {
  eventsCreated: number;
  events: EventDto[];
};

export type MediaAssetDto = {
  id: string;
  originalFileName: string;
  contentType: string;
  sizeBytes: number;
  source: string;
  status: string;
  lastError: string | null;
  durationSeconds: number | null;
  framesExtracted: number;
  eventsCreated: number;
  capturedAt: string | null;
  createdAt: string;
};

export type VideoIngestResponse = {
  asset: MediaAssetDto;
  eventsCreated: number;
  events: EventDto[];
  notes: string[];
};
