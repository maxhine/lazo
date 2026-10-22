// Point clouds in the island. The conversion is the classic Lazo's (engine/CloudTool.cs over PointCloudConverter,
// CloudExport, CloudReprojector and CloudPreview, untouched); this keeps what the screen shows and asks for what
// it needs. Choices about who reprojects are made by the program, so the screen only sends what was picked.

import type { ToolMessage } from "../lazo/tools";
import type { Post } from "./xpace";

export type CloudFormat = "rcp" | "rcs" | "las" | "xyz" | "pts" | "csv";
export const CLOUD_FORMATS: CloudFormat[] = ["rcp", "rcs", "las", "xyz", "pts", "csv"];

/** The point clouds the converter takes. */
export const CLOUD_EXTENSIONS = [".las", ".laz", ".e57"];

export function isCloudFile(path: string): boolean {
  const lower = path.toLowerCase();
  return CLOUD_EXTENSIONS.some((e) => lower.endsWith(e));
}

export interface CloudFile {
  path: string;
  size: number;
}

/** A coordinate system that can be picked: one Lazo computes, one from the Autodesk library, or both. */
export interface CsChoice {
  ref: string;
  label: string;
  epsg: number;
  detail: string;
  recapKey?: string | null;
  lazo?: boolean;
  egm2008?: boolean;
}

export interface CloudStatus {
  deCap: string | null;
  geoid: boolean;
  library: boolean;
  units: string[];
  projections: { ref: string; label: string; epsg: number; geographic: boolean; featured: boolean; egm2008: boolean; recap: boolean }[];
}

export interface CloudPreviewData {
  original: string;
  reduced: string;
  before: number;
  after: number;
  windowMeters: number;
  reducedShown: boolean;
  canZone: boolean;
  e57: boolean;
}

export interface CloudResult {
  ok: boolean;
  output: string;
  message: string;
  report: string;
  elapsedMs: number;
}

export type PickerKind = "source" | "target" | null;

const PREVIEW_DELAY_MS = 260;

export class CloudModel {
  status: CloudStatus | null = null;
  files: CloudFile[] = [];
  folder = "";
  name = "";
  format: CloudFormat = "rcp";
  merge = false;
  unit = 2;
  decimation = "";
  source: CsChoice | null = null;
  target: CsChoice | null = null;
  ellipsoidal = false;
  detectNote = "";
  declared = "";
  picker: PickerKind = null;
  results: CsChoice[] = [];
  searched = false;
  running = false;
  progress = { value: 0, text: "" };
  result: CloudResult | null = null;
  /** What the preview shows: planta (top) or frente (front), and where in the file. */
  elevation = false;
  zone = 0.5;
  preview: CloudPreviewData | null = null;
  previewBusy = false;
  previewMessage = "";
  /** Something the screen should say that is not a result (a refused file, a dialog that failed). */
  notice = "";

  private seq = 0;
  private searchSeq = 0;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private post: Post;
  private changed: () => void;

  constructor(post: Post, changed: () => void) {
    this.post = post;
    this.changed = changed;
  }

  private send(command: { cmd: string } & Record<string, unknown>) {
    this.post(command).catch((err) => {
      this.running = false;
      this.previewBusy = false;
      this.notice = String(err).replace(/^Error:\s*/, "");
      this.changed();
    });
  }

  /** Asks what the program knows (ReCap, geoid, units, systems); once. */
  init() {
    if (!this.status) this.send({ cmd: "cloud.status" });
  }

  get ready(): boolean {
    return !!this.status?.deCap;
  }

  // ── Orders from the screen ──────────────────────────────────────────────────

  addFiles(paths: string[]) {
    if (this.running) return;
    const known = new Set(this.files.map((f) => f.path.toLowerCase()));
    const fresh = paths.filter((p) => {
      const key = p.toLowerCase();
      if (!isCloudFile(p) || known.has(key)) return false;
      known.add(key);
      return true;
    });
    if (fresh.length === 0) {
      this.notice = paths.length > 0 ? "unsupported" : "";
      this.changed();
      return;
    }
    this.notice = "";
    this.files = [...this.files, ...fresh.map((path) => ({ path, size: 0 }))];
    if (!this.name.trim()) this.name = baseName(this.files[0].path);
    this.result = null;
    this.progress = { value: 0, text: "" };
    this.probe();
    this.refreshPreview(true);
    this.changed();
  }

  removeFile(path: string) {
    if (this.running) return;
    this.files = this.files.filter((f) => f.path !== path);
    this.result = null;
    if (this.files.length === 0) {
      this.source = null;
      this.detectNote = "";
      this.declared = "";
    } else {
      this.probe();
    }
    this.refreshPreview(true);
    this.changed();
  }

  private probe() {
    this.send({ cmd: "cloud.probe", files: this.files.map((f) => f.path) });
  }

  pickFiles() {
    if (this.running) return;
    this.send({ cmd: "cloud.pickFiles" });
  }

  pickFolder() {
    if (this.running) return;
    this.send({ cmd: "cloud.pickFolder", start: this.folder || (this.files[0] ? dirName(this.files[0].path) : "") });
  }

  setFormat(format: CloudFormat) {
    if (this.running) return;
    this.format = format;
    this.changed();
  }

  setUnit(unit: number) {
    if (this.running) return;
    this.unit = unit;
    this.refreshPreview(false);
    this.changed();
  }

  setDecimation(text: string) {
    this.decimation = text;
    this.refreshPreview(false);
  }

  setElevation(value: boolean) {
    this.elevation = value;
    this.refreshPreview(false);
    this.changed();
  }

  setZone(zone: number) {
    this.zone = zone;
    this.refreshPreview(true);
    this.changed();
  }

