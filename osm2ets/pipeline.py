# SPDX-License-Identifier: GPL-3.0-or-later
"""Gesamtablauf: OSM-Datei -> Zwischenmodell (Python-Dict, siehe export.py)."""
from __future__ import annotations

import logging
import math
from dataclasses import replace
from datetime import datetime, timezone
from pathlib import Path
from typing import Optional

import numpy as np

from . import __version__
from .config import Profile, validate_geometry
from .elevation import DemSampler, align_partner_points, apply_elevation
from .geometry import NO_NODE, Projector, build_runs, chain_pieces, clip_piece, densify, finalize_piece
from .network import Network, build_chains
from .osmread import read_areas, read_node_locations, read_ways
from .qa import crossing_clearances

log = logging.getLogger(__name__)

FORMAT_NAME = "osm2ets/intermediate"
FORMAT_VERSION = 1
SECTOR_SIZE = 4000.0  # TruckLib Map.SectorSize
MIN_CLEARANCE_M = 4.5  # Höhenabstand, unter dem eine höhenfreie Kreuzung gemeldet wird


def with_overrides(profile: Profile, scale: Optional[float] = None, vertical_scale: Optional[float] = None,
                   origin: Optional[tuple[float, float]] = None) -> Profile:
    p = replace(profile)
    if scale is not None:
        if scale <= 0:
            raise ValueError("Maßstab muss > 0 sein")
        p.scale_h = float(scale)
        if vertical_scale is None:
            p.scale_v = float(scale)
    if vertical_scale is not None:
        if vertical_scale <= 0:
            raise ValueError("Höhenmaßstab muss > 0 sein")
        p.scale_v = float(vertical_scale)
    if origin is not None:
        p.origin_lonlat = (float(origin[0]), float(origin[1]))
    validate_geometry(p)
    return p


