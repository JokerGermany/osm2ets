# SPDX-License-Identifier: GPL-3.0-or-later
"""Gleise nahe der Strecke (rail.rail_sections)."""
from osm2ets.osmread import rail_tags
from osm2ets.pipeline import build
from osm2ets.qa import crossing_clearances

from .conftest import write_osm
from .test_visible import _motorway, _pt


def _rails(model):
    return [r for r in model["roads"] if r["role"] == "rail"]


def test_rail_under_motorway_bridge_only_near_section(tmp_path, profile):
    """Eine Bahnstrecke unterquert die Autobahn (Brücke). In L1 kommt nur der Abschnitt nahe der
    Autobahn in die Karte (near_m / crossing_m, je 300 m), als Pseudo-Klasse railway mit Rolle rail;
    die Brücke behält ihren Lichtraum."""
    nodes = {21: _pt(0, -2000), 22: _pt(0, 2000)}
    ways = [(21, [21, 22], {"railway": "rail", "usage": "main", "ref": "5051"})]
    _motorway(nodes, ways, bridge_x=0)
    model = build(str(write_osm(tmp_path / "r.osm", nodes, ways)), profile, "L1")
    rails = _rails(model)
    assert rails and all(run["highway"] == "railway" and run["role"] == "rail" and run["lanes"] == 1
                         for r in rails for run in r["runs"])
    length = sum(r["length_m"] for r in rails)
    assert 600 < length < 700, length            # 300 m beiderseits der Fahrbahnen (30 m auseinander)
    assert {r["start"]["kind"] for r in rails} | {r["end"]["kind"] for r in rails} == {"cut"}
    clear = crossing_clearances(model["roads"])
    assert len(clear) == 2 and min(c["clearance_m"] for c in clear) >= profile.bridge_clearance_m - 0.3
    st = model["stats"]["railway"]
    assert st["crossings"] == 2 and st["level_crossings"] == 0


def test_rail_bridge_over_motorway_keeps_rail_grade(tmp_path, profile):
    """Die Bahn führt auf einer Brücke über die Autobahn: Die Autobahn bleibt liegen, das Gleis wird
    angehoben – mit höchstens railway.max_grade (2,5 %), nicht mit der Rampensteigung der Straßen."""
    nodes = {21: _pt(0, -1500), 22: _pt(0, -40), 23: _pt(0, 70), 24: _pt(0, 1500)}
    ways = [(21, [21, 22], {"railway": "rail"}),
            (22, [22, 23], {"railway": "rail", "bridge": "yes", "layer": "1"}),
            (23, [23, 24], {"railway": "rail"})]
    _motorway(nodes, ways)
    model = build(str(write_osm(tmp_path / "b.osm", nodes, ways)), profile, "L1")
    rails = _rails(model)
    assert rails
    clear = crossing_clearances(model["roads"])
    assert len(clear) == 2 and min(c["clearance_m"] for c in clear) >= profile.bridge_clearance_m - 0.3
    for r in rails:
        p = r["points"]
        for a, b in zip(p, p[1:]):
            run = next(u for u in r["runs"] if u["from"] <= p.index(a) < u["to"])
            h = ((b[0] - a[0]) ** 2 + (b[2] - a[2]) ** 2) ** 0.5
            if not run["bridge"] and h > 1:
                assert abs(b[1] - a[1]) / h <= profile.railway["max_grade"] + 0.003
    mw = [r for r in model["roads"] if r["role"] == "core"]
    assert all(abs(p[1] - mw[0]["points"][0][1]) < 0.5 for r in mw for p in r["points"])


def test_far_rail_and_sidings_are_left_out(tmp_path, profile):
    """Ein Gleis 600 m neben der Autobahn und ein Abstellgleis direkt daneben bleiben weg."""
    nodes = {31: _pt(-500, -600), 32: _pt(500, -600), 41: _pt(-500, -60), 42: _pt(500, -60)}
    ways = [(31, [31, 32], {"railway": "rail"}),
            (41, [41, 42], {"railway": "rail", "service": "siding"})]
    _motorway(nodes, ways)
    model = build(str(write_osm(tmp_path / "f.osm", nodes, ways)), profile, "L1")
    assert not _rails(model)


def test_rail_is_cut_at_level_crossing_with_map_road(tmp_path, profile):
    """Ein Gleis neben der Autobahn kreuzt eine Straße der Karte ebenerdig (gemeinsamer OSM-Knoten):
    Prefabs für Bahnübergänge gibt es noch nicht, das Gleis endet beiderseits davor
    (level_crossing_gap_m) – und teilt keinen Knoten mit der Straße."""
    mw = {"highway": "motorway", "ref": "A 1", "oneway": "yes", "lanes": "2"}
    nodes = {1: _pt(-800, 0), 5: _pt(60, 0), 4: _pt(800, 0), 11: _pt(800, 30), 14: _pt(-800, 30),
             31: _pt(-500, -80), 32: _pt(0, -80), 33: _pt(500, -80), 41: _pt(-20, -300)}
    ways = [(1, [1, 5, 4], mw), (11, [11, 14], mw),
            (31, [31, 32, 33], {"railway": "rail"}),
            # Einfahrt als Straße der Stufe, die bei x = 0 über den Bahnübergang zur Autobahn führt
            (41, [41, 32, 5], {"highway": "motorway_link", "oneway": "yes"})]
    model = build(str(write_osm(tmp_path / "l.osm", nodes, ways)), profile, "L1")
    rails = _rails(model)
    assert len(rails) == 2, [r["id"] for r in rails]
    assert model["stats"]["railway"]["level_crossings"] == 1
    road_nodes = {r[k].get("osm_node") for r in model["roads"] if r["role"] != "rail" for k in ("start", "end")}
    assert 32 not in road_nodes | {r[k].get("osm_node") for r in rails for k in ("start", "end")}
    link = [r for r in model["roads"] if r["role"] == "link"]
    assert link
    road_pts = [(p[0], p[2]) for r in link for p in r["points"]]

    def dist_to_link(q):
        best = float("inf")
        for a, b in zip(road_pts, road_pts[1:]):
            ab = (b[0] - a[0], b[1] - a[1])
            t = max(0.0, min(1.0, ((q[0] - a[0]) * ab[0] + (q[1] - a[1]) * ab[1]) / max(ab[0] ** 2 + ab[1] ** 2, 1e-9)))
            best = min(best, ((q[0] - a[0] - t * ab[0]) ** 2 + (q[1] - a[1] - t * ab[1]) ** 2) ** 0.5)
        return best
    for r in rails:
        # 15 m längs des Gleises vor dem Bahnübergang; die Einfahrt kreuzt dort schräg (gut 50°)
        assert min(dist_to_link((p[0], p[2])) for p in r["points"]) >= 11.0


def test_rail_tags_tracks_and_direction():
    """Gleiszahl aus tracks (sonst 1) als lanes; eingleisig mit Vorzugsrichtung als Einbahn."""
    t = rail_tags({"railway": "rail", "tracks": "2", "railway:preferred_direction": "forward"})
    assert t["highway"] == "railway" and t["lanes"] == "2" and "oneway" not in t
    t = rail_tags({"railway": "rail", "railway:preferred_direction": "backward"})
    assert t["lanes"] == "1" and t["oneway"] == "-1"
    t = rail_tags({"railway": "rail", "railway:preferred_direction": "both"})
    assert "oneway" not in t
