// The Lazo card: the people on the network (a click sends them the file that was dropped on the
// island), and the files people offer you. What it shows comes from the engine
// (core/lazo.ts); nothing here talks to the network.

import { h, clear } from "./dom";
import { State } from "../core/state";
import { Lazo, lazoConnected, lazoOffers, lazoPeers, LAZO_COLOR } from "../core/lazo";
import type { EngineOffer, EnginePeer } from "../lazo/engine";
import { t } from "../i18n/i18n";

export interface LazoCardHost {
  el: HTMLElement;
  sync(): void;
}

export function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  const units = ["KB", "MB", "GB", "TB"];
  let value = bytes / 1024;
  let i = 0;
  while (value >= 1024 && i < units.length - 1) {
    value /= 1024;
    i++;
  }
  return `${value >= 100 || i === 0 ? Math.round(value) : value.toFixed(1)} ${units[i]}`;
}

function initial(name: string): string {
  return (Array.from(name.trim())[0] ?? "?").toUpperCase();
}

function personRow(peer: EnginePeer, send: ((peer: EnginePeer) => void) | null): HTMLElement {
  const row = h("div", { class: `lazo-row${send ? " pick" : ""}`, title: send ? t("Send") : "" },
    h("span", { class: "lazo-avatar", text: initial(peer.name) }),
    h("span", { class: "lazo-name", text: peer.name }),
    h("span", { class: "lazo-sub", text: peer.identityChanged ? t("Changed identity") : peer.sharing ? t("Sharing") : t("Available") }),
  );
  if (send && !peer.identityChanged) row.addEventListener("click", () => send(peer));
  return row;
}

function offerRow(offer: EngineOffer): HTMLElement {
  const engine = Lazo.engine;
  const head = h("div", { class: "lazo-offer-text" },
    h("span", { class: "lazo-name", text: offer.fileName }),
    h("span", { class: "lazo-sub", text: `${offer.sender} · ${formatSize(offer.size)}` }),
  );
  const row = h("div", { class: "lazo-offer" }, head);
  if (offer.status === "pending") {
    row.append(h("div", { class: "lazo-actions" },
      h("button", { class: "btn primary", onclick: () => engine?.decideOffer(offer.id, true) }, h("span", { text: t("Accept") })),
      h("button", { class: "btn secondary", onclick: () => engine?.decideOffer(offer.id, false) }, h("span", { text: t("Decline") })),
    ));
  } else if (offer.status === "receiving") {
    const bar = h("div", { class: "lazo-bar" }, h("i", { style: `width:${Math.round(offer.progress * 100)}%` }));
    row.append(bar);
  } else {
    row.append(h("div", { class: "lazo-actions" },
      h("span", { class: "lazo-sub", text: offer.status === "done" ? t("Saved") : offer.detail }),
      h("button", { class: "btn secondary", onclick: () => { engine?.dismissOffer(offer.id); } }, h("span", { text: t("OK") })),
    ));
  }
  return row;
}

export function buildLazoCard(): LazoCardHost {
  const title = h("div", { class: "lazo-title" },
    h("i", { class: "dot", style: `width:6px;height:6px;background:${LAZO_COLOR}` }),
    h("span", { text: "Lazo" }),
  );
  const body = h("div", { class: "lazo-body" });
  const el = h("div", { class: "lazo-card" }, title, body);
  let key = "";

  function send(peer: EnginePeer) {
    const file = Lazo.pendingFile;
    const engine = Lazo.engine;
    if (!file || !engine) return;
    Lazo.pendingFile = null;
    Lazo.notice = "";
    State.notify();
    void engine.sendFile(peer.id, file.path).then((r) => {
      Lazo.notice = r.ok ? t("Sent to {name}", { name: peer.name }) : r.message;
      State.notify();
    });
  }

  return {
    el,
    sync() {
      const engine = Lazo.engine;
      const peers = lazoPeers();
      const offers = lazoOffers();
      const sending = Object.keys(engine?.state.sending ?? {}).length;
      // Rebuilt only when what it shows changed: a click must not land on a row that was just redrawn.
      const next = JSON.stringify([
        lazoConnected(), Lazo.error, Lazo.notice, Lazo.pendingFile?.path ?? "", sending,
        peers.map((p) => [p.id, p.name, p.sharing, p.identityChanged]),
        offers.map((o) => [o.id, o.status, Math.round(o.progress * 20), o.detail]),
      ]);
      if (next === key) return;
      key = next;
      clear(body);
      if (!lazoConnected()) {
        body.append(h("div", { class: "lazo-empty", text: Lazo.error || t("Lazo is starting…") }));
        return;
      }
      for (const offer of offers) body.append(offerRow(offer));
      if (Lazo.pendingFile) {
        body.append(h("div", { class: "lazo-sub lazo-ask", text: t("Send {name} to…", { name: Lazo.pendingFile.name }) }));
      }
      if (peers.length === 0) {
        body.append(h("div", { class: "lazo-empty", text: t("Nobody nearby yet. Open Lazo on another computer in this network.") }));
      } else {
        const list = h("div", { class: "lazo-list" });
        for (const p of peers) list.append(personRow(p, Lazo.pendingFile ? send : null));
        body.append(list);
        if (!Lazo.pendingFile) body.append(h("div", { class: "lazo-sub lazo-foot", text: t("Drop a file on the island to send it.") }));
      }
      if (sending > 0) body.append(h("div", { class: "lazo-sub lazo-foot", text: t("Sending…") }));
      else if (Lazo.notice) body.append(h("div", { class: "lazo-sub lazo-foot", text: Lazo.notice }));
    },
  };
}
