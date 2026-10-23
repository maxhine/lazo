// The eye break and the hourly pause inside the island: it opens a little, says what to do and counts down. When the
// count ends the break is done (it counts in the day); "Snooze" puts it off five minutes. The model is core/eyecare.ts.

import { h } from "./dom";
import { EyeCare } from "../core/lazo-tools";
import { countdown } from "../core/eyecare";
import { State } from "../core/state";
import { t } from "../i18n/i18n";
import type { ViewHost } from "./views";

export function buildRest(): ViewHost {
  const title = h("div", { class: "title" });
  const hint = h("div", { class: "sub" });
  const time = h("div", { class: "rest-time" });
  const fill = h("i", { class: "rest-fill" });
  const snooze = h("button", { class: "btn secondary rest-btn" }, h("span", { text: t("Snooze 5 min") }));
  const done = h("button", { class: "btn primary rest-btn" }, h("span", { text: t("Done") }));
  snooze.addEventListener("click", () => EyeCare.finish("snooze"));
  done.addEventListener("click", () => EyeCare.finish("complete"));
  const body = h("div", { class: "stack rest-body" },
    h("div", { class: "rest-row" }, h("div", { class: "rest-text" }, title, hint), time),
    h("div", { class: "rest-bar" }, fill),
    h("div", { class: "rest-actions" }, snooze, done));
  const el = h("div", { class: "view" }, h("div", { class: "card rest-card" }, body));
  let shown = "";

  function paint(now: number) {
    const a = EyeCare.alert;
    if (!a) return;
    const left = Math.max(0, a.seconds - (now - a.startedAt) / 1000);
    const key = `${a.kind}|${Math.ceil(left)}`;
    fill.style.width = `${Math.min(100, (1 - left / a.seconds) * 100)}%`;
    if (key !== shown) {
      shown = key;
      title.textContent = a.kind === "micro" ? t("Eye break") : t("Active pause");
      hint.textContent = a.kind === "micro" ? t("Look at something 6 meters away.") : t("Stand up and stretch.");
      time.textContent = countdown(Math.ceil(left));
    }
    if (left <= 0) EyeCare.finish("complete");
  }

  return {
    el,
    sync() {
      shown = "";
      paint(Date.now());
    },
    // One frame after another while the card is up, so the bar glides and the numbers keep time.
    tick() {
      if (State.view !== "rest" || !EyeCare.alert) return false;
      paint(Date.now());
      return true;
    },
  };
}
