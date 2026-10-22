// The Lazo pill on the page: the people on the network, files waiting to be accepted, and the
// file that was dropped on the island and waits for someone to send it to. Everything comes from
// the engine (engine/Lazo.Engine.exe, started by src-tauri/src/lazo_engine.rs) through the client
// in ../lazo/engine.ts.

import { LazoEngine, engineUrl, type EngineEvent, type EngineOffer, type EnginePeer } from "../lazo/engine";
import { Bridge } from "./bridge";

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
}

/** Connects to the engine and keeps the connection (it asks Rust for the address on each try). */
export function startLazo(host: LazoHost): LazoEngine {
  const engine = new LazoEngine(async () => {
    try {
      const info = await Bridge.lazoEngineInfo();
      Lazo.error = "";
      return engineUrl(info.port, info.token);
    } catch (err) {
      Lazo.error = String(err).replace(/^Error:\s*/, "");
      host.changed();
      throw err;
    }
  }, (url) => new WebSocket(url));
  Lazo.engine = engine;
  let offers = 0;
  engine.on((e: EngineEvent) => {
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
