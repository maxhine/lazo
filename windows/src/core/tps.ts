// TPS to RINEX in the island. The conversion is Topcon's tps2rin.exe, untouched; engine/Tps2RinTool.cs runs it on each
// file in a temporary folder and reads the RINEX it leaves (the times observed, the interval, the epochs and the gaps).
// Converting copies those files to the folder chosen, with the name wanted. This keeps what the screen shows.

import type { ToolMessage } from "../lazo/tools";
import type { Post } from "./xpace";

export interface TpsObservation {
  hasTimes: boolean;
  /** GPS time, "2026-10-21T21:02:25". */
  first: string;
  last: string;
  leap: number;
  interval: number;
  epochs: number;
  expected: number;
  gaps: number;
  missing: number;
  marker: string;
  receiver: string;
  antenna: string;
  satellites: number;
  systems: string;
}

export interface TpsFile {
  path: string;
  /** The name the result will have (without extension); empty: the .tps file's own. */
  name: string;
  state: "reading" | "ok" | "bad";
  message: string;
  obs: TpsObservation | null;
  /** The extensions TPS2RIN made (.26O, .26N…). */
  outputs: string[];
}

export function isTpsFile(path: string): boolean {
  return path.toLowerCase().endsWith(".tps");
}

export function baseName(path: string): string {
  const file = path.split(/[\\/]/).pop() ?? path;
  const dot = file.lastIndexOf(".");
  return dot > 0 ? file.slice(0, dot) : file;
}

const pad = (n: number) => String(Math.floor(n)).padStart(2, "0");

/** A time from the program ("2026-10-21T21:02:25", no zone) plus some seconds, as a date and a time of day. */
export function clock(iso: string, shiftSeconds = 0): { date: string; time: string } {
  const [d, t] = iso.split("T");
  const [y, mo, da] = d.split("-").map(Number);
  const [h, mi, s] = t.split(":").map(Number);
  const at = new Date(Date.UTC(y, mo - 1, da, h, mi, s) + shiftSeconds * 1000);
  return {
    date: `${at.getUTCFullYear()}-${pad(at.getUTCMonth() + 1)}-${pad(at.getUTCDate())}`,
    time: `${pad(at.getUTCHours())}:${pad(at.getUTCMinutes())}:${pad(at.getUTCSeconds())}`,
  };
}

/** 16 min 10 s, 1 h 02 min, 45 s: how long something lasted. */
export function span(seconds: number): string {
  const s = Math.max(0, Math.round(seconds));
  if (s < 60) return `${s} s`;
  if (s < 3600) return `${Math.floor(s / 60)} min ${pad(s % 60)} s`;
  return `${Math.floor(s / 3600)} h ${pad((s % 3600) / 60)} min`;
}

/** Seconds between two program times. */
export function between(first: string, last: string): number {
  const at = (iso: string) => {
    const [d, t] = iso.split("T");
    const [y, mo, da] = d.split("-").map(Number);
    const [h, mi, s] = t.split(":").map(Number);
    return Date.UTC(y, mo - 1, da, h, mi, s) / 1000;
  };
  return at(last) - at(first);
}

export class TpsModel {
  /** Where tps2rin.exe is: null once known not to be there, undefined until the program has said. */
  exe: string | null | undefined = undefined;
  files: TpsFile[] = [];
  folder = "";
  running = false;
  converting = false;
  written: string[] = [];
  /** The last conversion's outcome: "" nothing yet, "ok", or the reason it failed. */
  result = "";
  /** The timeline reads in UTC instead of GPS time. */
  utc = false;
  /** A message the screen shows that is not a result ("unsupported", an error from a dialog). */
  notice = "";

  private post: Post;
  private changed: () => void;

  constructor(post: Post, changed: () => void) {
    this.post = post;
    this.changed = changed;
  }

  private send(command: { cmd: string } & Record<string, unknown>) {
    this.post(command).catch((err) => {
      this.running = false;
      this.converting = false;
      this.notice = String(err).replace(/^Error:\s*/, "");
      this.changed();
    });
  }

  init() {
    if (this.exe === undefined) this.send({ cmd: "tps.status" });
  }

