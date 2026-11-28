# SPDX-License-Identifier: GPL-3.0-or-later
"""Bodenbedeckung fürs Geländematerial (landcover.landcover_entries)."""
import pytest

from osm2ets.landcover import kind_of
from osm2ets.pipeline import build

from .conftest import write_osm
from .test_buildings import _box
from .test_visible import _motorway


def test_kind_of_rules():
    assert kind_of({"landuse": "grass"}) == "lawn"
    assert kind_of({"landcover": "grass"}) == "lawn"
    assert kind_of({"leisure": "park"}) == "lawn"
    assert kind_of({"leisure": "pitch"}) == "lawn"                       # ohne surface: Rasen
    assert kind_of({"leisure": "pitch", "surface": "asphalt"}) is None
    assert kind_of({"landuse": "meadow"}) == "meadow" and kind_of({"natural": "grassland"}) == "meadow"
    assert kind_of({"landuse": "farmland"}) == "farmland"
    assert kind_of({"natural": "heath"}) == "heath" and kind_of({"natural": "scrub"}) == "scrub"
    assert kind_of({"landuse": "quarry"}) == "bare"
    assert kind_of({"landuse": "residential"}) is None and kind_of({"landuse": "forest"}) is None


def test_landcover_polygons_in_intermediate(tmp_path, profile):
    """Ganze Datei (nicht nur nahe der Strecke); Flächen unter landcover.min_area_m2 entfallen."""
    nodes, ways = {}, []
    _motorway(nodes, ways)
    _box(nodes, ways, 301, 0, 400, 300, 200, tags={"landuse": "farmland"})
    _box(nodes, ways, 302, 0, 3000, 100, 80, tags={"landuse": "meadow"})        # weit weg – trotzdem
    _box(nodes, ways, 303, 300, 400, 8, 8, tags={"landuse": "grass"})            # 64 m² – zu klein
    model = build(str(write_osm(tmp_path / "l.osm", nodes, ways)), profile, "L1")
    lc = {e["osm"]: e for e in model["landcover"]}
    assert set(lc) == {"w301", "w302"}
    assert lc["w301"]["kind"] == "farmland" and len(lc["w301"]["outer"]) >= 4
    assert model["stats"]["landcover"]["by_kind"] == {"farmland": 1, "meadow": 1}
