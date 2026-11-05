# TODO

- **Cut next to the 4-lane A 9 section** (x 4610-4625, z -820 to -860): since the A 9 is built
  with 4 lanes (ger18) there, the farm road on top of the cut has a 4-5 m step at its edge
  (road terrain of the A 9 and of the farm road do not meet). Also small walls where the
  2-lane link (A 70 -> A 9 south) runs next to the farm road (x 4522-4525, z -1355 to -1393).
- **ger18 model extents** (4 lanes, assumed -9.0 ... +12.5 m around the line) are not measured.
- **5-lane section after the A 70 -> A 9 south entry** (node 198339441, ~128 m with 5 lanes in
  OSM, then 4): find or build a prefab 3 lanes + 2-lane ramp -> 5 lanes (both ramp lanes kept)
  and a lane end 5 -> 4. Road look for 5 lanes exists (ger28 "ger hw5 one lane tmpl"); no German
  prefab found so far. Currently ger_2i037 joins the two ramp lanes into one (3 + 2 -> 4).
- **Entry 3 + 1 -> 3 for 3-lane motorways** (ramp merging into the right lane, like
  `ger_hw3_r2_merge_double` but for a 1-lane ramp and without extra lane): find or build it.
  Known German merges all end with 4 lanes (ch_4202j 3 + 1 -> 4, ger_2i037 3 + 2 -> 4).
  Lower priority since ch_4202s ends the 4th lane; only needed where OSM has no acceleration
  lane. Look in the editor's prefab browser ("hw3", "merge") for names/tokens.
- **B 85 slip lanes** (x -3700, z -3230): loops of chained T prefabs (mod_ger_67), node
  2166477040 without prefab – waiting for a decision.
