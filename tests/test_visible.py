# SPDX-License-Identifier: GPL-3.0-or-later
"""Sichtstücke: Straßen höherer Stufen, die man von der Strecke aus sieht (extras.visible_roads)."""
import numpy as np

from osm2ets.pipeline import build, implicit_bridges
from osm2ets.qa import crossing_clearances

from .conftest import write_osm

LAT = 50.0
DX = 1 / 71_500   # ≈ 1 m in Länge bei 50° N
DY = 1 / 111_200  # ≈ 1 m in Breite


def _pt(x_m, y_m, lon0=11.5):
    return (lon0 + x_m * DX, LAT + y_m * DY)


def _motorway(nodes, ways, bridge_x=None):
    """Zwei Richtungsfahrbahnen längs x (y = 0 und y = 30), optional mit Brücke bei bridge_x ± 10."""
    mw = {"highway": "motorway", "ref": "A 1", "oneway": "yes", "lanes": "2"}
    br = dict(mw, bridge="yes", layer="1")
    nodes.update({1: _pt(-800, 0), 4: _pt(800, 0), 11: _pt(800, 30), 14: _pt(-800, 30)})
    if bridge_x is None:
        ways += [(1, [1, 4], mw), (11, [11, 14], mw)]
        return
    nodes.update({2: _pt(bridge_x - 10, 0), 3: _pt(bridge_x + 10, 0),
                  12: _pt(bridge_x + 10, 30), 13: _pt(bridge_x - 10, 30)})
    ways += [(1, [1, 2], mw), (2, [2, 3], br), (3, [3, 4], mw),
             (11, [11, 12], mw), (12, [12, 13], br), (13, [13, 14], mw)]


def _visible(model):
    return [r for r in model["roads"] if r["runs"][0]["role"] == "visible"]


def test_l1_shows_the_road_under_a_motorway_bridge(tmp_path, profile):
    """Die Autobahn führt über einen Wirtschaftsweg, der erst zu L5 gehört. Auch in L1 liegt er
    unter der Brücke – beiderseits ein Stück (visible_crossing_m) mit offenem Ende –, die Brücke
    bleibt Brücke und hat Lichtraum. Höhere Stufen setzen den Weg fort."""
    nodes, ways = {21: _pt(0, -600), 22: _pt(0, 600)}, [(21, [21, 22], {"highway": "service"})]
    _motorway(nodes, ways, bridge_x=0)
    model = build(str(write_osm(tmp_path / "v.osm", nodes, ways)), profile, "L1")
    vis = _visible(model)
    assert vis and all(run["highway"] == "service" for r in vis for run in r["runs"])
    length = sum(r["length_m"] for r in vis)
    assert 380 < length < 460, length            # 200 m beiderseits der beiden Kreuzungen
    assert {r["start"]["kind"] for r in vis} | {r["end"]["kind"] for r in vis} == {"cut"}
    bridges = [run for r in model["roads"] for run in r["runs"] if run["highway"] == "motorway" and run["bridge"]]
    assert len(bridges) == 2
    clear = crossing_clearances(model["roads"])
    assert len(clear) == 2 and min(c["clearance_m"] for c in clear) >= profile.bridge_clearance_m - 0.3
    assert model["stats"]["visible_roads"]["crossings"] == 2


def test_near_track_is_shown_far_track_is_not(tmp_path, profile):
    """Ein Feldweg 40 m neben der Fahrbahn gehört dazu, einer 150 m daneben nicht."""
    nodes = {31: _pt(-300, -40), 32: _pt(300, -40), 41: _pt(-300, -150), 42: _pt(300, -150)}
    ways = [(31, [31, 32], {"highway": "track"}), (41, [41, 42], {"highway": "track"})]
    _motorway(nodes, ways)
    model = build(str(write_osm(tmp_path / "n.osm", nodes, ways)), profile, "L1")
    vis = _visible(model)
    assert [w for r in vis for run in r["runs"] for w in run["osm_ways"]] == [31]
    assert vis[0]["length_m"] > 500


