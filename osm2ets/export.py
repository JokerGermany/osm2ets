# SPDX-License-Identifier: GPL-3.0-or-later
"""Ausgabe: Zwischenformat (JSON), GeoJSON zur Kontrolle in QGIS, Textbericht."""
from __future__ import annotations

import json
from pathlib import Path

import numpy as np


def public_model(model: dict) -> dict:
    return {k: v for k, v in model.items() if not k.startswith("_")}


def write_json(model: dict, path: Path) -> None:
    with open(path, "w", encoding="utf-8") as fh:
        json.dump(public_model(model), fh, ensure_ascii=False, separators=(",", ":"))
        fh.write("\n")


def write_geojson(model: dict, path: Path) -> None:
    """Straßen und Knoten in WGS84 – zeigt genau die Geometrie, die ins Spiel geht."""
    projector = model["_projector"]
    pieces = model["_pieces"]
    scale = model["projection"]["scale"]
    features = []
    for p, road in zip(pieces, model["roads"]):
        lonlat = np.round(projector.inverse(p.result.xy_real), 7)
        # ein Feature je Lauf, damit Rolle/Klasse/Brücke abschnittsgenau sichtbar sind
        for run in road["runs"]:
            features.append({
                "type": "Feature",
                "geometry": {"type": "LineString",
                             "coordinates": lonlat[run["from"]:run["to"] + 1].tolist()},
                "properties": {
                    "road": road["id"],
                    "role": run["role"],
                    "highway": run["highway"],
                    "ref": run["ref"] or "",
                    "oneway": run["oneway"],
                    "lanes": run["lanes"],
                    "bridge": run["bridge"],
                    "tunnel": run["tunnel"],
                    "layer": run["layer"],
                    "maxspeed": run["maxspeed"] or "",
                    "items": run["to"] - run["from"],
                    "osm_ways": ",".join(str(w) for w in run["osm_ways"]),
                },
            })
    for j in model["junctions"]:
        x, _y, z = j["position"]
        lonlat = projector.inverse(np.array([[x * scale, -z * scale]]))[0]
        features.append({
            "type": "Feature",
            "geometry": {"type": "Point", "coordinates": np.round(lonlat, 7).tolist()},
            "properties": {"junction": j["id"], "kind": j["kind"], "degree": j["degree"],
                           "osm_node": j["osm_node"]},
        })
    with open(path, "w", encoding="utf-8") as fh:
        json.dump({"type": "FeatureCollection", "features": features}, fh, ensure_ascii=False)
        fh.write("\n")


def report_text(model: dict) -> str:
    s = model["stats"]
    pr = model["projection"]
    ele = s.get("elevation", {})
    jk = s["junctions_by_kind"]
    lines = [
        "osm2ets – Bericht",
        f"Quelle:        {model['source']['file']}"
        + (f"  (Zuschnitt {model['source']['bbox']})" if model["source"]["bbox"] else ""),
        f"Profil/Stufe:  {model['profile']} / {model['lod']['name']} – {model['lod']['description']}",
        f"Projektion:    {pr['crs']}, Ursprung lon/lat {pr['origin_lonlat']}",
        f"Maßstab:       1:{pr['scale']:g} (Höhe 1:{pr['vertical_scale']:g})",
        "",
        f"Straßen:       {s['roads']} durchgehende Stücke, {s['road_items']} ETS2-Straßenstücke",
        "km im Spiel:   " + ", ".join(f"{k} {v:.2f}" for k, v in s["km_game_by_role"].items()),
        "nach Klasse:   " + ", ".join(f"{k} {v:.2f}" for k, v in s["km_game_by_highway"].items()),
        f"Echt-km:       {s['km_real_total']:.2f}",
        f"Knoten:        Kreuzungen {jk.get('junction', 0)}, Enden {jk.get('dead_end', 0)}, "
        f"Durchgänge {jk.get('through', 0)}",
        f"Segmente (m):  min {s['segment_m']['min']}, Median {s['segment_m']['median']}, "
        f"max {s['segment_m']['max']}; geteilt wegen Maximallänge: {s['segments_split_for_max_length']}; "
        f"verworfene Kurzstücke (< 1,25 m): {s['pieces_dropped_too_short']}",
        f"Lageabweichung: höchstens {s['max_deviation_game_m']} m von der OSM-Geometrie",
        f"Ausdehnung:    x {s['bounds_game']['x']}, z {s['bounds_game']['z']}, Höhe y {s['bounds_game']['y']}",
        f"Koordinaten:   größter Betrag {s['max_abs_horizontal_coordinate_m']:.0f} m → "
        f"Float32-Auflösung dort {s['float32_resolution_at_max_m']:.4f} m",
        f"Sektoren:      {s['sectors']} (4 × 4 km)",
        f"Kreuzungen ohne gemeinsamen Knoten: {s['grade_separated_crossings']}, davon mit "
        f"weniger als {model['qa']['min_clearance_m']} m Höhenabstand: {s['crossings_low_clearance']}",
        f"Höhen:         Quelle {model['elevation']['source']}, Brücken {ele.get('bridges', 0)} "
        f"(kurze ignoriert: {ele.get('bridges_ignored_short', 0)}), Tunnel {ele.get('tunnels', 0)}, "
        f"max. Anhebung {ele.get('max_lift_m', 0):.1f} m, max. Absenkung {ele.get('max_sink_m', 0):.1f} m, "
        f"max. Steigung {100 * s['max_grade']:.1f} %"
        + (f", DEM-Lücken {ele['dem_missing_samples']}" if ele.get("dem_missing_samples") else ""),
        "",
        "Kartendaten © OpenStreetMap contributors (ODbL).",
    ]
    return "\n".join(lines) + "\n"


def write_all(model: dict, out_dir: Path, name: str) -> dict[str, Path]:
    out_dir.mkdir(parents=True, exist_ok=True)
    paths = {
        "json": out_dir / f"{name}.ets.json",
        "geojson": out_dir / f"{name}.geojson",
        "report": out_dir / f"{name}.report.txt",
    }
    write_json(model, paths["json"])
    write_geojson(model, paths["geojson"])
    paths["report"].write_text(report_text(model), encoding="utf-8")
    return paths
