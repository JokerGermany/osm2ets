# SPDX-License-Identifier: GPL-3.0-or-later
"""End-to-end-Tests über die Kommandozeile und Prüfung der Ausgabe-Invarianten."""
import json
import os
from pathlib import Path

import numpy as np
import pytest

from osm2ets.cli import main
from osm2ets.config import ConfigError, load_profile
from osm2ets.pipeline import build, with_overrides
from osm2ets.qa import crossing_clearances

from .conftest import write_osm

REAL_EXTRACT = os.environ.get("OSM2ETS_REAL_EXTRACT")


def check_invariants(model: dict, profile) -> None:
    roads = {r["id"]: r for r in model["roads"]}
    assert roads, "keine Straßen"
    for r in model["roads"]:
        pts = np.asarray(r["points"], float)
        assert pts.shape[1] == 3 and len(pts) >= 2 and np.isfinite(pts).all()
        seg = np.linalg.norm(np.diff(pts, axis=0), axis=1)
        # Rundung auf mm in der Ausgabe
        assert seg.min() >= profile.min_segment_game_m - 0.01, (r["id"], seg.min())
        assert seg.max() <= profile.max_segment_game_m + 0.01, (r["id"], seg.max())
        # Läufe decken alle Abschnitte lückenlos ab
        pos = 0
        for run in r["runs"]:
            assert run["from"] == pos and run["to"] > run["from"]
            pos = run["to"]
        assert pos == len(pts) - 1
        assert abs(r["length_m"] - seg.sum()) < 0.05
    for j in model["junctions"]:
        for e in j["ends"]:
            road = roads[e["road"]]
            p = road["points"][0 if e["at"] == "start" else -1]
            assert np.allclose(p, j["position"], atol=0.002)
            info = road["start" if e["at"] == "start" else "end"]
            assert info == {"kind": "node", "osm_node": j["osm_node"]}
        assert j["degree"] == len(j["ends"])


def test_synthetic_end_to_end(synthetic_osm, tmp_path, profile, capsys):
    rc = main(["build", "-i", str(synthetic_osm), "-o", str(tmp_path / "out"), "--name", "t"])
    assert rc == 0
    out = capsys.readouterr().out
    assert "Kreuzungen" in out
    model = json.loads((tmp_path / "out" / "t.ets.json").read_text())
    assert model["format"] == "osm2ets/intermediate" and model["version"] == 1
    check_invariants(model, profile)
    gj = json.loads((tmp_path / "out" / "t.geojson").read_text())
    assert gj["type"] == "FeatureCollection" and gj["features"]

    by_osm = {}
    for r in model["roads"]:
        by_osm[(r["start"].get("osm_node"), r["end"].get("osm_node"))] = r
    # Autobahn Nord: Richtung = OSM-Richtung, also Süd -> Nord (z nimmt ab)
    nb = by_osm[(1, 2)]
    assert nb["points"][0][2] > nb["points"][-1][2]
    assert all(run["oneway"] for run in nb["runs"])
    # Brücke der Bundesstraße liegt 6,0 m hoch, die Autobahn darunter auf 0
    b = next(r for r in model["roads"] if any(run["bridge"] for run in r["runs"]))
    assert max(p[1] for p in b["points"]) == pytest.approx(6.0, abs=0.01)
    assert max(p[1] for p in nb["points"]) == pytest.approx(0.0, abs=0.01)
    # Kreuzungsknoten 32 liegt für alle angrenzenden Straßen auf derselben Höhe 0
    j32 = next(j for j in model["junctions"] if j["osm_node"] == 32)
    assert j32["kind"] == "junction" and j32["position"][1] == pytest.approx(0.0, abs=0.01)
    # höhenfreie Kreuzungen: Bundesstraße über beide Richtungsfahrbahnen
    clearances = crossing_clearances(model["roads"])
    assert len(clearances) == 2
    assert min(c["clearance_m"] for c in clearances) >= 6.4
    # Rampensteigung eingehalten (kleine Toleranz für die Vereinfachung)
    assert model["stats"]["max_grade"] <= profile.max_ramp_grade + 0.005


def test_crossing_without_node_gets_separated(tmp_path, profile):
    nodes = {1: (11.50, 50.00), 2: (11.51, 50.00), 3: (11.505, 49.995), 4: (11.505, 50.005)}
    ways = [(1, [1, 2], {"highway": "primary", "ref": "B 1"}),
            (2, [3, 4], {"highway": "primary", "ref": "B 2"})]
    path = write_osm(tmp_path / "x.osm", nodes, ways)
    model = build(str(path), profile, "L1")
    c = crossing_clearances(model["roads"])
    assert len(c) == 1 and c[0]["clearance_m"] >= profile.bridge_clearance_m - 0.3
    assert model["stats"]["elevation"]["crossing_raised_points"] >= 1
    assert model["stats"]["crossings_low_clearance"] == 0