def test_short_tunnel_under_road_becomes_open_underpass():
    """Ein Weg, der in OSM als kurzer Tunnel unter der Autobahn liegt: Die Autobahn wird darüber zur
    Brücke (lang genug für die Böschungen), der Weg zum normalen Abschnitt – offene Unterführung."""
    upper = {"id": "u", "points": [[-100.0, 10.0, 0.0], [100.0, 10.0, 0.0]],
             "runs": [{"highway": "motorway", "role": "core", "bridge": False, "tunnel": False, "layer": 0,
                       "osm_ways": [1], "from": 0, "to": 1}]}
    lower = {"id": "l", "points": [[0.0, 3.0, -60.0], [0.0, 3.0, -15.0], [0.0, 3.0, 15.0], [0.0, 3.0, 60.0]],
             "runs": [{"highway": "track", "role": "visible", "bridge": False, "tunnel": False, "layer": 0,
                       "osm_ways": [2], "from": 0, "to": 1},
                      {"highway": "track", "role": "visible", "bridge": False, "tunnel": True, "layer": -1,
                       "osm_ways": [3], "from": 1, "to": 2},
                      {"highway": "track", "role": "visible", "bridge": False, "tunnel": False, "layer": 0,
                       "osm_ways": [4], "from": 2, "to": 3}]}
    assert implicit_bridges([upper, lower]) == 1
    br = [r for r in upper["runs"] if r["bridge"]]
    assert len(br) == 1 and br[0]["osm_ways"] == [1] and br[0]["layer"] == 1
    xs = [p[0] for p in upper["points"][br[0]["from"]:br[0]["to"] + 1]]
    # halbe Länge: 4 m Weg + 1,5 m flach + 7 m Höhe / 0,67 ≈ 16 m
    assert -18 < min(xs) < -14 and 14 < max(xs) < 18
    assert not any(r["tunnel"] for r in lower["runs"])
    assert upper["runs"][0]["from"] == 0 and upper["runs"][-1]["to"] == len(upper["points"]) - 1
    assert np.allclose([p[1] for p in upper["points"]], 10.0)


def test_short_dangling_stub_is_dropped(tmp_path, profile):
    """Ein 12-m-Feldweg, der vom Weg unter der Brücke abzweigt und gleich endet, entfällt – sein
    Straßengelände stand sonst als Keil neben der Einmündung."""
    nodes = {21: _pt(0, -600), 23: _pt(0, -45), 22: _pt(0, 600), 24: _pt(12, -45)}
    ways = [(21, [21, 23, 22], {"highway": "service"}), (31, [23, 24], {"highway": "track"})]
    _motorway(nodes, ways, bridge_x=0)
    model = build(str(write_osm(tmp_path / "s.osm", nodes, ways)), profile, "L1")
    ways_in = {w for r in _visible(model) for run in r["runs"] for w in run["osm_ways"]}
    assert 21 in ways_in and 31 not in ways_in


def test_short_edge_between_bridge_and_junction_is_kept(tmp_path, profile):
    """Ein Feldweg führt auf einer Brücke über die Autobahn; das Brückenende liegt 6 m vor einer
    Einmündung. Das 6-m-Stück dazwischen gehört dazu – fehlte es, endete die Brücke frei in der Luft
    (A 70 bei Bayreuth: 13 m über dem Weg)."""
    nodes = {51: _pt(0, -40), 52: _pt(0, 70), 53: _pt(0, -300), 61: _pt(0, 76),
             63: _pt(300, 76), 65: _pt(-300, 76), 67: _pt(0, 300)}
    track = {"highway": "track"}
    ways = [(50, [51, 52], dict(track, bridge="yes", layer="1")), (54, [53, 51], track),
            (60, [52, 61], track), (62, [61, 63], track), (64, [61, 65], track), (66, [61, 67], track)]
    _motorway(nodes, ways)
    model = build(str(write_osm(tmp_path / "j.osm", nodes, ways)), profile, "L1")
    vis = _visible(model)
    ways_in = {w for r in vis for run in r["runs"] for w in run["osm_ways"]}
    assert {50, 60} <= ways_in
    # kein Brückenende ohne anschließende Straße
    ends: dict = {}
    for r in model["roads"]:
        for e in (r["start"], r["end"]):
            if e["kind"] == "node":
                ends[e["osm_node"]] = ends.get(e["osm_node"], 0) + 1
    for r in vis:
        for e, run in ((r["start"], r["runs"][0]), (r["end"], r["runs"][-1])):
            if run["bridge"]:
                assert e["kind"] == "node" and ends[e["osm_node"]] >= 2, (r["id"], e)


def test_skewed_way_under_bridge_has_no_gap(tmp_path, profile):
    """Ein Weg unterquert die Autobahn sehr schräg (20°). Neben der Brücke liegt er tiefer als die
    Fahrbahn daneben; früher fiel dort ein Stück heraus (Loch in der Straße, St 2183 unter der A 70).
    Jetzt bleibt der Weg am Stück."""
    import math
    a = math.radians(20)
    nodes = {21: _pt(-300 * math.cos(a), -300 * math.sin(a)), 22: _pt(300 * math.cos(a), 300 * math.sin(a))}
    ways = [(21, [21, 22], {"highway": "service"})]
    _motorway(nodes, ways, bridge_x=0)
    model = build(str(write_osm(tmp_path / "k.osm", nodes, ways)), profile, "L1")
    vis = [r for r in _visible(model) if any(21 in run["osm_ways"] for run in r["runs"])]
    assert vis
    # höchstens die beiden offenen Enden (200 m ab den Kreuzungen) – kein Loch dazwischen
    cuts = sum(e["kind"] == "cut" for r in vis for e in (r["start"], r["end"]))
    assert cuts == 2, [(r["id"], r["start"]["kind"], r["end"]["kind"]) for r in vis]
