#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
"""Gebäudekatalog für den Writer (writer/Osm2Ets.Writer/buildings.jsonc) aus den Spieldateien.

Liest die Modelldefinitionen (/def/world/model*.sii) und die Maße der Modelle (Bounding-Box aus
den .pmg-Dateien) aus einem mit ``osm2ets-writer extract`` entpackten Ordner. Die Spieldateien
selbst gehören nicht ins Repository – nur Kürzel, Art und Maße landen im Katalog.

Aufruf:
    scripts/building_catalog.py <entpackt>/def/world <entpackt> > writer/Osm2Ets.Writer/buildings.jsonc

Auswahl (nur Grundspiel, deutsche Modelle bevorzugt):
* Land aus der Endung des Modellnamens (``_de``, ``_at`` …). Grundspiel-Länder: de, at, ch,
  be/lux, nl, uk, cz; dazu Modelle ohne Länderendung aus den Grundspiel-Definitionen (auch die mit
  Amerika-Kürzeln oder -Dateinamen (tx_…) – nur für Hallen, Silos, Tanks). Kürzel mit DLC-Präfix (dlc_…)
  und Länder aus DLCs (fr, it, balt, ibe, gr, blkw, blke, rus, poc, sc …) bleiben weg.
* Art aus Ordner und Name (Wohnhaus, Wohnblock, Garage, Schuppen, Halle, Büro, Laden, Hof, Kirche,
  Silo). Sprites, Ruinen, Baustellen, Schilder, Wahrzeichen, Eckhäuser usw. bleiben weg.
"""
import glob
import json
import os
import re
import struct
import sys

CHARSET = "\0" + "0123456789abcdefghijklmnopqrstuvwxyz_"
BASE_COUNTRIES = {"de", "at", "ch", "be", "lux", "nl", "uk", "cz"}
DLC_COUNTRIES = {"fr", "it", "balt", "ibe", "gr", "blkw", "blke", "rus", "poc", "sc", "pl", "hu", "sk",
                 "ro", "bg", "tr", "no", "se", "dk", "fi", "ee", "lv", "lt", "ru", "es", "pt", "al", "ba",
                 "hr", "me", "mk", "rs", "si", "is"}
BASE_SOURCES = {"core", "base_share", "stable", "mod_germany4_reskin", "mod_germany5_reskin",
                "mod_benelux_reskin", "mod_switzerland_reskin"}
SKIP = re.compile(r"sprite|_far(?![a-z])|lod|panorama|sign|ruin|construction|bus_stop|parking|landmark|"
                  r"props|airport|dock|crane|ship|corner|air_cond|chimney|cooling_tower|tobogan|aquapark|"
                  r"brand|logo|billboard|fence|wall_|gate_|_gate|train_garage|warehouse_garage|ramp|"
                  r"wash|cochem|castle|bridge|tower|ok_logistic_add|conveyor|asphalt|dormer|mobileheim|beach", re.I)
# Kürzel mit Länderpräfix eines DLC (ibe_…, greece_…, balt_…)
DLC_TOKEN = re.compile(r"^(dlc|ibe|greece|gr|balt|blkw|blke|rus|poc|fr|it|sc)_")
# Dateinamen mit US-Bundesstaat vorn (tx_cardealer1, mt_warehouse_brick_1 …: mit dem Amerikanischen Truck
# Simulator geteilt) – ohne „de“, das in ETS2 Deutschland heißt
US_FILE = re.compile(r"^(al|ak|az|ar|ca|co|ct|fl|ga|hi|id|il|in|ia|ks|ky|la|me|md|ma|mi|mn|ms|mo|mt|ne|nv|nh|"
                     r"nj|nm|ny|nc|nd|oh|ok|or|pa|ri|sc|sd|tn|tx|ut|vt|va|wa|wv|wi|wy)_")


def read_bbox(path):
    with open(path, "rb") as f:
        b = f.read(72)
    if len(b) < 72 or b[0] != 0x15 or b[1:4] != b"gmP":
        return None
    return struct.unpack_from("<3f", b, 48), struct.unpack_from("<3f", b, 60)


def defs(defdir):
    for f in sorted(glob.glob(os.path.join(defdir, "model*.sii"))):
        name = os.path.basename(f)
        src = "core" if name == "model.sii" else name[len("model."):-len(".sii")]
        txt = open(f, encoding="utf-8", errors="replace").read()
        for m in re.finditer(r"model_def\s*:\s*model\.([a-z0-9_]+)\s*\{(.*?)\n\}", txt, re.S):
            d = re.search(r'model_desc:\s*"([^"]+)"', m.group(2))
            if d:
                yield m.group(1), d.group(1), src