def build(input_path: str, profile: Profile, lod_name: str, bbox=None, dem_path: Optional[str] = None) -> dict:
    lod = profile.lod(lod_name)
    if bbox is not None:
        minlon, minlat, maxlon, maxlat = bbox
        if not (minlon < maxlon and minlat < maxlat):
            raise ValueError("bbox: minlon,minlat,maxlon,maxlat mit min < max erwartet")

    log.info("Lese Wege aus %s ...", input_path)
    ind = profile.industry or {}
    ext = profile.extras or {}
    use_ind = bool(ind.get("enabled"))
    use_links = float(ext.get("same_level_max_m", 0)) > 0
    use_escape = bool(ext.get("escape"))
    via = frozenset(ext.get("via_highway", frozenset()))
    use_ends = float(ext.get("end_extension_m", 0)) > 0
    vis_cross = float(ext.get("visible_crossing_m", 0)) * profile.scale_h   # Spielmeter → Echtmeter
    vis_near = float(ext.get("visible_near_m", 0)) * profile.scale_h
    vis_classes = (frozenset(ext.get("visible_highway", frozenset())) - lod.all_classes
                   if vis_cross > 0 or vis_near > 0 else frozenset())
    use_vis = bool(vis_classes)
    extra_classes = (via if use_links or use_ends else frozenset()) | (ind.get("highway", frozenset()) if use_ind else frozenset()) \
        | (frozenset({"escape"}) if use_escape else frozenset()) | vis_classes
    excl_service = lod.exclude_service if lod.exclude_service is not None else profile.exclude_service
    excl_access = lod.exclude_access if lod.exclude_access is not None else profile.exclude_access
    all_ways = read_ways(input_path, lod.all_classes | extra_classes, excl_access, excl_service)
    # Auswahl der Detailstufe nur auf ihren eigenen Klassen (sonst enden Anschlussstraßen
    # an jeder zusätzlichen Einmündung)
    ways = [w for w in all_ways if w.tags.get("highway") in lod.all_classes]
    net = Network(ways, profile)
    selected = net.select(lod)
    industry_stats = links_stats = escape_stats = services_stats = ends_stats = vis_stats = None
    if use_ind or use_links or use_escape or use_ends or use_vis:
        from .extras import escape_lanes, local_xy, same_level_links
        # alle Wege, damit an Einmündungen ins Netz geteilt wird; Netzwege selbst sind gesperrt
        net2 = Network(all_ways, profile)
        spans: dict = {}
        for e in selected:
            spans.setdefault(e.way.id, []).append((e.i0, e.i1, e.role))
        for e in net2.edges:
            if any(i0 <= e.i0 and e.i1 <= i1 for i0, i1, _ in spans.get(e.way.id, ())):
                e.role = "taken"
        nn = set()
        for e in selected:
            nn.update(e.node_ids())   # auch Einmündungen mitten in einem Netzstück
        targets, tnodes = [], set()
        if use_ind:
            from .industry import read_targets
            targets, tnodes = read_targets(input_path)
        cand = set(tnodes) | nn
        for e in net2.edges:
            if e.role is None:
                cand.update(e.node_ids())
        loc = read_node_locations(input_path, cand)
        xy = local_xy(loc)
        # 2. kurze Verbindungen zwischen zwei Straßen der Stufe
        major = {"motorway", "trunk", "motorway_link", "trunk_link"}
        carriage = set()
        for e in selected:
            if e.highway in ("motorway", "trunk"):
                carriage.update(e.node_ids())
        if use_links:
            links_stats = same_level_links(net2, nn, xy, via, float(ext["same_level_max_m"]),
                                           float(ext.get("same_level_ratio", 1.5)), major_nodes=carriage,
                                           major_max_m=float(ext.get("rest_loop_max_m", 1500.0)))
        # 3. Notfallbuchten direkt an der Strecke
        if use_escape:
            escape_stats = escape_lanes(net2, nn)
        # 1. Zufahrten zu Industrie/Gewerbe, ab Kreuzungen/Abfahrten des Netzes (inkl. 2./3.)
        if use_ind:
            from .industry import connect, connect_services
            starts = set(nn)
            for e in net2.edges:
                if e.role not in (None, "taken"):
                    starts.update(e.node_ids())
            on_major = set()
            for e in selected:
                if e.highway in major:
                    ids = e.node_ids()
                    on_major.update(ids[1:-1] if e.highway.endswith("_link") else ids)
            # Knoten, an denen auch eine nicht-autobahnartige Netzstraße endet, bleiben Start
            for e in selected:
                if e.highway not in major:
                    on_major.difference_update(e.node_ids())
            for e in net2.edges:
                if e.role not in (None, "taken"):
                    on_major.difference_update(e.node_ids())
            industry_stats = connect(net2, starts, targets, loc, ind["highway"], ind["max_distance_m"],
                                     ind["inside_m"], no_start=on_major)
            # Rast- und Tankanlagen: auch direkt von der Autobahn aus, alle Zu- und Abfahrten
            starts2 = set(nn)
            for e in net2.edges:
                if e.role not in (None, "taken"):
                    starts2.update(e.node_ids())
            services_stats = connect_services(net2, starts2, targets, loc, ind["highway"], ind["max_distance_m"])
        # 4. Straßenenden weiterführen (zuletzt, damit auch Zufahrten und Verbindungen enden)
        # Länge in Spielmetern (wie die Mindest-Segmentlänge), also × Maßstab in Echtmetern
        ext_len = float(ext.get("end_extension_m", 0)) * profile.scale_h
        if ext_len > 0:
            from .extras import extend_ends
            ends_stats = extend_ends(net2, xy, loc, via | lod.all_classes, ext_len)
        # 5. Straßen höherer Stufen, die man von der Strecke aus sieht (höhenfrei kreuzend oder
        #    unmittelbar daneben)
        if use_vis:
            from .extras import visible_roads
            end_parts = ends_stats["partial"] if ends_stats else []
            end_loc = ends_stats["locations"] if ends_stats else {}
            vis_loc = {**loc, **end_loc}
            height = None
            if dem_path and vis_near > 0:
                # Geländemodell schon hier: Nahstücke nicht dort, wo sie viel höher/tiefer liegen
                pr0 = Projector(profile.crs, profile.origin_lonlat)
                ll = np.array(list(vis_loc.values()), float)
                pxy = pr0.forward(ll[:, 0], ll[:, 1])
                dem0 = DemSampler(dem_path, pr0, (float(pxy[:, 0].min()), float(pxy[:, 1].min()),
                                                   float(pxy[:, 0].max()), float(pxy[:, 1].max())))

                def height(lon, lat, _pr=pr0, _dem=dem0):
                    return _dem.sample(_pr.forward(np.asarray(lon, float), np.asarray(lat, float)))
            vis_stats = visible_roads(net2, local_xy(vis_loc), vis_loc, vis_classes, vis_cross, vis_near,
                                      height=height, extra_edges=end_parts,
                                      blocked=frozenset(ends_stats["partial_src"]) if ends_stats else frozenset())
        new = [e for e in net2.edges if e.role not in (None, "taken")]
        if new:
            # Netzkanten an den neuen Einmündungen teilen: Auswahl aus net2 übernehmen
            resel = []
            for e in net2.edges:
                if e.role != "taken":
                    continue
                for i0, i1, role in spans.get(e.way.id, ()):
                    if i0 <= e.i0 and e.i1 <= i1:
                        e.role = role
                        resel.append(e)
                        break
            selected = resel + new
        for st in (ends_stats, vis_stats):
            if st and st["partial"]:
                top = max(e.idx for e in selected) + 1 if selected else 0
                for k, e in enumerate(st["partial"]):
                    e.idx = top + k
                selected = selected + st["partial"]
    chains = build_chains(selected)
    log.info("%d durchgehende Ketten", len(chains))

    needed: set[int] = set()
    for c in chains:
        needed.update(c.node_ids())
    log.info("Lese Koordinaten für %d Knoten ...", len(needed))
    locations = read_node_locations(input_path, {n for n in needed if n > 0})
    for st in (ends_stats, vis_stats):
        if st:
            locations.update(st["locations"])   # berechnete Endpunkte (Verlängerungen, Sichtstücke)

    projector = Projector(profile.crs, profile.origin_lonlat)
    areas = _areas(input_path, projector, profile.scale_h, bbox)
    pieces = []
    for c in chains:
        raw = chain_pieces(c, locations)
        if bbox is not None:
            clipped = []
            for p in raw:
                clipped.extend(clip_piece(p, bbox))
            raw = clipped
        for i, p in enumerate(raw):
            p.index = i
        pieces.extend(raw)
    if not pieces:
        raise RuntimeError("Keine Straßen im gewählten Gebiet/der gewählten Detailstufe gefunden")

    for p in pieces:
        p.xy = projector.forward(p.lonlat[:, 0], p.lonlat[:, 1])
        p.dense = densify(p.xy, p.node_ids, p.seg_part, profile.densify_m)
        p.dense.set_scale(profile.scale_h)
    # Punkte nebeneinanderliegender Fahrbahnen gegenüberstellen (Höhenbindung quer, keine Treppen)
    aligned = align_partner_points(pieces, profile.scale_h)

    dem = None
    if dem_path:
        all_xy = np.concatenate([p.dense.xy for p in pieces])
        bounds = (float(all_xy[:, 0].min()), float(all_xy[:, 1].min()),
                  float(all_xy[:, 0].max()), float(all_xy[:, 1].max()))
        dem = DemSampler(dem_path, projector, bounds)
    elevation_stats = apply_elevation(pieces, profile, dem)
    elevation_stats["aligned_points"] = aligned
    if vis_stats and vis_near > 0:
        from .extras import trim_steep_visible
        vis_stats["trim"] = trim_steep_visible(pieces, vis_near, profile.scale_h)

    run_cache: dict[int, tuple] = {}
    kept, dropped_info = [], []
    for p in pieces:
        if p.chain.id not in run_cache:
            run_cache[p.chain.id] = build_runs(p.chain)
        part_run, runs = run_cache[p.chain.id]
        p.result = finalize_piece(p, profile, part_run, runs)
        if p.result is None:
            dropped_info.append({"start": _end_info(p, True), "end": _end_info(p, False),
                                 "length_game_m": round(float(p.dense.s_game[-1]), 3)})
        else:
            kept.append(p)
    dropped = len(dropped_info)
    if dropped:
        log.info("%d Stücke kürzer als das Spielminimum verworfen", dropped)
    if not kept:
        raise RuntimeError("Alle Straßenstücke sind kürzer als das Spielminimum von 1,25 m – "
                           "Gebiet, Detailstufe und Maßstab prüfen")

    roads = [_road_dict(p) for p in kept]
    open_ends = mark_open_ends(roads, input_path, excl_access, excl_service)
    implicit = implicit_bridges(roads)
    terrain_grid = None
    if dem is not None:
        from .terrain import build_grid
        terrain_grid = build_grid(roads, dem, profile)
    junctions = _junctions(kept)
    stats = _stats(kept, roads, junctions, profile, dropped)
    stats["elevation"] = elevation_stats
    stats["elevation"]["implicit_bridges"] = implicit
    stats["industry_access"] = industry_stats
    stats["same_level_links"] = links_stats
    stats["escape_lanes"] = escape_stats
    stats["services_access"] = services_stats
    stats["end_extensions"] = ({k: v for k, v in ends_stats.items() if k in ("ends", "edges")}
                               | {"partial": len(ends_stats["partial"])}) if ends_stats else None
    stats["visible_roads"] = ({k: v for k, v in vis_stats.items()
                               if k in ("crossings", "ways", "near_ways", "edges", "dropped", "kept_off", "steep_m",
                                        "trim")}
                                  | {"partial": len(vis_stats["partial"])}) if vis_stats else None
    crossings = crossing_clearances(roads)
    low = [c for c in crossings if c["clearance_m"] < MIN_CLEARANCE_M]
    stats["grade_separated_crossings"] = len(crossings)
    stats["crossings_low_clearance"] = len(low)

    return {
        "format": FORMAT_NAME,
        "version": FORMAT_VERSION,
        "generator": f"osm2ets {__version__}",
        "created": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "source": {
            "file": Path(input_path).name,
            "attribution": "© OpenStreetMap contributors, ODbL 1.0 (https://www.openstreetmap.org/copyright)",
            "bbox": list(bbox) if bbox else None,
        },
        "profile": profile.name,
        "lod": {"name": lod.name, "description": lod.description},
        "projection": {
            "crs": profile.crs,
            "origin_lonlat": list(profile.origin_lonlat),
            "origin_projected": [round(v, 3) for v in projector.origin],
            "scale": profile.scale_h,
            "vertical_scale": profile.scale_v,
            "axes": "x = Osten, y = Höhe, z = Süden (Norden = -z), Spielmeter",
        },
        "elevation": {"source": f"dem:{Path(dem_path).name}" if dem_path else "none"},
        "roads": roads,
        "junctions": junctions,
        "terrain_grid": terrain_grid,
        "areas": areas,
        "stats": stats,
        "qa": {"min_clearance_m": MIN_CLEARANCE_M, "low_clearance_crossings": low[:200],
               "dropped_short_pieces": dropped_info[:200]},
        "_projector": projector,  # nur intern (für GeoJSON), wird nicht serialisiert
        "_pieces": kept,
    }


