# SPDX-License-Identifier: GPL-3.0-or-later
"""Kommandozeile: osm2ets build | osm2ets lods"""
from __future__ import annotations

import argparse
import logging
import sys
from pathlib import Path

from .config import ConfigError, load_profile
from .export import report_text, write_all
from .pipeline import build, with_overrides


def _floats(text: str, n: int, what: str) -> tuple[float, ...]:
    parts = [p.strip() for p in text.split(",")]
    if len(parts) != n:
        raise argparse.ArgumentTypeError(f"{what}: {n} durch Komma getrennte Zahlen erwartet")
    try:
        return tuple(float(p) for p in parts)
    except ValueError:
        raise argparse.ArgumentTypeError(f"{what}: ungültige Zahl in {text!r}") from None


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(prog="osm2ets", description="OpenStreetMap -> ETS2-Straßennetz (Zwischenformat)")
    sub = ap.add_subparsers(dest="cmd", required=True)

    b = sub.add_parser("build", help="Zwischenformat aus einer OSM-Datei erzeugen")
    b.add_argument("--input", "-i", required=True, help="OSM-Datei (.osm.pbf, .osm, .osm.gz)")
    b.add_argument("--out", "-o", required=True, help="Ausgabeordner")
    b.add_argument("--name", default="roads", help="Dateiname ohne Endung (Standard: roads)")
    b.add_argument("--profile", "-p", default="de", help="Profilname oder Pfad zu einer YAML-Datei (Standard: de)")
    b.add_argument("--lod", "-l", default="L1", help="Detailstufe (Standard: L1)")
    b.add_argument("--bbox", type=lambda t: _floats(t, 4, "--bbox"),
                   help="Zuschnitt minLon,minLat,maxLon,maxLat (WGS84)")
    b.add_argument("--dem", help="Geländemodell (GeoTIFF/VRT, braucht rasterio)")
    b.add_argument("--scale", type=float, help="Maßstab 1:N (überschreibt Profil; Standard 1)")
    b.add_argument("--vertical-scale", type=float, help="Höhenmaßstab 1:N (Standard: wie --scale)")
    b.add_argument("--origin", type=lambda t: _floats(t, 2, "--origin"),
                   help="Ursprung lon,lat (überschreibt Profil)")
    b.add_argument("-v", "--verbose", action="store_true")

    li = sub.add_parser("lods", help="Detailstufen eines Profils anzeigen")
    li.add_argument("--profile", "-p", default="de")

    args = ap.parse_args(argv)
    logging.basicConfig(level=logging.INFO if getattr(args, "verbose", False) else logging.WARNING,
                        format="%(levelname)s %(message)s")
    try:
        profile = load_profile(args.profile)
        if args.cmd == "lods":
            for name, lod in profile.lods.items():
                print(f"{name}: {lod.description}")
            return 0
        profile = with_overrides(profile, args.scale, args.vertical_scale, args.origin)
        if not Path(args.input).exists():
            raise FileNotFoundError(f"Eingabedatei nicht gefunden: {args.input}")
        model = build(args.input, profile, args.lod, bbox=args.bbox, dem_path=args.dem)
        paths = write_all(model, Path(args.out), args.name)
    except (ConfigError, ValueError, RuntimeError, FileNotFoundError) as exc:
        print(f"Fehler: {exc}", file=sys.stderr)
        return 2
    print(report_text(model), end="")
    for kind, path in paths.items():
        print(f"{kind:8} {path}")
    return 0


if __name__ == "__main__":  # pragma: no cover
    sys.exit(main())
