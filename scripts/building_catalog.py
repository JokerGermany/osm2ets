#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
"""Gebäudekatalog für den Writer (writer/Osm2Ets.Writer/buildings.jsonc) aus den Spieldateien.

Liest die Modelldefinitionen (/def/world/model*.sii), die Geometrie der Modelle (.pmg: Bounding-Box
je Teil) und ihre Beschreibung (.pmd: Looks, Varianten, welche Teile je Variante sichtbar sind) aus
mit ``osm2ets-writer extract`` entpackten Ordnern. Die Spieldateien selbst gehören nicht ins
Repository – nur Kürzel, Namen von Variante und Looks, Art, Land und Maße landen im Katalog.

Aufruf:
    scripts/building_catalog.py <entpackt>/def/world <entpackt-pmg> [<entpackt-pmd>] \
        > writer/Osm2Ets.Writer/buildings.jsonc
(ohne dritten Ordner liegen die .pmd neben den .pmg). Modelle ohne .pmd bleiben weg.

Je Modell und Variante ein Eintrag mit der Bounding-Box der in dieser Variante sichtbaren Teile –
Varianten unterscheiden sich oft stark (Anbauten, Stockwerke). Varianten für Reihen, Ecken und Hänge
(left, right, corner, row, connect, slope …), leere und solche, die nicht bis zum Boden reichen (nur
Obergeschosse), bleiben weg, ebenso Looks für andere Länder
(fr, south) und „reference“.

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
                  r"wash|cochem|castle|bridge|tower|ok_logistic_add|conveyor|asphalt|dormer|mobileheim|beach|"
                  r"lighthouse", re.I)
# Kürzel mit Länderpräfix eines DLC (ibe_…, greece_…, balt_…)
DLC_TOKEN = re.compile(r"^(dlc|ibe|greece|gr|balt|blkw|blke|rus|poc|fr|it|sc)_")
# Varianten, die nur an andere Gebäude oder an Hänge passen
ROW_VARIANT = re.compile(r"left|right|center|corner|crnr|^row|_row|connect|^con_|^ph_|together|bridge|blank|"
                         r"slope|^empty")
# Looks für andere Gegenden
SKIP_LOOK = re.compile(r"(^|_)(fr|south|reference)(_|$)")
# Dateinamen mit US-Bundesstaat vorn (tx_cardealer1, mt_warehouse_brick_1 …: mit dem Amerikanischen Truck
# Simulator geteilt) – ohne „de“, das in ETS2 Deutschland heißt
US_FILE = re.compile(r"^(al|ak|az|ar|ca|co|ct|fl|ga|hi|id|il|in|ia|ks|ky|la|me|md|ma|mi|mn|ms|mo|mt|ne|nv|nh|"
                     r"nj|nm|ny|nc|nd|oh|ok|or|pa|ri|sc|sd|tn|tx|ut|vt|va|wa|wv|wi|wy)_")


def token_str(v):
    s = ""
    while v:
        s += CHARSET[v % 38]
        v //= 38
    return s


def read_pmg_parts(path):
    """Teile der Geometrie: [(Name, [(min, max) je Stück])]; None, wenn kein .pmg Version 0x15."""
    b = open(path, "rb").read()
    if len(b) < 112 or b[0] != 0x15 or b[1:4] != b"gmP":
        return None
    npiece, npart = struct.unpack_from("<2i", b, 4)
    off = struct.unpack_from("<10i", b, 72)       # Skelett, Teile, Lokatoren, Stücke, …
    pieces = [(struct.unpack_from("<3f", b, off[3] + 100 * i + 36), struct.unpack_from("<3f", b, off[3] + 100 * i + 48))
              for i in range(npiece)]
    parts = []
    for i in range(npart):
        name, cnt, start, _, _ = struct.unpack_from("<Q4I", b, off[1] + 24 * i)
        parts.append((token_str(name), pieces[start:start + cnt]))
    return parts


def read_pmd(path):
    """Looks, Varianten und je Variante die Sichtbarkeit der Teile (Attribut „visible“); None, wenn kein
    .pmd Version 4."""
    b = open(path, "rb").read()
    if len(b) < 64:
        return None
    (ver, _nmat, nlook, _npiece, nvar, npart, nattr, attrsize, _matsize, look_off, var_off, pa_off, av_off,
     attr_off, _mat_off, _matdata_off) = struct.unpack_from("<16I", b, 0)
    if ver != 4:
        return None
    looks = [token_str(struct.unpack_from("<Q", b, look_off + 8 * i)[0]) for i in range(nlook)]
    variants = [token_str(struct.unpack_from("<Q", b, var_off + 8 * i)[0]) for i in range(nvar)]
    attrs = [struct.unpack_from("<QiI", b, attr_off + 16 * i) for i in range(nattr)]   # Name, Typ, Offset
    visible = []
    for v in range(nvar):
        row = []
        for p in range(npart):
            frm, to = struct.unpack_from("<2I", b, pa_off + 8 * p)     # Attribute frm … to-1 gehören zum Teil
            seen = True
            for name, _typ, offset in attrs[frm:to]:
                if token_str(name) == "visible":
                    seen = struct.unpack_from("<i", b, av_off + v * attrsize + offset)[0] != 0
            row.append(seen)
        visible.append(row)
    return looks, variants, visible


def variant_boxes(parts, visible):
    """Bounding-Box der sichtbaren Teile je Variante (None, wenn nichts sichtbar ist)."""
    out = []
    for row in visible:
        boxes = [pc for (_name, pcs), seen in zip(parts, row) if seen for pc in pcs]
        if not boxes:
            out.append(None)
            continue
        out.append(([min(bx[0][k] for bx in boxes) for k in range(3)], [max(bx[1][k] for bx in boxes) for k in range(3)]))
    return out


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
    pmd_root = sys.argv[3] if len(sys.argv) > 3 else root
    seen = set()
    rows = []
    no_pmd = 0
    models = set()
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
        pmd = os.path.join(pmd_root, model.lstrip("/"))
        if not os.path.exists(pmg):
            continue
        parts = read_pmg_parts(pmg)
        if parts is None:
            continue
        desc = read_pmd(pmd) if os.path.exists(pmd) else None
        if desc is None or len(desc[2][0] if desc[2] else []) != len(parts):
            no_pmd += 1
            continue
        looks, variants, visible = desc
        looks = [lk for lk in looks if not SKIP_LOOK.search(lk)] or looks
        for var, box in zip(variants, variant_boxes(parts, visible)):
            if box is None or ROW_VARIANT.search(var):
                continue
            lo, hi = box
            size = [hi[k] - lo[k] for k in range(3)]
            if min(size[0], size[2]) < 3.0 or size[1] < 2.5 or max(size[0], size[2]) > 250:
                continue
            if lo[1] > 0.5:          # schwebt: Aufsatz für ein anderes Modell (z. B. nur Obergeschosse)
                continue
            k = kind(model, size)
            if k is None:
                continue
            # Hochhäuser sind keine Einfamilienhäuser, Getreidehochhäuser keine Hallen
            if k == "house" and size[1] > 20:
                k = "apartments"
            elif k == "industrial" and size[1] > 25:
                k = "silo"
            # Modelle aus base_share und amerikanische (mit dem Amerikanischen Truck Simulator geteilt: ne_,
            # ok_, tx_ …) nur für Hallen, Silos und Tanks
            american = src == "base_share" or re.match(r"^[a-z]{2,3}_", tok) or US_FILE.match(os.path.basename(model))
            if c == "" and american and k not in ("industrial", "silo"):
                continue
            rows.append({"token": tok, "variant": var, "looks": looks, "kind": k, "country": c or "-",
                         "min": [round(v, 2) for v in lo], "max": [round(v, 2) for v in hi], "model": model})
            models.add(tok)
    rows.sort(key=lambda r: (r["kind"], r["country"] != "de", r["token"], r["variant"]))
    out = ["// SPDX-License-Identifier: GPL-2.0-or-later",
           "// Gebäudekatalog – erzeugt von scripts/building_catalog.py aus den Spieldateien (ETS2 1.61):",
           "// Kürzel aus /def/world/model*.sii; je Modell und Variante die Bounding-Box der sichtbaren Teile",
           "// (.pmg/.pmd, Modellkoordinaten, m); Looks aus der .pmd. Nur Grundspiel; deutsche Modelle bevorzugt",
           "// der Writer (country \"de\").",
           "{",
           "  // lokale Achse, die zur Straße zeigt (Vorderseite): \"-z\" oder \"+z\" – ungeprüft",
           "  \"front\": \"-z\",",
           "  \"models\": ["]
    for i, r in enumerate(rows):
        line = ("    { \"token\": %s, \"variant\": %s, \"looks\": %s, \"kind\": %s, \"country\": %s, \"min\": %s, "
                "\"max\": %s, \"model\": %s }"
                % (json.dumps(r["token"]), json.dumps(r["variant"]), json.dumps(r["looks"]), json.dumps(r["kind"]),
                   json.dumps(r["country"]), json.dumps(r["min"]), json.dumps(r["max"]), json.dumps(r["model"])))
        out.append(line + ("," if i < len(rows) - 1 else ""))
    out += ["  ]", "}"]
    print("\n".join(out))
    counts = {}
    for r in rows:
        counts.setdefault(r["kind"], [0, 0])
        counts[r["kind"]][0] += 1
        counts[r["kind"]][1] += r["country"] == "de"
    print(f"{len(models)} Modelle, {len(rows)} Varianten ({no_pmd} Modelle ohne passende .pmd weggelassen): "
          + "; ".join(f"{k}: {v[0]} ({v[1]} de)" for k, v in sorted(counts.items())), file=sys.stderr)


if __name__ == "__main__":
    main()