def _areas(input_path: str, projector, scale_h: float, bbox) -> list[dict]:
    """area:highway-Flächen in Spielkoordinaten [x, z] (z = Süden), nur zur Lageprüfung."""
    raw = read_areas(input_path)
    if not raw:
        return []
    loc = read_node_locations(input_path, {n for _, _, refs in raw for n in refs})
    out = []
    for wid, kind, refs in raw:
        ll = [loc[n] for n in refs if n in loc]
        if len(ll) < 4:
            continue
        if bbox is not None and not any(bbox[0] <= lon <= bbox[2] and bbox[1] <= lat <= bbox[3] for lon, lat in ll):
            continue
        xy = projector.forward(np.array([p[0] for p in ll]), np.array([p[1] for p in ll])) / scale_h
        out.append({"osm_way": wid, "kind": kind,
                    "points": [[round(float(x), 2), round(float(-y), 2)] for x, y in xy]})
    return out


def _end_info(p, at_start: bool) -> dict:
    nid = int(p.dense.node_ids[0 if at_start else -1])
    cut = p.start_cut if at_start else p.end_cut
    if cut or nid == NO_NODE or nid < 0:   # < 0: berechneter Endpunkt einer Verlängerung
        return {"kind": "cut"}
    return {"kind": "node", "osm_node": nid}


