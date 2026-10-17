# SPDX-License-Identifier: GPL-3.0-or-later
"""OSM-Daten lesen (pyosmium).

Zwei Durchläufe über die Datei:
  1. Wege mit highway-Tag der benötigten Klassen (nur Knoten-IDs, keine Koordinaten).
  2. Koordinaten nur für die Knoten, die nach der Auswahl wirklich gebraucht werden.

So bleibt der Speicherbedarf klein, auch wenn die Eingabedatei groß ist.
"""
from __future__ import annotations

import logging
from array import array
from dataclasses import dataclass
from typing import Iterable

import osmium

log = logging.getLogger(__name__)

# Tags, die später gebraucht werden. Alles andere wird verworfen.
KEPT_TAGS = (
    "highway", "ref", "name", "oneway", "junction", "lanes", "lanes:forward",
    "lanes:backward", "maxspeed", "bridge", "tunnel", "layer", "service",
)

_ALLOWED_VEHICLE_VALUES = frozenset({"yes", "designated", "permissive", "destination"})


@dataclass
class Way:
    id: int
    nodes: array  # array('q') mit OSM-Knoten-IDs
    tags: dict


def _excluded(tags, exclude_access: frozenset, exclude_service: frozenset) -> bool:
    if tags.get("area") == "yes":
        return True
    if tags.get("access") in exclude_access:
        if not any(tags.get(k) in _ALLOWED_VEHICLE_VALUES
                   for k in ("motor_vehicle", "motorcar", "vehicle")):
            return True
    if tags.get("highway") == "service" and tags.get("service") in exclude_service:
        return True
    return False


def read_ways(path: str, classes: frozenset, exclude_access: frozenset = frozenset(),
              exclude_service: frozenset = frozenset()) -> list[Way]:
    """Liest alle Wege, deren highway-Wert in `classes` liegt."""
    ways: list[Way] = []
    skipped = 0
    fp = osmium.FileProcessor(path, osmium.osm.WAY).with_filter(osmium.filter.KeyFilter("highway"))
    for w in fp:
        hw = w.tags.get("highway")
        if hw not in classes:
            continue
        if _excluded(w.tags, exclude_access, exclude_service):
            skipped += 1
            continue
        refs = array("q")
        last = None
        for n in w.nodes:
            if n.ref != last:  # doppelte aufeinanderfolgende Knoten (Datenfehler) entfernen
                refs.append(n.ref)
                last = n.ref
        if len(refs) < 2:
            continue
        tags = {k: w.tags.get(k) for k in KEPT_TAGS if k in w.tags}
        ways.append(Way(id=w.id, nodes=refs, tags=tags))
    log.info("%d Wege gelesen, %d wegen Zugang/Art ausgeschlossen", len(ways), skipped)
    return ways


def read_node_locations(path: str, node_ids: Iterable[int]) -> dict[int, tuple[float, float]]:
    """Liefert {Knoten-ID: (lon, lat)} für die angefragten Knoten.

    Fehlende Knoten (z. B. außerhalb eines zugeschnittenen Ausschnitts) fehlen
    einfach im Ergebnis.
    """
    ids = set(node_ids)
    if not ids:
        return {}
    out: dict[int, tuple[float, float]] = {}
    fp = osmium.FileProcessor(path, osmium.osm.NODE).with_filter(osmium.filter.IdFilter(ids))
    for n in fp:
        if n.location.valid():
            out[n.id] = (n.location.lon, n.location.lat)
    missing = len(ids) - len(out)
    if missing:
        log.info("%d von %d Knoten ohne Koordinate (außerhalb des Ausschnitts?)", missing, len(ids))
    return out
