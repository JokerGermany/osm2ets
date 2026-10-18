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

    # Straßenhöhen (ohne Bauwerke) dicht abtasten, je Straße getrennt
    per_road = []
    for r in roads:
        p = np.asarray(r["points"], float)
        seg_ok = np.zeros(len(p) - 1, bool)
        for run in r["runs"]:
            if not (run.get("bridge") or run.get("tunnel")):
                seg_ok[run["from"]:run["to"]] = True
        xy, hh = [], []
        for i in np.flatnonzero(seg_ok):
            a, b = p[i], p[i + 1]
            n = max(2, int(np.hypot(b[0] - a[0], b[2] - a[2]) / 5.0) + 1)
            t = np.linspace(0, 1, n)[:, None]
            q = a + (b - a) * t
            xy.append(q[:, [0, 2]])
            hh.append(q[:, 1])
        if xy:
            per_road.append((np.concatenate(xy), np.concatenate(hh)))
    if per_road:
        h = blend_roads(np.column_stack([X.ravel(), Z.ravel()]), per_road, h.ravel(),
                        blend_from, blend_to).reshape(nz, nx)

    return {
        "origin": [round(float(x0), 2), round(float(z0), 2)],
        "cell": cell,
        "nx": nx,
        "nz": nz,
        # zeilenweise (z), je Zeile nx Werte
        "heights": [round(float(v), 2) for v in h.ravel()],
    }


def blend_roads(q: np.ndarray, per_road: list, dem_h: np.ndarray, blend_from: float, blend_to: float,
                core: float = 6.0, sigma_along: float = 5.0) -> np.ndarray:
    """Geländehöhe nahe Straßen: stetiger Übergang zwischen mehreren Straßen.

    Früher galt die Höhe der *nächsten* Straße – auf der Mittelsenkrechten zwischen zwei
    unterschiedlich hohen Straßen entstand so eine Klippe (bis 12 m in einer Rasterzelle).
    Jetzt: je Straße eine entlang der Straße geglättete Höhe h_r (Gauß über die nächsten
    Abtastpunkte dieser Straße), dazwischen inverse Abstandsgewichtung
    u_r = 1/max(d_r − core, 0.25) · (1 − smoothstep(d_r / R)) · (1 + 100·(1 − d_r/core)²) mit endlicher Reichweite R
    (stetig, unabhängig davon, welche Straßen gerade „in der Nähe“ sind). Bis ``core`` Meter
    an einer Straße bleibt deren Höhe praktisch exakt. Weiter weg wie bisher weich ins
    Geländemodell (blend_from … blend_to, Abstand zur nächsten Straße).
    """
    from scipy.spatial import cKDTree

    R = blend_to + 30.0
    n = len(q)
    usum = np.zeros(n)
    uh = np.zeros(n)
    dmin = np.full(n, np.inf)
    for xy, hh in per_road:
        k = int(min(10, len(xy)))
        lo = xy.min(0) - R; hi = xy.max(0) + R
        sel = np.flatnonzero((q[:, 0] >= lo[0]) & (q[:, 0] <= hi[0]) & (q[:, 1] >= lo[1]) & (q[:, 1] <= hi[1]))
        if len(sel) == 0:
            continue
        d, idx = cKDTree(xy).query(q[sel], k=k, distance_upper_bound=R)
        if k == 1:
            d = d[:, None]; idx = idx[:, None]
        okl = np.isfinite(d[:, 0])
        if not okl.any():
            continue
        ok = sel[okl]
        d = d[okl]; idx = idx[okl]
        valid = np.isfinite(d)
        idx = np.where(valid, idx, 0)
        d0 = d[:, 0]
        wa = np.where(valid, np.exp(-(np.where(valid, d, 0) ** 2 - d0[:, None] ** 2) / (2 * sigma_along ** 2)), 0.0)
        hr = (wa * hh[idx]).sum(1) / wa.sum(1)
        t = np.clip(d0 / R, 0.0, 1.0)
        c = np.clip(1.0 - d0 / core, 0.0, 1.0)
        u = (1.0 / np.maximum(d0 - core, 0.25)) * (1.0 - t * t * (3 - 2 * t)) * (1.0 + 100.0 * c * c)
        usum[ok] += u
        uh[ok] += u * hr
        dmin[ok] = np.minimum(dmin[ok], d0)
    road_h = np.where(usum > 0, uh / np.maximum(usum, 1e-12), dem_h)
    w = np.clip((dmin - blend_from) / (blend_to - blend_from), 0.0, 1.0)
    w = np.where(np.isfinite(dmin), w * w * (3 - 2 * w), 1.0)   # weicher Übergang
    return road_h * (1 - w) + dem_h * w
