# SPDX-License-Identifier: GPL-3.0-or-later
"""Zufahrten zu Industrie-, Gewerbe- und Firmenflächen.

In allen Detailstufen werden Flächen, die nahe am gewählten Netz liegen, über den
kürzesten Weg aus öffentlichen Straßen angebunden – in ETS2 sitzen dort später die
Firmen (Auftragsziele). Ziele:
  * Flächen (geschlossene Wege): landuse=industrial|commercial|retail|quarry|farmyard|port,
    industrial=*, man_made=works, building=warehouse|industrial, craft=*
  * Punkte (Knoten) mit industrial=*, man_made=works, craft=*, office=company
Multipolygon-Relationen werden (noch) nicht ausgewertet.

Dazu Rast- und Tankanlagen (``service_target``): amenity=fuel, highway=services|rest_area
und Lkw-Parkplätze (amenity=parking mit hgv=yes|designated). Sie liegen oft direkt an der
Autobahn und werden deshalb auch von deren Fahrbahn aus angebunden – und zwar über alle
Zu- und Abfahrten (Schleifen), nicht nur über die nächste (siehe connect_services).
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


def is_service_target(tags) -> bool:
    return (tags.get("amenity") == "fuel" or tags.get("highway") in ("services", "rest_area")
            or (tags.get("amenity") == "parking" and tags.get("hgv") in ("yes", "designated")))


def is_target(tags) -> bool:
    return (tags.get("landuse") in AREA_LANDUSE or "industrial" in tags
            or tags.get("man_made") == "works" or tags.get("building") in AREA_BUILDING
            or "craft" in tags or tags.get("office") == "company" or is_service_target(tags))


@dataclass
class Target:
    id: str
    kind: str
    nodes: list = field(default_factory=list)   # Umriss (Knoten-IDs) oder ein Knoten
    poly: list = field(default_factory=list)    # [(x, y)] in Metern (lokal)
    service: bool = False                       # Rast-/Tankanlage


def read_targets(path: str) -> tuple[list[Target], set[int]]:
    targets: list[Target] = []
    need: set[int] = set()
    fp = osmium.FileProcessor(path, osmium.osm.NODE | osmium.osm.WAY)
    for o in fp:
        if not is_target(o.tags):
            continue
        svc = is_service_target(o.tags)
        if svc:
            kind = o.tags.get("amenity") if o.tags.get("amenity") in ("fuel", "parking") else o.tags.get("highway")
        else:
            kind = (o.tags.get("landuse") or o.tags.get("industrial") or o.tags.get("man_made")
                    or o.tags.get("building") or o.tags.get("craft") or o.tags.get("office") or "?")
        if o.is_node():
            targets.append(Target(f"n{o.id}", kind, [o.id], service=svc))
            need.add(o.id)
        elif o.is_way():
            refs = [n.ref for n in o.nodes]
            if len(refs) >= 4 and refs[0] == refs[-1]:
                targets.append(Target(f"w{o.id}", kind, refs, service=svc))
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
        if t.service:
            continue   # Rast-/Tankanlagen: connect_services
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


def connect_services(net, network_nodes: set[int], targets: list[Target], loc: dict, classes: frozenset,
                     max_dist: float, point_radius: float = 80.0, edge_tol: float = 40.0,
                     area_cap: float = 5000.0) -> dict:
    """Rast- und Tankanlagen anbinden (Rolle 'connector').

    Anders als bei Firmen: Start an jedem Netzknoten, auch auf der Autobahnfahrbahn (Rast-
    anlagen hängen direkt an ihr), und alle Zu- und Abfahrten innerhalb max_dist werden
    aufgenommen – so bleibt die Schleife durch Tankstelle oder Parkplatz befahrbar.
    """
    from .network import ROLE_CONNECTOR

    svc = [t for t in targets if t.service]
    if not loc or not svc:
        return {"targets": len(svc), "connected": 0, "edges": 0}
    lat0 = sum(v[1] for v in loc.values()) / len(loc)
    xy = {n: _local(lo, la, lat0) for n, (lo, la) in loc.items()}

    def edge_len(e):
        ids = e.node_ids()
        s = 0.0
        for a, b in zip(ids, ids[1:]):
            if a not in xy or b not in xy:
                return None
            s += math.dist(xy[a], xy[b])
        return s

    usable = {}
    for e in net.edges:
        if e.role is None and e.highway in classes:
            L = edge_len(e)
            if L is not None:
                usable[e.idx] = L
    road_nodes = set()
    for ei in usable:
        road_nodes.update(net.edges[ei].node_ids())
    road_nodes |= set(network_nodes)

    marked: set[int] = set()
    connected = 0
    for t in svc:
        pts = [xy[n] for n in t.nodes if n in xy]
        if not pts:
            continue
        if len(pts) == 1:
            c = pts[0]
            def inside(p, c=c):
                return math.dist(p, c) <= point_radius
        else:
            poly = pts
            xs = [p[0] for p in poly]; ys = [p[1] for p in poly]
            bb = (min(xs) - edge_tol, min(ys) - edge_tol, max(xs) + edge_tol, max(ys) + edge_tol)
            def inside(p, poly=poly, bb=bb):
                return (bb[0] <= p[0] <= bb[2] and bb[1] <= p[1] <= bb[3]
                        and (_inside(p, poly) or _near(p, poly, edge_tol)))
        seeds = [n for n in road_nodes if n in xy and inside(xy[n])]
        if not seeds:
            continue
        # ganze Anlage: zusammenhängende Straßen im/am Umriss
        area_edges: set[int] = set()
        seen, todo, total = set(seeds), list(seeds), 0.0
        while todo:
            a = todo.pop()
            for ei, end in net.adjacency.get(a, ()):
                L = usable.get(ei)
                if L is None or ei in area_edges:
                    continue
                b = net.other_end(net.edges[ei], end)
                if b not in xy or not inside(xy[b]) or total + L > area_cap:
                    continue
                area_edges.add(ei); total += L
                if b not in seen:
                    seen.add(b); todo.append(b)
        # alle Wege von der Anlage zum Netz (höchstens max_dist), nicht durch das Netz hindurch
        dist = {n: 0.0 for n in seen}
        prev: dict = {}
        heap = [(0.0, n) for n in seen]
        heapq.heapify(heap)
        hits = []
        while heap:
            d, n = heapq.heappop(heap)
            if d > dist.get(n, math.inf):
                continue
            if n in network_nodes:
                hits.append(n)
                continue
            for ei, end in net.adjacency.get(n, ()):
                L = usable.get(ei)
                if L is None:
                    continue
                m = net.other_end(net.edges[ei], end)
                nd = d + L
                if nd <= max_dist and nd < dist.get(m, math.inf):
                    dist[m] = nd
                    prev[m] = (n, ei)
                    heapq.heappush(heap, (nd, m))
        if not hits:
            continue
        connected += 1
        marked |= area_edges
        anchors = set(network_nodes)
        for h in hits:
            node = h
            while node in prev:
                p, ei = prev[node]
                marked.add(ei)
                anchors.add(node)
                node = p
            anchors.add(node)
        # weitere Zu-/Abfahrten der Anlage: jede Straße, die aus ihr hinausführt und nach
        # höchstens max_dist auf das Netz oder die schon gewählte Zufahrt trifft (Schleife)
        for a in list(seen):
            for ei, end in net.adjacency.get(a, ()):
                if ei in marked or usable.get(ei) is None:
                    continue
                b = net.other_end(net.edges[ei], end)
                if b in seen:
                    continue
                d0 = usable[ei]
                if b in anchors:
                    marked.add(ei)
                    continue
                dist2 = {b: d0}
                prev2: dict = {}
                heap2 = [(d0, b)]
                found = None
                while heap2:
                    d, n = heapq.heappop(heap2)
                    if d > dist2.get(n, math.inf):
                        continue
                    if n in anchors:
                        found = n
                        break
                    for ej, en in net.adjacency.get(n, ()):
                        L = usable.get(ej)
                        if L is None:
                            continue
                        m = net.other_end(net.edges[ej], en)
                        if m in seen:
                            continue
                        nd = d + L
                        if nd <= max_dist and nd < dist2.get(m, math.inf):
                            dist2[m] = nd
                            prev2[m] = (n, ej)
                            heapq.heappush(heap2, (nd, m))
                if found is None:
                    continue
                marked.add(ei)
                node = found
                while node in prev2:
                    p, ej = prev2[node]
                    marked.add(ej)
                    anchors.add(node)
                    node = p
    for ei in marked:
        net.edges[ei].role = ROLE_CONNECTOR
    log.info("Rast-/Tankanlagen: %d von %d angebunden, %d Kanten", connected, len(svc), len(marked))
    return {"targets": len(svc), "connected": connected, "edges": len(marked)}
