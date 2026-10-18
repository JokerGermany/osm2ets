# SPDX-License-Identifier: GPL-3.0-or-later
"""Zusätze in jeder Detailstufe (neben den Zufahrten in industry.py).

2. Kurze Verbindungen zwischen zwei Straßen der Stufe: Ein Weg über Straßen, die nicht
   zur Stufe gehören, höchstens ``max_m`` lang, der zwei Knoten des Netzes verbindet –
   z. B. das Stück Bundesstraße zwischen zwei Autobahnrampen in L1. Nur wenn der Weg über
   das Netz selbst deutlich länger ist (``ratio``); sonst wäre es bloß eine Schleife an
   derselben Straße (Wendeschleife, Parallelweg). Ausnahme: Schleifen, die an beiden Enden
   an einer Autobahn- oder Kraftfahrstraßenfahrbahn hängen, sind Rastplätze – sie zählen
   immer, bis ``major_max_m``. Tankstellen und Rastanlagen mit eigenem OSM-Tag bindet
   industry.connect_services an.
3. Notfallbuchten/Notauffahrten (``highway=escape``), die direkt am Netz liegen.

Alle Abstände in Echtmetern; Kanten erhalten die Rolle ``connector``.
"""
from __future__ import annotations

import heapq
import logging
import math

log = logging.getLogger(__name__)


def local_xy(loc: dict) -> dict:
    """{Knoten: (lon, lat)} -> {Knoten: (x, y)} in Metern (gleichabständig, für Längen)."""
    if not loc:
        return {}
    lat0 = sum(v[1] for v in loc.values()) / len(loc)
    k = 111320.0
    c = math.cos(math.radians(lat0))
    return {n: (lo * k * c, la * k) for n, (lo, la) in loc.items()}


def edge_length(e, xy: dict):
    ids = e.node_ids()
    s = 0.0
    for a, b in zip(ids, ids[1:]):
        pa, pb = xy.get(a), xy.get(b)
        if pa is None or pb is None:
            return None
        s += math.dist(pa, pb)
    return s


def _network_distance(net, taken_len: dict, a: int, b: int, limit: float) -> float:
    """Kürzester Weg von a nach b nur über Netzkanten; math.inf, wenn länger als limit."""
    dist = {a: 0.0}
    heap = [(0.0, a)]
    while heap:
        d, n = heapq.heappop(heap)
        if n == b:
            return d
        if d > dist.get(n, math.inf) or d > limit:
            continue
        for ei, end in net.adjacency.get(n, ()):
            L = taken_len.get(ei)
            if L is None:
                continue
            m = net.other_end(net.edges[ei], end)
            nd = d + L
            if nd <= limit and nd < dist.get(m, math.inf):
                dist[m] = nd
                heapq.heappush(heap, (nd, m))
    return math.inf


