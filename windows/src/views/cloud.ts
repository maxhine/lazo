// Point clouds inside the island: LAS, LAZ or E57 to ReCap, LAS, XYZ, PTS or CSV, with a preview of the reduction.
// The model is core/cloud.ts, the conversion the classic Lazo's. Files are dropped on the screen or picked with the
// button; the island grows to this view's size.

import { h, clear } from "./dom";
import { Bridge } from "../core/bridge";
import { Cloud } from "../core/lazo-tools";
import { CLOUD_FORMATS, formatDuration, type CloudFormat, type CsChoice } from "../core/cloud";
import { formatBytes } from "../core/xpace";
import { t } from "../i18n/i18n";
import type { ViewHost } from "./views";

const FORMAT_NAMES: Record<CloudFormat, () => string> = {
  rcp: () => t("Project .rcp"),
  rcs: () => t("Single cloud .rcs"),
  las: () => "LAS",
  xyz: () => "XYZ",
  pts: () => "PTS",
  csv: () => "CSV",
};

function formatHelp(format: CloudFormat): string {
  if (format === "rcp") return t("A .rcp project to open and organize in ReCap. Big clouds are converted in parts at once (one scan per part).");
  if (format === "rcs") return t("Everything in one .rcs, for Revit, AutoCAD or Navisworks. With point reduction it is converted in parts and joined at the end.");
  return t("Lazo writes this format itself (ReCap only makes .rcp and .rcs); it works for LAS, LAZ and E57.");
}

function chip(label: string, on: boolean, disabled: boolean, click: () => void): HTMLElement {
  const b = h("button", { class: on ? "cl-chip on" : "cl-chip", text: label });
  if (disabled) (b as HTMLButtonElement).disabled = true;
  else b.addEventListener("click", click);
  return b;
}

function field(label: string): HTMLElement {
  return h("div", { class: "cl-label", text: label });
}

function windowText(meters: number): string {
  return meters >= 1 ? `${meters.toFixed(1)} m` : `${Math.round(meters * 100)} cm`;
}

