# SPDX-License-Identifier: GPL-3.0-or-later
"""Höhen: Tunnel, kurze Brücken, Bauwerke über Kreuzungen hinweg, kurze Stücke."""
import numpy as np
import pytest

from osm2ets.geometry import Projector
from osm2ets.pipeline import build
from osm2ets.qa import crossing_clearances

from .conftest import write_osm

LAT = 50.0
DX = 1 / 71_500   # ≈ 1 m in Länge bei 50° N (grob, genügt für Testgeometrie)
DY = 1 / 111_200  # ≈ 1 m in Breite


def _pt(x_m, y_m, lon0=11.5):
    return (lon0 + x_m * DX, LAT + y_m * DY)


def test_tunnel_under_road_is_lowered(tmp_path, profile):
    nodes = {1: _pt(-300, 0), 2: _pt(300, 0),                     # B-Straße oben
             10: _pt(0, -400), 11: _pt(0, -60), 12: _pt(0, 60), 13: _pt(0, 400)}
    ways = [(1, [1, 2], {"highway": "primary", "ref": "B 1"}),
            (2, [10, 11], {"highway": "primary", "ref": "B 2"}),
            (3, [11, 12], {"highway": "primary", "ref": "B 2", "tunnel": "yes", "layer": "-1"}),
            (4, [12, 13], {"highway": "primary", "ref": "B 2"})]
    model = build(str(write_osm(tmp_path / "t.osm", nodes, ways)), profile, "L1")
    c = crossing_clearances(model["roads"])
    assert len(c) == 1 and c[0]["clearance_m"] >= profile.bridge_clearance_m - 0.3
    upper = next(r for r in model["roads"] if r["runs"][0]["ref"] == "B 1")
    assert max(abs(p[1]) for p in upper["points"]) < 0.01, "obere Straße darf nicht angehoben werden"
    assert model["stats"]["elevation"]["crossing_sunk_points"] >= 1


def test_short_bridge_is_not_lifted(tmp_path, profile):
    nodes = {1: _pt(0, 0), 2: _pt(200, 0), 3: _pt(204, 0), 4: _pt(400, 0)}
    ways = [(1, [1, 2], {"highway": "primary", "ref": "B 1"}),
            (2, [2, 3], {"highway": "primary", "ref": "B 1", "bridge": "yes", "layer": "1"}),
            (3, [3, 4], {"highway": "primary", "ref": "B 1"})]
    model = build(str(write_osm(tmp_path / "b.osm", nodes, ways)), profile, "L1")
    assert max(p[1] for r in model["roads"] for p in r["points"]) < 0.01
    assert model["stats"]["elevation"]["bridges_ignored_short"] == 1


def test_short_piece_between_junctions_is_kept(tmp_path, profile):
    # Bundesstraße 1-2-3-4, Knoten 2 und 3 nur 3 m auseinander, je eine Seitenstraße
    nodes = {1: _pt(0, 0), 2: _pt(200, 0), 3: _pt(203, 0), 4: _pt(400, 0),
             5: _pt(200, 100), 6: _pt(203, -100)}
    ways = [(1, [1, 2, 3, 4], {"highway": "primary", "ref": "B 1"}),
            (2, [2, 5], {"highway": "residential"}),
            (3, [3, 6], {"highway": "residential"})]
    model = build(str(write_osm(tmp_path / "s.osm", nodes, ways)), profile, "L1")
    short = [r for r in model["roads"] if r["start"].get("osm_node") in (2, 3) and r["end"].get("osm_node") in (2, 3)]
    assert len(short) == 1 and len(short[0]["points"]) == 2
    assert short[0]["length_m"] == pytest.approx(3.0, abs=0.2)
    kinds = {j["osm_node"]: j["kind"] for j in model["junctions"]}
    assert kinds[2] == "junction" and kinds[3] == "junction"


def test_everything_too_short_gives_clear_error(tmp_path, profile):
    nodes = {1: _pt(0, 0), 2: _pt(1, 0)}
    ways = [(1, [1, 2], {"highway": "primary", "ref": "B 1"})]
    with pytest.raises(RuntimeError, match="Spielminimum"):
        build(str(write_osm(tmp_path / "x.osm", nodes, ways)), profile, "L1")


def test_viaduct_junction_follows_deck(tmp_path, profile):
    rasterio = pytest.importorskip("rasterio")
    from rasterio.transform import from_origin

    # Tal: 300 m Hochfläche, dazwischen 40 m tiefer (x = -150 … 150 m)
    pr = Projector(profile.crs, profile.origin_lonlat)
    nodes = {1: _pt(-600, 0), 2: _pt(-200, 0), 3: _pt(0, 0), 4: _pt(200, 0), 5: _pt(600, 0),
             6: _pt(-100, -120)}
    ways = [(1, [1, 2], {"highway": "motorway", "ref": "A 1"}),
            (2, [2, 3], {"highway": "motorway", "ref": "A 1", "bridge": "viaduct", "layer": "1"}),
            (3, [3, 4], {"highway": "motorway", "ref": "A 1", "bridge": "viaduct", "layer": "1"}),
            (4, [4, 5], {"highway": "motorway", "ref": "A 1"}),
            # Rampe, die auf dem Viadukt einmündet und im Datenausschnitt endet
            (5, [6, 3], {"highway": "motorway_link", "bridge": "viaduct", "layer": "1"})]
    osm = write_osm(tmp_path / "v.osm", nodes, ways)
    center = pr.forward(np.array([11.5]), np.array([LAT]))[0] + np.array(pr.origin)
    x0, y1, cell = center[0] - 1000, center[1] + 1000, 5.0
    n = int(2000 / cell)
    xs = x0 + (np.arange(n) + 0.5) * cell
    row = np.where(np.abs(xs - center[0]) < 150, 260.0, 300.0)
    data = np.tile(row, (n, 1)).astype("float32")
    dem = tmp_path / "dem.tif"
    with rasterio.open(dem, "w", driver="GTiff", width=n, height=n, count=1, dtype="float32",
                       crs=profile.crs, transform=from_origin(x0, y1, cell, cell)) as ds:
        ds.write(data, 1)
    model = build(str(osm), profile, "L1", dem_path=str(dem))
    j3 = next(j for j in model["junctions"] if j["osm_node"] == 3)
    # Höhe am Widerlager (Beginn des Brückenlaufs). Die Geländeglättung zieht es
    # nahe der Talkante etwas herunter, daher nicht exakt 300 + 6,5 m.
    main = next(r for r in model["roads"] if r["end"].get("osm_node") == 3 and r["runs"][0]["highway"] == "motorway")
    abutment = next(main["points"][run["from"]][1] for run in main["runs"] if run["bridge"])
    assert abutment > 294.0  # Autobahn-Glättung (400 m) und 4-%-Grenze ziehen etwas mehr
    # Kreuzung im Bauwerk liegt auf Deckhöhe, nicht auf Talboden + 6,5 m (= 266,5 m)
    assert j3["position"][1] == pytest.approx(abutment, abs=0.5)
    # Die im Ausschnitt endende Rampe hängt nicht ins Tal
    ramp = next(r for r in model["roads"] if r["runs"][0]["highway"] == "motorway_link")
    assert min(p[1] for p in ramp["points"]) > abutment - 1.0


def test_parse_maxheight():
    from osm2ets.elevation import parse_maxheight
    assert parse_maxheight("3.8") == 3.8
    assert parse_maxheight("4,2 m") == 4.2
    assert parse_maxheight("12'6\"") == 3.81
    for v in (None, "", "none", "default", "below_default", "abc", "40"):
        assert parse_maxheight(v) is None