def _road_dict(p) -> dict:
    r = p.result
    pts = np.round(r.points, 3)
    seg = np.linalg.norm(np.diff(r.points, axis=0), axis=1)
    return {
        "id": p.key,
        "chain": p.chain.id,
        "piece": p.index,
        "role": p.chain.role,
        "closed": bool(p.chain.closed and not p.start_cut and not p.end_cut),
        "start": _end_info(p, True),
        "end": _end_info(p, False),
        "length_m": round(float(seg.sum()), 2),
        "points": pts.tolist(),
        "runs": r.runs,
    }


# Unterführungen, die OSM als Tunnel der unteren Straße erfasst
IMPLICIT_MAX_TUNNEL_M = 80.0    # längere Tunnel liegen unter einem Berg – keine Unterführung
IMPLICIT_MAX_CLEAR_M = 15.0
IMPLICIT_HALF_M = 4.0           # halbe Breite der unteren Straße (etwa)
IMPLICIT_SHOULDER_M = 1.5       # flach neben ihr (wie Underpasses.Shoulder im Writer)
IMPLICIT_SLOPE = 0.67           # Böschung unter der Brücke bis an die Brückenenden (1 : 1,5)
IMPLICIT_MAX_HALF_M = 25.0


# Klassen, auf denen ein Lkw weiterfahren könnte: endet eine Kartenstraße an einem Knoten, an dem
# eine solche Straße in OSM weitergeht (oder am Kartenrand), führt sie im Spiel ins Nichts
DRIVABLE = frozenset({"motorway", "motorway_link", "trunk", "trunk_link", "primary", "primary_link",
                      "secondary", "secondary_link", "tertiary", "tertiary_link", "unclassified",
                      "residential", "living_street", "service", "road"})


