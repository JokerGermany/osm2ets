# SPDX-License-Identifier: GPL-3.0-or-later
"""Geländeraster für flächiges Gelände zwischen den Straßen.

Ergebnis (im Zwischenformat unter ``terrain_grid``): regelmäßiges Raster in
Spielkoordinaten mit Höhen in Spielmetern. Das Oberflächenmodell (z. B.
Copernicus, mit Bäumen und Gebäuden) wird bodennah gefiltert und geglättet; nahe
an Straßen geht die Höhe weich in die Straßenhöhe über, damit das Gelände an den
Geländestreifen der Straßen anschließt. Brücken und Tunnel zählen dabei nicht.
"""
from __future__ import annotations

import numpy as np


def build_grid(roads: list[dict], dem, profile, cell: float = 10.0, margin: float = 150.0,
               blend_from: float = 24.0, blend_to: float = 110.0) -> dict | None:
    if dem is None or not roads:
        return None
    from scipy import ndimage
    from scipy.spatial import cKDTree

    pts = np.concatenate([np.asarray(r["points"], float) for r in roads])
    x0, x1 = pts[:, 0].min() - margin, pts[:, 0].max() + margin
    z0, z1 = pts[:, 2].min() - margin, pts[:, 2].max() + margin
    nx = int(np.ceil((x1 - x0) / cell)) + 1
    nz = int(np.ceil((z1 - z0) / cell)) + 1
    gx = x0 + np.arange(nx) * cell
    gz = z0 + np.arange(nz) * cell
    X, Z = np.meshgrid(gx, gz)                      # [nz, nx]
    # Spiel → projiziert (echt): x = x·Maßstab, y(Nord) = −z·Maßstab
    xy = np.column_stack([X.ravel() * profile.scale_h, -Z.ravel() * profile.scale_h])
    h = dem.sample(xy).reshape(nz, nx) / profile.scale_v

    # bodennah: 25-%-Perzentil über ~60 m, dann glätten
    size = max(3, int(round(60.0 / cell)) | 1)
    h = ndimage.percentile_filter(h, 25, size=size)
    h = ndimage.gaussian_filter(h, sigma=max(1.0, 40.0 / cell))

    # Straßenhöhen (ohne Bauwerke) dicht abtasten
    samples, heights = [], []
    for r in roads:
        p = np.asarray(r["points"], float)
        seg_ok = np.zeros(len(p) - 1, bool)
        for run in r["runs"]:
            if not (run.get("bridge") or run.get("tunnel")):
                seg_ok[run["from"]:run["to"]] = True
        for i in np.flatnonzero(seg_ok):
            a, b = p[i], p[i + 1]
            n = max(2, int(np.hypot(b[0] - a[0], b[2] - a[2]) / 5.0) + 1)
            t = np.linspace(0, 1, n)[:, None]
            q = a + (b - a) * t
            samples.append(q[:, [0, 2]])
            heights.append(q[:, 1])
    if samples:
        s = np.concatenate(samples)
        sh = np.concatenate(heights)
        d, idx = cKDTree(s).query(np.column_stack([X.ravel(), Z.ravel()]))
        d = d.reshape(nz, nx)
        road_h = sh[idx].reshape(nz, nx)
        # Damm/Einschnitt: bis blend_from auf Straßenhöhe (Straßengelände ist dort flach),
        # danach Böschung 1:1,5 bis zur Geländehöhe; ganz außen weich ins Gelände
        run = np.maximum(d - blend_from, 0.0) / 1.5
        dam = np.where(road_h > h, np.maximum(h, road_h - run), np.minimum(h, road_h + run))
        w = np.clip((d - blend_from) / (blend_to - blend_from), 0.0, 1.0)
        w = w * w * (3 - 2 * w)
        h = np.where(d <= blend_from, road_h, dam * (1 - w) + np.where(np.abs(dam - h) < 0.01, h, dam) * w)

    return {
        "origin": [round(float(x0), 2), round(float(z0), 2)],
        "cell": cell,
        "nx": nx,
        "nz": nz,
        # zeilenweise (z), je Zeile nx Werte
        "heights": [round(float(v), 2) for v in h.ravel()],
    }
