// A point cloud dropped on the notch: "convert it or share it?". Convert opens the point cloud tool with the files; share
// shows the people to send them to (island.ts: convertDropped / shareDropped).

import { h } from "./dom";
import { State } from "../core/state";
import { lazoConnected } from "../core/lazo";
import { convertible } from "../core/cloud-formats";
import { t } from "../i18n/i18n";
import type { ViewActions, ViewHost } from "./views";

export function buildCloudDrop(actions: ViewActions): ViewHost {
  const title = h("div", { class: "title" });
  const sub = h("div", { class: "sub" });
  const convert = h("button", { class: "btn primary cd-btn" }, h("span", { text: t("Convert") }));
  const share = h("button", { class: "btn secondary cd-btn" }, h("span", { text: t("Share") }));
  const cancel = h("button", { class: "cd-cancel", text: t("Cancel") });
  convert.addEventListener("click", () => actions.convertDropped());
  share.addEventListener("click", () => actions.shareDropped());
  cancel.addEventListener("click", () => {
    State.cloudDrop = null;
    actions.setView(State.defaultView());
  });
  const body = h("div", { class: "stack cd-body" }, title, sub, h("div", { class: "cd-actions" }, cancel, share, convert));
  const el = h("div", { class: "view" }, h("div", { class: "card cd-card" }, body));

  return {
    el,
    sync() {
      const files = State.cloudDrop ?? [];
      const name = files[0]?.split(/[\\/]/).pop() ?? "";
      const ok = files.filter(convertible).length;
      title.textContent = files.length > 1 ? t("{count} point clouds", { count: files.length }) : name;
      // The converter reads LAS, LAZ and E57; the rest can only be shared.
      sub.textContent = ok === 0 ? t("Lazo converts LAS, LAZ and E57. This one can be shared.") : t("What do you want to do with it?");
      (convert as HTMLButtonElement).disabled = ok === 0;
      (share as HTMLButtonElement).disabled = !lazoConnected();
      share.title = lazoConnected() ? "" : t("Lazo is starting…");
    },
  };
}
