# SPDX-License-Identifier: GPL-3.0-or-later
"""Straßengraph, Auswahl nach Detailstufe und durchgehende Ketten.

Begriffe:
  Graphknoten  – OSM-Knoten, an dem ein Weg endet oder den mehrere Wege teilen.
  Kante        – Wegstück zwischen zwei Graphknoten.
  Kreuzung     – Graphknoten mit mindestens drei Kantenenden.
  Kette        – maximale Folge von Kanten der Auswahl, die nur über Knoten mit
                 genau zwei Kantenenden (in der Auswahl) läuft. Das ist die
                 "durchgehende Straße", die später als Folge von ETS2-Straßenstücken
                 geschrieben wird.
"""
from __future__ import annotations

import logging
from collections import defaultdict, deque
from dataclasses import dataclass, field
from typing import Optional

import numpy as np

from .config import LodConfig, Profile
from .osmread import Way

log = logging.getLogger(__name__)

ROLE_CORE = "core"
ROLE_LINK = "link"
ROLE_CONNECTOR = "connector"
_ROLE_RANK = {ROLE_CORE: 0, ROLE_LINK: 1, ROLE_CONNECTOR: 2}


def oneway_sign(tags: dict, implied: frozenset) -> int:
    """+1 = Einbahn in Wegrichtung, -1 = gegen Wegrichtung, 0 = beide Richtungen."""
    ow = tags.get("oneway")
    if ow in ("yes", "true", "1"):
        return 1
    if ow in ("-1", "reverse"):
        return -1
    if ow in ("no", "false", "0"):
        return 0
    if ow is not None:
        # reversible, alternating, ...: zeitabhängig – als Zweirichtungsstraße behandeln
        return 0
    if tags.get("junction") in ("roundabout", "circular"):
        return 1
    if tags.get("highway") in implied:
        return 1
    return 0


def parse_layer(tags: dict) -> int:
    raw = tags.get("layer")
    if raw is None:
        return 0
    try:
        return int(round(float(str(raw).split(";")[0].strip())))
    except ValueError:
        return 0


def is_bridge(tags: dict) -> bool:
    return tags.get("bridge") not in (None, "no")


def is_tunnel(tags: dict) -> bool:
    # building_passage = Durchfahrt unter einem Gebäude, liegt auf Geländehöhe
    return tags.get("tunnel") not in (None, "no", "building_passage")


def parse_lanes(tags: dict) -> Optional[int]:
    raw = tags.get("lanes")
    if raw is None:
        return None
    try:
        value = int(str(raw).split(";")[0].strip())
    except ValueError:
        return None
    return value if value > 0 else None


@dataclass
class Edge:
    idx: int
    way: Way
    i0: int
    i1: int
    oneway: int
    role: Optional[str] = None

    @property
    def u(self) -> int:
        return self.way.nodes[self.i0]

    @property
    def v(self) -> int:
        return self.way.nodes[self.i1]

    @property
    def highway(self) -> str:
        return self.way.tags["highway"]

    def node_ids(self, reverse: bool = False) -> list[int]:
        seq = list(self.way.nodes[self.i0:self.i1 + 1])
        return seq[::-1] if reverse else seq


@dataclass
class ChainPart:
    edge: Edge
    reverse: bool

    @property
    def start(self) -> int:
        return self.edge.v if self.reverse else self.edge.u

    @property
    def end(self) -> int:
        return self.edge.u if self.reverse else self.edge.v

    @property
    def direction(self) -> int:
        """Einbahn-Richtung relativ zur Kettenrichtung."""
        return -self.edge.oneway if self.reverse else self.edge.oneway


@dataclass
class Chain:
    id: int
    parts: list[ChainPart] = field(default_factory=list)

    @property
    def start_node(self) -> int:
        return self.parts[0].start

    @property
    def end_node(self) -> int:
        return self.parts[-1].end

    @property
    def closed(self) -> bool:
        return self.start_node == self.end_node

    @property
    def role(self) -> str:
        return min((p.edge.role for p in self.parts), key=_ROLE_RANK.__getitem__)

    def node_ids(self) -> list[int]:
        out: list[int] = []
        for part in self.parts:
            seq = part.edge.node_ids(part.reverse)
            out.extend(seq if not out else seq[1:])
        return out

    def reverse(self) -> None:
        self.parts = [ChainPart(p.edge, not p.reverse) for p in reversed(self.parts)]


