# SPDX-License-Identifier: GPL-3.0-or-later
"""Zusätze je Detailstufe: kurze Verbindungen gleicher Stufe, Notfallbuchten, Zufahrten."""
from osm2ets.pipeline import build

from .conftest import write_osm

LAT = 50.0
DX = 1 / 71_500   # ≈ 1 m in Länge bei 50° N
DY = 1 / 111_200  # ≈ 1 m in Breite


def _pt(x_m, y_m):
    return (11.5 + x_m * DX, LAT + y_m * DY)


def _ways_in(model):
    out = {}
    for r in model["roads"]:
        for run in r["runs"]:
            for w in run["osm_ways"]:
                out.setdefault(w, set()).add(run["role"])
    return out


def _interchange(gap_m):
    """Autobahn W–O, zwei Rampen enden an einer Bundesstraße (N–S) im Abstand gap_m."""
    nodes = {1: _pt(-2000, 0), 2: _pt(-200, 0), 3: _pt(200, 0), 4: _pt(2000, 0),
             # Bundesstraße
             20: _pt(0, -1500), 21: _pt(0, -gap_m / 2), 22: _pt(0, gap_m / 2), 23: _pt(0, 1500)}
    ways = [(1, [1, 2, 3, 4], {"highway": "motorway", "oneway": "no", "ref": "A 9"}),
            (2, [2, 21], {"highway": "motorway_link", "oneway": "no"}),
            (3, [3, 22], {"highway": "motorway_link", "oneway": "no"}),
            (10, [20, 21], {"highway": "primary", "ref": "B 1"}),
            (11, [21, 22], {"highway": "primary", "ref": "B 1"}),
            (12, [22, 23], {"highway": "primary", "ref": "B 1"})]
    return nodes, ways


def test_short_link_between_two_ramps_in_l1(tmp_path, profile):
    nodes, ways = _interchange(300)
    model = build(str(write_osm(tmp_path / "a.osm", nodes, ways)), profile, "L1")
    w = _ways_in(model)
    assert w[11] == {"connector"}            # Bundesstraße zwischen den Rampen (300 m)
    assert 10 not in w and 12 not in w       # darüber hinaus nicht
    assert model["stats"]["same_level_links"]["links"] == 1


def test_long_link_is_not_added(tmp_path, profile):
    nodes, ways = _interchange(700)
    model = build(str(write_osm(tmp_path / "b.osm", nodes, ways)), profile, "L1")
    assert 11 not in _ways_in(model)


def test_loop_at_same_road_is_rejected(tmp_path, profile):
    # Bundesstraße mit einer Schleife (Tankstelle): 100 m auf der Straße, 160 m über die Schleife
    nodes = {1: _pt(0, 0), 2: _pt(400, 0), 3: _pt(500, 0), 4: _pt(900, 0),
             5: _pt(420, 30), 6: _pt(480, 30)}
    ways = [(1, [1, 2, 3, 4], {"highway": "primary", "ref": "B 1"}),
            (2, [2, 5, 6, 3], {"highway": "service"})]
    model = build(str(write_osm(tmp_path / "c.osm", nodes, ways)), profile, "L2")
    assert 2 not in _ways_in(model)
    assert model["stats"]["same_level_links"]["rejected_loop"] == 1


def test_escape_lane_next_to_road(tmp_path, profile):
    nodes = {1: _pt(0, 0), 2: _pt(500, 0), 3: _pt(1000, 0), 4: _pt(600, 40),
             10: _pt(0, 300), 11: _pt(200, 320)}
    ways = [(1, [1, 2, 3], {"highway": "primary", "ref": "B 1"}),
            (2, [2, 4], {"highway": "escape"}),
            (3, [10, 11], {"highway": "escape"})]   # nicht an der Strecke
    model = build(str(write_osm(tmp_path / "d.osm", nodes, ways)), profile, "L2")
    w = _ways_in(model)
    assert w[2] == {"connector"}
    assert 3 not in w