def mark_open_ends(roads: list[dict], input_path: str, excl_access=frozenset(), excl_service=frozenset()) -> int:
    """Setzt end["open"] = True an Straßenenden, die in OSM auf befahrbaren Wegen weitergehen,
    die nicht in der Karte sind (Stufengrenze), und an abgeschnittenen Enden (Kartenrand)."""
    ends: dict[int, list[dict]] = {}
    in_map: dict[int, set] = {}
    for r in roads:
        ways = {w for run in r["runs"] for w in run.get("osm_ways", [])}
        for key in ("start", "end"):
            e = r[key]
            if e.get("kind") == "node":
                ends.setdefault(e["osm_node"], []).append(e)
                in_map.setdefault(e["osm_node"], set()).update(ways)
    drive_ways: dict[int, set] = {}
    if ends:
        for w in read_ways(input_path, DRIVABLE, excl_access, excl_service):
            for n in w.nodes:
                if n in ends:
                    drive_ways.setdefault(n, set()).add(w.id)
    all_map = {w for r in roads for run in r["runs"] for w in run.get("osm_ways", [])}
    count = 0
    for r in roads:
        drivable = any(run["highway"] in DRIVABLE for run in r["runs"])
        for key in ("start", "end"):
            e = r[key]
            if not drivable:
                continue
            if e.get("kind") == "cut" or (e.get("kind") == "node" and drive_ways.get(e["osm_node"], set()) - all_map):
                e["open"] = True
                count += 1
    return count


