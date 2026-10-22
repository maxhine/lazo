// Xpace in the island: where the disk space goes. The analysis is the classic Lazo's (engine/XpaceTool.cs over
// XpaceScanner and XpaceActions); this keeps what the screen shows and asks for what it needs.

import type { ToolMessage } from "../lazo/tools";

export type XpaceTab = "map" | "list" | "large" | "types" | "tips";

export interface XNode {
  id: number;
  name: string;
  path: string;
  size: number;
  files: number;
  folders: number;
  /** What sits directly in the folder, without its subfolders. */
  ownSize: number;
  ownFiles: number;
  denied: boolean;
  modified: string;
}

export interface XChildren {
  node: XNode;
  children: XNode[];
  more: number;
  moreSize: number;
}

export interface XSummary {
  path: string;
  rootId: number;
  files: number;
  bytes: number;
  folders: number;
  denied: number;
  elapsedMs: number;
}

export interface XFile {
  name: string;
  folder: string;
  path: string;
  size: number;
  modified: string;
}

export interface XType {
  extension: string;
  size: number;
  count: number;
}

export interface XTip {
  path: string;
  title: string;
  why: string;
  safe: boolean;
  size: number;
  isFolder: boolean;
  infoOnly: boolean;
  contentsOnly: boolean;
}

export type Post = (command: { cmd: string } & Record<string, unknown>) => Promise<void>;

export class XpaceModel {
  path = "";
  running = false;
  progress = { files: 0, bytes: 0, folders: 0, current: "" };
  summary: XSummary | null = null;
  error = "";
  tab: XpaceTab = "map";
  /** Folders whose contents have arrived, by id. */
  nodes = new Map<number, XChildren>();
  /** The folder the map shows, as the ids from the top down. */
  trail: number[] = [];
  expanded = new Set<number>();
  largest: XFile[] | null = null;
  types: XType[] | null = null;
  tips: XTip[] | null = null;
  loadingTips = false;
  notice = "";

  private asked = new Set<number>();
  /** The tabs whose data was asked for and has not come yet. */
  private asking = new Set<string>();
  private post: Post;
  private changed: () => void;

  constructor(post: Post, changed: () => void) {
    this.post = post;
    this.changed = changed;
  }

  private send(command: { cmd: string } & Record<string, unknown>) {
    this.post(command).catch((err) => {
      this.running = false;
      this.loadingTips = false;
      this.error = String(err).replace(/^Error:\s*/, "");
      this.changed();
    });
  }

  // ── Orders from the screen ──────────────────────────────────────────────────

  scan(path: string) {
    const clean = path.trim().replace(/^"(.*)"$/, "$1");
    if (this.running) return;
    if (!clean) {
      this.error = "";
      this.changed();
      return;
    }
    this.path = clean;
    this.error = "";
    this.notice = "";
    this.summary = null;
    this.nodes.clear();
    this.asked.clear();
    this.asking.clear();
    this.expanded.clear();
    this.trail = [];
    this.largest = this.types = this.tips = null;
    this.progress = { files: 0, bytes: 0, folders: 0, current: "" };
    this.running = true;
    this.changed();
    this.send({ cmd: "xpace.scan", path: clean });
  }

  cancel() {
    if (this.running) this.send({ cmd: "xpace.cancel" });
  }

  ensureChildren(id: number) {
    if (this.nodes.has(id) || this.asked.has(id)) return;
    this.asked.add(id);
    this.send({ cmd: "xpace.children", id });
  }

  /** The map goes into a folder. */
  enter(id: number) {
    this.trail = [...this.trail, id];
    this.ensureChildren(id);
    this.changed();
  }

  /** The map goes back up to the folder at `index` of the trail. */
  up(index: number) {
    this.trail = this.trail.slice(0, index + 1);
    this.changed();
  }

  toggle(id: number) {
    if (this.expanded.has(id)) this.expanded.delete(id);
    else {
      this.expanded.add(id);
      this.ensureChildren(id);
    }
    this.changed();
  }

  setTab(tab: XpaceTab) {
    this.tab = tab;
    if (this.summary) this.loadTab();
    else if (tab === "tips") this.loadTips();
    this.changed();
  }

