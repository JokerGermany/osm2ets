# SPDX-License-Identifier: GPL-3.0-or-later
"""Höhenfreie Kreuzungen finden und prüfen.

Zwei Straßen, die sich in der Draufsicht schneiden, ohne einen Knoten zu
teilen, kreuzen sich höhenfrei (Brücke/Unterführung) – oder die OSM-Daten
haben dort einen Fehler. Dieses Modul findet solche Stellen.
"""
from __future__ import annotations

import math
from collections import defaultdict
from typing import Iterable

import numpy as np


def _seg_intersection(p1, p2, q1, q2):
    """Schnitt zweier 2D-Strecken im Inneren beider; gibt (t, u) zurück oder None."""
    r = p2 - p1
    s = q2 - q1
    denom = r[0] * s[1] - r[1] * s[0]
    if abs(denom) < 1e-12:
        return None
    qp = q1 - p1
    t = (qp[0] * s[1] - qp[1] * s[0]) / denom
    u = (qp[0] * r[1] - qp[1] * r[0]) / denom
    eps = 1e-9
    if eps < t < 1 - eps and eps < u < 1 - eps:
        return t, u
    return None


def find_crossings(lines: list[np.ndarray], cell: float = 200.0) -> list[tuple[int, int, float, int, int, float]]:
    """Alle Schnitte zwischen Polylinien (N×2-Arrays).

    Rückgabe: (Linie a, Segment a, t, Linie b, Segment b, u) – der Schnittpunkt
    liegt bei a[sa] + t·(a[sa+1]-a[sa]). Berührungen an Endpunkten zählen nicht.
    """
    segs: list[tuple[int, int]] = []
    grid: dict[tuple[int, int], list[int]] = defaultdict(list)
    for li, pts in enumerate(lines):
        for si in range(len(pts) - 1):
            k = len(segs)
            segs.append((li, si))
            a, b = pts[si], pts[si + 1]
            x0, x1 = (a[0], b[0]) if a[0] <= b[0] else (b[0], a[0])
            y0, y1 = (a[1], b[1]) if a[1] <= b[1] else (b[1], a[1])
            for gx in range(math.floor(x0 / cell), math.floor(x1 / cell) + 1):
                for gy in range(math.floor(y0 / cell), math.floor(y1 / cell) + 1):
                    grid[(gx, gy)].append(k)
    seen: set[tuple[int, int]] = set()
    out = []
    for bucket in grid.values():
        n = len(bucket)
        for x in range(n):
            i = bucket[x]
            la, sa = segs[i]
            a0, a1 = lines[la][sa], lines[la][sa + 1]
            for y in range(x + 1, n):
                j = bucket[y]
                lb, sb = segs[j]
                if la == lb and abs(sa - sb) <= 1:
                    continue
                key = (i, j) if i < j else (j, i)
                if key in seen:
                    continue
                seen.add(key)
                hit = _seg_intersection(a0, a1, lines[lb][sb], lines[lb][sb + 1])
                if hit is not None:
                    out.append((la, sa, hit[0], lb, sb, hit[1]))
    return out


def crossing_clearances(roads: list[dict], cell: float = 200.0) -> list[dict]:
    """Höhenabstand an allen höhenfreien Kreuzungen der fertigen Straßen
    (Spielkoordinaten), aufsteigend sortiert."""
    pts3 = [np.asarray(r["points"], float) for r in roads]
    lines = [p[:, [0, 2]] for p in pts3]
    out = []
    for la, sa, t, lb, sb, u in find_crossings(lines, cell):
        a, b = pts3[la], pts3[lb]
        ya = a[sa, 1] + t * (a[sa + 1, 1] - a[sa, 1])
        yb = b[sb, 1] + u * (b[sb + 1, 1] - b[sb, 1])
        p = a[sa] + t * (a[sa + 1] - a[sa])
        out.append({
            "roads": [roads[la]["id"], roads[lb]["id"]],
            "position": [round(float(p[0]), 2), round(float(p[2]), 2)],
            "clearance_m": round(abs(float(ya - yb)), 2),
        })
    return sorted(out, key=lambda c: c["clearance_m"])


def summarize(values: Iterable[float]) -> dict:
    arr = np.asarray(list(values), float)
    if not len(arr):
        return {"count": 0}
    return {"count": int(len(arr)), "min": float(arr.min()), "max": float(arr.max())}
