// Client of the Lazo engine (engine/Lazo.Engine.exe): people on the network, files, chat.
// The engine speaks JSON over a local WebSocket, protocol in docs/engine-protocol.md. This module
// has no DOM and no Tauri: the socket constructor is injected, so tests can drive it.

export interface EnginePeer {
  id: string;
  name: string;
  address: string;
  paired: boolean;
  verified: boolean;
  identityChanged: boolean;
  /** What they are sharing right now ("" when nothing). */
  sharing: string;
  hasPhoto: boolean;
}

/** A file somebody wants to send us; it waits for accept/reject. */
export interface EngineOffer {
  id: string;
  peerId: string;
  sender: string;
  fileName: string;
  size: number;
  /** Base64 JPEG thumbnail, "" when the file is not an image. */
  preview: string;
  /** 0…1 once accepted. */
  progress: number;
  status: "pending" | "receiving" | "done" | "failed";
  /** Where it was saved, or the reason it failed. */
  detail: string;
}

export interface EngineChat {
  peerId: string;
  name: string;
  text: string;
}

export interface EngineSelf {
  id: string;
  name: string;
  fingerprint: string;
  receiveDir: string;
}

export interface EngineState {
  connected: boolean;
  self: EngineSelf | null;
  peers: EnginePeer[];
  offers: EngineOffer[];
  /** Outgoing transfers by token: 0…1 while sending. */
  sending: Record<string, number>;
}

export type EngineEvent =
  | { type: "state" }
  | { type: "chat"; chat: EngineChat }
  | { type: "sendDone"; token: string; peerId: string; ok: boolean; message: string }
  | { type: "chatSent"; peerId: string; ok: boolean; message: string }
  | { type: "photo"; peerId: string; data: string }
  | { type: "error"; message: string };

/** The part of the WebSocket API the client uses. */
export interface SocketLike {
  onopen: ((e: unknown) => void) | null;
  onmessage: ((e: { data: unknown }) => void) | null;
  onclose: ((e: unknown) => void) | null;
  onerror: ((e: unknown) => void) | null;
  send(data: string): void;
  close(): void;
}
export type SocketFactory = (url: string) => SocketLike;

export const ENGINE_PROTOCOL = 1;

export function engineUrl(port: number, token: string): string {
  return `ws://127.0.0.1:${port}/ws?t=${encodeURIComponent(token)}`;
}

export class LazoEngine {
  state: EngineState = { connected: false, self: null, peers: [], offers: [], sending: {} };

  private socket: SocketLike | null = null;
  private listeners = new Set<(e: EngineEvent) => void>();
  private pending = new Map<string, (r: { ok: boolean; message: string }) => void>();
  private nextToken = 1;
  private closed = false;
  private retry: ReturnType<typeof setTimeout> | null = null;

  /**
   * @param url ws://127.0.0.1:<port>/ws?t=<token>, or a function returning it (the engine may
   *            have restarted on another port, so it is asked again on every attempt).
   */
  private url: string | (() => Promise<string>);
  private make: SocketFactory;
  private retryMs: number;

  constructor(url: string | (() => Promise<string>), make: SocketFactory, retryMs = 2000) {
    this.url = url;
    this.make = make;
    this.retryMs = retryMs;
  }