def same_level_links(net, network_nodes: set, xy: dict, classes: frozenset, max_m: float,
                     ratio: float, taken: str = "taken", major_nodes: set | None = None,
                     major_max_m: float = 0.0) -> dict:
    """Markiert kurze Verbindungen zwischen zwei Netzknoten (Rolle 'connector')."""
    from .network import ROLE_CONNECTOR

    major_nodes = major_nodes or set()
    if max_m <= 0 or not network_nodes:
        return {"links": 0, "edges": 0, "rejected_loop": 0, "rest_loops": 0}
    reach = max(max_m, major_max_m if major_nodes else 0.0)
    usable, taken_len = {}, {}
    for e in net.edges:
        if e.role == taken:
            L = edge_length(e, xy)
            if L is not None:
                taken_len[e.idx] = L
        elif e.role is None and e.highway in classes:
            L = edge_length(e, xy)
            if L is not None:
                usable[e.idx] = L

    # Dijkstra von allen Netzknoten zugleich; jeder erreichte Knoten merkt sich seine Quelle.
    # Nicht durch Netzknoten hindurch (dort beginnt eine eigene Suche).
    src = {n: n for n in network_nodes}
    dist = {n: 0.0 for n in network_nodes}
    prev: dict = {}
    heap = [(0.0, n) for n in network_nodes]
    heapq.heapify(heap)
    while heap:
        d, n = heapq.heappop(heap)
        if d > dist.get(n, math.inf):
            continue
        for ei, end in net.adjacency.get(n, ()):
            L = usable.get(ei)
            if L is None:
                continue
            m = net.other_end(net.edges[ei], end)
            if m in network_nodes:
                continue
            nd = d + L
            if nd > reach or (nd > max_m and src[n] not in major_nodes):
                continue
            if nd < dist.get(m, math.inf):
                dist[m] = nd
                src[m] = src[n]
                prev[m] = (n, ei)
                heapq.heappush(heap, (nd, m))

    # Treffpunkte: Kante zwischen zwei Suchgebieten verschiedener Quellen
    best: dict = {}
    for ei, L in usable.items():
        e = net.edges[ei]
        u, v = e.u, e.v
        if u not in src or v not in src:
            continue
        su, sv = src[u], src[v]
        if su == sv:
            continue
        total = dist[u] + L + dist[v]
        both_major = su in major_nodes and sv in major_nodes
        if total > (major_max_m if both_major else max_m):
            continue
        key = (min(su, sv), max(su, sv))
        if key not in best or total < best[key][0]:
            best[key] = (total, ei, u, v)

    marked: set[int] = set()
    links = rejected = rest = 0
    rejected_ways: list = []

    def walk(node):
        while node in prev:
            p, pe = prev[node]
            marked.add(pe)
            node = p

    for (sa, sb), (total, ei, u, v) in sorted(best.items(), key=lambda kv: kv[1][0]):
        if sa in major_nodes and sb in major_nodes:
            rest += 1          # Schleife an der Autobahn: Rastplatz
        elif _network_distance(net, taken_len, sa, sb, ratio * total) <= ratio * total:
            rejected += 1
            if len(rejected_ways) < 100:
                path = {net.edges[ei].way.id}
                for node in (u, v):
                    while node in prev:
                        node, pe = prev[node][0], prev[node][1]
                        path.add(net.edges[pe].way.id)
                rejected_ways.append(sorted(path))
            continue
        links += 1
        marked.add(ei)
        walk(u)
        walk(v)
    for ei in marked:
        net.edges[ei].role = ROLE_CONNECTOR
    log.info("Verbindungen zwischen Straßen der Stufe: %d (davon Rastplatzschleifen %d; Kanten %d, "
             "als Schleife verworfen %d)", links, rest, len(marked), rejected)
    return {"links": links, "edges": len(marked), "rejected_loop": rejected, "rest_loops": rest,
            "rejected_loop_ways": rejected_ways}


def escape_lanes(net, network_nodes: set) -> dict:
    """Markiert highway=escape, das an einem Netzknoten beginnt (mit anschließenden escape-Kanten)."""
    from .network import ROLE_CONNECTOR

    todo = [n for n in network_nodes]
    seen = set(todo)
    marked: set[int] = set()
    while todo:
        n = todo.pop()
        for ei, end in net.adjacency.get(n, ()):
            e = net.edges[ei]
            if e.role is not None or e.highway != "escape" or ei in marked:
                continue
            marked.add(ei)
            m = net.other_end(e, end)
            if m not in seen:
                seen.add(m)
                todo.append(m)
    for ei in marked:
        net.edges[ei].role = ROLE_CONNECTOR
    if marked:
        log.info("Notfallbuchten (highway=escape): %d Kanten", len(marked))
    return {"edges": len(marked)}


