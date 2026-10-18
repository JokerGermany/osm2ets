# SPDX-License-Identifier: GPL-3.0-or-later
"""Zufahrten zu Industrie-, Gewerbe- und Firmenflächen.

In allen Detailstufen werden Flächen, die nahe am gewählten Netz liegen, über den
kürzesten Weg aus öffentlichen Straßen angebunden – in ETS2 sitzen dort später die
Firmen (Auftragsziele). Ziele:
  * Flächen (geschlossene Wege): landuse=industrial|commercial|retail|quarry|farmyard|port,
    industrial=*, man_made=works, building=warehouse|industrial, craft=*
  * Punkte (Knoten) mit industrial=*, man_made=works, craft=*, office=company
Multipolygon-Relationen werden (noch) nicht ausgewertet.
"""
from __future__ import annotations

import heapq
import logging
import math
from dataclasses import dataclass, field

import osmium

log = logging.getLogger(__name__)

AREA_LANDUSE = {"industrial", "commercial", "retail", "quarry", "farmyard", "port"}
AREA_BUILDING = {"warehouse", "industrial"}


def is_target(tags) -> bool:
    return (tags.get("landuse") in AREA_LANDUSE or "industrial" in tags
            or tags.get("man_made") == "works" or tags.get("building") in AREA_BUILDING
            or "craft" in tags or tags.get("office") == "company")


@dataclass
class Target:
    id: str
    kind: str
    nodes: list = field(default_factory=list)   # Umriss (Knoten-IDs) oder ein Knoten
    poly: list = field(default_factory=list)    # [(x, y)] in Metern (lokal)


def read_targets(path: str) -> tuple[list[Target], set[int]]:
    targets: list[Target] = []
    need: set[int] = set()
    fp = osmium.FileProcessor(path, osmium.osm.NODE | osmium.osm.WAY)
    for o in fp:
        if not is_target(o.tags):
            continue
        kind = (o.tags.get("landuse") or o.tags.get("industrial") or o.tags.get("man_made")
                or o.tags.get("building") or o.tags.get("craft") or o.tags.get("office") or "?")
        if o.is_node():
            targets.append(Target(f"n{o.id}", kind, [o.id]))
            need.add(o.id)
        elif o.is_way():
            refs = [n.ref for n in o.nodes]
            if len(refs) >= 4 and refs[0] == refs[-1]:
                targets.append(Target(f"w{o.id}", kind, refs))
                need.update(refs)
    return targets, need


def _local(lon, lat, lat0):
    k = 111320.0
    return (lon * k * math.cos(math.radians(lat0)), lat * k)


def _inside(pt, poly) -> bool:
    x, y = pt
    c = False
    for (x1, y1), (x2, y2) in zip(poly, poly[1:]):
        if (y1 > y) != (y2 > y) and x < (x2 - x1) * (y - y1) / (y2 - y1 + 1e-12) + x1:
            c = not c
    return c


def _near(pt, poly, tol) -> bool:
    """Punkt höchstens tol vom Umriss entfernt (Straße entlang des Gebietsrands)."""
    px, py = pt
    for (x1, y1), (x2, y2) in zip(poly, poly[1:]):
        dx, dy = x2 - x1, y2 - y1
        l2 = dx * dx + dy * dy
        t = 0.0 if l2 == 0 else max(0.0, min(1.0, ((px - x1) * dx + (py - y1) * dy) / l2))
        if math.hypot(px - (x1 + t * dx), py - (y1 + t * dy)) <= tol:
            return True
    return False


