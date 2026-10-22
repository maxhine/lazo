// The Lazo pill on the page: the people on the network, files waiting to be accepted, and the
// file that was dropped on the island and waits for someone to send it to. Everything comes from
// the engine (src-tauri/src/lazo_net, running inside the island) through the client in ../lazo/engine.ts:
// its JSON messages travel as a Tauri event out and a command in.

import { invoke } from "@tauri-apps/api/core";
import { listen, type UnlistenFn } from "@tauri-apps/api/event";
import { LazoEngine, type EngineEvent, type EngineOffer, type EnginePeer, type SocketLike } from "../lazo/engine";

export const LAZO_ID = "integration_lazo";
export const LAZO_COLOR = "#E5E7EB";

export const Lazo = {
  engine: null as LazoEngine | null,
  /** Why the engine is not there (from Rust); "" while it is, or before the first answer. */
  error: "",
  /** A file dropped on the island with this pill in front: it waits for a person to be chosen. */
  pendingFile: null as { name: string; path: string } | null,
  /** The last thing that happened to a transfer, for the card's footer. */
  notice: "",
  /** What the message icon opens: the AI, or the people on the network. */
  chatMode: "ai" as "ai" | "people",
  /** The person whose conversation is open in the people chat. */
  chatPeer: null as string | null,
  /** What the views ask of the island (set by island/lazo.ts). */
  hooks: {
    /** "Ask the AI instead": send a dropped file to the AI chat. */
    askAboutFile: (_path: string): void => {},
    /** Open the conversation with somebody. */
    openChat: (_peerId: string): void => {},
  },
};

export function lazoConnected(): boolean {
  return Lazo.engine?.state.connected === true;
}

export function lazoPeers(): EnginePeer[] {
  return Lazo.engine?.state.peers ?? [];
}

/** Offers still waiting for a decision, then the ones receiving or just done. */
export function lazoOffers(): EngineOffer[] {
  return Lazo.engine?.state.offers ?? [];
}

export function hasPendingOffer(): boolean {
  return lazoOffers().some((o) => o.status === "pending");
}

/** What the page does when something happens in the engine. */
export interface LazoHost {
  /** Redraw. */
  changed(): void;
  /** A file is offered while the island may be closed: bring the card up. */
  offered(offer: EngineOffer): void;
  /** Somebody wrote to us. */
  messaged(peerId: string, name: string, text: string): void;
}

/**
 * The engine's messages over Tauri: the engine emits each as an event, commands go in through
 * lazo_engine_send. Asking for the state is also how the page learns the engine is not there
 * (Rust answers with the reason), which closes the "socket" so the client tries again later.
 */
export function tauriSocket(onError: (reason: string) => void): SocketLike {
  let unlisten: UnlistenFn | null = null;
  let closed = false;
  const socket: SocketLike = {
    onopen: null, onmessage: null, onclose: null, onerror: null,
    send(data: string) {
      void invoke("lazo_engine_send", { message: data }).catch(() => {});
    },
    close() {
      closed = true;
      unlisten?.();
    },
  };
  void (async () => {
    try {
      const stop = await listen<string>("lazo-engine", (e) => socket.onmessage?.({ data: e.payload }));
      if (closed) return stop();
      unlisten = stop;
      await invoke("lazo_engine_send", { message: JSON.stringify({ cmd: "state" }) });
      socket.onopen?.({});
    } catch (err) {
      onError(String(err).replace(/^Error:s*/, ""));
      unlisten?.();
      socket.onclose?.({});
    }
  })();
  return socket;
}

/** Connects to the engine and keeps trying while it is not there. */
export function startLazo(host: LazoHost): LazoEngine {
  const engine = new LazoEngine(
    async () => "tauri",
    () => tauriSocket((reason) => {
      Lazo.error = reason;
      host.changed();
    }),
  );
  Lazo.engine = engine;
  let offers = 0;
  engine.on((e: EngineEvent) => {
    if (engine.state.connected) Lazo.error = "";
    if (e.type === "chat") host.messaged(e.chat.peerId, e.chat.name, e.chat.text);
    if (e.type === "sendDone") {
      Lazo.notice = e.ok ? "sent" : e.message;
    }
    const seen = engine.state.offers.filter((o) => o.status === "pending").length;
    if (seen > offers) {
      const newest = engine.state.offers.filter((o) => o.status === "pending").at(-1);
      if (newest) host.offered(newest);
    }
    offers = seen;
    host.changed();
  });
  void engine.connect();
  return engine;
}