def country(model):
    base = os.path.basename(model)[:-4]
    m = re.search(r"_([a-z]{2,4})(?:_\d+)?$", base)
    c = m.group(1) if m else ""
    if c == "lux" or base.endswith("_be_lux"):
        return "lux"
    if c in BASE_COUNTRIES or c in DLC_COUNTRIES:
        return c
    for part in model.split("/"):
        if part in BASE_COUNTRIES or part in DLC_COUNTRIES:
            return part
    return ""


def kind(model, size):
    p = model.lower()
    n = os.path.basename(p)
    if "/church/" in p:
        return "church"
    if re.search(r"silo|tank|hopper", n):
        return "silo"
    if "garage" in n:
        return "garage"
    if re.search(r"shed|cottage|cabin|wooden_barn|gatehouse|kiosk|hut_", n):
        return "shed"
    if re.search(r"barn|cowhouse|cattle|stable_|farm_building|farmhouse", n) or "/farm/" in p:
        return "farm"
    if re.search(r"flats|apartment", p):
        return "apartments"
    if re.search(r"supermarket|store|dealer|mall|shop_", n) and "/shop/" in p:
        return "retail"
    if re.search(r"/administration/|office|fire_?station|/hotel/", p):
        return "commercial"
    if re.search(r"/warehouse/|warehouse|factory_hall|/logistic/|hall|depot|quonset", p):
        return "industrial"
    if re.search(r"/village_close/|/village_mid/|family_house|house", p):
        return "house"
    return None


def main():
    defdir, root = sys.argv[1:3]
    seen = set()
    rows = []
    for tok, model, src in defs(defdir):
        if (tok, model) in seen:
            continue
        seen.add((tok, model))
        if DLC_TOKEN.match(tok) or SKIP.search(model) or "/village_far/" in model:
            continue
        c = country(model)
        if c in DLC_COUNTRIES or (c == "" and src not in BASE_SOURCES) or src.startswith("dlc_"):
            continue
        pmg = os.path.join(root, model.lstrip("/")[:-4] + ".pmg")
        if not os.path.exists(pmg):
            continue
        bb = read_bbox(pmg)
        if bb is None:
            continue
        lo, hi = bb
        size = [hi[k] - lo[k] for k in range(3)]
        if min(size[0], size[2]) < 3.0 or size[1] < 2.5 or max(size[0], size[2]) > 250:
            continue
        k = kind(model, size)
        if k is None:
            continue
        # Hochhäuser sind keine Einfamilienhäuser, Getreidehochhäuser keine Hallen
        if k == "house" and size[1] > 20:
            k = "apartments"
        elif k == "industrial" and size[1] > 25:
            k = "silo"
        # Modelle aus base_share und amerikanische (mit dem Amerikanischen Truck Simulator geteilt: ne_, ok_,
        # tx_ …) nur für Hallen, Silos und Tanks
        american = src == "base_share" or re.match(r"^[a-z]{2,3}_", tok) or US_FILE.match(os.path.basename(model))
        if c == "" and american and k not in ("industrial", "silo"):
            continue
        rows.append({"token": tok, "kind": k, "country": c or "-",
                     "min": [round(v, 2) for v in lo], "max": [round(v, 2) for v in hi],
                     "model": model})
    rows.sort(key=lambda r: (r["kind"], r["country"] != "de", r["token"]))
    out = ["// SPDX-License-Identifier: GPL-2.0-or-later",
           "// Gebäudekatalog – erzeugt von scripts/building_catalog.py aus den Spieldateien (ETS2 1.61):",
           "// Kürzel aus /def/world/model*.sii, Maße = Bounding-Box der .pmg (Modellkoordinaten, m).",
           "// Nur Grundspiel; deutsche Modelle bevorzugt der Writer (country \"de\").",
           "// look/variant: \"default\", solange die .pmd nicht ausgewertet sind (ungeprüft).",
           "{",
           "  // lokale Achse, die zur Straße zeigt (Vorderseite): \"-z\" oder \"+z\" – ungeprüft",
           "  \"front\": \"-z\",",
           "  \"models\": ["]
    for i, r in enumerate(rows):
        line = ("    { \"token\": %s, \"kind\": %s, \"country\": %s, \"min\": %s, \"max\": %s, \"model\": %s }"
                % (json.dumps(r["token"]), json.dumps(r["kind"]), json.dumps(r["country"]),
                   json.dumps(r["min"]), json.dumps(r["max"]), json.dumps(r["model"])))
        out.append(line + ("," if i < len(rows) - 1 else ""))
    out += ["  ]", "}"]
    print("\n".join(out))
    counts = {}
    for r in rows:
        counts.setdefault(r["kind"], [0, 0])
        counts[r["kind"]][0] += 1
        counts[r["kind"]][1] += r["country"] == "de"
    print("; ".join(f"{k}: {v[0]} ({v[1]} de)" for k, v in sorted(counts.items())), file=sys.stderr)


main()
