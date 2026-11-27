# SPDX-License-Identifier: GPL-3.0-or-later
"""Gebäude nahe der Strecke (buildings.building_entries) und ihre Regeln."""
import math

import numpy as np
import pytest

from osm2ets.buildings import kind_of, min_rect, parse_height
from osm2ets.pipeline import build

from .conftest import write_osm
from .test_visible import _motorway, _pt


def _box(nodes, ways, wid, cx, cy, length, width, angle_deg=0.0, tags=None):
    """Rechteckiges Gebäude (Echtmeter; Mitte cx/cy, y nach Norden; Längsseite unter angle_deg gegen Osten)
    als geschlossener Weg mit den Knoten wid·10 …"""
    a = math.radians(angle_deg)
    u, n = (math.cos(a), math.sin(a)), (-math.sin(a), math.cos(a))
    ids = []
    for k, (fl, fw) in enumerate(((-1, -1), (1, -1), (1, 1), (-1, 1))):
        nodes[wid * 10 + k] = _pt(cx + fl * length / 2 * u[0] + fw * width / 2 * n[0],
                                  cy + fl * length / 2 * u[1] + fw * width / 2 * n[1])
        ids.append(wid * 10 + k)
    ways.append((wid, ids + [ids[0]], tags or {"building": "yes"}))


def test_kind_of_rules():
    """Art aus building=*, sonst aus eigenen Tags, umgebender landuse-Fläche und Grundfläche (m²)."""
    assert kind_of({"building": "house"}, None, 150) == "house"
    assert kind_of({"building": "house"}, None, 600) == "apartments"     # als Haus erfasster Block
    assert kind_of({"building": "garage"}, "residential", 18) == "garage"
    assert kind_of({"building": "yes"}, "residential", 10) == "shed"
    assert kind_of({"building": "yes"}, "residential", 30) == "garage"
    assert kind_of({"building": "yes"}, "residential", 140) == "house"
    assert kind_of({"building": "yes"}, "residential", 110, 22, 5) == "garage"     # Garagenzeile
    assert kind_of({"building": "yes"}, "residential", 110, 12, 9.2) == "house"
    assert kind_of({"building": "yes"}, "residential", 450) == "apartments"
    assert kind_of({"building": "yes"}, None, 900) == "industrial"       # große Halle im Freien
    assert kind_of({"building": "yes"}, "industrial", 2000) == "industrial"
    assert kind_of({"building": "yes"}, "farmyard", 400) == "farm"
    assert kind_of({"building": "yes", "shop": "supermarket"}, "retail", 1500) == "retail"
    assert kind_of({"building": "yes", "amenity": "place_of_worship"}, "religious", 300) == "church"


def test_parse_height():
    """height in m (Komma oder Punkt, optional „m“), building:levels; Unlesbares und 0 zählen nicht."""
    assert parse_height({"height": "12.5"}) == (12.5, None)
    assert parse_height({"height": "7,5 m", "building:levels": "2"}) == (7.5, 2.0)
    assert parse_height({"height": "ca. 10", "building:levels": "0"}) == (None, None)


def test_min_rect_rotated_and_l_shape():
    """Kleinstes umschließendes Rechteck: gedrehtes Rechteck exakt, L-Form als Rechteck um die Hülle."""
    a = math.radians(30)
    u, n = np.array([math.cos(a), math.sin(a)]), np.array([-math.sin(a), math.cos(a)])
    c = np.array([5.0, -3.0])
    pts = np.array([c + sl * 10 * u + sw * 4 * n for sl, sw in ((-1, -1), (1, -1), (1, 1), (-1, 1))])
    cx, cy, length, width, ang = min_rect(pts)
    assert (cx, cy) == pytest.approx((5.0, -3.0), abs=1e-3)
    assert (length, width) == pytest.approx((20.0, 8.0), abs=1e-3)
    assert math.degrees(ang) == pytest.approx(30.0, abs=1e-3)
    cx, cy, length, width, ang = min_rect(np.array([(0, 0), (12, 0), (12, 4), (4, 4), (4, 10), (0, 10)], float))
    assert (cx, cy, length, width) == pytest.approx((6.0, 5.0, 12.0, 10.0), abs=1e-3)
    assert abs(math.sin(ang)) < 1e-6


