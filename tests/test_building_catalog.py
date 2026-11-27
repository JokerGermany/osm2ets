# SPDX-License-Identifier: GPL-3.0-or-later
"""Gebäudekatalog (scripts/building_catalog.py): .pmg-Teile, .pmd-Varianten, Box je Variante – an
künstlichen Dateien im Format der Spieldateien (keine echten Spieldateien)."""
import importlib.util
import pathlib
import struct

import pytest

_spec = importlib.util.spec_from_file_location(
    "building_catalog", pathlib.Path(__file__).resolve().parents[1] / "scripts" / "building_catalog.py")
bc = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(bc)

CHARS = "0123456789abcdefghijklmnopqrstuvwxyz_"


def tok(s: str) -> int:
    return sum((CHARS.index(c) + 1) * 38 ** i for i, c in enumerate(s))


def write_pmg(path, parts):
    """parts: [(Name, [(min, max) je Stück])] → .pmg Version 0x15 (nur Kopf, Teile, Stückköpfe)."""
    pieces = [pc for _n, pcs in parts for pc in pcs]
    parts_off = 112
    pieces_off = parts_off + 24 * len(parts)
    lo = [min(p[0][k] for p in pieces) for k in range(3)]
    hi = [max(p[1][k] for p in pieces) for k in range(3)]
    head = bytearray(112)
    head[0:4] = b"\x15gmP"
    struct.pack_into("<5i", head, 4, len(pieces), len(parts), 0, 0, 0)
    struct.pack_into("<3f", head, 48, *lo)
    struct.pack_into("<3f", head, 60, *hi)
    struct.pack_into("<10i", head, 72, parts_off, parts_off, pieces_off, pieces_off, 0, 0, 0, 0, 0, 0)
    body = bytearray()
    start = 0
    for name, pcs in parts:
        body += struct.pack("<Q4I", tok(name), len(pcs), start, 0, 0)
        start += len(pcs)
    for mn, mx in pieces:
        pc = bytearray(100)
        struct.pack_into("<3f", pc, 36, *mn)
        struct.pack_into("<3f", pc, 48, *mx)
        body += pc
    path.write_bytes(bytes(head + body))


def write_pmd(path, looks, variants, visible):
    """visible[v][p]: Teil p in Variante v sichtbar; je Teil ein Attribut „visible“ (Paar from/to)."""
    npart = len(visible[0])
    look_off = 64
    var_off = look_off + 8 * len(looks)
    pa_off = var_off + 8 * len(variants)
    attr_off = pa_off + 8 * npart
    av_off = attr_off + 16 * npart
    attrsize = 4 * npart
    mat_off = av_off + attrsize * len(variants)
    b = struct.pack("<16I", 4, 0, len(looks), 0, len(variants), npart, npart, attrsize, 0,
                    look_off, var_off, pa_off, av_off, attr_off, mat_off, mat_off)
    b += b"".join(struct.pack("<Q", tok(x)) for x in looks + variants)
    b += b"".join(struct.pack("<2I", p, p + 1) for p in range(npart))
    b += b"".join(struct.pack("<QiI", tok("visible"), 0, 4 * p) for p in range(npart))
    b += b"".join(struct.pack("<i", int(v)) for row in visible for v in row)
    path.write_bytes(b)


def test_variant_boxes_from_pmg_and_pmd(tmp_path):
    """Box je Variante aus den sichtbaren Teilen: Haus, Anbau nur in „big“, Aufsatz in „upper“ schwebt."""
    house = [((0, -1, 0), (10, 8, 6)), ((0, 8, 0), (10, 11, 6))]       # zwei Stücke
    annex = [((10, -1, 0), (14, 4, 6))]
    upper = [((0, 6, 0), (10, 9, 6))]
    write_pmg(tmp_path / "m.pmg", [("part1", house), ("part2", annex), ("part3", upper)])
    write_pmd(tmp_path / "m.pmd", ["default", "red"], ["small", "big", "upper"],
              [[1, 0, 0], [1, 1, 0], [0, 0, 1]])
    parts = bc.read_pmg_parts(str(tmp_path / "m.pmg"))
    assert [n for n, _ in parts] == ["part1", "part2", "part3"]
    looks, variants, visible = bc.read_pmd(str(tmp_path / "m.pmd"))
    assert (looks, variants) == (["default", "red"], ["small", "big", "upper"])
    boxes = dict(zip(variants, bc.variant_boxes(parts, visible)))
    assert boxes["small"] == pytest.approx(([0, -1, 0], [10, 11, 6]))
    assert boxes["big"] == pytest.approx(([0, -1, 0], [14, 11, 6]))     # mit Anbau
    assert boxes["upper"][0][1] == pytest.approx(6)                     # schwebt → bleibt im Katalog weg


def test_variant_filters():
    """Reihen-, Eck- und Hangstücke sowie Looks anderer Gegenden bleiben weg."""
    for v in ("left", "right", "corner_tall", "row_short", "connect", "slope_up", "empty", "ph_con_tall"):
        assert bc.ROW_VARIANT.search(v), v
    for v in ("default", "var1", "tall", "floors_4", "no_garage1", "variant2"):
        assert not bc.ROW_VARIANT.search(v), v
    assert bc.SKIP_LOOK.search("crs_fr") and bc.SKIP_LOOK.search("blk_south") and bc.SKIP_LOOK.search("reference")
    assert not bc.SKIP_LOOK.search("red_brick") and not bc.SKIP_LOOK.search("default")
