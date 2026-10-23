// Eye care inside the island: the 20-20-20 eye break and the active pause of the classic Lazo, with the clock, the two
// countdowns, the day's counts and the settings. The model is core/eyecare.ts; the reminders run in the tools program.

import { h, clear } from "./dom";
import { EyeCare } from "../core/lazo-tools";
import { countdown, worked, type EyeCareState } from "../core/eyecare";
import { t } from "../i18n/i18n";
import type { ViewHost } from "./views";

const SVG_NS = "http://www.w3.org/2000/svg";

function chip(label: string, on: boolean, click: () => void): HTMLElement {
  const b = h("button", { class: on ? "ec-chip on" : "ec-chip", text: label });
  b.addEventListener("click", click);
  return b;
}

function two(n: number): string {
  return String(n).padStart(2, "0");
}

/** A round clock with hour marks and three hands, drawn in SVG. */
function analogClock(now: Date): SVGSVGElement {
  const svg = document.createElementNS(SVG_NS, "svg");
  svg.setAttribute("viewBox", "0 0 100 100");
  svg.setAttribute("class", "ec-analog");
  const add = (tag: string, attrs: Record<string, string>) => {
    const el = document.createElementNS(SVG_NS, tag);
    for (const [k, v] of Object.entries(attrs)) el.setAttribute(k, v);
    svg.append(el);
  };
  add("circle", { cx: "50", cy: "50", r: "47", class: "ec-face" });
  for (let i = 0; i < 60; i++) {
    const major = i % 5 === 0;
    const a = (i / 60) * Math.PI * 2;
    const r1 = major ? 39 : 43;
    add("line", {
      x1: String(50 + Math.sin(a) * r1), y1: String(50 - Math.cos(a) * r1),
      x2: String(50 + Math.sin(a) * 45), y2: String(50 - Math.cos(a) * 45), class: major ? "ec-tick major" : "ec-tick",
    });
  }
  const sec = now.getSeconds();
  const min = now.getMinutes() + sec / 60;
  const hour = (now.getHours() % 12) + min / 60;
  const hand = (angle: number, length: number, cls: string) =>
    add("line", { x1: "50", y1: "50", x2: String(50 + Math.sin(angle) * length), y2: String(50 - Math.cos(angle) * length), class: cls });
  hand((hour / 12) * Math.PI * 2, 22, "ec-hand hour");
  hand((min / 60) * Math.PI * 2, 33, "ec-hand minute");
  hand((sec / 60) * Math.PI * 2, 38, "ec-hand second");
  add("circle", { cx: "50", cy: "50", r: "2.6", class: "ec-pin" });
  return svg;
}

