// Xpace inside the island: where the disk space goes. The model is core/xpace.ts, the analysis the classic Lazo's.
// A folder is chosen and analysed; the results are shown as a map (the folder's contents as rectangles you can go
// into), a list, the biggest files, the types, and what can be cleaned up.

import { h, clear } from "./dom";
import { Bridge } from "../core/bridge";
import { Xpace } from "../core/lazo-tools";
import { squarify } from "../core/treemap";
import { State } from "../core/state";
import { formatBytes, type XNode, type XpaceTab, type XTip } from "../core/xpace";
import { t } from "../i18n/i18n";
import type { ViewHost } from "./views";

const MAP_W = 840;
const MAP_H = 236;
const TABS: { id: XpaceTab; label: () => string }[] = [
  { id: "map", label: () => t("Map") },
  { id: "list", label: () => t("List") },
  { id: "large", label: () => t("Large files") },
  { id: "types", label: () => t("Types") },
  { id: "tips", label: () => t("Clean up") },
];

const count = (n: number) => n.toLocaleString();

function empty(text: string): HTMLElement {
  return h("div", { class: "xp-empty", text });
}

function reveal(path: string) {
  void Bridge.revealPath(path);
}

export function buildXpace(): ViewHost {
  const input = h("input", { type: "text", class: "xp-path", spellcheck: "false", placeholder: t("Choose a folder or type its path") }) as HTMLInputElement;
  const go = h("button", { class: "btn primary xp-go" }, h("span"));
  const chips = h("div", { class: "xp-chips" });
  const status = h("div", { class: "xp-status" });
  const tabs = h("div", { class: "xp-tabs" });
  const body = h("div", { class: "xp-body" });
  const note = h("div", { class: "xp-note" });
  const card = h("div", { class: "card xp-card" }, h("div", { class: "xp-top" }, input, go), chips, status, tabs, body, note);
  const el = h("div", { class: "view" }, card);

  let chipsLoaded = false;
  let drawn = "";
  /** The tip waiting for a second click before it is deleted for good. */
  let confirming = "";

  function start() {
    if (Xpace.running) Xpace.cancel();
    else Xpace.scan(input.value);
  }
  go.addEventListener("click", start);
  input.addEventListener("keydown", (e) => {
    if ((e as KeyboardEvent).key === "Enter") {
      e.preventDefault();
      start();
    }
    e.stopPropagation(); // Escape closes the island, not the field
  });

  function loadChips() {
    if (chipsLoaded) return;
    chipsLoaded = true;
    void Bridge.knownFolders().then((folders) => {
      clear(chips);
      for (const f of folders ?? []) {
        const chip = h("button", { class: "xp-chip", title: f.path, text: f.label });
        chip.addEventListener("click", () => {
          input.value = f.path;
          Xpace.scan(f.path);
        });
        chips.append(chip);
      }
    });
  }

  // ── Tabs ────────────────────────────────────────────────────────────────────

  function drawMap(): HTMLElement {
    const current = Xpace.current;
    if (!current) return empty("…");
    const wrap = h("div", { class: "xp-map-wrap" });
    // Where the map is: the folders from the top, each one a way back.
    const crumbs = h("div", { class: "xp-crumbs" });
    Xpace.trail.forEach((id, i) => {
      const node = Xpace.nodes.get(id)?.node;
      const last = i === Xpace.trail.length - 1;
      const label = i === 0 ? (Xpace.summary?.path ?? node?.name ?? "") : (node?.name ?? "…");
      const crumb = h("button", { class: last ? "xp-crumb on" : "xp-crumb", text: label });
      if (!last) crumb.addEventListener("click", () => Xpace.up(i));
      crumbs.append(crumb);
    });
    wrap.append(crumbs);
    const map = h("div", { class: "xp-map" });
    type Item = { size: number; node: XNode | null; label: string };
    const items: Item[] = current.children.map((c) => ({ size: c.size, node: c, label: c.name }));
    if (current.node.ownSize > 0) items.push({ size: current.node.ownSize, node: null, label: t("Files in this folder") });
    if (current.moreSize > 0) items.push({ size: current.moreSize, node: null, label: t("{count} more", { count: count(current.more) }) });
    squarify(items, { x: 0, y: 0, w: MAP_W, h: MAP_H }).forEach(({ item, rect }, i) => {
      const cell = h("div", { class: item.node ? "xp-rect" : "xp-rect own" });
      cell.style.left = `${(rect.x / MAP_W) * 100}%`;
      cell.style.top = `${(rect.y / MAP_H) * 100}%`;
      cell.style.width = `${(rect.w / MAP_W) * 100}%`;
      cell.style.height = `${(rect.h / MAP_H) * 100}%`;
      cell.style.setProperty("--shade", String(14 + (i % 4) * 3));
      if (rect.w > 76 && rect.h > 34) cell.append(h("b", { text: item.label }), h("span", { text: formatBytes(item.size) }));
      else cell.title = `${item.label} ${formatBytes(item.size)}`;
      if (item.node && item.node.folders > 0) {
        cell.classList.add("go");
        const id = item.node.id;
        cell.addEventListener("click", () => Xpace.enter(id));
      }
      map.append(cell);
    });
    wrap.append(map);
    return wrap;
  }

  function drawList(): HTMLElement {
    const root = Xpace.summary ? Xpace.nodes.get(Xpace.summary.rootId) : undefined;
    if (!root) return empty("…");
    const list = h("div", { class: "xp-list" });
    let rows = 0;
    const add = (id: number, depth: number, parentSize: number) => {
      const entry = Xpace.nodes.get(id);
      if (!entry || rows > 600) return;
      for (const c of entry.children) {
        if (++rows > 600) return;
        const open = Xpace.expanded.has(c.id);
        const has = c.folders > 0 || c.ownFiles > 0;
        const caret = h("button", { class: "xp-caret", text: has ? (open ? "▾" : "▸") : "" });
        if (has) caret.addEventListener("click", () => Xpace.toggle(c.id));
        const share = parentSize > 0 ? Math.max(1, Math.round((c.size / parentSize) * 100)) : 0;
        const bar = h("i", { class: "xp-share" });
        bar.style.width = `${share}%`;
        const row = h("div", { class: "xp-row", title: c.path },
          h("span", { class: "xp-indent", style: `width:${depth * 14}px` }), caret,
          h("span", { class: "xp-name", text: c.name }), h("span", { class: "xp-bar" }, bar),
          h("span", { class: "xp-size", text: formatBytes(c.size) }),
          h("span", { class: "xp-count", text: t("{count} files", { count: count(c.files) }) }));
        row.addEventListener("dblclick", () => reveal(c.path));
        list.append(row);
        if (open) add(c.id, depth + 1, c.size);
      }
      if (entry.more > 0) list.append(h("div", { class: "xp-row dim" }, h("span", { class: "xp-name", text: t("{count} more", { count: count(entry.more) }) })));
    };
    add(root.node.id, 0, root.node.size);
    return list;
  }

  function drawLarge(): HTMLElement {
    if (!Xpace.largest) return empty("…");
    const list = h("div", { class: "xp-list" });
    for (const f of Xpace.largest) {
      const show = h("button", { class: "xp-link", text: t("Show in folder") });
      show.addEventListener("click", () => reveal(f.path));
      list.append(h("div", { class: "xp-row", title: f.path },
        h("span", { class: "xp-name", text: f.name }), h("span", { class: "xp-folder", text: f.folder }),
        h("span", { class: "xp-size", text: formatBytes(f.size) }), show));
    }
    return list;
  }

  function drawTypes(): HTMLElement {
    if (!Xpace.types) return empty("…");
    const biggest = Xpace.types[0]?.size ?? 1;
    const list = h("div", { class: "xp-list" });
    for (const ty of Xpace.types) {
      const bar = h("i", { class: "xp-share" });
      bar.style.width = `${Math.max(1, Math.round((ty.size / biggest) * 100))}%`;
      list.append(h("div", { class: "xp-row" },
        h("span", { class: "xp-name", text: ty.extension || t("No extension") }), h("span", { class: "xp-bar wide" }, bar),
        h("span", { class: "xp-size", text: formatBytes(ty.size) }),
        h("span", { class: "xp-count", text: t("{count} files", { count: count(ty.count) }) })));
    }
    return list;
  }

  function tipRow(tip: XTip): HTMLElement {
    const actions = h("div", { class: "xp-actions" });
    if (tip.infoOnly) {
      actions.append(h("button", { class: "xp-link", text: t("Show in folder"), onclick: () => reveal(tip.path) }));
    } else if (confirming === tip.path) {
      actions.append(
        h("span", { class: "xp-sure", text: t("Delete forever?") }),
        h("button", { class: "btn secondary xp-btn", onclick: () => { confirming = ""; Xpace.remove(tip.path, tip.isFolder, true, tip.contentsOnly); } }, h("span", { text: t("Yes") })),
        h("button", { class: "btn secondary xp-btn", onclick: () => { confirming = ""; State.notify(); } }, h("span", { text: t("Cancel") })),
      );
    } else {
      actions.append(
        h("button", { class: "btn secondary xp-btn", onclick: () => Xpace.remove(tip.path, tip.isFolder, false, tip.contentsOnly) }, h("span", { text: t("Recycle bin") })),
        h("button", { class: "xp-link", text: t("Delete forever"), onclick: () => { confirming = tip.path; State.notify(); } }),
      );
    }
    return h("div", { class: "xp-tip" },
      h("div", { class: "xp-tip-text" },
        h("div", { class: "xp-tip-title" }, h("b", { text: tip.title }), h("em", { class: tip.safe ? "safe" : "review", text: tip.safe ? t("Safe") : t("Review first") })),
        h("div", { class: "xp-why", text: tip.why }), h("div", { class: "xp-why path", text: tip.path })),
      h("span", { class: "xp-size", text: formatBytes(tip.size) }), actions);
  }

  function drawTips(): HTMLElement {
    if (Xpace.loadingTips || !Xpace.tips) return empty(t("Looking for what can go…"));
    if (Xpace.tips.length === 0) return empty(t("Nothing worth removing was found."));
    const list = h("div", { class: "xp-list" });
    for (const tip of Xpace.tips) list.append(tipRow(tip));
    return list;
  }

  return {
    el,
    sync() {
      loadChips();
      const working = Xpace.running;
      go.firstElementChild!.textContent = working ? t("Cancel") : t("Analyze");
      go.classList.toggle("secondary", working);
      go.classList.toggle("primary", !working);
      input.disabled = working;
      if (document.activeElement !== input && input.value !== Xpace.path && Xpace.path) input.value = Xpace.path;

      const live = t("Analyzing… {count} files · {size}", { count: count(Xpace.progress.files), size: formatBytes(Xpace.progress.bytes) });
      if (Xpace.error) status.textContent = Xpace.error;
      else if (working) status.textContent = live;
      else if (Xpace.summary) {
        const s = Xpace.summary;
        status.textContent = t("Done in {seconds} s: {count} files, {size}", { seconds: (s.elapsedMs / 1000).toFixed(1), count: count(s.files), size: formatBytes(s.bytes) });
      } else status.textContent = "";
      status.classList.toggle("error", !!Xpace.error);
      note.textContent = Xpace.notice;

      // The tabs and the body are drawn again only when what they show changed: a click must land on what was seen.
      const key = JSON.stringify([
        Xpace.tab, Xpace.trail, [...Xpace.expanded], [...Xpace.nodes.keys()], Xpace.largest?.length, Xpace.types?.length,
        Xpace.tips?.map((x) => x.path), Xpace.loadingTips, Xpace.summary?.rootId ?? -1, Xpace.running ? 1 : 0, confirming,
      ]);
      if (key === drawn) return;
      drawn = key;
      clear(tabs);
      for (const tab of TABS) {
        const b = h("button", { class: tab.id === Xpace.tab ? "xp-tab on" : "xp-tab", text: tab.label() });
        b.addEventListener("click", () => Xpace.setTab(tab.id));
        tabs.append(b);
      }
      clear(body);
      if (Xpace.running) body.append(empty(live));
      else if (!Xpace.summary && Xpace.tab !== "tips") body.append(empty(t("Nothing to show yet. Choose a folder and analyze it.")));
      else {
        body.append(Xpace.tab === "map" ? drawMap() : Xpace.tab === "list" ? drawList() : Xpace.tab === "large" ? drawLarge()
          : Xpace.tab === "types" ? drawTypes() : drawTips());
      }
    },
  };
}
