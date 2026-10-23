// What is in progress right now, for the notch: a point-cloud conversion, a TPS conversion, an analysis of Xpace, files on
// their way out or in. The island's compact form shows it as a thin bar along its bottom edge and a line of text.

import { t } from "../i18n/i18n";

export interface Progress {
  /** What is going on, already in the interface language. */
  label: string;
  /** 0…1, or null when there is no way to know how far along it is. */
  value: number | null;
  /** The colour of the tool that is working. */
  color: string;
}

/** The parts of the app that can be busy; each is read, never changed. */
export interface ProgressSources {
  cloud: { running: boolean; progress: { value: number } };
  tps: { converting: boolean };
  xpace: { running: boolean };
  /** Files being received (0…1 each) and sent (0…1 each). */
  receiving: number[];
  sending: number[];
}

const clamp = (v: number) => (Number.isFinite(v) ? Math.max(0, Math.min(1, v)) : 0);
const mean = (values: number[]) => values.reduce((a, b) => a + b, 0) / values.length;

/** The most important thing in progress, or null when nothing is. Conversions come first: they take the longest. */
export function activeProgress(s: ProgressSources): Progress | null {
  if (s.cloud.running) return { label: t("Point clouds"), value: clamp(s.cloud.progress.value), color: "#38BDF8" };
  if (s.tps.converting) return { label: t("TPS to RINEX"), value: null, color: "#2DD4BF" };
  if (s.xpace.running) return { label: "Xpace", value: null, color: "#C084FC" };
  if (s.sending.length > 0) return { label: t("Sending files"), value: clamp(mean(s.sending)), color: "#F5F6F8" };
  if (s.receiving.length > 0) return { label: t("Receiving files"), value: clamp(mean(s.receiving)), color: "#F5F6F8" };
  return null;
}

/** What the progress card says about the file: "Sending X to Ana" when it goes to a person, else "Uploading X" (to the AI). */
export function transferLabel(name: string, to: string | null): string {
  return to ? t("Sending {name} to {person}", { name, person: to }) : t("Uploading {name}", { name });
}

/** "Point clouds 42 %": the line under the bar. */
export function progressText(p: Progress): string {
  return p.value == null ? p.label : `${p.label} ${Math.round(p.value * 100)} %`;
}
