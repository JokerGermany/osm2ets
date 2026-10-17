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
from .elevation import DemSampler, apply_elevation
from .geometry import NO_NODE, Projector, build_runs, chain_pieces, clip_piece, densify, finalize_piece
from .network import Network, build_chains
from .osmread import read_node_locations, read_ways
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
    use_ind = bool(ind.get("enabled"))
    classes = lod.all_classes | (ind.get("highway", frozenset()) if use_ind else frozenset())
    all_ways = read_ways(input_path, classes, profile.exclude_access, profile.exclude_service)
    # Auswahl der Detailstufe nur auf ihren eigenen Klassen (sonst enden Anschlussstraßen
    # an jeder zusätzlichen Einmündung)
    ways = [w for w in all_ways if w.tags.get("highway") in lod.all_classes]
    net = Network(ways, profile)
    selected = net.select(lod)
    industry_stats = None
    if use_ind:
        from .industry import connect, read_targets
        targets, tnodes = read_targets(input_path)
        used = {e.way.id for e in selected}
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
        cand = set(tnodes) | nn
        for e in net2.edges:
            if e.highway in ind["highway"]:
                cand.update(e.node_ids())
        loc = read_node_locations(input_path, cand)
        industry_stats = connect(net2, nn, targets, loc, ind["highway"], ind["max_distance_m"], ind["inside_m"])
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
    chains = build_chains(selected)
    log.info("%d durchgehende Ketten", len(chains))

    needed: set[int] = set()
    for c in chains:
        needed.update(c.node_ids())
    log.info("Lese Koordinaten für %d Knoten ...", len(needed))
    locations = read_node_locations(input_path, needed)

    projector = Projector(profile.crs, profile.origin_lonlat)
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

    dem = None
    if dem_path:
        all_xy = np.concatenate([p.dense.xy for p in pieces])
        bounds = (float(all_xy[:, 0].min()), float(all_xy[:, 1].min()),
                  float(all_xy[:, 0].max()), float(all_xy[:, 1].max()))
        dem = DemSampler(dem_path, projector, bounds)
    elevation_stats = apply_elevation(pieces, profile, dem)

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
    terrain_grid = None
    if dem is not None:
        from .terrain import build_grid
        terrain_grid = build_grid(roads, dem, profile)
    junctions = _junctions(kept)
    stats = _stats(kept, roads, junctions, profile, dropped)
    stats["elevation"] = elevation_stats
    stats["industry_access"] = industry_stats
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
        "stats": stats,
        "qa": {"min_clearance_m": MIN_CLEARANCE_M, "low_clearance_crossings": low[:200],
               "dropped_short_pieces": dropped_info[:200]},
        "_projector": projector,  # nur intern (für GeoJSON), wird nicht serialisiert
        "_pieces": kept,
    }


def _end_info(p, at_start: bool) -> dict:
    nid = int(p.dense.node_ids[0 if at_start else -1])
    cut = p.start_cut if at_start else p.end_cut
    if cut or nid == NO_NODE:
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