  pickExe() {
    this.send({ cmd: "tps.pick", kind: "exe" });
  }

  pickFiles() {
    if (!this.converting) this.send({ cmd: "tps.pick", kind: "files" });
  }

  pickFolder() {
    if (this.converting) return;
    this.send({ cmd: "tps.pick", kind: "folder", start: this.folder });
  }

  addFiles(paths: string[]) {
    if (this.converting) return;
    const known = new Set(this.files.map((f) => f.path.toLowerCase()));
    const fresh = paths.filter((p) => {
      const key = p.toLowerCase();
      if (!isTpsFile(p) || known.has(key)) return false;
      known.add(key);
      return true;
    });
    if (fresh.length === 0) {
      this.notice = paths.length > 0 ? "unsupported" : "";
      this.changed();
      return;
    }
    this.notice = "";
    this.result = "";
    this.files = [...this.files, ...fresh.map((path): TpsFile => ({ path, name: "", state: "reading", message: "", obs: null, outputs: [] }))];
    this.probe();
    this.changed();
  }

  setUtc(value: boolean) {
    this.utc = value;
    this.changed();
  }

  removeFile(path: string) {
    if (this.converting) return;
    this.files = this.files.filter((f) => f.path !== path);
    this.result = "";
    this.changed();
  }

  setName(path: string, name: string) {
    const file = this.files.find((f) => f.path === path);
    if (file) file.name = name;
  }

  private probe() {
    if (this.exe === null) return;
    this.running = true;
    this.send({ cmd: "tps.probe", files: this.files.filter((f) => f.state === "reading").map((f) => f.path) });
  }

  get ready(): boolean {
    return !!this.exe && !this.converting && this.files.some((f) => f.state === "ok");
  }

  convert() {
    if (!this.ready) return;
    this.converting = true;
    this.result = "";
    this.written = [];
    this.notice = "";
    this.changed();
    this.send({
      cmd: "tps.convert",
      folder: this.folder,
      items: this.files.filter((f) => f.state === "ok").map((f) => ({ file: f.path, name: f.name.trim() })),
    });
  }

  handle(m: ToolMessage) {
    switch (m.type) {
      case "tps.status":
        this.exe = m.exe ?? null;
        if (m.problem) this.notice = m.problem;
        else if (this.exe) {
          this.notice = "";
          // Files added before the program was known are read now.
          if (this.files.some((f) => f.state === "reading")) this.probe();
        }
        break;
      case "tps.picked":
        if (m.kind === "exe" && m.paths?.[0]) this.send({ cmd: "tps.setExe", path: m.paths[0] });
        else if (m.kind === "files") this.addFiles(m.paths ?? []);
        else if (m.kind === "folder" && m.paths?.[0]) this.folder = m.paths[0];
        break;
      case "tps.working":
        break;
      case "tps.file": {
        const file = this.files.find((f) => f.path.toLowerCase() === String(m.path).toLowerCase());
        if (!file) return;
        file.state = m.ok ? "ok" : "bad";
        file.message = m.message ?? "";
        file.obs = m.obs ?? null;
        file.outputs = (m.outputs ?? []).map((o: { ext: string }) => o.ext);
        break;
      }
      case "tps.probed":
        this.running = false;
        break;
      case "tps.done":
        this.converting = false;
        this.written = m.written ?? [];
        this.result = m.ok ? "ok" : m.message || "failed";
        break;
      default:
        return;
    }
    this.changed();
  }
}

// ── The timeline of several files ───────────────────────────────────────────────

export interface TimelineBar {
  path: string;
  label: string;
  /** Position and width as a percentage of the day's axis. */
  left: number;
  width: number;
  from: string;
  to: string;
  /** The bar continues before or after this day. */
  openStart: boolean;
  openEnd: boolean;
}

export interface TimelineDay {
  /** "2026-10-21". */
  date: string;
  ticks: { left: number; label: string }[];
  bars: TimelineBar[];
  /** What all the files have in common, when they share some time. */
  common: { left: number; width: number } | null;
}

