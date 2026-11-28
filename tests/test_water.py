# SPDX-License-Identifier: GPL-3.0-or-later
"""Wasserflächen (water.water_entries) und ihre Arten."""
from osm2ets.pipeline import build
from osm2ets.water import kind_of

from .conftest import write_osm
from .test_buildings import _box
from .test_visible import _motorway


def test_kind_of_rules():
    """Arten aus natural=water + water=*, landuse=basin/reservoir, Schwimm- und Kneippbecken, Flussflächen."""
    assert kind_of({"natural": "water"}) == "water"
    assert kind_of({"natural": "water", "water": "pond"}) == "pond"
    assert kind_of({"natural": "water", "water": "basin"}) == "basin"
    assert kind_of({"landuse": "basin"}) == "basin"
    assert kind_of({"natural": "water", "water": "reservoir"}) == "reservoir"
    assert kind_of({"landuse": "reservoir"}) == "reservoir"
    assert kind_of({"natural": "water", "water": "lock"}) == "lock"
    assert kind_of({"natural": "water", "water": "wastewater"}) == "wastewater"
    assert kind_of({"natural": "water", "water": "stream"}) == "river"
    assert kind_of({"natural": "water", "water": "river"}) == "river"
    assert kind_of({"waterway": "riverbank"}) == "river"
    assert kind_of({"natural": "water", "water": "oxbow"}) == "lake"
    assert kind_of({"natural": "water", "water": "something_else"}) == "water"
    assert kind_of({"leisure": "swimming_pool"}) == "pool"
    assert kind_of({"leisure": "swimming_pool", "indoor": "yes"}) is None
    assert kind_of({"leisure": "swimming_pool", "location": "roof"}) is None
    assert kind_of({"leisure": "swimming_pool", "covered": "yes"}) is None
    assert kind_of({"amenity": "kneipp_water_cure"}) == "kneipp"
    assert kind_of({"waterway": "stream"}) is None and kind_of({"landuse": "grass"}) is None


def test_water_polygons_in_intermediate(tmp_path, profile):
    """Ganze Datei, Multipolygone mit Inseln; Becken im Haus und Flächen unter water.min_area_m2 entfallen."""
    nodes, ways = {}, []
    _motorway(nodes, ways)
    _box(nodes, ways, 401, 0, 300, 60, 40, tags={"natural": "water", "water": "pond"})
    _box(nodes, ways, 402, 0, 3000, 25, 12, tags={"leisure": "swimming_pool"})                  # weit weg – trotzdem
    _box(nodes, ways, 403, 100, 300, 25, 12, tags={"leisure": "swimming_pool", "indoor": "yes"})
    _box(nodes, ways, 404, 200, 300, 4, 4, tags={"natural": "water"})                            # 16 m² – zu klein
    _box(nodes, ways, 405, -400, 500, 300, 200, tags={})                                         # See
    _box(nodes, ways, 406, -400, 500, 40, 30, tags={})                                           # Insel
    rel = (501, [("w", 405, "outer"), ("w", 406, "inner")], {"type": "multipolygon", "natural": "water", "water": "lake"})
    model = build(str(write_osm(tmp_path / "w.osm", nodes, ways, [rel])), profile, "L1")
    wa = {e["osm"]: e for e in model["water"]}
    assert set(wa) == {"w401", "w402", "r501"}, sorted(wa)
    assert wa["w401"]["kind"] == "pond" and len(wa["w401"]["outer"]) >= 4
    assert wa["w402"]["kind"] == "pool"
    assert wa["r501"]["kind"] == "lake" and len(wa["r501"]["inner"]) == 1
    o = wa["w401"]["outer"]                                     # Fläche (drehungsfrei: Meridiankonvergenz)
    area = 0.5 * abs(sum(a[0] * b[1] - b[0] * a[1] for a, b in zip(o, o[1:] + o[:1])))
    assert 2350 < area < 2450
    st = model["stats"]["water"]
    assert st["by_kind"] == {"pond": 1, "pool": 1, "lake": 1} and st["kept"] == 3
