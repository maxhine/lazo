// TPS to RINEX inside the island: Topcon observation files (.tps) are dropped or chosen, each one shows the times it
// observed, and the RINEX is written with the name wanted. The model is core/tps.ts, the conversion Topcon's tps2rin.exe.

import { h, clear } from "./dom";
import { Bridge } from "../core/bridge";
import { Tps } from "../core/lazo-tools";
import { baseName, between, clock, span, type TpsFile } from "../core/tps";
import { t } from "../i18n/i18n";
import type { ViewHost } from "./views";

function times(file: TpsFile): HTMLElement {
  const o = file.obs;
  const box = h("div", { class: "tp-times" });
  if (!o || !o.hasTimes) {
    box.append(h("div", { class: "tp-dim", text: t("No observation times found in this file.") }));
    return box;
  }
  const a = clock(o.first);
  const b = clock(o.last);
  const sameDay = a.date === b.date;
  const ua = clock(o.first, -o.leap);
  const ub = clock(o.last, -o.leap);
  box.append(
    h("div", { class: "tp-line" },
      h("span", { class: "tp-key", text: t("Observed (GPS)") }),
      h("b", { text: `${a.date}  ${a.time} → ${sameDay ? "" : b.date + "  "}${b.time}` }),
      h("span", { class: "tp-pill", text: span(between(o.first, o.last)) })),
    h("div", { class: "tp-line" },
      h("span", { class: "tp-key", text: t("UTC") }),
      h("span", { text: `${ua.date}  ${ua.time} → ${ub.date === ua.date ? "" : ub.date + "  "}${ub.time}` }),
      h("span", { class: "tp-dim", text: t("GPS − {seconds} s", { seconds: o.leap }) })),
    h("div", { class: "tp-line" },
      h("span", { class: "tp-key", text: t("Epochs") }),
      h("span", { text: t("Every {seconds} s · {count} of {expected}", { seconds: o.interval, count: o.epochs.toLocaleString(), expected: o.expected.toLocaleString() }) }),
      o.gaps > 0
        ? h("span", { class: "tp-pill warn", text: t("{count} gaps · {seconds} s missing", { count: o.gaps, seconds: Math.round(o.missing) }) })
        : h("span", { class: "tp-pill ok", text: t("No gaps") })),
  );
  const extra = [o.marker && `${t("Marker")}: ${o.marker}`, o.receiver && `${t("Receiver")}: ${o.receiver}`, o.satellites > 0 && t("{count} satellites", { count: o.satellites })].filter(Boolean);
  if (extra.length) box.append(h("div", { class: "tp-dim", text: extra.join("  ·  ") }));
  return box;
}

export function buildTps(): ViewHost {
  const missing = h("div", { class: "tp-missing" });
  const drop = h("button", { class: "tp-drop" });
  const list = h("div", { class: "tp-list" });
  const folder = h("div", { class: "tp-folder" });
  const folderBtn = h("button", { class: "tp-link", text: t("Change…") });
  const status = h("div", { class: "tp-status" });
  const buttons = h("div", { class: "tp-buttons" });
  const footer = h("div", { class: "tp-footer" }, h("div", { class: "tp-folder-row" }, h("span", { class: "tp-key", text: t("Save to") }), folder, folderBtn), status, buttons);
  const main = h("div", { class: "tp-main" }, drop, list);
  const el = h("div", { class: "view" }, h("div", { class: "card tp-card" }, missing, main, footer));

  drop.addEventListener("click", () => Tps.pickFiles());
  folderBtn.addEventListener("click", () => Tps.pickFolder());

  const drawn: Record<string, string> = {};
  const redraw = (key: string, signature: unknown, draw: () => void) => {
    const s = JSON.stringify(signature);
    if (drawn[key] === s) return;
    drawn[key] = s;
    draw();
  };

  function drawList() {
    clear(list);
    for (const f of Tps.files) {
      const input = h("input", { type: "text", class: "tp-input", spellcheck: "false", placeholder: baseName(f.path) }) as HTMLInputElement;
      input.value = f.name;
      input.disabled = Tps.converting || f.state !== "ok";
      input.addEventListener("keydown", (e) => e.stopPropagation()); // Escape closes the island, not the field
      input.addEventListener("input", () => Tps.setName(f.path, input.value));
      const remove = h("button", { class: "tp-x", title: t("Remove"), text: "×" });
      if (Tps.converting) (remove as HTMLButtonElement).disabled = true;
      else remove.addEventListener("click", () => Tps.removeFile(f.path));
      const body = f.state === "reading" ? h("div", { class: "tp-dim", text: Tps.exe === null ? t("Waiting for tps2rin.exe") : t("Reading…") })
        : f.state === "bad" ? h("div", { class: "tp-error", text: f.message || t("It could not be converted.") })
        : times(f);
      list.append(h("div", { class: f.state === "ok" ? "tp-file" : f.state === "bad" ? "tp-file bad" : "tp-file reading", title: f.path },
        h("div", { class: "tp-head" }, h("b", { class: "tp-fname", text: f.path.split(/[\\/]/).pop() ?? f.path }), remove),
        body,
        f.state === "ok" ? h("div", { class: "tp-out" }, h("span", { class: "tp-key", text: t("Output name") }), input,
          h("span", { class: "tp-dim", text: f.outputs.join("  ") })) : null));
    }
  }

  return {
    el,
    sync() {
      Tps.init();
      const unavailable = Tps.exe === null;
      missing.style.display = unavailable ? "" : "none";
      if (unavailable) {
        clear(missing);
        const choose = h("button", { class: "btn primary" }, h("span", { text: t("Choose tps2rin.exe") }));
        choose.addEventListener("click", () => Tps.pickExe());
        missing.append(h("div", { text: t("Lazo needs Topcon's tps2rin.exe to convert. Choose it once and Lazo remembers it.") }), choose);
      }
      drop.textContent = Tps.files.length ? t("Add more files") : t("Drop Topcon .tps files here, or click to choose them");
      drop.classList.toggle("has", Tps.files.length > 0);
      (drop as HTMLButtonElement).disabled = Tps.converting;
      folder.textContent = Tps.folder || t("The folder of each file");
      folder.classList.toggle("dim", !Tps.folder);
      (folderBtn as HTMLButtonElement).disabled = Tps.converting;

      redraw("list", [Tps.files.map((f) => [f.path, f.state, f.message, f.obs?.first, f.obs?.last, f.obs?.gaps, f.outputs]), Tps.converting, Tps.exe], drawList);
      redraw("footer", [Tps.converting, Tps.result, Tps.written, Tps.notice, Tps.running, Tps.files.map((f) => f.state)], () => {
        status.className = Tps.result && Tps.result !== "ok" ? "tp-status error" : "tp-status";
        status.textContent = Tps.converting ? t("Converting…")
          : Tps.result === "ok" ? t("Done: {count} files written", { count: Tps.written.length })
          : Tps.result ? Tps.result
          : Tps.notice === "unsupported" ? t("Only .tps files can be converted")
          : Tps.notice;
        clear(buttons);
        if (Tps.result === "ok" && Tps.written[0]) {
          buttons.append(h("button", { class: "btn secondary", onclick: () => void Bridge.revealPath(Tps.written[0]) }, h("span", { text: t("Show in folder") })));
        }
        const go = h("button", { class: "btn primary" }, h("span", { text: t("Convert") }));
        if (!Tps.ready) (go as HTMLButtonElement).disabled = true;
        else go.addEventListener("click", () => Tps.convert());
        buttons.append(go);
      });
    },
  };
}
