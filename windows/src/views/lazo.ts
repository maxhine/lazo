// What Lazo puts on the island: the card of the Lazo pill (people, files offered), the people column
// that can stand where the pills are, and the conversation with people that the message icon opens.
// What they show comes from the engine (core/lazo.ts); nothing here talks to the network.

import { h, svg, clear } from "./dom";
import { ICONS } from "./icons";
import { State } from "../core/state";
import { Lazo, lazoConnected, lazoOffers, lazoPeers, LAZO_COLOR } from "../core/lazo";
import type { EngineOffer, EnginePeer } from "../lazo/engine";
import { t } from "../i18n/i18n";

export interface LazoCardHost {
  el: HTMLElement;
  sync(): void;
}

export interface PeopleChatHost extends LazoCardHost {
  focus(): void;
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

function unread(peerId: string): number {
  return Lazo.engine?.unread[peerId] ?? 0;
}

/** Sends the file that waits for somebody to be chosen. */
function sendPending(peer: EnginePeer) {
  const file = Lazo.pendingFile;
  const engine = Lazo.engine;
  if (!file || !engine || peer.identityChanged) return;
  Lazo.pendingFile = null;
  Lazo.notice = "";
  State.notify();
  void engine.sendFile(peer.id, file.path).then((r) => {
    Lazo.notice = r.ok ? t("Sent to {name}", { name: peer.name }) : r.message;
    State.notify();
  });
}

/** A click on somebody: the waiting file goes to them, else their conversation opens. */
function pick(peer: EnginePeer) {
  if (Lazo.pendingFile) sendPending(peer);
  else if (!peer.identityChanged) Lazo.hooks.openChat(peer.id);
}

function personRow(peer: EnginePeer): HTMLElement {
  const count = unread(peer.id);
  const row = h("div", { class: "lazo-row pick" },
    h("span", { class: "lazo-avatar", text: initial(peer.name) }),
    h("span", { class: "lazo-name", text: peer.name }),
    count > 0 ? h("span", { class: "lazo-unread", text: String(count) }) : null,
    h("span", { class: "lazo-sub", text: peer.identityChanged ? t("Changed identity") : peer.sharing ? t("Sharing") : t("Available") }),
  );
  row.addEventListener("click", () => pick(peer));
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
    row.append(h("div", { class: "lazo-bar" }, h("i", { style: `width:${Math.round(offer.progress * 100)}%` })));
  } else {
    row.append(h("div", { class: "lazo-actions" },
      h("span", { class: "lazo-sub", text: offer.status === "done" ? t("Saved") : offer.detail }),
      h("button", { class: "btn secondary", onclick: () => { engine?.dismissOffer(offer.id); } }, h("span", { text: t("OK") })),
    ));
  }
  return row;
}

/** What changes the people list as drawn: names, who is sharing, unread counts. */
function peopleKey(): string {
  return JSON.stringify(lazoPeers().map((p) => [p.id, p.name, p.sharing, p.identityChanged, unread(p.id)]));
}

// ── The Lazo pill's card ──────────────────────────────────────────────────────

export function buildLazoCard(): LazoCardHost {
  const title = h("div", { class: "lazo-title" },
    h("i", { class: "dot", style: `width:6px;height:6px;background:${LAZO_COLOR}` }),
    h("span", { text: "Lazo" }),
  );
  // What can be done with the file that waits for somebody to be chosen sits on the title line, out of the list's way.
  const links = h("span", { class: "lazo-links" });
  title.append(links);
  const body = h("div", { class: "lazo-body" });
  const el = h("div", { class: "lazo-card" }, title, body);
  let key = "";

  return {
    el,
    sync() {
      const engine = Lazo.engine;
      const peers = lazoPeers();
      const offers = lazoOffers();
      const sending = Object.keys(engine?.state.sending ?? {}).length;
      // Rebuilt only when what it shows changed: a click must not land on a row that was just redrawn.
      const next = JSON.stringify([
        lazoConnected(), Lazo.error, Lazo.notice, Lazo.pendingFile?.path ?? "", sending, peopleKey(),
        offers.map((o) => [o.id, o.status, Math.round(o.progress * 20), o.detail]),
      ]);
      if (next === key) return;
      key = next;
      clear(body);
      clear(links);
      if (!lazoConnected()) {
        body.append(h("div", { class: "lazo-empty", text: Lazo.error || t("Lazo is starting…") }));
        return;
      }
      for (const offer of offers) body.append(offerRow(offer));
      const file = Lazo.pendingFile;
      if (file) {
        links.append(
          h("button", {
            class: "lazo-link",
            onclick: () => {
              Lazo.pendingFile = null;
              Lazo.hooks.askAboutFile(file.path);
            },
          }, t("Ask the AI instead")),
          h("button", { class: "lazo-link", onclick: () => { Lazo.pendingFile = null; State.notify(); } }, t("Cancel")),
        );
      }
      if (file) body.append(h("div", { class: "lazo-sub lazo-ask", text: t("Send {name} to…", { name: file.name }) }));
      if (peers.length === 0) {
        body.append(h("div", { class: "lazo-empty", text: t("Nobody nearby yet. Open Lazo on another computer in this network.") }));
      } else {
        const list = h("div", { class: "lazo-list" });
        for (const p of peers) list.append(personRow(p));
        body.append(list);
        if (!file) body.append(h("div", { class: "lazo-sub lazo-foot", text: t("Drop a file on the island to send it.") }));
      }
      if (sending > 0) body.append(h("div", { class: "lazo-sub lazo-foot", text: t("Sending…") }));
      else if (Lazo.notice) body.append(h("div", { class: "lazo-sub lazo-foot", text: Lazo.notice }));
    },
  };
}

