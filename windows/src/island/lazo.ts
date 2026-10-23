// The Lazo pill's side of the island: connects to the engine, and when somebody offers a file
// while the island is closed, brings the card up (as a permission request would).

import { IS_TAURI } from "../core/bridge";
import { LAZO_ID, Lazo, startLazo } from "../core/lazo";
import { EyeCare } from "../core/lazo-tools";
import { Sound } from "../core/sound";
import { State } from "../core/state";

export interface LazoIslandHost {
  /** Opens the island on its usual view. */
  alert(view: "overview"): void;
  /** Shows the island's compact bar. */
  reveal(): void;
  /** Opens the island on a view. */
  expand(view: "prompt"): void;
  /** A dropped file goes to the AI chat. */
  askAboutFile(path: string): void;
  /** Somebody sent a buzz: the island opens, Nudo shakes and says who. */
  buzzed(name: string): void;
  /** A break is due and the style is "In the notch". */
  restAlert(kind: "micro" | "active", sound: boolean): void;
  /** The break is over (done, snoozed or counted out). */
  restDone(): void;
}

export function registerLazoHandlers(island: LazoIslandHost) {
  // Windows has the engine inside the app; the people and the chat work even if the Lazo pill is not declared.
  if (!IS_TAURI || State.os !== "windows") return;
  Lazo.hooks.askAboutFile = (path) => island.askAboutFile(path);
  Lazo.hooks.openChat = (peerId) => {
    Lazo.chatMode = "people";
    Lazo.chatPeer = peerId;
    island.expand("prompt");
  };
  // Eye care keeps its reminders in the tools program: it must be running with the island closed too.
  EyeCare.start();
  // With the style "In the notch" the eye break and the hourly pause come here: the island opens a little and counts down.
  EyeCare.onAlert = (a) => island.restAlert(a.kind, a.sound);
  EyeCare.onDone = () => island.restDone();
  startLazo({
    changed: () => State.notify(),
    messaged: () => {
      Sound.play("blip");
      if (State.mode === "hidden") island.reveal();
    },
    buzzed: (_peerId, name) => island.buzzed(name),
    offered: () => {
      State.setFocus(LAZO_ID);
      Sound.play("question");
      island.alert("overview");
    },
  });
}
