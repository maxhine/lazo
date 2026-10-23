// The point cloud formats the notch recognises, and the ones the converter reads.
import { test } from "node:test";
import assert from "node:assert/strict";
import { isPointCloud, convertible, POINT_CLOUD_EXTENSIONS } from "../src/core/cloud-formats.ts";

test("the usual point cloud formats are recognised, whatever the case", () => {
  for (const name of ["a.las", "B.LAZ", "c.e57", "d.pts", "e.ptx", "f.xyz", "g.pcd", "h.ply", "i.rcp", "j.rcs", "k.fls", "l.fws", "m.zfs", "n.rdbx", "o.rxp", "p.pod", "q.ptg", "r.cl3"]) {
    assert.equal(isPointCloud("C:/datos/" + name), true, name);
  }
});

test("other files are not point clouds", () => {
  for (const name of ["a.pdf", "b.txt", "c.dwg", "d.rvt", "e.jpg", "las", "f.las.bak", "g.tps", ""]) assert.equal(isPointCloud(name), false, name);
});

test("only LAS, LAZ and E57 can be converted", () => {
  assert.equal(convertible("a.las"), true);
  assert.equal(convertible("a.LAZ"), true);
  assert.equal(convertible("a.e57"), true);
  assert.equal(convertible("a.rcp"), false);
  assert.equal(convertible("a.ply"), false);
});

test("every convertible format is also a recognised one, and the list has no repeats", () => {
  for (const e of [".las", ".laz", ".e57"]) assert.ok(POINT_CLOUD_EXTENSIONS.includes(e));
  assert.equal(new Set(POINT_CLOUD_EXTENSIONS).size, POINT_CLOUD_EXTENSIONS.length);
});
