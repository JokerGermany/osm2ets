from osm2ets.geometry import parse_width


def test_parse_width():
    assert parse_width({"width": "3"}) == 3.0
    assert parse_width({"width": "3,5 m"}) == 3.5
    assert parse_width({"width": "x"}) is None
    assert parse_width({}) is None
