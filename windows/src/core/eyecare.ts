// Eye care in the island. The reminders are the classic Lazo's (engine/EyeCareTool.cs over EyeCareService, untouched:
// its timers, the day's counts, eyecare.txt and the break windows); this keeps what the card shows. The program pushes its
// state every second, so the card never counts by itself.

import type { ToolMessage } from "../lazo/tools";
import type { Post } from "./xpace";

export interface EyeCareState {
  microLeft: number;
  activeLeft: number;
  workToday: number;
  micro: number;
  active: number;
  paused: boolean;
  sound: boolean;
  clock: "digital" | "analog";
  rest: "notice" | "eyes";
  microEvery: number;
  activeEvery: number;
  microFor: number;
  activeFor: number;
}

export class EyeCareModel {
  state: EyeCareState | null = null;
  error = "";
  private post: Post;
  private changed: () => void;
  private visible: () => boolean;

  /** `visible` says whether the card is on screen: the countdowns only matter then. */
  constructor(post: Post, changed: () => void, visible: () => boolean = () => true) {
    this.post = post;
    this.changed = changed;
    this.visible = visible;
  }

  private send(command: { cmd: string } & Record<string, unknown>) {
    this.post(command).catch((err) => {
      this.error = String(err).replace(/^Error:\s*/, "");
      this.changed();
    });
  }

  /** Starts the reminders (they must run with the island closed too) and asks for the state. */
  start() {
    this.send({ cmd: "eyecare.state" });
  }

  setPaused(value: boolean) {
    this.send({ cmd: "eyecare.pause", value });
  }

  setSound(value: boolean) {
    this.send({ cmd: "eyecare.sound", value });
  }

  setClock(value: "digital" | "analog") {
    this.send({ cmd: "eyecare.clock", value });
  }

  setRest(value: "notice" | "eyes") {
    this.send({ cmd: "eyecare.rest", value });
  }

  /** Takes the break now: the classic window appears and the timer starts over. */
  trigger(kind: "micro" | "active") {
    this.send({ cmd: "eyecare.trigger", value: kind });
  }

  handle(m: ToolMessage) {
    if (m.type !== "eyecare.state") return;
    const { type: _type, ...state } = m;
    const before = this.state;
    this.state = state as unknown as EyeCareState;
    this.error = "";
    // The program pushes every second; only what the person can notice (or a card on screen) redraws the island.
    const settled = (s: EyeCareState | null) => JSON.stringify(s && [s.paused, s.sound, s.clock, s.rest, s.micro, s.active]);
    if (settled(before) !== settled(this.state) || this.visible()) this.changed();
  }
}

/** 19:05 (or 1:04:05 from an hour on): a countdown. */
export function countdown(seconds: number): string {
  const s = Math.max(0, Math.floor(seconds));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const rest = String(s % 60).padStart(2, "0");
  return h > 0 ? `${h}:${String(m).padStart(2, "0")}:${rest}` : `${m}:${rest}`;
}

/** 2 h 05 min: time worked. */
export function worked(seconds: number): string {
  const minutes = Math.floor(Math.max(0, seconds) / 60);
  return minutes >= 60 ? `${Math.floor(minutes / 60)} h ${String(minutes % 60).padStart(2, "0")} min` : `${minutes} min`;
}
