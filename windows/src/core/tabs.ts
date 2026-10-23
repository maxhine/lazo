// The island's header tabs (overview, ask, drop and tools) in the order the person arranged them by dragging.

export const TAB_IDS = ["home", "chat", "drop", "tools"] as const;
export type TabId = (typeof TAB_IDS)[number];

/** The saved order made whole: unknown ids and repeats dropped, any tab it forgot added at the end in its usual place. */
export function tabOrder(saved: unknown): TabId[] {
  const order: TabId[] = [];
  if (Array.isArray(saved)) {
    for (const id of saved) if ((TAB_IDS as readonly unknown[]).includes(id) && !order.includes(id as TabId)) order.push(id as TabId);
  }
  for (const id of TAB_IDS) if (!order.includes(id)) order.push(id);
  return order;
}

/**
 * Where a tab dragged to `x` lands among the others: the index of the first one whose middle is to its right
 * (`centers` are the other tabs' middles, left to right, without the dragged one).
 */
export function dropIndex(x: number, centers: number[]): number {
  const i = centers.findIndex((c) => x < c);
  return i < 0 ? centers.length : i;
}
