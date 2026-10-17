# SPDX-License-Identifier: GPL-3.0-or-later
import numpy as np
import pytest

from osm2ets.geometry import (NO_NODE, Piece, Projector, chain_pieces, clip_piece, densify,
                              douglas_peucker, enforce_min_spacing, split_long_segments)


def test_projector_roundtrip():
    pr = Projector("EPSG:25832", (10.4477, 51.1634))
    lon = np.array([10.4477, 11.5, 7.0])
    lat = np.array([51.1634, 50.0, 53.5])
    xy = pr.forward(lon, lat)
    assert np.allclose(xy[0], [0.0, 0.0], atol=1e-6)
    back = pr.inverse(xy)
    assert np.allclose(back[:, 0], lon, atol=1e-9) and np.allclose(back[:, 1], lat, atol=1e-9)
    # grobe Plausibilität: 11,5° O liegt ~75 km östlich, 50,0° N ~130 km südlich
    assert 70_000 < xy[1, 0] < 80_000 and -135_000 < xy[1, 1] < -125_000


def test_douglas_peucker_straight_line_and_mandatory():
    pts = np.column_stack([np.linspace(0, 100, 11), np.zeros(11)])
    mand = np.zeros(11, bool)
    keep = douglas_peucker(pts, 0.1, mand)
    assert np.flatnonzero(keep).tolist() == [0, 10]
    mand[4] = True
    keep = douglas_peucker(pts, 0.1, mand)
    assert np.flatnonzero(keep).tolist() == [0, 4, 10]


def test_douglas_peucker_keeps_corner():
    pts = np.array([[0, 0], [50, 0], [100, 0], [100, 50], [100, 100]], float)
    keep = douglas_peucker(pts, 0.5, np.zeros(5, bool))
    assert np.flatnonzero(keep).tolist() == [0, 2, 4]


def test_enforce_min_spacing():
    pts = np.column_stack([np.array([0, 2, 4, 6, 8, 10, 12.0]), np.zeros(7), np.zeros(7)])
    prio = np.array([2, 0, 0, 1, 0, 0, 2])
    out = enforce_min_spacing(pts, list(range(7)), prio, 5.0)
    assert out[0] == 0 and out[-1] == 6
    assert all(np.linalg.norm(pts[b] - pts[a]) >= 5.0 for a, b in zip(out, out[1:]))
    # Stück kürzer als die Mindestlänge
    assert enforce_min_spacing(pts[:2], [0, 1], np.array([2, 2]), 5.0) is None


def test_enforce_min_spacing_keeps_last_point():
    pts = np.column_stack([np.array([0, 10, 12.0]), np.zeros(3), np.zeros(3)])
    out = enforce_min_spacing(pts, [0, 1, 2], np.array([2, 1, 2]), 5.0)
    assert out == [0, 2]


def test_split_long_segments():
    xs = np.linspace(0, 1200, 61)
    pts = np.column_stack([xs, np.zeros_like(xs), np.zeros_like(xs)])
    out = split_long_segments(pts, xs, [0, 60], 500.0)
    lengths = [xs[b] - xs[a] for a, b in zip(out, out[1:])]
    assert out[0] == 0 and out[-1] == 60
    assert max(lengths) <= 500.0 and len(lengths) == 3


@pytest.mark.parametrize("spacing", [7.3, 13.1, 19.86, 24.9])
@pytest.mark.parametrize("max_len", [100.0, 333.0, 500.0])
def test_split_long_segments_never_exceeds_max(spacing, max_len):
    # Regression: Einrasten auf verdichtete Punkte darf die Grenze nicht reißen
    xs = np.arange(0.0, 2500.0, spacing)
    pts = np.column_stack([xs, np.zeros_like(xs), np.zeros_like(xs)])
    out = split_long_segments(pts, xs, [0, 5, 6, len(xs) - 1], max_len)
    lengths = np.diff(xs[out])
    assert lengths.max() <= max_len + 1e-9
    assert out[0] == 0 and out[-1] == len(xs) - 1 and out == sorted(set(out))


def test_densify_keeps_vertices():
    xy = np.array([[0, 0], [100, 0], [100, 30]], float)
    d = densify(xy, np.array([7, 8, 9]), np.array([0, 1]), 20.0)
    assert d.node_ids[0] == 7 and d.node_ids[-1] == 9
    assert 8 in d.node_ids.tolist()
    assert np.isclose(d.s[-1], 130.0)
    assert np.all(np.diff(d.s) <= 20.0 + 1e-9)


class _FakeChain:
    def __init__(self, ids, parts_len):
        self._ids = ids
        self.parts = [type("P", (), {"edge": type("E", (), {"i0": 0, "i1": n})()})() for n in parts_len]
        self.id = 0

    def node_ids(self):
        return self._ids


def test_chain_pieces_splits_at_missing_nodes():
    chain = _FakeChain([1, 2, 3, 4, 5], [4])
    locs = {1: (0, 0), 2: (0.001, 0), 4: (0.003, 0), 5: (0.004, 0)}
    pieces = chain_pieces(chain, locs)
    assert [p.node_ids.tolist() for p in pieces] == [[1, 2], [4, 5]]
    assert pieces[0].end_cut and not pieces[0].start_cut
    assert pieces[1].start_cut and not pieces[1].end_cut


def test_clip_piece():
    chain = _FakeChain([1, 2, 3], [2])
    p = Piece(chain=chain, index=0, lonlat=np.array([[0.0, 0.5], [2.0, 0.5], [2.0, 2.0]]),
              node_ids=np.array([1, 2, 3]), seg_part=np.array([0, 0]))
    out = clip_piece(p, (1.0, 0.0, 3.0, 1.0))
    assert len(out) == 1
    q = out[0]
    assert np.allclose(q.lonlat[0], [1.0, 0.5]) and np.allclose(q.lonlat[-1], [2.0, 1.0])
    assert q.node_ids[0] == NO_NODE and q.node_ids[1] == 2 and q.node_ids[-1] == NO_NODE
    assert q.start_cut and q.end_cut
    # ganz außerhalb
    assert clip_piece(p, (5.0, 5.0, 6.0, 6.0)) == []
