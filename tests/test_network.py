# SPDX-License-Identifier: GPL-3.0-or-later
from array import array

import pytest

from osm2ets.network import Network, build_chains, oneway_sign
from osm2ets.osmread import Way, read_ways


def _roles(selected):
    return {(e.way.id, e.u, e.v): e.role for e in selected}


def test_oneway_sign(profile):
    imp = profile.implied_oneway
    assert oneway_sign({"highway": "motorway"}, imp) == 1
    assert oneway_sign({"highway": "motorway", "oneway": "no"}, imp) == 0
    assert oneway_sign({"highway": "primary"}, imp) == 0
    assert oneway_sign({"highway": "primary", "oneway": "-1"}, imp) == -1
    assert oneway_sign({"highway": "primary", "oneway": "yes"}, imp) == 1
    assert oneway_sign({"highway": "primary", "junction": "roundabout"}, imp) == 1
    assert oneway_sign({"highway": "primary", "oneway": "reversible"}, imp) == 0


def test_read_ways_filters(synthetic_osm, profile):
    lod = profile.lod("L1")
    ways = read_ways(str(synthetic_osm), lod.all_classes, profile.exclude_access, profile.exclude_service)
    ids = {w.id for w in ways}
    assert 1013 not in ids          # track
    assert 1014 not in ids          # access=private
    assert {1001, 1004, 1006, 1011, 1012} <= ids


def test_l1_selection(synthetic_osm, profile):
    lod = profile.lod("L1")
    ways = read_ways(str(synthetic_osm), lod.all_classes, profile.exclude_access, profile.exclude_service)
    net = Network(ways, profile)
    roles = _roles(net.select(lod))

    # Kern: Autobahn (an der Ausfahrt 2 geteilt) und Bundesstraße
    assert roles[(1001, 1, 2)] == "core" and roles[(1001, 2, 3)] == "core"
    assert roles[(1002, 11, 13)] == "core"
    assert roles[(1006, 51, 52)] == "core" and roles[(1007, 52, 53)] == "core"
    assert roles[(1008, 53, 32)] == "core" and roles[(1008, 32, 54)] == "core"
    # Rampe am Kernnetz
    assert roles[(1003, 2, 22)] == "link"
    # Anschlussstraßen bis zur nächsten Kreuzung
    assert roles[(1004, 31, 22)] == "connector"   # nach Süden bis zum Ende
    assert roles[(1004, 22, 32)] == "connector"   # nach Norden bis zur Bundesstraße
    assert roles[(1004, 32, 33)] == "connector"   # von der Bundesstraße bis zur Kreuzung 33
    assert roles[(1009, 51, 61)] == "connector"   # Wohnstraße bis zur Kreuzung 61
    # nicht weiter als bis zur nächsten Kreuzung
    assert (1004, 33, 34) not in roles
    assert (1005, 33, 41) not in roles
    assert (1009, 61, 62) not in roles
    assert (1010, 61, 63) not in roles
    # nicht angebunden
    assert (1011, 71, 72) not in roles
    assert (1012, 72, 73) not in roles


def test_connector_depth_two(synthetic_osm, profile):
    from dataclasses import replace
    lod = replace(profile.lod("L1"), connector_depth=2)
    ways = read_ways(str(synthetic_osm), lod.all_classes, profile.exclude_access, profile.exclude_service)
    roles = _roles(Network(ways, profile).select(lod))
    assert roles[(1004, 33, 34)] == "connector"
    assert roles[(1005, 33, 41)] == "connector"
    assert roles[(1009, 61, 62)] == "connector"


def test_chains_break_at_junctions_and_follow_oneway(synthetic_osm, profile):
    lod = profile.lod("L1")
    ways = read_ways(str(synthetic_osm), lod.all_classes, profile.exclude_access, profile.exclude_service)
    chains = build_chains(Network(ways, profile).select(lod))
    ends = {(c.start_node, c.end_node) for c in chains}
    # Autobahn Nord bricht an der Ausfahrt (Knoten 2); Fahrtrichtung = Wegrichtung
    assert (1, 2) in ends and (2, 3) in ends
    assert (11, 13) in ends
    assert (2, 22) in ends
    # Bundesstraße: Wohnstraße an 51 setzt sie durchgehend fort (Knoten 51 hat
    # in der Auswahl genau zwei Kantenenden), Bruch an der Kreuzung 32
    b = [c for c in chains if 32 in (c.start_node, c.end_node)
         and any(p.edge.way.id == 1007 for p in c.parts)]
    assert len(b) == 1
    assert set(b[0].node_ids()) >= {61, 51, 52, 53, 32}
    for c in chains:
        assert all(p.direction in (0, 1) for p in c.parts), "Einbahn muss vorwärts laufen"


def _net(ways, profile):
    return Network(ways, profile)


def test_reversed_oneway_is_flipped(profile):
    ways = [Way(1, array("q", [1, 2, 3]), {"highway": "primary", "oneway": "-1", "ref": "B 1"})]
    net = _net(ways, profile)
    chains = build_chains(net.select(profile.lod("L1")))
    assert len(chains) == 1
    assert chains[0].node_ids() == [3, 2, 1]


def test_head_to_head_oneways_break(profile):
    ways = [
        Way(1, array("q", [1, 2]), {"highway": "primary", "oneway": "yes", "ref": "B 1"}),
        Way(2, array("q", [3, 2]), {"highway": "primary", "oneway": "yes", "ref": "B 1"}),
    ]
    chains = build_chains(_net(ways, profile).select(profile.lod("L1")))
    assert len(chains) == 2
    assert {c.end_node for c in chains} == {2}


def test_isolated_ring_is_closed_chain(profile):
    ways = [Way(1, array("q", [1, 2, 3, 4, 1]), {"highway": "primary", "junction": "roundabout", "ref": "B 1"})]
    chains = build_chains(_net(ways, profile).select(profile.lod("L1")))
    assert len(chains) == 1
    assert chains[0].closed
    assert chains[0].node_ids()[0] == chains[0].node_ids()[-1]


def test_mixed_oneway_through_twoway_breaks(profile):
    # +1, dann Zweirichtung, dann gegenläufig: darf keine gemischte Kette ergeben
    ways = [
        Way(1, array("q", [1, 2]), {"highway": "primary", "oneway": "yes", "ref": "B 1"}),
        Way(2, array("q", [2, 3]), {"highway": "primary", "ref": "B 1"}),
        Way(3, array("q", [4, 3]), {"highway": "primary", "oneway": "yes", "ref": "B 1"}),
    ]
    chains = build_chains(_net(ways, profile).select(profile.lod("L1")))
    for c in chains:
        dirs = {p.direction for p in c.parts if p.direction}
        assert dirs <= {1}
    assert sum(len(c.parts) for c in chains) == 3