def extend_ends(net, xy: dict, loc: dict, classes: frozenset, length_m: float,
                id_start: int = -1_000_000_000) -> dict:
    """4. Straßenenden weiterführen: An jedem Ende der Auswahl, an dem es in OSM weitergeht,
    werden die anschließenden Straßen ``length_m`` Meter (Echtmeter) mit aufgenommen – an
    weiteren Abzweigen nur geradeaus. Ganze Kanten bekommen die Rolle 'connector'; das letzte,
    angeschnittene Stück wird als neue Kante mit einem berechneten Endknoten (negative ID,
    in der Ausgabe ein offenes Ende "cut") zurückgegeben.

    Rückgabe: {"ends", "edges", "partial": [Edge], "locations": {id: (lon, lat)}}.
    """
    from .network import ROLE_CONNECTOR, Edge
    from .osmread import Way

    stats = {"ends": 0, "edges": 0, "partial": [], "locations": {}}
    if length_m <= 0:
        return stats
    deg: dict[int, int] = {}
    for e in net.edges:
        if e.role is not None:
            deg[e.u] = deg.get(e.u, 0) + 1
            deg[e.v] = deg.get(e.v, 0) + 1
    in_sel = set(deg)
    ends = [n for n, d in deg.items() if d == 1]
    next_id = id_start
    marked: set[int] = set()

    def usable(e):
        return e.role is None and e.idx not in marked and e.highway in classes

    def outward(e, start):
        """Knoten der Kante von start aus."""
        return e.node_ids(reverse=(e.v == start and e.u != start))

    def heading(a, b):
        pa, pb = xy.get(a), xy.get(b)
        if pa is None or pb is None or pa == pb:
            return None
        return math.atan2(pb[1] - pa[1], pb[0] - pa[0])

    for n in sorted(ends):
        starts = [(ei, end) for ei, end in net.adjacency.get(n, ()) if usable(net.edges[ei])]
        if not starts:
            continue
        stats["ends"] += 1
        for ei, end in starts:
            e = net.edges[ei]
            node, left = n, length_m
            while e is not None and usable(e):
                ids = outward(e, node)
                if any(i not in xy for i in ids):
                    break
                acc = 0.0
                cut = None
                for k in range(1, len(ids)):
                    seg = math.dist(xy[ids[k - 1]], xy[ids[k]])
                    if acc + seg >= left:
                        cut = (k, (left - acc) / seg if seg > 0 else 0.0)
                        break
                    acc += seg
                if cut is None:
                    # ganze Kante
                    marked.add(e.idx)
                    stats["edges"] += 1
                    far = ids[-1]
                    left -= acc
                    if far in in_sel or left <= 1.0:
                        break
                    # geradeaus weiter (kleinste Richtungsänderung, höchstens 45°)
                    h0 = heading(ids[-2], far)
                    best, bd = None, math.radians(45)
                    for ej, en in net.adjacency.get(far, ()):
                        f = net.edges[ej]
                        if ej == e.idx or not usable(f):
                            continue
                        nxt = outward(f, far)
                        h1 = heading(far, nxt[1])
                        if h0 is None or h1 is None:
                            continue
                        d = abs((h1 - h0 + math.pi) % (2 * math.pi) - math.pi)
                        if d < bd:
                            best, bd = f, d
                    e, node = best, far
                    continue
                # angeschnittene Kante: neuer Endknoten
                k, t = cut
                a, b = ids[k - 1], ids[k]
                (la, pa), (lb, pb) = loc[a], loc[b]
                nid = next_id
                next_id -= 1
                stats["locations"][nid] = (la + t * (lb - la), pa + t * (pb - pa))
                keep = ids[:k] + [nid]
                nodes = keep if node == e.u else keep[::-1]   # Wegrichtung beibehalten (Einbahn)
                from array import array
                way = Way(id=e.way.id, nodes=array("q", nodes), tags=dict(e.way.tags))
                stats["partial"].append(Edge(idx=-1, way=way, i0=0, i1=len(nodes) - 1,
                                             oneway=e.oneway, role=ROLE_CONNECTOR))
                break
    for ei in marked:
        net.edges[ei].role = ROLE_CONNECTOR
    log.info("Straßenenden weitergeführt: %d Enden, %d ganze Kanten, %d angeschnitten",
             stats["ends"], stats["edges"], len(stats["partial"]))
    return stats
