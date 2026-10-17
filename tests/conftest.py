# SPDX-License-Identifier: GPL-3.0-or-later
"""Hilfen für Tests: kleine OSM-Dateien aus Python-Beschreibungen erzeugen."""
from __future__ import annotations

from pathlib import Path
from xml.sax.saxutils import quoteattr

import pytest

from osm2ets.config import load_profile


def write_osm(path: Path, nodes: dict, ways: list) -> Path:
    """nodes: {id: (lon, lat)}, ways: [(id, [node ids], {tags})]"""
    lines = ['<?xml version="1.0" encoding="UTF-8"?>', '<osm version="0.6" generator="osm2ets-tests">']
    for nid, (lon, lat) in sorted(nodes.items()):
        lines.append(f'  <node id="{nid}" version="1" lat="{lat:.7f}" lon="{lon:.7f}"/>')
    for wid, refs, tags in ways:
        lines.append(f'  <way id="{wid}" version="1">')
        lines.extend(f'    <nd ref="{r}"/>' for r in refs)
        lines.extend(f"    <tag k={quoteattr(k)} v={quoteattr(str(v))}/>" for k, v in tags.items())
        lines.append("  </way>")
    lines.append("</osm>")
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return path


# Synthetisches Netz bei 11,5° O / 50,0° N (0,001° Breite ≈ 111 m, 0,001° Länge ≈ 71,5 m)
NODES = {
    # Autobahn Richtung Nord (W1, östliche Fahrbahn) und Süd (W2, westliche)
    1: (11.5003, 50.000), 2: (11.5003, 50.010), 3: (11.5003, 50.020),
    11: (11.5000, 50.020), 12: (11.5000, 50.010), 13: (11.5000, 50.000),
    # Ausfahrt (W3)
    21: (11.5015, 50.0105), 22: (11.5030, 50.011),
    # Landesstraße (W4) durch 22
    31: (11.5030, 50.005), 32: (11.5030, 50.015), 33: (11.5030, 50.020), 34: (11.5030, 50.025),
    # Wohnstraße an 33 (W5)
    41: (11.5050, 50.020),
    # Bundesstraße (W6a/b/c) mit Brücke über die Autobahn, Kreuzung mit W4 in 32
    51: (11.4950, 50.015), 52: (11.4995, 50.015), 53: (11.5008, 50.015), 54: (11.5060, 50.015),
    # Wohnstraßen am Westende der Bundesstraße (W7, W8)
    61: (11.4950, 50.018), 62: (11.4950, 50.021), 63: (11.4930, 50.018),
    # nicht angebundene Landesstraße (W9) und Rampe ohne Kernnetz (W10)
    71: (11.5200, 50.030), 72: (11.5250, 50.030), 73: (11.5260, 50.031),
    # Feldweg und private Straße
    81: (11.5040, 50.025), 82: (11.5030, 50.026),
}
WAYS = [
    (1001, [1, 2, 3], {"highway": "motorway", "ref": "A 99", "lanes": "2"}),
    (1002, [11, 12, 13], {"highway": "motorway", "ref": "A 99", "lanes": "2"}),
    (1003, [2, 21, 22], {"highway": "motorway_link"}),
    (1004, [31, 22, 32, 33, 34], {"highway": "secondary", "ref": "L 1"}),
    (1005, [33, 41], {"highway": "residential"}),
    (1006, [51, 52], {"highway": "primary", "ref": "B 999"}),
    (1007, [52, 53], {"highway": "primary", "ref": "B 999", "bridge": "yes", "layer": "1"}),
    (1008, [53, 32, 54], {"highway": "primary", "ref": "B 999"}),
    (1009, [51, 61, 62], {"highway": "residential"}),
    (1010, [61, 63], {"highway": "residential"}),
    (1011, [71, 72], {"highway": "secondary", "ref": "L 2"}),
    (1012, [72, 73], {"highway": "primary_link"}),
    (1013, [34, 81], {"highway": "track"}),
    (1014, [34, 82], {"highway": "residential", "access": "private"}),
]


@pytest.fixture
def profile():
    return load_profile("de")


@pytest.fixture
def synthetic_osm(tmp_path):
    return write_osm(tmp_path / "synthetic.osm", NODES, WAYS)
