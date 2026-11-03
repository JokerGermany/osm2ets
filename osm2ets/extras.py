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
4. Straßenenden weiterführen (``extend_ends``).
5. Straßen, die man von der Strecke aus sieht (``visible_roads``): Straßen höherer Stufen,
   die eine Straße der Stufe höhenfrei kreuzen (im Umkreis der Kreuzung) oder unmittelbar
   daneben liegen – Rolle ``visible``.

Alle Abstände in Echtmetern; Kanten erhalten die Rolle ``connector`` (außer 5.).
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

    Rückgabe: {"ends", "edges", "partial": [Edge], "partial_src": [Kantenindex in net],
    "locations": {id: (lon, lat)}}.
    """
    from .network import ROLE_CONNECTOR, Edge
    from .osmread import Way

    stats = {"ends": 0, "edges": 0, "partial": [], "partial_src": [], "locations": {}}
    if length_m <= 0:
        return stats
    deg: dict[int, int] = {}
    for e in net.edges:
        if e.role is not None:
            deg[e.u] = deg.get(e.u, 0) + 1
            deg[e.v] = deg.get(e.v, 0) + 1
    in_sel = set(deg)
    ends = sorted(deg)
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

    def straight(n, came_from):
        """Unbenutzte Kante, die eine bei n endende Kante geradeaus fortsetzt (≤ 45°), sonst None.
        Gibt es geradeaus schon eine ausgewählte Kante, geht die Straße weiter: None."""
        h0 = heading(came_from, n)
        if h0 is None:
            return None
        best, bd, sel_straight = None, math.radians(45), False
        for ej, en in net.adjacency.get(n, ()):
            f = net.edges[ej]
            nxt = outward(f, n)
            if len(nxt) < 2 or nxt[1] == came_from:
                continue
            h1 = heading(n, nxt[1])
            if h1 is None:
                continue
            d = abs((h1 - h0 + math.pi) % (2 * math.pi) - math.pi)
            if d > math.radians(45):
                continue
            if f.role is not None:
                sel_straight = True
            elif usable(f) and d < bd:
                best, bd = (ej, en), d
        return None if sel_straight else best

    for n in ends:
        if deg[n] == 1:
            # echtes Ende der Auswahl: alle anschließenden Straßen
            starts = [(ei, end) for ei, end in net.adjacency.get(n, ()) if usable(net.edges[ei])]
        else:
            # Straße hört an einer Kreuzung auf, geht in OSM aber geradeaus weiter
            starts = []
            for ei, end in net.adjacency.get(n, ()):
                e = net.edges[ei]
                if e.role is None:
                    continue
                ids = outward(e, n)
                if len(ids) < 2:
                    continue
                c = straight(n, ids[1])
                if c is not None and c not in starts:
                    starts.append(c)
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
                stats["partial_src"].append(e.idx)
                break
    for ei in marked:
        net.edges[ei].role = ROLE_CONNECTOR
    log.info("Straßenenden weitergeführt: %d Enden, %d ganze Kanten, %d angeschnitten",
             stats["ends"], stats["edges"], len(stats["partial"]))
    return stats


# --------------------------------------------------------------------------- 5. Sichtstücke

def _structure(tags: dict) -> tuple[bool, bool, int]:
    from .network import is_bridge, is_tunnel, parse_layer
    return is_bridge(tags), is_tunnel(tags), parse_layer(tags)


def _separated(a: tuple, b: tuple) -> bool:
    """Höhenfreie Kreuzung laut OSM: eine Seite ist Brücke oder Tunnel, oder die Ebenen
    (layer) unterscheiden sich. Sonst ist es eine Kreuzung ohne gemeinsamen Knoten
    (Datenfehler) – dafür wird nichts aufgenommen."""
    return a[0] or a[1] or b[0] or b[1] or a[2] != b[2]


def _seg_cut(p, q, r, s):
    """Schnitt der Strecken p–q und r–s: Anteil t auf p–q, sonst None (Berührung zählt nicht)."""
    d = (q[0] - p[0]) * (s[1] - r[1]) - (q[1] - p[1]) * (s[0] - r[0])
    if abs(d) < 1e-12:
        return None
    t = ((r[0] - p[0]) * (s[1] - r[1]) - (r[1] - p[1]) * (s[0] - r[0])) / d
    u = ((r[0] - p[0]) * (q[1] - p[1]) - (r[1] - p[1]) * (q[0] - p[0])) / d
    if 0.0 < t < 1.0 and 0.0 < u < 1.0:
        return t
    return None


def _cells(p, q, size: float):
    x0, x1 = sorted((p[0], q[0]))
    y0, y1 = sorted((p[1], q[1]))
    for i in range(math.floor(x0 / size), math.floor(x1 / size) + 1):
        for j in range(math.floor(y0 / size), math.floor(y1 / size) + 1):
            yield i, j


def _merge(intervals: list, gap: float) -> list:
    out: list = []
    for a, b in sorted(intervals):
        if out and a <= out[-1][1] + gap:
            out[-1] = (out[-1][0], max(out[-1][1], b))
        else:
            out.append((a, b))
    return out


def _subtract(intervals: list, cuts: list) -> list:
    """Abschnitte ohne die Schnitte (alle als (a, b))."""
    out = list(intervals)
    for c0, c1 in cuts:
        nxt = []
        for a, b in out:
            if c1 <= a or c0 >= b:
                nxt.append((a, b))
                continue
            if c0 > a:
                nxt.append((a, c0))
            if c1 < b:
                nxt.append((c1, b))
        out = nxt
    return out


def visible_roads(net, xy: dict, loc: dict, classes: frozenset, cross_m: float, near_m: float,
                  extra_edges=(), blocked=frozenset(), tol_m: float = 5.0, gap_m: float = 40.0,
                  min_m: float = 30.0, keep_off_m: float = 15.0, height=None, slope: float = 0.67,
                  tol_h: float = 1.0, id_start: int = -2_000_000_000) -> dict:
    """5. Straßen, die man von der Strecke aus sieht oder die unmittelbar daneben liegen.

    Straßen, die nicht zur Auswahl gehören (Klasse in ``classes``), werden mit aufgenommen:

    - Kreuzt eine solche Straße eine Straße der Auswahl höhenfrei – unter einer Brücke
      hindurch, auf einer Brücke darüber oder durch einen Tunnel –, ab der Kreuzung ``cross_m``
      (Echtmeter) in beide Richtungen: über Wegegrenzen hinweg, an Abzweigen nur geradeaus
      (höchstens 45°), an Knoten der Auswahl ist Schluss. Sonst läge z. B. unter einer
      Autobahnbrücke in L1 nichts (der Wirtschaftsweg darunter gehört erst zu L5).
    - Abschnitte höchstens ``near_m`` neben einer Straße der Auswahl (Parallelwege, Abzweige an
      Knoten des Netzes, Wege bis an die Strecke heran). Lücken bis ``gap_m`` werden
      geschlossen; freistehende Stücke unter ``min_m`` entfallen. Mit Geländemodell (``height``:
      (lon, lat) → Höhe) nicht dort, wo die beiden Straßen so verschieden hoch liegen, dass
      zwischen ihren Rändern keine Böschung mit ``slope`` (+ ``tol_h``) Platz hätte – etwa ein
      Feldweg am Fuß eines hohen Autobahndamms: Das Straßengelände endete dort an einer Wand.

    An Knoten einer Autobahn-/Kraftfahrstraßenfahrbahn (z. B. Betriebszufahrten) enden die
    Stücke ``keep_off_m`` davor – ebenerdige Einmündungen in die Fahrbahn gibt es im Spiel nicht.
    Höhere Stufen setzen die Straßen fort. Ganze Kanten bekommen die Rolle 'visible';
    angeschnittene Kanten werden als neue Kanten mit berechneten Endknoten (negative IDs,
    offenes Ende "cut") zurückgegeben.

    ``extra_edges``: weitere Kanten der Auswahl außerhalb von ``net`` (z. B. angeschnittene
    Straßenenden); ``blocked``: Kanten von ``net``, die schon teilweise vergeben sind.
    ``xy`` muss auch die berechneten Knoten der ``extra_edges`` enthalten.

    Rückgabe: {"crossings", "ways", "near_ways", "edges", "dropped", "kept_off",
    "partial": [Edge], "locations": {id: (lon, lat)}}.
    """
    from array import array

    import numpy as np

    from .network import ROLE_VISIBLE, Edge, is_tunnel
    from .osmread import Way

    stats = {"crossings": 0, "ways": 0, "near_ways": 0, "edges": 0, "dropped": 0, "kept_off": 0,
             "steep_m": 0.0, "partial": [], "locations": {}}
    if cross_m <= 0 and near_m <= 0:
        return stats

    # Straßen der Auswahl: Segmente in einem Raster
    cell = 100.0
    grid: dict = {}
    sel_nodes: set[int] = set()
    carriage: set[int] = set()      # Knoten von Autobahn-/Kraftfahrstraßenfahrbahnen
    for e in [e for e in net.edges if e.role is not None] + list(extra_edges):
        ids = e.node_ids()
        sel_nodes.update(ids)
        if e.highway in ("motorway", "trunk"):
            carriage.update(ids)
        st = _structure(e.way.tags)
        half = 8.0 if e.highway in ("motorway", "trunk") else 4.0   # Modellrand ab Linie (etwa)
        for a, b in zip(ids, ids[1:]):
            pa, pb = xy.get(a), xy.get(b)
            if pa is None or pb is None:
                continue
            seg = (a, b, pa, pb, st, half)
            for c in _cells(pa, pb, cell):
                grid.setdefault(c, []).append(seg)

    def usable(e) -> bool:
        return e.role is None and e.highway in classes and e.idx not in blocked

    geo_cache: dict = {}

    def geo(ei):
        """Knoten, Punkte und Laufmeter einer Kante (None ohne Koordinaten oder als Ring)."""
        if ei not in geo_cache:
            e = net.edges[ei]
            ids = e.node_ids()
            pts = [xy.get(n) for n in ids]
            if any(p is None for p in pts) or ids[0] == ids[-1]:
                geo_cache[ei] = None
            else:
                cum = [0.0]
                for p, q in zip(pts, pts[1:]):
                    cum.append(cum[-1] + math.dist(p, q))
                geo_cache[ei] = (ids, pts, cum) if cum[-1] > 0 else None
        return geo_cache[ei]

    cover: dict[int, list] = {}
    anchors: dict[int, list] = {}
    steep: dict[int, list] = {}     # Nahabschnitte, die wegen des Höhenunterschieds entfallen

    def add(ei, a, b, anchor: bool) -> None:
        if b > a:
            cover.setdefault(ei, []).append((a, b))
            if anchor:
                anchors.setdefault(ei, []).append((a, b))

    # --- höhenfreie Kreuzungen
    crossings: list[tuple[int, float]] = []
    crossed_ways: set[int] = set()
    if cross_m > 0:
        for e in net.edges:
            if not usable(e):
                continue
            g = geo(e.idx)
            if g is None:
                continue
            ids, pts, cum = g
            st = _structure(e.way.tags)
            for k in range(len(ids) - 1):
                seen: set[int] = set()
                for c in _cells(pts[k], pts[k + 1], cell):
                    for seg in grid.get(c, ()):
                        if id(seg) in seen:
                            continue
                        seen.add(id(seg))
                        sa, sb, qa, qb, sst, _ = seg
                        if sa in (ids[k], ids[k + 1]) or sb in (ids[k], ids[k + 1]):
                            continue
                        # im Tunnel sieht man nichts von dem, was darüber liegt
                        if not _separated(st, sst) or sst[1]:
                            continue
                        t = _seg_cut(pts[k], pts[k + 1], qa, qb)
                        if t is None:
                            continue
                        crossings.append((e.idx, cum[k] + t * (cum[k + 1] - cum[k])))
                        crossed_ways.add(e.way.id)

    def heading(p, q):
        return math.atan2(q[1] - p[1], q[0] - p[0])

    def walk(ei: int, s: float, forward: bool, left: float) -> None:
        seen = {(ei, forward)}
        while True:
            ids, pts, cum = geo(ei)
            length = cum[-1]
            if forward:
                if s + left <= length:
                    add(ei, s, s + left, True)
                    return
                add(ei, s, length, True)
                left -= length - s
                node, came = ids[-1], (pts[-2], pts[-1])
            else:
                if s - left >= 0.0:
                    add(ei, s - left, s, True)
                    return
                add(ei, 0.0, s, True)
                left -= s
                node, came = ids[0], (pts[1], pts[0])
            if left <= 1.0 or node in sel_nodes:
                return
            h0 = heading(*came)
            best, bd = None, math.radians(45)
            for ej, end in net.adjacency.get(node, ()):
                if ej == ei or not usable(net.edges[ej]):
                    continue
                g = geo(ej)
                if g is None:
                    continue
                fpts = g[1]
                fwd = end == 0
                h1 = heading(fpts[0], fpts[1]) if fwd else heading(fpts[-1], fpts[-2])
                d = abs((h1 - h0 + math.pi) % (2 * math.pi) - math.pi)
                if d < bd:
                    best, bd = (ej, fwd), d
            if best is None or best in seen:
                return
            seen.add(best)
            ei, forward = best
            s = 0.0 if forward else geo(ei)[2][-1]

    for ei, s in crossings:
        walk(ei, s, True, cross_m)
        walk(ei, s, False, cross_m)

    # --- unmittelbar neben der Strecke
    near_ways: set[int] = set()
    if near_m > 0:
        step = max(1.0, min(5.0, near_m / 4.0))
        for e in net.edges:
            if not usable(e) or is_tunnel(e.way.tags):
                continue
            g = geo(e.idx)
            if g is None:
                continue
            ids, pts, cum = g
            P = np.asarray(pts, float)
            lo = P.min(axis=0) - near_m
            hi = P.max(axis=0) + near_m
            found: dict = {}
            for i in range(math.floor(lo[0] / cell), math.floor(hi[0] / cell) + 1):
                for j in range(math.floor(lo[1] / cell), math.floor(hi[1] / cell) + 1):
                    for seg in grid.get((i, j), ()):
                        if not seg[4][1]:       # aus einem Tunnel der Auswahl sieht man nichts
                            found[id(seg)] = seg
            if not found:
                continue
            fs = list(found.values())
            A = np.array([sg[2] for sg in fs], float)
            AB = np.array([sg[3] for sg in fs], float) - A
            L2 = np.maximum((AB ** 2).sum(axis=1), 1e-9)
            LLn = np.array([loc.get(n, (np.nan, np.nan)) for n in ids], float)
            ss, qs, ls = [], [], []
            for k in range(len(pts) - 1):
                n = max(1, math.ceil((cum[k + 1] - cum[k]) / step))
                t = np.arange(n) / n
                ss.append(cum[k] + t * (cum[k + 1] - cum[k]))
                qs.append(P[k] + t[:, None] * (P[k + 1] - P[k]))
                ls.append(LLn[k] + t[:, None] * (LLn[k + 1] - LLn[k]))
            ss.append(np.array([cum[-1]]))
            qs.append(P[-1:])
            ls.append(LLn[-1:])
            S = np.concatenate(ss)
            Q = np.concatenate(qs)
            LQ = np.concatenate(ls)
            d = np.full(len(Q), np.inf)
            near_seg = np.zeros(len(Q), int)
            near_t = np.zeros(len(Q))
            for c0 in range(0, len(Q), 512):
                q = Q[c0:c0 + 512, None, :]
                t = np.clip(((q - A[None]) * AB[None]).sum(axis=2) / L2[None], 0.0, 1.0)
                D = np.sqrt(((q - A[None] - t[..., None] * AB[None]) ** 2).sum(axis=2))
                arg = D.argmin(axis=1)
                rows = np.arange(len(arg))
                d[c0:c0 + 512] = D[rows, arg]
                near_seg[c0:c0 + 512] = arg
                near_t[c0:c0 + 512] = t[rows, arg]
            mask = d <= near_m
            if not mask.any():
                continue
            bad = np.zeros(len(Q), bool)
            if height is not None:
                idx = np.flatnonzero(mask)
                sa = np.array([loc.get(fs[i][0], (np.nan, np.nan)) for i in near_seg[idx]], float)
                sb = np.array([loc.get(fs[i][1], (np.nan, np.nan)) for i in near_seg[idx]], float)
                LC = sa + near_t[idx, None] * (sb - sa)
                ok = np.isfinite(LC).all(axis=1) & np.isfinite(LQ[idx]).all(axis=1)
                if ok.any():
                    hq = np.asarray(height(LQ[idx][ok, 0], LQ[idx][ok, 1]), float)
                    hc = np.asarray(height(LC[ok, 0], LC[ok, 1]), float)
                    half = np.array([fs[i][5] for i in near_seg[idx][ok]], float)
                    gap = np.maximum(d[idx][ok] - half - 3.0, 0.0)
                    bad[idx[ok]] = np.abs(hq - hc) > tol_h + slope * gap
            good = mask & ~bad

            def runs(m):
                out = []
                k, n = 0, len(S)
                while k < n:
                    if m[k]:
                        j = k
                        while j + 1 < n and m[j + 1]:
                            j += 1
                        out.append((float(S[k]), float(S[j])))
                        k = j + 1
                    else:
                        k += 1
                return out

            cuts = [(max(a - step, 0.0), b + step) for a, b in runs(bad)]
            if cuts:
                steep.setdefault(e.idx, []).extend(cuts)
                stats["steep_m"] += sum(b - a for a, b in runs(bad))
            for a, b in _subtract(_merge(runs(good), gap_m), cuts):
                add(e.idx, a, b, False)
            if good.any():
                near_ways.add(e.way.id)

    # --- Stücke: zusammenfassen, an Fahrbahnknoten zurücknehmen, freistehende Reste weglassen
    pieces: list[list] = []
    for ei, ivs in sorted(cover.items()):
        ids, pts, cum = geo(ei)
        length = cum[-1]
        anc = anchors.get(ei, [])
        # zu steile Nahabschnitte bleiben offen, außer im Umkreis einer Kreuzung
        cuts = _subtract(_merge(steep.get(ei, []), 0.0), anc)
        for a, b in _subtract(_merge(ivs, gap_m), cuts):
            a = 0.0 if a <= tol_m else a
            b = length if b >= length - tol_m else b
            if a <= 0.0 and ids[0] in carriage:
                a = min(keep_off_m, length)
                stats["kept_off"] += 1
            if b >= length and ids[-1] in carriage:
                b = max(length - keep_off_m, 0.0)
                stats["kept_off"] += 1
            # kurze Reste weg – nicht aber ganze kurze Kanten: Sie verbinden zwei Knoten (z. B. 6 m
            # Feldweg zwischen dem Ende einer Brücke über die A 70 und der nächsten Einmündung;
            # fehlten sie, endete die Brücke 13 m über dem Weg)
            if b - a < 2 * tol_m and not (a <= 0.0 and b >= length):
                continue
            anchored = any(x < b and y > a for x, y in anc) \
                or (a <= 0.0 and ids[0] in sel_nodes) or (b >= length and ids[-1] in sel_nodes)
            pieces.append([ei, a, b, anchored])
    parent = list(range(len(pieces)))

    def find(i: int) -> int:
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    at_node: dict[int, int] = {}
    for i, (ei, a, b, _) in enumerate(pieces):
        ids, _, cum = geo(ei)
        for node, touches in ((ids[0], a <= 0.0), (ids[-1], b >= cum[-1])):
            if not touches:
                continue
            if node in at_node:
                parent[find(i)] = find(at_node[node])
            else:
                at_node[node] = i
    comp_len: dict[int, float] = {}
    comp_anchor: dict[int, bool] = {}
    for i, (ei, a, b, anc) in enumerate(pieces):
        r = find(i)
        comp_len[r] = comp_len.get(r, 0.0) + (b - a)
        comp_anchor[r] = comp_anchor.get(r, False) or anc

    keep = [comp_anchor[find(i)] or comp_len[find(i)] >= min_m for i in range(len(pieces))]

    # kurze Stummel ohne Kreuzung, die an einem Ende frei enden (offenes Ende oder Sackgasse),
    # z. B. 11 m Feldweg an einer Einmündung zweier Sichtstücke: ihr Straßengelände stand als Keil
    def ends(i):
        ei, a, b, _ = pieces[i]
        ids, _, cum = geo(ei)
        return (ids[0] if a <= 0.0 else None), (ids[-1] if b >= cum[-1] else None)

    for _ in range(3):
        use: dict[int, int] = {}
        for i in range(len(pieces)):
            if keep[i]:
                for n in ends(i):
                    if n is not None:
                        use[n] = use.get(n, 0) + 1
        changed = False
        for i, (ei, a, b, _) in enumerate(pieces):
            if not keep[i] or b - a >= min_m or any(x < b and y > a for x, y in anchors.get(ei, [])):
                continue
            free = [n is None or (use.get(n, 0) <= 1 and n not in sel_nodes) for n in ends(i)]
            if any(free) and not all(free):
                keep[i] = False
                stats["stubs"] = stats.get("stubs", 0) + 1
                changed = True
        if not changed:
            break

    next_id = id_start
    for i, (ei, a, b, _) in enumerate(pieces):
        if not keep[i]:
            stats["dropped"] += 1
            continue
        e = net.edges[ei]
        ids, pts, cum = geo(ei)
        length = cum[-1]
        if a <= 0.0 and b >= length:
            e.role = ROLE_VISIBLE
            stats["edges"] += 1
            continue
        nodes: list[int] = []
        start_k = 0
        for pos, first in ((a, True), (b, False)):
            snap = next((m for m, c in enumerate(cum) if abs(c - pos) <= tol_m), None)
            if snap is not None:
                point = ids[snap]
                k = snap if first else snap - 1      # letzter Index vor bzw. erster nach dem Punkt
            else:
                k = max(m for m in range(len(cum) - 1) if cum[m] <= pos)
                t = (pos - cum[k]) / (cum[k + 1] - cum[k])
                (la, pa), (lb, pb) = loc[ids[k]], loc[ids[k + 1]]
                point = next_id
                next_id -= 1
                stats["locations"][point] = (la + t * (lb - la), pa + t * (pb - pa))
            if first:
                nodes.append(point)
                start_k = k
            else:
                nodes.extend(ids[start_k + 1:k + 1])
                nodes.append(point)
        # doppelte Knoten (Punkt auf einem OSM-Knoten) entfernen
        clean = [n for m, n in enumerate(nodes) if m == 0 or n != nodes[m - 1]]
        if len(clean) < 2:
            continue
        way = Way(id=e.way.id, nodes=array("q", clean), tags=dict(e.way.tags))
        stats["partial"].append(Edge(idx=-1, way=way, i0=0, i1=len(clean) - 1, oneway=e.oneway,
                                     role=ROLE_VISIBLE))
    stats["crossings"] = len(crossings)
    stats["ways"] = len(crossed_ways)
    stats["near_ways"] = len(near_ways - crossed_ways)
    stats["steep_m"] = round(stats["steep_m"])
    log.info("Sichtstücke: %d höhenfreie Kreuzungen mit %d Wegen, %d weitere Wege daneben; "
             "%d ganze Kanten, %d angeschnitten, %d freistehende Reste weggelassen, %d m zu steil",
             stats["crossings"], stats["ways"], stats["near_ways"], stats["edges"],
             len(stats["partial"]), stats["dropped"], stats["steep_m"])
    return stats


def trim_steep_visible(pieces: list, near_m: float, scale_h: float, slope: float = 0.67,
                       tol_h: float = 1.0, half_near: float = 3.0, min_m: float = 30.0,
                       cross_keep_m: float = 30.0, bridge_keep_m: float = 60.0) -> dict:
    """Nach den Höhen: Sichtstücke (Rolle 'visible') dort weglassen, wo sie neben einer Straße
    der Stufe so viel höher oder tiefer liegen, dass zwischen den Modellrändern keine Böschung mit
    ``slope`` (+ ``tol_h``) Platz hat – z. B. ein Feldweg am Fuß eines hohen Autobahndamms oder
    oben an der Kante eines Einschnitts. Dort endete das Straßengelände an einer Wand. Ausgenommen
    ist der Bereich ``cross_keep_m`` (Spielmeter, längs des Wegs) um eine höhenfreie Kreuzung mit
    einer Straße der Stufe: Unterführung und Brückenrampen gehören dazu – ebenso jede Brücke samt
    ``bridge_keep_m`` davor und dahinter (sonst hing z. B. eine Feldwegbrücke über dem Einschnitt der
    A 70 an einem Ende in der Luft, weil ihre Zufahrt an der Einschnittkante fehlte). Die Stücke werden an den
    verdichteten Punkten geteilt; Reste unter ``min_m`` ohne Anschluss entfallen. Ändert
    ``pieces`` an Ort und Stelle.

    Rückgabe: {"pieces_split", "removed_m"}.
    """
    import numpy as np

    from .geometry import Dense, Piece
    from .network import is_bridge

    def is_visible(part) -> bool:
        return part.edge.role == "visible"

    la, lb, lza, lzb, lh = [], [], [], [], []
    for p in pieces:
        d = p.dense
        for j, part_idx in enumerate(d.seg_part):
            part = p.chain.parts[part_idx]
            if is_visible(part):
                continue
            la.append(d.xy_game[j]); lb.append(d.xy_game[j + 1])
            lza.append(d.z[j]); lzb.append(d.z[j + 1])
            lh.append(8.0 if part.edge.highway in ("motorway", "trunk") else 4.0)
    stats = {"pieces_split": 0, "removed_m": 0.0}
    if not la:
        return stats
    LA, LB = np.array(la), np.array(lb)
    LZA, LZB, LH = np.array(lza), np.array(lzb), np.array(lh)
    LAB = LB - LA
    LL2 = np.maximum((LAB ** 2).sum(axis=1), 1e-9)
    lmin = np.minimum(LA, LB)
    lmax = np.maximum(LA, LB)
    reach = near_m / scale_h + 10.0

    out: list = []
    for p in pieces:
        d = p.dense
        m = len(d.xy_game)
        check = np.array([is_visible(p.chain.parts[k]) for k in d.seg_part], bool)
        if not check.any():
            out.append(p)
            continue
        # Brücken samt Zufahrten bleiben
        is_br = np.array([is_bridge(p.chain.parts[k].edge.way.tags) for k in d.seg_part], bool)
        if is_br.any():
            mid = (d.s_game[:-1] + d.s_game[1:]) / 2
            k = 0
            while k < m - 1:
                if not is_br[k]:
                    k += 1
                    continue
                k2 = k
                while k2 + 1 < m - 1 and is_br[k2 + 1]:
                    k2 += 1
                s0, s1 = d.s_game[k], d.s_game[k2 + 1]
                check &= ~((mid > s0 - bridge_keep_m) & (mid < s1 + bridge_keep_m))
                k = k2 + 1
            if not check.any():
                out.append(p)
                continue
        lo = d.xy_game.min(axis=0) - reach
        hi = d.xy_game.max(axis=0) + reach
        sel = np.flatnonzero((lmax[:, 0] >= lo[0]) & (lmin[:, 0] <= hi[0]) &
                             (lmax[:, 1] >= lo[1]) & (lmin[:, 1] <= hi[1]))
        bad = np.zeros(m - 1, bool)
        if len(sel):
            A, AB, L2 = LA[sel], LAB[sel], LL2[sel]
            ZA, ZB, H = LZA[sel], LZB[sel], LH[sel]
            # Kreuzungen mit Straßen der Stufe (Draufsicht): drumherum nichts weglassen
            for j in range(m - 1):
                p0, p1 = d.xy_game[j], d.xy_game[j + 1]
                r = p1 - p0
                den = r[0] * AB[:, 1] - r[1] * AB[:, 0]
                with np.errstate(divide="ignore", invalid="ignore"):
                    t = ((A[:, 0] - p0[0]) * AB[:, 1] - (A[:, 1] - p0[1]) * AB[:, 0]) / den
                    u = ((A[:, 0] - p0[0]) * r[1] - (A[:, 1] - p0[1]) * r[0]) / den
                hit = (t > 0) & (t < 1) & (u > 0) & (u < 1)
                if hit.any():
                    sc = d.s_game[j] + float(t[hit].min()) * (d.s_game[j + 1] - d.s_game[j])
                    mid = (d.s_game[:-1] + d.s_game[1:]) / 2
                    check &= ~(np.abs(mid - sc) < cross_keep_m)
            for j in np.flatnonzero(check):
                p0, p1 = d.xy_game[j], d.xy_game[j + 1]
                n = max(2, int(np.ceil(np.hypot(*(p1 - p0)) / 5.0)) + 1)
                t = np.linspace(0.0, 1.0, n)
                Q = p0 + t[:, None] * (p1 - p0)
                zq = d.z[j] + t * (d.z[j + 1] - d.z[j])
                u = np.clip(((Q[:, None, :] - A[None]) * AB[None]).sum(axis=2) / L2[None], 0.0, 1.0)
                D = np.sqrt(((Q[:, None, :] - A[None] - u[..., None] * AB[None]) ** 2).sum(axis=2))
                k = D.argmin(axis=1)
                rows = np.arange(n)
                dist = D[rows, k]
                zl = ZA[k] + u[rows, k] * (ZB[k] - ZA[k])
                gap = np.maximum(dist - H[k] - half_near, 0.0)
                near = dist <= near_m / scale_h + H[k]
                bad[j] = bool(np.any(near & (np.abs(zq - zl) > tol_h + slope * gap)))
        if not bad.any():
            out.append(p)
            continue
        stats["pieces_split"] += 1
        seg_len = np.diff(d.s_game)
        stats["removed_m"] += float(seg_len[bad].sum())
        keep_runs = []
        j = 0
        while j < m - 1:
            if bad[j]:
                j += 1
                continue
            k = j
            while k + 1 < m - 1 and not bad[k + 1]:
                k += 1
            keep_runs.append((j, k + 1))      # Punktindizes
            j = k + 1
        for n_sub, (i0, i1) in enumerate(keep_runs):
            length = float(d.s_game[i1] - d.s_game[i0])
            at_end = (i0 == 0 and not p.start_cut) or (i1 == m - 1 and not p.end_cut)
            if length < min_m / scale_h and not at_end:
                stats["removed_m"] += length
                continue
            nd = Dense(xy=d.xy[i0:i1 + 1].copy(), s=d.s[i0:i1 + 1] - d.s[i0],
                       seg_part=d.seg_part[i0:i1].copy(), node_ids=d.node_ids[i0:i1 + 1].copy(),
                       z=d.z[i0:i1 + 1].copy())
            nd.set_scale(scale_h)
            q = Piece(chain=p.chain, index=1000 * (p.index + 1) + n_sub, lonlat=p.lonlat,
                      node_ids=p.node_ids, seg_part=p.seg_part,
                      start_cut=p.start_cut if i0 == 0 else True,
                      end_cut=p.end_cut if i1 == m - 1 else True)
            q.xy = p.xy
            q.dense = nd
            out.append(q)
    pieces[:] = out
    stats["removed_m"] = round(stats["removed_m"] * scale_h)
    log.info("Sichtstücke neben zu hohen/tiefen Straßen der Stufe: %d Stücke geteilt, %d m weggelassen",
             stats["pieces_split"], stats["removed_m"])
    return stats