export function buildCloud(): ViewHost {
  // Pieces that keep their state (a field being typed in is never rebuilt).
  const name = h("input", { type: "text", class: "cl-input", spellcheck: "false" }) as HTMLInputElement;
  const decimation = h("input", { type: "text", class: "cl-input cl-mm", spellcheck: "false", inputmode: "decimal" }) as HTMLInputElement;
  const query = h("input", { type: "text", class: "cl-input", spellcheck: "false", placeholder: t("Search by name or EPSG, for example Bogota, UTM 18N or 3116") }) as HTMLInputElement;
  // Escape closes the island, not the field.
  for (const input of [name, decimation, query]) input.addEventListener("keydown", (e) => e.stopPropagation());
  name.addEventListener("input", () => {
    Cloud.name = name.value;
  });
  decimation.addEventListener("input", () => Cloud.setDecimation(decimation.value));
  query.addEventListener("input", () => Cloud.search(query.value.trim()));

  const missing = h("div", { class: "cl-missing" });
  const drop = h("button", { class: "cl-drop" });
  const files = h("div", { class: "cl-files" });
  const formats = h("div", { class: "cl-chips" });
  const help = h("div", { class: "cl-help" });
  const mergeBox = h("input", { type: "checkbox", id: "cl-merge" }) as HTMLInputElement;
  const merge = h("label", { class: "cl-check", for: "cl-merge" }, mergeBox, h("span", { text: t("Join the parts at the end into a single scan") }));
  mergeBox.addEventListener("change", () => {
    Cloud.merge = mergeBox.checked;
  });
  const folder = h("div", { class: "cl-folder" });
  const folderBtn = h("button", { class: "cl-link", text: t("Change…") });
  const units = h("div", { class: "cl-chips" });
  const unitsHelp = h("div", { class: "cl-help", text: t("Only matters for LAS and LAZ; E57 already stores its units.") });
  const previewHead = h("div", { class: "cl-row" });
  const previewPair = h("div", { class: "cl-pair" });
  const previewCaption = h("div", { class: "cl-help" });
  const zones = h("div", { class: "cl-chips" });
  const crs = h("div", { class: "cl-crs" });
  const footer = h("div", { class: "cl-footer" });

  drop.addEventListener("click", () => Cloud.pickFiles());
  folderBtn.addEventListener("click", () => Cloud.pickFolder());

  const left = h("div", { class: "cl-col" },
    drop, files,
    field(t("Result")), formats, help, merge,
    field(t("Name")), name,
    field(t("Save to")), h("div", { class: "cl-row" }, folder, folderBtn));
  const right = h("div", { class: "cl-col" },
    field(t("File units")), units, unitsHelp,
    field(t("Reduce points (optional)")), h("div", { class: "cl-row" }, decimation, h("span", { class: "cl-help", text: t("mm between points. Empty: every point.") })),
    previewHead, previewPair, previewCaption, zones,
    crs);
  const body = h("div", { class: "cl-body" }, left, right);
  const el = h("div", { class: "view" }, h("div", { class: "card cl-card" }, missing, body, footer));

  /** Parts that are drawn again only when what they show changed. */
  const drawn: Record<string, string> = {};
  const redraw = (key: string, signature: unknown, draw: () => void) => {
    const s = JSON.stringify(signature);
    if (drawn[key] === s) return;
    drawn[key] = s;
    draw();
  };

  let lastPicker: string | null = null;
  function drawCrs(running: boolean) {
    const refocus = document.activeElement === query;
    const opened = Cloud.picker !== lastPicker;
    lastPicker = Cloud.picker;
    if (opened) query.value = "";
    clear(crs);
    crs.append(field(t("Coordinate system")));
    // Source: what the file declares, or what was chosen.
    const sourceText = Cloud.source ? Cloud.source.label : Cloud.declared
      ? `${Cloud.declared} (${t("not known to Lazo")})`
      : Cloud.files.length ? t("The file does not say which one; choose it with Change.") : "—";
    const change = h("button", { class: "cl-link", text: Cloud.picker === "source" ? t("Close") : t("Change") });
    if (running) (change as HTMLButtonElement).disabled = true;
    else change.addEventListener("click", () => Cloud.openPicker("source"));
    crs.append(h("div", { class: "cl-row cl-source" },
      h("div", { class: "cl-grow" }, h("div", { class: "cl-dim", text: t("Source") }), h("div", { class: Cloud.source ? "cl-strong" : "cl-strong dim", text: sourceText }),
        Cloud.detectNote ? h("div", { class: "cl-dim", text: Cloud.detectNote }) : null),
      change));
    if (Cloud.picker === "source") crs.append(pickerBox());

    crs.append(h("div", { class: "cl-dim cl-gap", text: t("Output") }));
    const same = h("button", { class: Cloud.target ? "cl-opt" : "cl-opt on" }, h("b", { text: t("Same as the source") }), h("span", { text: t("Not reprojected") }));
    if (!running) same.addEventListener("click", () => Cloud.chooseTarget(null));
    crs.append(same);
    const featured = (Cloud.status?.projections ?? []).filter((p) => p.featured);
    for (const p of featured) {
      const choice: CsChoice = { ref: p.ref, label: p.label, epsg: p.epsg, detail: "", lazo: true, egm2008: p.egm2008 };
      const how = p.recap ? t("EPSG {code}", { code: p.epsg }) : t("Lazo computes it (ReCap doesn't have it) · LAS/LAZ only");
      const opt = h("button", { class: Cloud.target?.ref === p.ref ? "cl-opt on" : "cl-opt" }, h("b", { text: p.label }), h("span", { text: how }));
      if (!running) opt.addEventListener("click", () => Cloud.chooseTarget(choice));
      crs.append(opt);
    }
    const other = Cloud.target && !featured.some((p) => p.ref === Cloud.target!.ref);
    const otherBtn = h("button", { class: other ? "cl-opt on" : "cl-opt" },
      h("b", { text: other ? Cloud.target!.label : t("Other system…") }), h("span", { text: other ? t("Chosen from the list") : t("Search by name or EPSG") }));
    if (!running) otherBtn.addEventListener("click", () => Cloud.openPicker("target"));
    crs.append(otherBtn);
    if (Cloud.picker === "target") crs.append(pickerBox());

    if (Cloud.target?.egm2008) {
      const box = h("input", { type: "checkbox", id: "cl-ell" }) as HTMLInputElement;
      box.checked = Cloud.ellipsoidal && !!Cloud.status?.geoid;
      box.disabled = running || !Cloud.status?.geoid;
      box.addEventListener("change", () => Cloud.setEllipsoidal(box.checked));
      crs.append(
        h("label", { class: "cl-check", for: "cl-ell" }, box, h("span", { text: t("Heights are ellipsoidal: convert them to EGM2008") })),
        h("div", { class: "cl-help", text: Cloud.status?.geoid
          ? t("If they are already orthometric (EGM2008), leave it unchecked: heights do not change.")
          : t("Converting ellipsoidal heights to EGM2008 needs egm08_25.gtx (for example in Documents\\Bentley\\iTwin Capture Modeler).") }));
    }
    if (Cloud.picker && (refocus || opened)) query.focus();
  }

  function pickerBox(): HTMLElement {
    const list = h("div", { class: "cl-results" });
    if (Cloud.results.length === 0) {
      list.append(h("div", { class: "cl-dim", text: !Cloud.searched ? "…" : Cloud.status?.library ? t("No results.") : t("The Autodesk coordinate library was not found.") }));
    }
    for (const r of Cloud.results) {
      const detail = (r.epsg > 0 ? `EPSG ${r.epsg} · ` : "") + (r.lazo ? (r.recapKey ? t("Colombia") : t("Lazo computes it")) : r.detail);
      const opt = h("button", { class: "cl-opt" }, h("b", { text: r.label }), h("span", { text: detail }));
      opt.addEventListener("click", () => Cloud.choose(r));
      list.append(opt);
    }
    return h("div", { class: "cl-picker" }, query, list);
  }

  function drawFooter() {
    clear(footer);
    const result = Cloud.result;
    const text = h("div", { class: result && !result.ok ? "cl-status error" : "cl-status" });
    if (Cloud.running) {
      const bar = h("i", { class: "cl-progress-fill" });
      bar.style.width = `${Math.round(Cloud.progress.value * 100)}%`;
      text.append(h("div", { class: "cl-progress" }, bar), h("span", { text: `${Cloud.progress.text} ${Math.round(Cloud.progress.value * 100)} %` }));
    } else if (result?.ok) {
      text.textContent = t("Done in {time}: {file}", { time: formatDuration(result.elapsedMs), file: result.output.split(/[\\/]/).pop() ?? result.output });
    } else if (result) {
      text.textContent = result.message;
    } else if (Cloud.notice === "unsupported") {
      text.textContent = t("Only .las, .laz or .e57 files can be converted");
    } else if (Cloud.notice) {
      text.textContent = Cloud.notice;
    }
    footer.append(text);
    const buttons = h("div", { class: "cl-buttons" });
    if (Cloud.running) {
      buttons.append(h("button", { class: "btn secondary", onclick: () => Cloud.cancel() }, h("span", { text: t("Cancel") })));
    } else {
      if (result?.ok) {
        buttons.append(h("button", { class: "btn secondary", onclick: () => void Bridge.revealPath(result.output) }, h("span", { text: t("Show in folder") })));
        if (/\.(rcp|rcs)$/i.test(result.output)) {
          buttons.append(h("button", { class: "btn secondary", onclick: () => Cloud.open(result.output) }, h("span", { text: t("Open in ReCap") })));
        }
      }
      const go = h("button", { class: "btn primary" }, h("span", { text: t("Convert") }));
      if (Cloud.files.length === 0) (go as HTMLButtonElement).disabled = true;
      else go.addEventListener("click", () => Cloud.convert());
      buttons.append(go);
    }
    footer.append(buttons);
  }

  function drawPreview() {
    clear(previewPair);
    const p = Cloud.preview;
    const frame = (label: string, data: string | null) => {
      const box = h("div", { class: "cl-shot" });
      if (data) box.append(h("img", { src: `data:image/png;base64,${data}`, alt: label }));
      box.append(h("span", { class: "cl-tag", text: label }));
      return box;
    };
    previewPair.append(frame(t("Original"), p?.original ?? null), frame(t("Reduced"), p?.reducedShown ? p.reduced : null));
    if (p) {
      const size = windowText(p.windowMeters);
      previewCaption.textContent = p.reducedShown
        ? t("Window of {size}: {before} → {after} points ({percent}% remain). Approximate: ReCap may pick other points.", {
            size, before: p.before.toLocaleString(), after: p.after.toLocaleString(), percent: Math.round((100 * p.after) / Math.max(1, p.before)),
          })
        : t("Window of {size} with {before} points. Type a distance in mm to see how it would look.", { size, before: p.before.toLocaleString() });
      if (p.e57) previewCaption.textContent += " " + t("E57: the start of the file is shown.");
    } else {
      previewCaption.textContent = Cloud.previewBusy ? t("Reading a sample of the cloud…")
        : Cloud.previewMessage || (Cloud.files.length === 0 ? t("Choose a cloud to see the reduction.") : "");
    }
  }

  return {
    el,
    sync() {
      Cloud.init();
      const running = Cloud.running;
      const status = Cloud.status;
      const unavailable = !!status && !status.deCap;
      missing.textContent = unavailable
        ? t("Needs Autodesk ReCap installed on this computer: ReCap's format is only written by Autodesk's tools, and Lazo uses its converter with your license.")
        : "";
      missing.style.display = unavailable ? "" : "none";
      body.style.display = unavailable ? "none" : "";
      footer.style.display = unavailable ? "none" : "";

      if (document.activeElement !== name && name.value !== Cloud.name) name.value = Cloud.name;
      if (document.activeElement !== decimation && decimation.value !== Cloud.decimation) decimation.value = Cloud.decimation;
      for (const input of [name, decimation]) input.disabled = running;
      mergeBox.checked = Cloud.merge;
      mergeBox.disabled = running;
      merge.style.display = Cloud.format === "rcp" ? "" : "none";
      help.textContent = formatHelp(Cloud.format);
      folder.textContent = Cloud.folder || t("The folder of the first file");
      folder.classList.toggle("dim", !Cloud.folder);
      (folderBtn as HTMLButtonElement).disabled = running;
      drop.classList.toggle("has", Cloud.files.length > 0);
      drop.textContent = Cloud.files.length ? t("Add more clouds") : t("Drop LAS, LAZ or E57 clouds here, or click to choose them");
      (drop as HTMLButtonElement).disabled = running;

      redraw("fileList", [Cloud.files, running], () => {
        clear(files);
        for (const f of Cloud.files) {
          const remove = h("button", { class: "cl-x", title: t("Remove"), text: "×" });
          if (running) (remove as HTMLButtonElement).disabled = true;
          else remove.addEventListener("click", () => Cloud.removeFile(f.path));
          files.append(h("div", { class: "cl-file", title: f.path }, h("span", { class: "cl-fname", text: f.path.split(/[\\/]/).pop() ?? f.path }),
            h("span", { class: "cl-dim", text: f.size ? formatBytes(f.size) : "" }), remove));
        }
      });
      redraw("formats", [Cloud.format, running], () => {
        clear(formats);
        for (const f of CLOUD_FORMATS) formats.append(chip(FORMAT_NAMES[f](), Cloud.format === f, running, () => Cloud.setFormat(f)));
      });
      redraw("units", [Cloud.unit, running, status?.units], () => {
        clear(units);
        (status?.units ?? ["mm", "cm", "m", "in", "ft", "ft EE. UU."]).forEach((u, i) => units.append(chip(u, Cloud.unit === i, running, () => Cloud.setUnit(i))));
      });
      redraw("previewHead", [Cloud.elevation], () => {
        clear(previewHead);
        previewHead.append(h("div", { class: "cl-label cl-grow", text: t("Reduction preview") }),
          chip(t("Top"), !Cloud.elevation, false, () => Cloud.setElevation(false)), chip(t("Front"), Cloud.elevation, false, () => Cloud.setElevation(true)));
      });
      redraw("preview", [Cloud.preview ? [Cloud.preview.before, Cloud.preview.after, Cloud.preview.original.length, Cloud.preview.reduced.length, Cloud.preview.reducedShown] : null,
        Cloud.previewBusy, Cloud.previewMessage, Cloud.files.length], drawPreview);
      redraw("zones", [Cloud.zone, Cloud.preview?.canZone], () => {
        clear(zones);
        if (!Cloud.preview?.canZone) return;
        zones.append(h("span", { class: "cl-dim", text: t("Part of the file") }));
        ([[t("Start"), 0], [t("Middle"), 0.5], [t("End"), 1]] as [string, number][]).forEach(([label, zone]) =>
          zones.append(chip(label, Math.abs(Cloud.zone - zone) < 0.01, false, () => Cloud.setZone(zone))));
      });
      redraw("crs", [Cloud.source, Cloud.target, Cloud.declared, Cloud.detectNote, Cloud.picker, Cloud.results, Cloud.searched,
        Cloud.ellipsoidal, running, status?.geoid, status?.library, Cloud.files.length], () => drawCrs(running));
      redraw("footer", [Cloud.running, Math.round(Cloud.progress.value * 100), Cloud.progress.text, Cloud.result, Cloud.notice, Cloud.files.length], drawFooter);
    },
  };
}
