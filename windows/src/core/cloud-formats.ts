// The point cloud formats the notch knows. A file in one of them dropped on the island asks whether to convert it or share it.
// Only some can be converted (the converter reads LAS, LAZ and E57); the others are still recognised and can be shared.

import { CLOUD_EXTENSIONS } from "./cloud";

/**
 * Every point cloud format the notch recognises, by who makes it: the open ones (LAS, LAZ, E57, PTS, PTX, XYZ, PCD, PLY),
 * Autodesk ReCap (RCP, RCS), Leica (PTG, CL3, LGS), FARO (FLS, FWS, FPR), Trimble (TZF), Z+F (ZFS, ZFPRJ), RIEGL (RDBX, RXP,
 * RSP), Bentley (POD, PODX) and Entwine (EPT).
 */
export const POINT_CLOUD_EXTENSIONS = [
  ".las", ".laz", ".e57",
  ".pts", ".ptx", ".xyz", ".xyzrgb", ".xyzn", ".pcd", ".ply",
  ".rcp", ".rcs",
  ".ptg", ".cl3", ".lgs", ".fls", ".fws", ".fpr", ".tzf", ".zfs", ".zfprj",
  ".rdbx", ".rxp", ".rsp", ".pod", ".podx", ".ept",
] as const;

const extension = (path: string) => {
  const dot = path.lastIndexOf(".");
  return dot < 0 ? "" : path.slice(dot).toLowerCase();
};

/** A file in one of the point cloud formats. */
export function isPointCloud(path: string): boolean {
  return (POINT_CLOUD_EXTENSIONS as readonly string[]).includes(extension(path));
}

/** A point cloud the converter reads (LAS, LAZ, E57). */
export function convertible(path: string): boolean {
  return CLOUD_EXTENSIONS.includes(extension(path));
}