def test_industry_near_exit_whole_area_but_not_without_exit(tmp_path, profile):
    nodes = {1: _pt(-3000, 0), 2: _pt(-200, 0), 3: _pt(3000, 0),
             # Rampe endet an einer Gemeindestraße (Knoten 21)
             21: _pt(0, -150), 31: _pt(60, -150), 32: _pt(150, -150),
             # Gewerbegebiet am Ende der Gemeindestraße (200 m von der Rampe), zwei Straßen darin
             40: _pt(140, -250), 41: _pt(400, -250), 42: _pt(400, -50), 43: _pt(140, -50),
             51: _pt(300, -150), 52: _pt(300, -100), 53: _pt(300, -200),
             # Firma direkt neben der Autobahn, Zufahrt erst 2,5 km weiter über die Gemeindestraße
             60: _pt(2000, 40), 61: _pt(2100, 40), 62: _pt(2100, 120), 63: _pt(2000, 120),
             70: _pt(2050, 80), 71: _pt(2050, 400), 72: _pt(-200, 400)}
    ways = [(1, [1, 2, 3], {"highway": "motorway", "oneway": "no", "ref": "A 9"}),
            (2, [2, 21], {"highway": "motorway_link", "oneway": "no"}),
            (3, [21, 31, 32], {"highway": "unclassified"}),     # 150 m bis ins Gebiet
            (4, [40, 41, 42, 43, 40], {"landuse": "industrial"}),
            (5, [32, 51, 52], {"highway": "service"}),
            (6, [51, 53], {"highway": "service"}),
            (7, [60, 61, 62, 63, 60], {"landuse": "industrial"}),
            (8, [70, 71, 72, 21], {"highway": "unclassified"})]
    model = build(str(write_osm(tmp_path / "e.osm", nodes, ways)), profile, "L1")
    w = _ways_in(model)
    assert 3 in w                          # Zufahrt zum nahen Gebiet
    assert 5 in w and 6 in w               # ganzes Gebiet
    assert 8 not in w                      # Firma an der Autobahn ohne nahe Ausfahrt


def test_rest_area_loop_on_motorway_is_kept(tmp_path, profile):
    # Rastplatz: Schleife von der Autobahn (Knoten 2) zurück auf die Autobahn (Knoten 3), 700 m
    nodes = {1: _pt(-2000, 0), 2: _pt(-300, 0), 3: _pt(300, 0), 4: _pt(2000, 0),
             5: _pt(-200, -60), 6: _pt(200, -60)}
    ways = [(1, [1, 2, 3, 4], {"highway": "motorway", "oneway": "yes", "ref": "A 9"}),
            (2, [2, 5, 6, 3], {"highway": "service"})]
    model = build(str(write_osm(tmp_path / "r.osm", nodes, ways)), profile, "L1")
    assert _ways_in(model)[2] == {"connector"}
    assert model["stats"]["same_level_links"]["rest_loops"] == 1


def test_fuel_station_loop_on_road_is_kept(tmp_path, profile):
    # Tankstelle an der Bundesstraße: Ein- und Ausfahrt 60 m auseinander (eine Schleife)
    nodes = {1: _pt(0, 0), 2: _pt(400, 0), 3: _pt(460, 0), 4: _pt(900, 0),
             5: _pt(410, 25), 6: _pt(450, 25),
             10: _pt(415, 30), 11: _pt(445, 30), 12: _pt(445, 45), 13: _pt(415, 45)}
    ways = [(1, [1, 2, 3, 4], {"highway": "primary", "ref": "B 1"}),
            (2, [2, 5], {"highway": "service"}),
            (3, [5, 6], {"highway": "service"}),
            (4, [6, 3], {"highway": "service"}),
            (5, [10, 11, 12, 13, 10], {"amenity": "fuel", "building": "yes"})]
    model = build(str(write_osm(tmp_path / "f.osm", nodes, ways)), profile, "L2")
    w = _ways_in(model)
    assert w.get(2) == {"connector"} and w.get(3) == {"connector"} and w.get(4) == {"connector"}
    assert model["stats"]["services_access"]["connected"] == 1


def test_truck_stop_near_exit_with_both_accesses(tmp_path, profile):
    # Autohof 150 m hinter dem Rampenende, mit Ein- und Ausfahrt zur Gemeindestraße
    nodes = {1: _pt(-3000, 0), 2: _pt(-200, 0), 3: _pt(3000, 0),
             21: _pt(0, -150), 31: _pt(100, -150), 32: _pt(250, -150), 33: _pt(400, -150),
             40: _pt(140, -230), 41: _pt(210, -230),
             50: _pt(150, -260), 51: _pt(200, -260), 52: _pt(200, -300), 53: _pt(150, -300)}
    ways = [(1, [1, 2, 3], {"highway": "motorway", "oneway": "no", "ref": "A 9"}),
            (2, [2, 21], {"highway": "motorway_link", "oneway": "no"}),
            (3, [21, 31, 32, 33], {"highway": "unclassified"}),
            (4, [31, 40], {"highway": "service"}),
            (5, [40, 41], {"highway": "service"}),
            (6, [41, 32], {"highway": "service"}),
            (7, [50, 51, 52, 53, 50], {"amenity": "fuel", "name": "Autohof"})]
    model = build(str(write_osm(tmp_path / "t.osm", nodes, ways)), profile, "L1")
    w = _ways_in(model)
    assert 4 in w and 5 in w and 6 in w      # Schleife über den Autohof
    assert 3 in w                            # Zufahrt vom Rampenende
