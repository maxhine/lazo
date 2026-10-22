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