class Network:
    """Graph über alle gelesenen Wege."""

    def __init__(self, ways: list[Way], profile: Profile):
        self.ways = ways
        self.profile = profile
        self.edges: list[Edge] = []
        self.adjacency: dict[int, list[tuple[int, int]]] = defaultdict(list)
        self._build()

    def _build(self) -> None:
        if not self.ways:
            return
        refs = np.concatenate([np.frombuffer(w.nodes, dtype=np.int64) for w in self.ways])
        uniq, counts = np.unique(refs, return_counts=True)
        shared = set(uniq[counts >= 2].tolist())
        implied = self.profile.implied_oneway
        for way in self.ways:
            nodes = way.nodes
            n = len(nodes)
            cuts = [0] + [i for i in range(1, n - 1) if nodes[i] in shared] + [n - 1]
            ow = oneway_sign(way.tags, implied)
            for a, b in zip(cuts, cuts[1:]):
                e = Edge(idx=len(self.edges), way=way, i0=a, i1=b, oneway=ow)
                self.edges.append(e)
                self.adjacency[e.u].append((e.idx, 0))
                self.adjacency[e.v].append((e.idx, 1))
        log.info("Graph: %d Kanten, %d Graphknoten", len(self.edges), len(self.adjacency))

    def degree(self, node: int) -> int:
        return len(self.adjacency.get(node, ()))

    @staticmethod
    def other_end(edge: Edge, end: int) -> int:
        return edge.v if end == 0 else edge.u

    # ------------------------------------------------------------------ Auswahl

    def select(self, lod: LodConfig) -> list[Edge]:
        """Markiert die Kanten der Detailstufe mit ihrer Rolle und gibt sie zurück."""
        for e in self.edges:
            e.role = None

        core_nodes: set[int] = set()
        for e in self.edges:
            if lod.is_core(e.way.tags):
                e.role = ROLE_CORE
                core_nodes.update((e.u, e.v))

        # Rampen: nur Rampen-Komponenten, die das Kernnetz berühren
        network_nodes = set(core_nodes)
        queue = deque(core_nodes)
        while queue:
            node = queue.popleft()
            for ei, end in self.adjacency.get(node, ()):
                e = self.edges[ei]
                if e.role is None and e.highway in lod.links:
                    e.role = ROLE_LINK
                    other = self.other_end(e, end)
                    if other not in network_nodes:
                        network_nodes.add(other)
                        queue.append(other)

        # Anschlussstraßen: von jedem Netzknoten aus bis zur nächsten Kreuzung
        frontier = set(network_nodes)
        reached = set(network_nodes)
        for _ in range(lod.connector_depth):
            new_frontier: set[int] = set()
            for node in frontier:
                for ei, end in list(self.adjacency.get(node, ())):
                    e = self.edges[ei]
                    if e.role is not None or e.highway not in lod.connector_classes:
                        continue
                    last = self._walk_connector(node, ei, end, lod, network_nodes)
                    if last is not None and last not in reached:
                        new_frontier.add(last)
            reached |= new_frontier
            network_nodes |= new_frontier
            frontier = new_frontier

        selected = [e for e in self.edges if e.role is not None]
        counts = defaultdict(int)
        for e in selected:
            counts[e.role] += 1
        log.info("Auswahl %s: %s", lod.name, dict(counts))
        return selected

    def _walk_connector(self, start: int, ei: int, end: int, lod: LodConfig,
                        network_nodes: set[int]) -> Optional[int]:
        """Folgt einer Anschlussstraße ab `start`, bis eine Kreuzung oder ein
        Ende erreicht ist. Gibt den letzten Knoten zurück."""
        seen: set[int] = set()
        node = start
        while True:
            e = self.edges[ei]
            if ei in seen:
                return None
            seen.add(ei)
            e.role = ROLE_CONNECTOR
            nxt = self.other_end(e, end)
            if nxt == node and e.u == e.v:  # Schleife
                return nxt
            if nxt in network_nodes or self.degree(nxt) != 2:
                return nxt
            # einzige andere Kante am Knoten suchen
            following = [(i, en) for i, en in self.adjacency[nxt] if not (i == ei and en == (1 - end))]
            if len(following) != 1:
                return nxt
            ei, end = following[0]
            f = self.edges[ei]
            if f.role is not None or f.highway not in lod.connector_classes:
                return nxt
            node = nxt