def test_bbox_clipping(synthetic_osm, profile):
    model = build(str(synthetic_osm), profile, "L1", bbox=(11.499, 49.999, 11.5035, 50.012))
    check_invariants(model, profile)
    kinds = {r["start"]["kind"] for r in model["roads"]} | {r["end"]["kind"] for r in model["roads"]}
    assert "cut" in kinds
    lonlat_max = model["_projector"].inverse(np.concatenate([p.result.xy_real for p in model["_pieces"]]))
    assert lonlat_max[:, 1].max() <= 50.012 + 1e-6 and lonlat_max[:, 0].max() <= 11.5035 + 1e-6


def test_scale_parameter(synthetic_osm, profile):
    p19 = with_overrides(profile, scale=19)
    m1 = build(str(synthetic_osm), profile, "L1")
    m19 = build(str(synthetic_osm), p19, "L1")
    assert m19["projection"]["scale"] == 19
    check_invariants(m19, p19)
    ext1 = m1["stats"]["bounds_game"]["z"][1] - m1["stats"]["bounds_game"]["z"][0]
    ext19 = m19["stats"]["bounds_game"]["z"][1] - m19["stats"]["bounds_game"]["z"][0]
    assert ext19 == pytest.approx(ext1 / 19, rel=0.02)


def test_profile_validation():
    from importlib import resources

    import yaml

    from osm2ets.config import parse_profile, validate_geometry

    p = load_profile("de")
    too_short = with_overrides(p)
    too_short.min_segment_game_m = 0.5
    with pytest.raises(ConfigError):
        validate_geometry(too_short)
    too_long = with_overrides(p)
    too_long.max_segment_game_m = 1500
    with pytest.raises(ConfigError):
        validate_geometry(too_long)
    with pytest.raises(ValueError):
        with_overrides(p, scale=0)
    # YAML-Falle: unquotiertes "no" wird zu False
    data = yaml.safe_load(resources.files("osm2ets").joinpath("profiles", "de.yaml").read_text(encoding="utf-8"))
    data["network"]["exclude_access"] = [False]
    with pytest.raises(ConfigError):
        parse_profile(data)
    with pytest.raises(ConfigError):
        p.lod("L9")


def test_dem(synthetic_osm, tmp_path, profile):
    rasterio = pytest.importorskip("rasterio")
    from rasterio.transform import from_origin
    from osm2ets.geometry import Projector

    pr = Projector(profile.crs, profile.origin_lonlat)
    xy = pr.forward(np.array([11.49, 11.53]), np.array([49.99, 50.04])) + np.array(pr.origin)
    x0, y1 = xy[0, 0] - 500, xy[1, 1] + 500
    w, h = int((xy[1, 0] - xy[0, 0] + 1000) / 10), int((xy[1, 1] - xy[0, 1] + 1000) / 10)
    cols = np.arange(w)
    # Ebene: 300 m + 1 % Anstieg nach Osten
    data = np.tile(300.0 + 0.01 * (cols * 10.0 + 5.0), (h, 1)).astype("float32")
    dem_path = tmp_path / "dem.tif"
    with rasterio.open(dem_path, "w", driver="GTiff", width=w, height=h, count=1, dtype="float32",
                       crs=profile.crs, transform=from_origin(x0, y1, 10.0, 10.0)) as ds:
        ds.write(data, 1)

    model = build(str(synthetic_osm), profile, "L1", dem_path=str(dem_path))
    check_invariants(model, profile)
    # Höhe am Knoten 32 = Gelände dort (keine Brücke in der Nähe des Knotens)
    j32 = next(j for j in model["junctions"] if j["osm_node"] == 32)
    e32 = pr.forward(np.array([11.5030]), np.array([50.015]))[0] + np.array(pr.origin)
    expected = 300.0 + 0.01 * (e32[0] - x0)
    assert j32["position"][1] == pytest.approx(expected, abs=0.3)
    assert model["stats"]["elevation"]["dem_missing_samples"] == 0
    assert min(c["clearance_m"] for c in crossing_clearances(model["roads"])) >= 5.95


@pytest.mark.skipif(not REAL_EXTRACT, reason="OSM2ETS_REAL_EXTRACT nicht gesetzt")
def test_real_extract(profile):
    model = build(REAL_EXTRACT, profile, "L1")
    check_invariants(model, profile)
    assert model["stats"]["crossings_low_clearance"] == 0
