# SPDX-License-Identifier: GPL-3.0-or-later
"""Eisenbahn: Gleise nahe der Strecke.

Wie die Sichtstücke (``extras.visible_roads``) kommen nur Abschnitte in die Karte, die man von
den Straßen der Stufe aus sieht: höchstens ``near_m`` daneben oder im Umkreis ``crossing_m`` einer
höhenfreien Kreuzung (z. B. unter einer Autobahnbrücke hindurch). Maßgeblich sind nur Kern und
Rampen der Stufe, nicht Zufahrten oder Sichtstücke. Lücken bis ``gap_m`` werden geschlossen,
freistehende Stücke unter ``min_m`` entfallen.

Gleise sind ein eigenes Netz ohne Verbindung zu den Straßen. Wo ein Gleis eine Straße der Karte
ebenerdig kreuzt (Bahnübergang, gemeinsamer OSM-Knoten), wird es beiderseits
``level_crossing_gap_m`` unterbrochen – Bahnübergangs-Prefabs setzt osm2ets noch nicht.

Intern laufen Gleise als Pseudo-Straßenklasse ``railway`` (osmread.RAIL_CLASS), mit der Gleiszahl
als ``lanes``. Alle Abstände in Echtmetern.
"""
from __future__ import annotations

import logging

from .extras import local_xy, visible_roads
from .network import ROLE_CORE, ROLE_LINK, ROLE_RAIL, Network
from .osmread import RAIL_CLASS, read_node_locations, read_railways

log = logging.getLogger(__name__)

RAIL_ID_START = -2_100_000_000   # berechnete Endknoten (Sichtstücke: ab -2_000_000_000)


def rail_sections(input_path: str, profile, selected: list, ways=None) -> tuple[list, dict]:
    """Gleiskanten nahe den Straßen der Stufe (Rolle ``rail``) und Kennzahlen.

    ``selected``: Kanten der Karte (alle Rollen); Kern und Rampen sind die Bezugsstraßen, alle
    zusammen bestimmen die Bahnübergänge. ``ways``: Gleise (Vorgabe: aus ``input_path`` lesen).
    Rückgabe: (Kanten, {"ways", "crossings", "near_ways", "edges", "partial", "dropped",
    "level_crossings", "locations"}).
    """
    cfg = profile.railway or {}
    stats = {"ways": 0, "crossings": 0, "near_ways": 0, "edges": 0, "partial": 0, "dropped": 0,
             "level_crossings": 0, "locations": {}}
    if not cfg.get("enabled"):
        return [], stats
    if ways is None:
        ways = read_railways(input_path, cfg["kinds"], cfg["exclude_service"])
    stats["ways"] = len(ways)
    anchors = [e for e in selected if e.role in (ROLE_CORE, ROLE_LINK)]
    if not ways or not anchors:
        return [], stats
    net = Network(ways, profile)
    rail_nodes = {n for w in ways for n in w.nodes}
    road_nodes = {n for e in selected for n in e.node_ids()}
    cut_nodes = rail_nodes & road_nodes
    need = {n for e in anchors for n in e.node_ids() if n > 0} | rail_nodes
    loc = read_node_locations(input_path, need)
    scale = profile.scale_h      # Spielmeter → Echtmeter
    st = visible_roads(net, local_xy(loc), loc, frozenset({RAIL_CLASS}),
                       cross_m=float(cfg["crossing_m"]) * scale, near_m=float(cfg["near_m"]) * scale,
                       extra_edges=anchors, gap_m=float(cfg["gap_m"]) * scale,
                       min_m=float(cfg["min_m"]) * scale, keep_off_m=0.0, id_start=RAIL_ID_START,
                       role=ROLE_RAIL, cut_nodes=cut_nodes,
                       cut_m=float(cfg["level_crossing_gap_m"]) * scale, label="Gleisabschnitte")
    edges = [e for e in net.edges if e.role == ROLE_RAIL] + st["partial"]
    stats.update(crossings=st["crossings"], near_ways=st["near_ways"], edges=st["edges"],
                 partial=len(st["partial"]), dropped=st["dropped"], level_crossings=st["cut_points"],
                 locations=st["locations"])
    log.info("Gleise: %d Wege gelesen, %d höhenfreie Kreuzungen, %d ganze Kanten, %d angeschnitten, "
             "%d Bahnübergänge unterbrochen", len(ways), stats["crossings"], stats["edges"],
             stats["partial"], stats["level_crossings"])
    return edges, stats