  on(listener: (e: EngineEvent) => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  async connect(): Promise<void> {
    this.closed = false;
    let url: string;
    try {
      url = typeof this.url === "string" ? this.url : await this.url();
    } catch {
      this.scheduleRetry();
      return;
    }
    if (this.closed) return;
    const socket = this.make(url);
    this.socket = socket;
    socket.onopen = () => {
      this.state.connected = true;
      this.emit({ type: "state" });
    };
    socket.onmessage = (e) => this.receive(String(e.data));
    socket.onerror = () => {};
    socket.onclose = () => {
      if (this.socket !== socket) return;
      this.socket = null;
      this.state = { connected: false, self: null, peers: [], offers: [], sending: {} };
      for (const done of this.pending.values()) done({ ok: false, message: "El motor de Lazo se desconectó." });
      this.pending.clear();
      this.emit({ type: "state" });
      this.scheduleRetry();
    };
  }

  close() {
    this.closed = true;
    if (this.retry) clearTimeout(this.retry);
    this.retry = null;
    const socket = this.socket;
    this.socket = null;
    socket?.close();
  }

  // ── Commands ────────────────────────────────────────────────────────────────

  /** Sends a file to a person. Resolves when it arrived (or failed); progress is in `state.sending`. */
  sendFile(peerId: string, path: string): Promise<{ ok: boolean; message: string }> {
    const token = `s${this.nextToken++}`;
    this.state.sending[token] = 0;
    this.emit({ type: "state" });
    return new Promise((resolve) => {
      this.pending.set(token, resolve);
      if (!this.post({ cmd: "send", token, peerId, path })) {
        this.pending.delete(token);
        delete this.state.sending[token];
        this.emit({ type: "state" });
        resolve({ ok: false, message: "El motor de Lazo no está conectado." });
      }
    });
  }

  sendChat(peerId: string, text: string) {
    this.post({ cmd: "chat", peerId, text });
  }

  decideOffer(id: string, accept: boolean) {
    const offer = this.state.offers.find((o) => o.id === id);
    if (offer) {
      if (accept) offer.status = "receiving";
      else this.state.offers = this.state.offers.filter((o) => o.id !== id);
      this.emit({ type: "state" });
    }
    this.post({ cmd: "offerDecision", id, accept });
  }

  requestPhoto(peerId: string) {
    this.post({ cmd: "photo", peerId });
  }

  dismissOffer(id: string) {
    this.state.offers = this.state.offers.filter((o) => o.id !== id);
    this.emit({ type: "state" });
  }

  // ── Messages from the engine ────────────────────────────────────────────────

  private receive(raw: string) {
    let m: Record<string, any>;
    try {
      m = JSON.parse(raw);
    } catch {
      return;
    }
    const s = this.state;
    switch (m.type) {
      case "hello":
        if (m.version !== ENGINE_PROTOCOL) {
          this.emit({ type: "error", message: `Versión de motor no compatible (${m.version}).` });
          this.close();
          return;
        }
        s.self = { id: m.selfId, name: m.name, fingerprint: m.fingerprint, receiveDir: m.receiveDir };
        this.emit({ type: "state" });
        break;
      case "peers":
        s.peers = (m.peers as EnginePeer[]).map((p) => ({ ...p }));
        this.emit({ type: "state" });
        break;
      case "offer":
        s.offers.push({
          id: m.id, peerId: m.peerId, sender: m.sender, fileName: m.fileName, size: m.size,
          preview: m.preview ?? "", progress: 0, status: "pending", detail: "",
        });
        this.emit({ type: "state" });
        break;
      case "receiveProgress": {
        const offer = s.offers.find((o) => o.id === m.id);
        if (offer) { offer.progress = m.value; this.emit({ type: "state" }); }
        break;
      }
      case "receiveFinished": {
        const offer = s.offers.find((o) => o.id === m.id);
        if (offer) {
          offer.progress = 1;
          offer.status = m.path ? "done" : "failed";
          offer.detail = m.path || m.message;
          this.emit({ type: "state" });
        }
        break;
      }
      case "sendProgress":
        if (m.token in s.sending) { s.sending[m.token] = m.value; this.emit({ type: "state" }); }
        break;
      case "sendDone": {
        delete s.sending[m.token];
        const done = this.pending.get(m.token);
        this.pending.delete(m.token);
        done?.({ ok: !!m.ok, message: m.message ?? "" });
        this.emit({ type: "sendDone", token: m.token, peerId: m.peerId, ok: !!m.ok, message: m.message ?? "" });
        this.emit({ type: "state" });
        break;
      }
      case "chat":
        this.emit({ type: "chat", chat: { peerId: m.peerId, name: m.name, text: m.text } });
        break;
      case "chatSent":
        this.emit({ type: "chatSent", peerId: m.peerId, ok: !!m.ok, message: m.message ?? "" });
        break;
      case "photo":
        this.emit({ type: "photo", peerId: m.peerId, data: m.data });
        break;
      case "error":
        this.emit({ type: "error", message: m.message });
        break;
    }
  }

  private post(command: Record<string, unknown>): boolean {
    if (!this.socket || !this.state.connected) return false;
    this.socket.send(JSON.stringify(command));
    return true;
  }

  private emit(e: EngineEvent) {
    for (const l of this.listeners) l(e);
  }

  private scheduleRetry() {
    if (this.closed || this.retry) return;
    this.retry = setTimeout(() => {
      this.retry = null;
      void this.connect();
    }, this.retryMs);
  }
}
