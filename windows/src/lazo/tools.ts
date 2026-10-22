// The channel to Lazo's tools program (engine/Lazo.Engine.exe --tools-only): the classic logic of Xpace, the point
// clouds and the rest, which the island draws inside itself. Messages are the JSON of docs/engine-protocol.md,
// "tool.command" in, "tool.event" out. The transport is injected, so tests can drive it.

export type ToolMessage = { type: string } & Record<string, any>;

export interface ToolsTransport {
  /** Sends one JSON text to the program (starting it if it is not running). */
  send(text: string): Promise<void>;
  /** Calls `handler` with each text the program says; resolves to what stops it. */
  subscribe(handler: (text: string) => void): Promise<() => void>;
}

export class LazoTools {
  private transport: ToolsTransport;
  private listeners = new Set<(m: ToolMessage) => void>();
  private starting: Promise<void> | null = null;

  constructor(transport: ToolsTransport) {
    this.transport = transport;
  }

  on(listener: (m: ToolMessage) => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  private start(): Promise<void> {
    this.starting ??= this.transport
      .subscribe((text) => {
        let message: ToolMessage;
        try {
          message = JSON.parse(text);
        } catch {
          return;
        }
        if (typeof message?.type !== "string") return;
        for (const l of this.listeners) l(message);
      })
      .then(() => undefined);
    return this.starting;
  }

  /** One command. Rejects with the reason when the program cannot be started. */
  async post(command: { cmd: string } & Record<string, unknown>): Promise<void> {
    await this.start();
    await this.transport.send(JSON.stringify(command));
  }
}