def connect(net, network_nodes: set[int], targets: list[Target], loc: dict, classes: frozenset,
            max_dist: float, inside: float, point_radius: float = 80.0, edge_tol: float = 40.0,
            no_start: set[int] | None = None, area_cap: float = 5000.0) -> dict:
    """Markiert Kanten als Zufahrt (Rolle 'connector'). Gibt Kennzahlen zurück."""
    from .network import ROLE_CONNECTOR

    if not loc or not targets:
        return {"targets": len(targets), "connected": 0, "edges": 0}
    lat0 = sum(v[1] for v in loc.values()) / len(loc)
    xy = {n: _local(lo, la, lat0) for n, (lo, la) in loc.items()}

    def edge_len(e):
        ids = e.node_ids()
        s = 0.0
        for a, b in zip(ids, ids[1:]):
            if a in xy and b in xy:
                s += math.dist(xy[a], xy[b])
            else:
                return None
        return s

    usable = {}
    for e in net.edges:
        if e.role is None and e.highway in classes:
            L = edge_len(e)
            if L is not None:
                usable[e.idx] = L

    # Ziele: Umriss bzw. Punkt, grobes Gitter zum Nachschlagen
    cell = 200.0
    grid: dict = {}
    for ti, t in enumerate(targets):
        pts = [xy[n] for n in t.nodes if n in xy]
        if not pts:
            continue
        t.poly = pts
        xs = [p[0] for p in pts]; ys = [p[1] for p in pts]
        pad = point_radius if len(pts) == 1 else edge_tol
        for gx in range(int((min(xs) - pad) // cell), int((max(xs) + pad) // cell) + 1):
            for gy in range(int((min(ys) - pad) // cell), int((max(ys) + pad) // cell) + 1):
                grid.setdefault((gx, gy), []).append(ti)

    def hit(node):
        p = xy.get(node)
        if p is None:
            return None
        for ti in grid.get((int(p[0] // cell), int(p[1] // cell)), ()):
            t = targets[ti]
            if len(t.poly) == 1:
                if math.dist(p, t.poly[0]) <= point_radius:
                    return ti
            elif _inside(p, t.poly) or _near(p, t.poly, edge_tol):
                return ti
        return None

    # Dijkstra vom Netz aus über Zufahrtsklassen; Abstand begrenzt
    # Start nur an Knoten, an denen öffentlich abgebogen werden kann (nicht mitten auf der
    # Autobahn: eine Firma neben der Autobahn ohne nahe Ausfahrt wird nicht angebunden)
    network_nodes = set(network_nodes) - (no_start or set())
    dist = {n: 0.0 for n in network_nodes}
    prev: dict = {}
    heap = [(0.0, n) for n in network_nodes]
    heapq.heapify(heap)
    reached: dict = {}            # Ziel -> (Knoten, Abstand)
    while heap:
        d, n = heapq.heappop(heap)
        if d > dist.get(n, math.inf):
            continue
        ti = hit(n)
        if ti is not None and ti not in reached:
            reached[ti] = (n, d)
        for ei, end in net.adjacency.get(n, ()):
            L = usable.get(ei)
            if L is None:
                continue
            e = net.edges[ei]
            m = net.other_end(e, end)
            nd = d + L
            if nd > max_dist + inside:
                continue
            if nd < dist.get(m, math.inf):
                dist[m] = nd
                prev[m] = (n, ei)
                heapq.heappush(heap, (nd, m))

    marked: set[int] = set()

    def mark(node):
        while node in prev:
            p, ei = prev[node]
            marked.add(ei)
            node = p

    connected = 0
    for ti, (n, d) in reached.items():
        if d > max_dist:
            continue
        connected += 1
        mark(n)
        t = targets[ti]
        if len(t.poly) > 1:
            # ganzes Gebiet: alle Zufahrtsstraßen im/am Umriss, die vom Eintritt aus
            # zusammenhängend erreichbar sind (höchstens area_cap Meter)
            seen, todo, total = {n}, [n], 0.0
            while todo:
                a = todo.pop()
                for ei, end in net.adjacency.get(a, ()):
                    L = usable.get(ei)
                    if L is None or ei in marked:
                        continue
                    b = net.other_end(net.edges[ei], end)
                    pb = xy.get(b)
                    if pb is None or not (_inside(pb, t.poly) or _near(pb, t.poly, edge_tol)):
                        continue
                    if total + L > area_cap:
                        continue
                    marked.add(ei); total += L
                    if b not in seen:
                        seen.add(b); todo.append(b)
        if False:
            best, bd = n, d
            for m, dm in dist.items():
                if d < dm <= d + inside and dm > bd and _inside(xy[m], t.poly):
                    k = m
                    while k in prev and k != n:
                        k = prev[k][0]
                    if k == n:
                        best, bd = m, dm
            mark(best)
    for ei in marked:
        net.edges[ei].role = ROLE_CONNECTOR
    log.info("Zufahrten: %d von %d Zielen angebunden, %d Kanten (nutzbar %d, erreicht %d Knoten, Start %d)", connected, len(targets), len(marked), len(usable), len(dist), len(network_nodes))
    return {"targets": len(targets), "connected": connected, "edges": len(marked)}