  openPicker(kind: Exclude<PickerKind, null>) {
    if (this.running) return;
    this.picker = this.picker === kind ? null : kind;
    this.results = [];
    this.searched = false;
    if (this.picker) this.search("");
    this.changed();
  }

  search(query: string) {
    const kind = this.picker;
    if (!kind) return;
    this.searchSeq++;
    this.send({ cmd: "cloud.search", query, source: kind === "source" });
  }

  choose(choice: CsChoice | null) {
    if (this.running) return;
    if (this.picker === "source") this.source = choice;
    else this.target = choice;
    this.picker = null;
    this.changed();
  }

  /** The output system picked straight from the list of the team's systems. */
  chooseTarget(choice: CsChoice | null) {
    if (this.running) return;
    this.target = choice;
    this.changed();
  }

  setEllipsoidal(value: boolean) {
    if (this.running) return;
    this.ellipsoidal = value;
    this.changed();
  }

  convert() {
    if (this.running || this.files.length === 0 || !this.ready) return;
    this.running = true;
    this.result = null;
    this.notice = "";
    this.progress = { value: 0, text: "" };
    this.changed();
    this.send({
      cmd: "cloud.convert",
      files: this.files.map((f) => f.path),
      folder: this.folder,
      name: this.name.trim() || baseName(this.files[0].path),
      format: this.format,
      unit: this.unit,
      decimation: this.decimation,
      merge: this.format === "rcp" && this.merge,
      source: this.source?.ref ?? "",
      target: this.target?.ref ?? "",
      ellipsoidal: this.ellipsoidal && !!this.target?.egm2008 && !!this.status?.geoid,
    });
  }

  cancel() {
    if (this.running) this.send({ cmd: "cloud.cancel" });
  }

  open(path: string) {
    this.send({ cmd: "cloud.open", path });
  }

  // ── The preview ─────────────────────────────────────────────────────────────

  /** Asks for the preview a moment from now, in case the person is still typing. */
  private refreshPreview(reload: boolean) {
    if (reload) {
      this.preview = null;
      this.previewMessage = "";
    }
    if (this.timer) clearTimeout(this.timer);
    this.timer = setTimeout(() => {
      this.timer = null;
      this.requestPreview();
    }, PREVIEW_DELAY_MS);
  }

  private requestPreview() {
    const file = this.files[0]?.path;
    if (!file) {
      this.preview = null;
      this.previewBusy = false;
      this.changed();
      return;
    }
    const mm = parseFloat(this.decimation.trim().replace(",", "."));
    this.previewBusy = !this.preview;
    this.seq++;
    this.send({
      cmd: "cloud.preview",
      seq: this.seq,
      file,
      zone: this.zone,
      mm: Number.isFinite(mm) && mm > 0 ? mm : 0,
      unit: this.unit,
      elevation: this.elevation,
    });
    this.changed();
  }

  // ── What the program says ───────────────────────────────────────────────────

  handle(m: ToolMessage) {
    switch (m.type) {
      case "cloud.status":
        this.status = {
          deCap: m.deCap ?? null, geoid: !!m.geoid, library: !!m.library, units: m.units ?? [], projections: m.projections ?? [],
        };
        if (m.running) this.running = true;
        break;
      case "cloud.probe": {
        const sizes = new Map<string, number>((m.files ?? []).map((f: CloudFile) => [f.path.toLowerCase(), f.size]));
        this.files = this.files.map((f) => ({ path: f.path, size: sizes.get(f.path.toLowerCase()) ?? f.size }));
        this.source = m.detected ?? null;
        this.detectNote = m.note ?? "";
        this.declared = m.declared ?? "";
        break;
      }
      case "cloud.search":
        this.results = m.results ?? [];
        this.searched = true;
        break;
      case "cloud.picked":
        if (m.kind === "files") this.addFiles(m.paths ?? []);
        else if (m.paths?.[0]) this.folder = m.paths[0];
        break;
      case "cloud.preview":
        if (m.seq !== this.seq) return;
        this.previewBusy = false;
        if (m.ok) {
          this.preview = {
            original: m.original, reduced: m.reduced, before: m.before, after: m.after, windowMeters: m.windowMeters,
            reducedShown: !!m.reducedShown, canZone: !!m.canZone, e57: !!m.e57,
          };
          this.previewMessage = "";
        } else {
          this.preview = null;
          this.previewMessage = m.message ?? "";
        }
        break;
      case "cloud.started":
        this.running = true;
        break;
      case "cloud.progress":
        this.progress = { value: m.value, text: m.text };
        break;
      case "cloud.done":
        this.running = false;
        this.result = { ok: !!m.ok, output: m.output ?? "", message: m.message ?? "", report: m.report ?? "", elapsedMs: m.elapsedMs ?? 0 };
        this.progress = { value: m.ok ? 1 : 0, text: "" };
        break;
      case "cloud.opened":
        if (m.problem) this.notice = m.problem;
        break;
      default:
        return;
    }
    this.changed();
  }
}

export function baseName(path: string): string {
  const file = path.split(/[\\/]/).pop() ?? path;
  const dot = file.lastIndexOf(".");
  return dot > 0 ? file.slice(0, dot) : file;
}

export function dirName(path: string): string {
  const cut = Math.max(path.lastIndexOf("\\"), path.lastIndexOf("/"));
  return cut > 0 ? path.slice(0, cut) : "";
}

/** 12 s, 3 min 05 s: how long a conversion took. */
export function formatDuration(ms: number): string {
  const total = Math.max(0, Math.round(ms / 1000));
  if (total < 60) return `${total} s`;
  const minutes = Math.floor(total / 60);
  const seconds = total % 60;
  return `${minutes} min ${String(seconds).padStart(2, "0")} s`;
}
