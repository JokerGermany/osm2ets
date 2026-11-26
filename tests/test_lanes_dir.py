from types import SimpleNamespace

from osm2ets.geometry import RUN_FIELDS, run_key


def _part(tags, oneway=0, reverse=False):
    way = SimpleNamespace(tags={"highway": "primary", **tags})
    edge = SimpleNamespace(way=way, highway="primary", oneway=oneway)
    direction = (-oneway if reverse else oneway)
    return SimpleNamespace(edge=edge, reverse=reverse, direction=direction)


def _run(part):
    return dict(zip(RUN_FIELDS, run_key(part, "core")))


def test_forward_backward_kept_and_summed():
    r = _run(_part({"lanes:forward": "3", "lanes:backward": "1"}))
    assert (r["lanes_forward"], r["lanes_backward"], r["lanes"]) == (3, 1, 4)


def test_reversed_chain_swaps_directions():
    r = _run(_part({"lanes:forward": "3", "lanes:backward": "1"}, reverse=True))
    assert (r["lanes_forward"], r["lanes_backward"]) == (1, 3)


def test_lanes_tag_wins_over_sum():
    r = _run(_part({"lanes": "5", "lanes:forward": "3", "lanes:backward": "1"}))
    assert r["lanes"] == 5


def test_oneway_uses_forward_when_lanes_missing():
    r = _run(_part({"lanes:forward": "2"}, oneway=1))
    assert r["lanes"] == 2


def test_parse_sidewalk():
    from osm2ets.geometry import parse_sidewalk
    assert parse_sidewalk({"sidewalk": "both"}) == "both"
    assert parse_sidewalk({"sidewalk": "yes"}) == "both"
    assert parse_sidewalk({"sidewalk": "left"}) == "left"
    assert parse_sidewalk({"sidewalk": "left"}, reverse=True) == "right"
    assert parse_sidewalk({"sidewalk:right": "yes"}) == "right"
    assert parse_sidewalk({"sidewalk:left": "yes", "sidewalk:right": "yes"}) == "both"
    assert parse_sidewalk({"sidewalk": "separate"}) is None
    assert parse_sidewalk({"sidewalk": "no"}) is None
    assert parse_sidewalk({}) is None