export function buildEyeCare(): ViewHost {
  const badge = h("span", { class: "ec-badge" });
  const head = h("div", { class: "ec-head" }, h("b", { text: t("Eye care") }), badge);
  const clock = h("div", { class: "ec-clock" });
  const timers = h("div", { class: "ec-timers" });
  const stats = h("div", { class: "ec-stats" });
  const controls = h("div", { class: "ec-controls" });
  const note = h("div", { class: "ec-note" });
  const el = h("div", { class: "view" }, h("div", { class: "card ec-card" }, head, h("div", { class: "ec-top" }, clock, timers), stats, controls, note));

  let controlsKey = "";
  let timerBuilt = false;
  const timerParts: { value: HTMLElement; fill: HTMLElement; go: HTMLElement; title: HTMLElement; why: HTMLElement }[] = [];

  function buildTimers() {
    timerBuilt = true;
    clear(timers);
    timerParts.length = 0;
    const kinds: { kind: "micro" | "active" }[] = [{ kind: "micro" }, { kind: "active" }];
    for (const { kind } of kinds) {
      const title = h("div", { class: "ec-timer-title" });
      const value = h("div", { class: "ec-timer-value" });
      const fill = h("i", { class: "ec-bar-fill" });
      const why = h("div", { class: "ec-why" });
      const go = h("button", { class: "ec-link", text: kind === "micro" ? t("Take an eye break now") : t("Take an active pause now") });
      go.addEventListener("click", () => EyeCare.trigger(kind));
      timers.append(h("div", { class: "ec-timer" }, title, value, h("span", { class: "ec-bar" }, fill), why, go));
      timerParts.push({ title, value, fill, go, why });
    }
  }

  function drawControls(s: EyeCareState) {
    clear(controls);
    controls.append(
      h("div", { class: "ec-group" },
        chip(s.paused ? t("Resume reminders") : t("Pause reminders"), s.paused, () => EyeCare.setPaused(!s.paused)),
        chip(t("Sound"), s.sound, () => EyeCare.setSound(!s.sound))),
      h("div", { class: "ec-group" }, h("span", { class: "ec-dim", text: t("Clock") }),
        chip(t("Digital"), s.clock === "digital", () => EyeCare.setClock("digital")),
        chip(t("Analog"), s.clock === "analog", () => EyeCare.setClock("analog"))),
      h("div", { class: "ec-group" }, h("span", { class: "ec-dim", text: t("Eye break style") }),
        chip(t("Notice"), s.rest === "notice", () => EyeCare.setRest("notice")),
        chip(t("Animated eyes"), s.rest === "eyes", () => EyeCare.setRest("eyes")),
        chip(t("In the notch"), s.rest === "notch", () => EyeCare.setRest("notch"))),
      h("div", { class: "ec-group" }, h("span", { class: "ec-dim", text: t("Active pause style") }),
        chip(t("Notice"), s.activeRest === "notice", () => EyeCare.setActiveRest("notice")),
        chip(t("In the notch"), s.activeRest !== "notice", () => EyeCare.setActiveRest("notch"))),
      h("div", { class: "ec-group" }, h("span", { class: "ec-dim", text: t("Try the style") }),
        chip(t("Eye break"), false, () => EyeCare.test("micro")),
        chip(t("Active pause"), false, () => EyeCare.test("active"))));
  }

  return {
    el,
    sync() {
      const s = EyeCare.state;
      if (!s) {
        note.textContent = EyeCare.error;
        return;
      }
      note.textContent = EyeCare.error;
      badge.textContent = s.paused ? t("Paused") : t("Active");
      badge.classList.toggle("paused", s.paused);

      const now = new Date();
      clear(clock);
      if (s.clock === "analog") clock.append(analogClock(now));
      else clock.append(h("div", { class: "ec-digital", text: `${two(now.getHours())}:${two(now.getMinutes())}:${two(now.getSeconds())}` }));

      if (!timerBuilt) buildTimers();
      const rows = [
        { left: s.microLeft, every: s.microEvery, title: t("Next eye break"), why: t("Every 20 minutes: look 6 meters away for 20 seconds.") },
        { left: s.activeLeft, every: s.activeEvery, title: t("Next active pause"), why: t("Every 55 minutes: stand up and stretch for 5 minutes.") },
      ];
      rows.forEach((r, i) => {
        const part = timerParts[i];
        part.title.textContent = r.title;
        part.value.textContent = s.paused ? "—" : countdown(r.left);
        part.fill.style.width = `${Math.max(0, Math.min(100, (1 - r.left / r.every) * 100))}%`;
        part.why.textContent = r.why;
        part.go.textContent = i === 0 ? t("Take an eye break now") : t("Take an active pause now");
      });

      clear(stats);
      const stat = (label: string, value: string) => h("div", { class: "ec-stat" }, h("b", { text: value }), h("span", { text: label }));
      stats.append(stat(t("Worked today"), worked(s.workToday)), stat(t("Eye breaks"), String(s.micro)), stat(t("Active pauses"), String(s.active)));

      const key = JSON.stringify([s.paused, s.sound, s.clock, s.rest, s.activeRest]);
      if (key !== controlsKey) {
        controlsKey = key;
        drawControls(s);
      }
    },
  };
}
