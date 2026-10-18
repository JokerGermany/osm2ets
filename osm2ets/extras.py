# SPDX-License-Identifier: GPL-3.0-or-later
"""Zusätze in jeder Detailstufe (neben den Zufahrten in industry.py).

2. Kurze Verbindungen zwischen zwei Straßen der Stufe: Ein Weg über Straßen, die nicht
   zur Stufe gehören, höchstens ``max_m`` lang, der zwei Knoten des Netzes verbindet –
   z. B. das Stück Bundesstraße zwischen zwei Autobahnrampen in L1. Nur wenn der Weg über
   das Netz selbst deutlich länger ist (``ratio``); sonst wäre es bloß eine Schleife an
   derselben Straße (Tankstelle, Rastplatz, Wendeschleife).
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
                     ratio: float, taken: str = "taken") -> dict:
    """Markiert kurze Verbindungen zwischen zwei Netzknoten (Rolle 'connector')."""
    from .network import ROLE_CONNECTOR

    if max_m <= 0 or not network_nodes:
        return {"links": 0, "edges": 0, "rejected_loop": 0}
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
            if nd > max_m:
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
        if total > max_m:
            continue
        key = (min(su, sv), max(su, sv))
        if key not in best or total < best[key][0]:
            best[key] = (total, ei, u, v)

    marked: set[int] = set()
    links = rejected = 0

    def walk(node):
        while node in prev:
            p, pe = prev[node]
            marked.add(pe)
            node = p

    for (sa, sb), (total, ei, u, v) in sorted(best.items(), key=lambda kv: kv[1][0]):
        if _network_distance(net, taken_len, sa, sb, ratio * total) <= ratio * total:
            rejected += 1
            continue
        links += 1
        marked.add(ei)
        walk(u)
        walk(v)
    for ei in marked:
        net.edges[ei].role = ROLE_CONNECTOR
    log.info("Verbindungen zwischen Straßen der Stufe: %d (Kanten %d, als Schleife verworfen %d)",
             links, len(marked), rejected)
    return {"links": links, "edges": len(marked), "rejected_loop": rejected}


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