  /** What the tab needs, asked for once. */
  private loadTab() {
    const tab = this.tab;
    if (tab === "tips") {
      if (!this.tips) this.loadTips();
      return;
    }
    if (tab !== "large" && tab !== "types") return;
    if ((tab === "large" && this.largest) || (tab === "types" && this.types) || this.asking.has(tab)) return;
    this.asking.add(tab);
    this.send(tab === "large" ? { cmd: "xpace.largest", limit: 100 } : { cmd: "xpace.types", limit: 40 });
  }

  loadTips() {
    if (this.loadingTips) return;
    this.loadingTips = true;
    this.send({ cmd: "xpace.tips" });
  }

  remove(path: string, isFolder: boolean, permanent: boolean, contentsOnly: boolean) {
    this.notice = "";
    this.send({ cmd: "xpace.remove", path, isFolder, permanent, contentsOnly });
  }

  /** What is at the folder the map shows. */
  get current(): XChildren | undefined {
    const id = this.trail[this.trail.length - 1];
    return id == null ? undefined : this.nodes.get(id);
  }

  // ── What the program says ───────────────────────────────────────────────────

  handle(m: ToolMessage) {
    switch (m.type) {
      case "xpace.started":
        this.running = true;
        break;
      case "xpace.progress":
        this.progress = { files: m.files, bytes: m.bytes, folders: m.folders, current: m.current ?? "" };
        break;
      case "xpace.done":
        this.running = false;
        this.summary = {
          path: m.path, rootId: m.rootId, files: m.files, bytes: m.bytes, folders: m.folders, denied: m.denied, elapsedMs: m.elapsedMs,
        };
        this.nodes.clear();
        this.asked.clear();
        this.asking.clear();
        this.trail = [m.rootId];
        this.expanded = new Set([m.rootId]);
        this.ensureChildren(m.rootId);
        this.loadTab();
        break;
      case "xpace.error":
        this.running = false;
        this.loadingTips = false;
        this.error = m.message;
        break;
      case "xpace.children":
        this.nodes.set(m.node.id, { node: m.node, children: m.children, more: m.more, moreSize: m.moreSize });
        break;
      case "xpace.largest":
        this.largest = m.files;
        this.asking.delete("large");
        break;
      case "xpace.types":
        this.types = m.types;
        this.asking.delete("types");
        break;
      case "xpace.tips":
        this.tips = m.tips;
        this.loadingTips = false;
        break;
      case "xpace.removed":
        this.removed(m.path, !!m.gone, m.problem ?? "");
        break;
      default:
        return;
    }
    this.changed();
  }

  /** A removal answered: what was removed leaves the lists, and what sizes changed is asked again. */
  private removed(path: string, gone: boolean, problem: string) {
    this.notice = problem;
    if (!gone) return;
    const same = (a: string) => a.toLowerCase().replace(/\+$/, "") === path.toLowerCase().replace(/\+$/, "");
    const inside = (a: string) => a.toLowerCase().startsWith(path.toLowerCase().replace(/\+$/, "") + "\\");
    if (this.tips) this.tips = this.tips.filter((t) => !same(t.path) && !inside(t.path));
    if (this.largest) this.largest = this.largest.filter((f) => !same(f.path) && !inside(f.path));
    // Sizes above it changed: the folders on screen are asked again.
    const stale = [...this.nodes.keys()];
    this.nodes.clear();
    this.asked.clear();
    for (const id of new Set([...this.trail, ...this.expanded, ...stale.filter((id) => this.expanded.has(id))])) this.ensureChildren(id);
  }
}

/** 1.5 GB, 320 MB, 12 KB: sizes as people read them. */
export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${Math.round(bytes)} B`;
  const units = ["KB", "MB", "GB", "TB"];
  let value = bytes / 1024;
  let i = 0;
  while (value >= 1024 && i < units.length - 1) {
    value /= 1024;
    i++;
  }
  return `${value >= 100 ? Math.round(value) : value >= 10 ? value.toFixed(1) : value.toFixed(2)} ${units[i]}`.replace(".00", "");
}