export interface Timeline {
  days: TimelineDay[];
  /** The time all the files were observed together, in the axis' time, when there is one. */
  commonFrom: string;
  commonTo: string;
  commonSeconds: number;
}

const STEPS = [60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200];
const DAY = 86400;

function seconds(iso: string): number {
  const [d, t] = iso.split("T");
  const [y, mo, da] = d.split("-").map(Number);
  const [h, mi, s] = t.split(":").map(Number);
  return Date.UTC(y, mo - 1, da, h, mi, s) / 1000;
}

const dayOf = (at: number) => clock(new Date(at * 1000).toISOString().slice(0, 19)).date;
const hhmm = (at: number) => {
  const c = clock(new Date(at * 1000).toISOString().slice(0, 19));
  return c.time.slice(0, 5);
};

/**
 * The files as bars on a time axis, one axis for each day they touch (a file that crosses midnight is on both). `utc` moves
 * every time back by the leap seconds of the file, so the axis reads in UTC instead of GPS time.
 */
export function timeline(files: TpsFile[], utc: boolean): Timeline {
  const ok = files.filter((f) => f.state === "ok" && f.obs?.hasTimes);
  const spans = ok.map((f) => {
    const shift = utc ? -f.obs!.leap : 0;
    return { file: f, from: seconds(f.obs!.first) + shift, to: seconds(f.obs!.last) + shift };
  });
  const out: Timeline = { days: [], commonFrom: "", commonTo: "", commonSeconds: 0 };
  if (spans.length === 0) return out;
  const cFrom = Math.max(...spans.map((s) => s.from));
  const cTo = Math.min(...spans.map((s) => s.to));
  const hasCommon = spans.length > 1 && cTo > cFrom;
  if (hasCommon) {
    out.commonFrom = `${dayOf(cFrom)} ${hhmm(cFrom)}`;
    out.commonTo = `${dayOf(cTo)} ${hhmm(cTo)}`;
    out.commonSeconds = cTo - cFrom;
  }
  const firstDay = Math.floor(Math.min(...spans.map((s) => s.from)) / DAY);
  const lastDay = Math.floor(Math.max(...spans.map((s) => s.to)) / DAY);
  for (let day = firstDay; day <= lastDay; day++) {
    const dayFrom = day * DAY;
    const dayTo = dayFrom + DAY;
    const here = spans.filter((s) => s.to >= dayFrom && s.from < dayTo);
    if (here.length === 0) continue;
    const lo = Math.max(dayFrom, Math.min(...here.map((s) => s.from)));
    const hi = Math.min(dayTo, Math.max(...here.map((s) => s.to)));
    // The axis: a little room around the bars, on round times.
    const step = STEPS.find((s) => (hi - lo) / s <= 8) ?? 43200;
    const pad = Math.max(step / 2, (hi - lo) * 0.05);
    const axisFrom = Math.max(dayFrom, Math.floor((lo - pad) / step) * step);
    const axisTo = Math.min(dayTo, Math.ceil((hi + pad) / step) * step);
    const length = Math.max(1, axisTo - axisFrom);
    const pct = (at: number) => ((at - axisFrom) / length) * 100;
    const ticks: TimelineDay["ticks"] = [];
    for (let at = Math.ceil(axisFrom / step) * step; at <= axisTo; at += step) ticks.push({ left: pct(at), label: hhmm(at) });
    out.days.push({
      date: dayOf(dayFrom),
      ticks,
      bars: here.map((s) => {
        const a = Math.max(s.from, axisFrom);
        const b = Math.min(s.to, axisTo);
        return {
          path: s.file.path,
          label: s.file.name.trim() || baseName(s.file.path),
          left: pct(a),
          width: Math.max(0.6, pct(b) - pct(a)),
          from: hhmm(s.from),
          to: hhmm(s.to),
          openStart: s.from < dayFrom,
          openEnd: s.to >= dayTo,
        };
      }),
      common: hasCommon && cTo > dayFrom && cFrom < dayTo
        ? { left: pct(Math.max(cFrom, axisFrom)), width: Math.max(0.6, pct(Math.min(cTo, axisTo)) - pct(Math.max(cFrom, axisFrom))) }
        : null,
    });
  }
  return out;
}
