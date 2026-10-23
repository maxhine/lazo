// What Nudo says when Lazo opens: a hello that fits the time of day, in a few variants, and then the person's name:
// "Buenos días, Dr. Sins" at nine, "Buenas noches, Dr. Sins" at ten at night, or just "Hola, Dr. Sins".

import { t } from "../i18n/i18n";

export type Period = "morning" | "afternoon" | "evening" | "night";

/** 5:00–11:59 morning, 12:00–17:59 afternoon, 18:00–22:59 evening, the rest night. */
export function period(hour: number): Period {
  if (hour >= 5 && hour < 12) return "morning";
  if (hour >= 12 && hour < 18) return "afternoon";
  if (hour >= 18 && hour < 23) return "evening";
  return "night";
}

/** The hellos of each part of the day: the usual one, the plain "Hello" and one more. */
function hellos(p: Period): string[] {
  switch (p) {
    case "morning":
      return [t("Good morning"), t("Hello"), t("Have a great morning")];
    case "afternoon":
      return [t("Good afternoon"), t("Hello"), t("Hope your afternoon is going well")];
    case "evening":
      return [t("Good evening"), t("Hello"), t("Good to see you")];
    default:
      return [t("Good night"), t("Hello"), t("Working late")];
  }
}

/**
 * The line for `now`. `pick` chooses the variant (the island passes a number that changes from launch to launch; the
 * first variant is always the proper "good morning/afternoon/evening/night"). The name, when there is one, follows a comma.
 */
export function greetingLine(now: Date, name: string | null | undefined, pick: number): string {
  const options = hellos(period(now.getHours()));
  const hello = options[((pick % options.length) + options.length) % options.length];
  const who = (name ?? "").trim();
  return who ? `${hello}, ${who}` : hello;
}