def implicit_bridges(roads: list[dict]) -> int:
    """Unterführungen, die OSM als kurzen Tunnel der unteren Straße erfasst (die obere ist dort
    keine Brücke): Die obere Straße wird über dem Tunnel zur Brücke (Punkte eingefügt, Lauf
    geteilt, bridge=True) – so baut der Writer darunter eine offene Unterführung wie unter jeder
    Brücke über eine Straße. Sonst lief die untere Straße in den Damm, das Gelände lag bis zu 3 m
    über ihr. Die Brücke reicht beiderseits so weit, dass die Böschung (1 : 1,5) vom Brückenende bis
    neben die untere Straße Platz hat; deren Tunnelabschnitt wird ein normaler Abschnitt (offen,
    mit Straßengelände). Nur Tunnel bis IMPLICIT_MAX_TUNNEL_M mit 3 m bis IMPLICIT_MAX_CLEAR_M
    Höhenabstand.
    Gibt die Zahl der neuen Brückenabschnitte zurück."""
    low = []
    for r in roads:
        P = np.asarray(r["points"], float)
        for run in r["runs"]:
            if not run["tunnel"]:
                continue
            Q = P[run["from"]:run["to"] + 1]
            if float(np.hypot(*np.diff(Q[:, [0, 2]], axis=0).T).sum()) > IMPLICIT_MAX_TUNNEL_M:
                continue
            for i in range(run["from"], run["to"]):
                low.append((r["id"], P[i], P[i + 1], run))
    if not low:
        return 0
    opened: list[dict] = []
    added = 0
    for r in roads:
        P = np.asarray(r["points"], float)
        cum = np.concatenate([[0.0], np.cumsum(np.hypot(*np.diff(P[:, [0, 2]], axis=0).T))])
        seg_run = [None] * (len(P) - 1)
        for run in r["runs"]:
            for i in range(run["from"], run["to"]):
                seg_run[i] = run
        spans = []
        for i in range(len(P) - 1):
            run = seg_run[i]
            if run is None or run["bridge"] or run["tunnel"]:
                continue
            a, b = P[i][[0, 2]], P[i + 1][[0, 2]]
            for lid, c3, d3, lrun in low:
                if lid == r["id"]:
                    continue
                c, d = c3[[0, 2]], d3[[0, 2]]
                ab, cd = b - a, d - c
                den = ab[0] * cd[1] - ab[1] * cd[0]
                if abs(den) < 1e-9:
                    continue
                t = ((c[0] - a[0]) * cd[1] - (c[1] - a[1]) * cd[0]) / den
                u = ((c[0] - a[0]) * ab[1] - (c[1] - a[1]) * ab[0]) / den
                if not (0.0 < t < 1.0 and 0.0 < u < 1.0):
                    continue
                clear = (P[i][1] + t * (P[i + 1][1] - P[i][1])) - (c3[1] + u * (d3[1] - c3[1]))
                if not (3.0 <= clear <= IMPLICIT_MAX_CLEAR_M):
                    continue
                sin = max(0.3, abs(den) / (np.hypot(*ab) * np.hypot(*cd)))
                # Brücke so lang, dass die Böschung vom Brückenende bis neben die untere Straße passt
                half = min(IMPLICIT_HALF_M / sin + IMPLICIT_SHOULDER_M + clear / IMPLICIT_SLOPE, IMPLICIT_MAX_HALF_M)
                sc = cum[i] + t * (cum[i + 1] - cum[i])
                spans.append((max(sc - half, 0.0), min(sc + half, cum[-1])))
                opened.append(lrun)
        if not spans:
            continue
        spans.sort()
        merged = [list(spans[0])]
        for s0, s1 in spans[1:]:
            if s0 <= merged[-1][1]:
                merged[-1][1] = max(merged[-1][1], s1)
            else:
                merged.append([s0, s1])
        pts = [list(map(float, p)) for p in P]
        seg_attr = [dict(run) for run in seg_run]   # je Segment eine Kopie des Laufs
        for s0, s1 in merged:
            idx = []
            for s in (s0, s1):
                cum_now = np.concatenate([[0.0], np.cumsum([np.hypot(pts[k + 1][0] - pts[k][0], pts[k + 1][2] - pts[k][2])
                                                            for k in range(len(pts) - 1)])])
                k = int(np.searchsorted(cum_now, s, side="right") - 1)
                k = min(max(k, 0), len(pts) - 2)
                if s - cum_now[k] < 2.0:
                    idx.append(k)
                elif cum_now[k + 1] - s < 2.0:
                    idx.append(k + 1)
                else:
                    f = (s - cum_now[k]) / (cum_now[k + 1] - cum_now[k])
                    q = [pts[k][j] + f * (pts[k + 1][j] - pts[k][j]) for j in range(3)]
                    pts.insert(k + 1, q)
                    seg_attr.insert(k + 1, dict(seg_attr[k]))
                    idx.append(k + 1)
            i0, i1 = idx
            if i1 <= i0:
                continue
            for k in range(i0, i1):
                seg_attr[k] = dict(seg_attr[k], bridge=True, layer=max(int(seg_attr[k].get("layer") or 0), 1))
            added += 1
        runs = []
        for k, attr in enumerate(seg_attr):
            base = {key: v for key, v in attr.items() if key not in ("from", "to")}
            if runs and {key: v for key, v in runs[-1].items() if key not in ("from", "to")} == base:
                runs[-1]["to"] = k + 1
            else:
                runs.append(dict(base, **{"from": k, "to": k + 1}))
        r["points"] = [[round(v, 3) for v in p] for p in pts]
        r["runs"] = runs
    # die untere Straße liegt jetzt offen unter der Brücke: kein Tunnel mehr (Straßengelände wie sonst)
    for run in opened:
        run["tunnel"] = False
    if added:
        log.info("Unterführungen unter kurzen Tunneln: %d Brückenabschnitte über dem Tunnel", added)
    return added