def test_buildings_near_route(tmp_path, profile):
    """Gebäude bis buildings.near_m neben Kern und Rampen kommen ins Zwischenformat, weiter entfernte
    nicht. Art aus landuse und Fläche, Richtung zur nächsten Straße – auch zu einer, die nicht in der Karte
    ist, aber nicht zur Autobahn. Ein Gebäude, das in einem größeren liegt, entfällt."""
    nodes, ways = {}, []
    _motorway(nodes, ways)                       # Fahrbahnen bei y = 0 und y = 30
    nodes.update({51: _pt(-300, 150), 52: _pt(300, 150)})
    ways.append((51, [51, 52], {"highway": "residential"}))     # Wohnstraße, in L1 nicht in der Karte
    nodes.update({61: _pt(-250, 110), 62: _pt(150, 110), 63: _pt(150, 145), 64: _pt(-250, 145)})
    ways.append((61, [61, 62, 63, 64, 61], {"landuse": "residential"}))
    _box(nodes, ways, 101, 0, 130, 12, 9, tags={"building": "yes", "building:levels": "2"})   # Haus
    _box(nodes, ways, 102, 20, 132, 6, 3.5)                       # Garage
    _box(nodes, ways, 103, 200, 70, 40, 20, angle_deg=15)          # Halle außerhalb des Wohngebiets
    _box(nodes, ways, 104, 200, 70, 6, 4)                          # in der Halle (doppelt erfasst)
    _box(nodes, ways, 105, 0, 1000, 12, 9, tags={"building": "house"})   # zu weit weg
    _box(nodes, ways, 106, 100, -15, 10, 8, tags={"building": "house"})  # südlich, nur die Autobahn nah
    model = build(str(write_osm(tmp_path / "g.osm", nodes, ways)), profile, "L1")
    by_id = {b["osm"]: b for b in model["buildings"]}
    assert set(by_id) == {"w101", "w102", "w103", "w106"}, sorted(by_id)
    house, garage, hall = by_id["w101"], by_id["w102"], by_id["w103"]
    # Spielkoordinaten (UTM, z = −Norden): Richtungen um die Meridiankonvergenz gedreht – Bezug ist die
    # Autobahn, die genau nach Osten läuft
    mw = next(r for r in model["roads"] if r["role"] == "core")["points"]
    east = math.degrees(math.atan2(mw[-1][2] - mw[0][2], mw[-1][0] - mw[0][0]))
    east = east if abs(east) < 90 else east - math.copysign(180, east)
    assert (house["kind"], house["landuse"], house["levels"]) == ("house", "residential", 2.0)
    assert (house["length"], house["width"]) == pytest.approx((12, 9), abs=0.1)
    assert math.dist(house["center"], by_id["w106"]["center"]) == pytest.approx(math.hypot(100, 145), abs=0.4)
    north = math.radians(east - 90)                                    # zur Wohnstraße im Norden
    assert house["street"] == pytest.approx([math.cos(north), math.sin(north)], abs=0.01)
    assert garage["kind"] == "garage"
    assert hall["kind"] == "industrial" and hall["angle"] - east == pytest.approx(-15, abs=0.1)
    assert by_id["w106"]["street"] is None                             # Autobahn zählt nicht als Straße
    assert model["stats"]["buildings"]["nested"] == 1


def test_unsorted_file_falls_back_to_closed_ways(tmp_path, profile, caplog):
    """Nicht nach IDs sortierte Datei: osmium setzt dann keine Flächen zusammen – Gebäude aus geschlossenen
    Wegen kommen trotzdem (Multipolygone nicht), der Lauf bricht nicht ab."""
    nodes, ways = {}, []
    _box(nodes, ways, 101, 0, 80, 12, 9, tags={"building": "house"})
    _motorway(nodes, ways)                       # Wege 1 und 11 nach 101
    model = build(str(write_osm(tmp_path / "u.osm", nodes, ways)), profile, "L1")
    assert "nicht nach IDs sortiert" in caplog.text
    assert [b["osm"] for b in model["buildings"]] == ["w101"]
    assert model["buildings"][0]["kind"] == "house"