// ── The people column (where the pills are) ───────────────────────────────────

export function buildPeoplePanel(): LazoCardHost {
  const el = h("div", { class: "lazo-people" });
  let key = "";
  return {
    el,
    sync() {
      const peers = lazoPeers();
      const next = JSON.stringify([lazoConnected(), Lazo.error, Lazo.pendingFile?.path ?? "", peopleKey()]);
      if (next === key) return;
      key = next;
      clear(el);
      if (!lazoConnected()) {
        el.append(h("div", { class: "lazo-empty", text: Lazo.error || t("Lazo is starting…") }));
      } else if (peers.length === 0) {
        el.append(h("div", { class: "lazo-empty", text: t("Nobody nearby yet. Open Lazo on another computer in this network.") }));
      } else {
        for (const p of peers) el.append(personRow(p));
      }
    },
  };
}

// ── Conversations with people (the message icon, second choice) ───────────────

export function buildPeopleChat(): PeopleChatHost {
  const strip = h("div", { class: "pchat-strip" });
  const log = h("div", { class: "chat-log pchat-log" });
  const input = h("input", { type: "text", class: "chat-input", spellcheck: "false" }) as HTMLInputElement;
  const send = h("button", { class: "send-btn", title: t("Send") }, svg(ICONS.arrowUp, 11));
  const bar = h("div", { class: "chat-bar pchat-bar" }, input, send);
  const el = h("div", { class: "pchat" }, strip, log, bar);
  let key = "";

  /** The person whose conversation is open: the one chosen, else the first one around. */
  function selected(): EnginePeer | null {
    const peers = lazoPeers();
    return peers.find((p) => p.id === Lazo.chatPeer) ?? peers[0] ?? null;
  }

  function submit() {
    const peer = selected();
    const text = input.value.trim();
    if (!peer || !text || !Lazo.engine) return;
    input.value = "";
    Lazo.engine.sendChat(peer.id, text);
  }
  send.addEventListener("click", submit);
  input.addEventListener("keydown", (e) => {
    if ((e as KeyboardEvent).key === "Enter") {
      e.preventDefault();
      submit();
    }
    e.stopPropagation(); // Escape closes the island, not the conversation
  });

  return {
    el,
    sync() {
      const engine = Lazo.engine;
      const peer = selected();
      const onScreen = State.view === "prompt" && State.mode === "expanded" && Lazo.chatMode === "people";
      if (engine) {
        engine.activeChat = onScreen ? peer?.id ?? null : null;
        if (onScreen && peer) engine.markRead(peer.id);
      }
      const messages = peer && engine ? engine.messages(peer.id) : [];
      const next = JSON.stringify([
        lazoConnected(), Lazo.error, peer?.id ?? "", peopleKey(),
        messages.map((m) => [m.own, m.text, m.pending, m.failed]),
      ]);
      input.disabled = !peer || !lazoConnected();
      input.placeholder = peer ? t("Message {name}…", { name: peer.name }) : "";
      if (next === key) return;
      key = next;
      clear(strip);
      for (const p of lazoPeers()) {
        const count = unread(p.id);
        const chip = h("button", { class: p.id === peer?.id ? "pchat-chip on" : "pchat-chip" },
          h("span", { class: "lazo-avatar", text: initial(p.name) }),
          h("span", { class: "lazo-name", text: p.name }),
          count > 0 ? h("span", { class: "lazo-unread", text: String(count) }) : null,
        );
        chip.addEventListener("click", () => {
          Lazo.chatPeer = p.id;
          State.notify();
          input.focus();
        });
        strip.append(chip);
      }
      clear(log);
      if (!lazoConnected()) {
        log.append(h("div", { class: "lazo-empty", text: Lazo.error || t("Lazo is starting…") }));
      } else if (!peer) {
        log.append(h("div", { class: "lazo-empty", text: t("Nobody nearby yet. Open Lazo on another computer in this network.") }));
      } else if (messages.length === 0) {
        log.append(h("div", { class: "lazo-empty", text: t("No messages yet.") }));
      } else {
        for (const m of messages) {
          const bubble = h("div", { class: "bubble", text: m.text });
          log.append(h("div", { class: m.own ? "chat-row user" : "chat-row" }, bubble));
          if (m.failed) log.append(h("div", { class: "chat-row user" }, h("div", { class: "lazo-sub pchat-failed", text: t("Not delivered") })));
        }
        log.scrollTop = log.scrollHeight;
      }
    },
    focus() {
      input.focus();
    },
  };
}