def _heading(a, b) -> float:
    dx, dz = b[0] - a[0], b[2] - a[2]
    return round(math.degrees(math.atan2(dx, -dz)) % 360.0, 2)


def _junctions(pieces) -> list[dict]:
    ends: dict[int, list] = {}
    pos: dict[int, list] = {}
    for p in pieces:
        pts = p.result.points
        for at_start in (True, False):
            info = _end_info(p, at_start)
            if info["kind"] != "node":
                continue
            nid = info["osm_node"]
            if at_start:
                heading = _heading(pts[0], pts[1])
                position = pts[0]
            else:
                heading = _heading(pts[-1], pts[-2])
                position = pts[-1]
            ends.setdefault(nid, []).append({"road": p.key, "at": "start" if at_start else "end",
                                             "heading_deg": heading})
            pos.setdefault(nid, position)
    out = []
    for i, nid in enumerate(sorted(ends)):
        deg = len(ends[nid])
        kind = "junction" if deg >= 3 else ("dead_end" if deg == 1 else "through")
        out.append({"id": i, "osm_node": nid, "kind": kind, "degree": deg,
                    "position": np.round(pos[nid], 3).tolist(), "ends": ends[nid]})
    return out


def _stats(pieces, roads, junctions, profile: Profile, dropped: int) -> dict:
    pts_all = np.concatenate([p.result.points for p in pieces])
    seg_lengths = []
    km_by_role: dict[str, float] = {}
    km_by_highway: dict[str, float] = {}
    sectors = set()
    max_grade = 0.0
    split = 0
    for p, road in zip(pieces, roads):
        pts = p.result.points
        d = np.diff(pts, axis=0)
        lengths = np.linalg.norm(d, axis=1)
        seg_lengths.append(lengths)
        horiz = np.hypot(d[:, 0], d[:, 2])
        with np.errstate(divide="ignore", invalid="ignore"):
            grades = np.where(horiz > 0, np.abs(d[:, 1]) / horiz, 0.0)
        if len(grades):
            max_grade = max(max_grade, float(grades.max()))
        mids = (pts[:-1] + pts[1:]) / 2
        for mx, mz in zip(mids[:, 0], mids[:, 2]):
            sectors.add((math.floor(mx / SECTOR_SIZE), math.floor(mz / SECTOR_SIZE)))
        for run in road["runs"]:
            km = float(lengths[run["from"]:run["to"]].sum()) / 1000.0
            km_by_role[run["role"]] = km_by_role.get(run["role"], 0.0) + km
            km_by_highway[run["highway"]] = km_by_highway.get(run["highway"], 0.0) + km
        split += p.result.split_count
    seg = np.concatenate(seg_lengths)
    max_abs = float(np.abs(pts_all[:, [0, 2]]).max())
    jk: dict[str, int] = {}
    for j in junctions:
        jk[j["kind"]] = jk.get(j["kind"], 0) + 1
    return {
        "roads": len(roads),
        "road_items": int(len(seg)),
        "km_game_by_role": {k: round(v, 3) for k, v in sorted(km_by_role.items())},
        "km_game_by_highway": {k: round(v, 3) for k, v in sorted(km_by_highway.items())},
        "km_real_total": round(sum(km_by_role.values()) * profile.scale_h, 3),
        "segment_m": {"min": round(float(seg.min()), 3), "median": round(float(np.median(seg)), 3),
                      "max": round(float(seg.max()), 3)},
        "segments_split_for_max_length": split,
        "max_deviation_game_m": round(max(p.result.deviation for p in pieces), 3),
        "pieces_dropped_too_short": dropped,
        "junctions_by_kind": jk,
        "bounds_game": {
            "x": [round(float(pts_all[:, 0].min()), 2), round(float(pts_all[:, 0].max()), 2)],
            "y": [round(float(pts_all[:, 1].min()), 2), round(float(pts_all[:, 1].max()), 2)],
            "z": [round(float(pts_all[:, 2].min()), 2), round(float(pts_all[:, 2].max()), 2)],
        },
        "max_abs_horizontal_coordinate_m": round(max_abs, 2),
        "float32_resolution_at_max_m": float(np.spacing(np.float32(max_abs))),
        "sectors": len(sectors),
        "max_grade": round(max_grade, 4),
    }