def _local_conflict(adj_entries, edges) -> bool:
    """True, wenn zwei Einbahn-Kanten an einem Knoten gegeneinander laufen."""
    (e1, end1), (e2, end2) = adj_entries
    a, b = edges[e1], edges[e2]
    # Durchlauf von a in den Knoten hinein, dann b hinaus
    d1 = a.oneway if end1 == 1 else -a.oneway
    d2 = b.oneway if end2 == 0 else -b.oneway
    return d1 != 0 and d2 != 0 and d1 != d2


def build_chains(selected: list[Edge]) -> list[Chain]:
    """Fasst die ausgewählten Kanten zu durchgehenden Ketten zusammen.

    Ketten brechen an Kreuzungen und Enden (in der Auswahl) sowie dort, wo
    Einbahnrichtungen gegeneinander laufen. Ketten mit Einbahnanteil werden
    so ausgerichtet, dass die Fahrtrichtung der Kettenrichtung entspricht.
    """
    sadj: dict[int, list[tuple[int, int]]] = defaultdict(list)
    by_idx = {e.idx: e for e in selected}
    for e in selected:
        sadj[e.u].append((e.idx, 0))
        sadj[e.v].append((e.idx, 1))

    breaks = {n for n, entries in sadj.items()
              if len(entries) != 2 or _local_conflict(entries, by_idx)}

    for _attempt in range(50):
        chains, new_breaks = _assemble(selected, sadj, by_idx, breaks)
        if not new_breaks:
            break
        breaks |= new_breaks
    else:  # pragma: no cover - sollte nie eintreten
        log.warning("Kettenbildung hat nach 50 Versuchen nicht stabilisiert")

    for chain in chains:
        dirs = {p.direction for p in chain.parts if p.direction != 0}
        if dirs == {-1}:
            chain.reverse()
    # stabile, reproduzierbare Reihenfolge und IDs
    chains.sort(key=lambda c: (_ROLE_RANK[c.role], min(p.edge.way.id for p in c.parts),
                               c.parts[0].edge.i0))
    for i, chain in enumerate(chains):
        chain.id = i
    return chains


def _assemble(selected, sadj, by_idx, breaks):
    visited: set[int] = set()
    chains: list[Chain] = []
    new_breaks: set[int] = set()

    def walk(start_node: int, ei: int, end: int) -> Chain:
        chain = Chain(id=-1)
        chain_dir = 0
        while True:
            e = by_idx[ei]
            part = ChainPart(e, reverse=(end == 1))
            d = part.direction
            if chain.parts and d != 0 and chain_dir != 0 and d != chain_dir:
                new_breaks.add(part.start)
                break
            visited.add(ei)
            chain.parts.append(part)
            if d != 0:
                chain_dir = d
            nxt = part.end
            if nxt in breaks or nxt == start_node:
                break
            following = [(i, en) for i, en in sadj[nxt] if i != ei]
            if len(following) != 1:
                break
            ei, end = following[0]
            if ei in visited:
                break
        return chain

    # zuerst von Bruchstellen aus (deterministische Reihenfolge)
    for node in sorted(breaks):
        for ei, end in sorted(sadj[node]):
            if ei in visited:
                continue
            chains.append(walk(node, ei, end))
    # übrig bleiben reine Ringe ohne Abzweig
    for e in sorted(selected, key=lambda x: x.idx):
        if e.idx not in visited:
            chains.append(walk(e.u, e.idx, 0))
    return chains, new_breaks
