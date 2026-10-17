#!/bin/sh
# SPDX-License-Identifier: GPL-3.0-or-later
#
# build_map.sh – OSM-Datei -> ETS2-Karte in einem Schritt (osm2ets + osm2ets-writer)
#
# Aufruf:
#   scripts/build_map.sh <eingabe.osm.pbf> <kartenname> [weitere osm2ets-build-Optionen ...]
#
# Beispiel (kleiner Testausschnitt, Detailstufe L1):
#   scripts/build_map.sh bayern-latest.osm.pbf osm_test --bbox 11.48,50.02,11.62,50.06 --lod L1
#
# Umgebungsvariablen:
#   ETS2_MAP_DIR  Zielordner für <kartenname>.mbd und den Sektorordner.
#                 Standard: "$HOME/.local/share/Euro Truck Simulator 2/mod/user_map/map"
#                 (Linux, native Version; unter Windows: Dokumente\Euro Truck Simulator 2\mod\user_map\map)
#   MAPPING       Zuordnungsdatei (Standard: writer/Osm2Ets.Writer/mapping.jsonc)
#   WORK_DIR      Ordner für Zwischendateien (Standard: ./build/<kartenname>)
#   WRITER        fertig gebautes osm2ets-writer (sonst: dotnet run)
#   FORCE=1       vorhandene Karte gleichen Namens überschreiben

set -eu

usage() {
    # Kommentarblock ab Zeile 4 bis zur ersten Nicht-Kommentarzeile ausgeben
    awk 'NR > 3 && /^#/ { sub(/^# ?/, ""); print; next } NR > 3 { exit }' "$0"
    exit 2
}

[ "$#" -ge 2 ] || usage
input=$1
name=$2
shift 2

[ -f "$input" ] || { echo "Fehler: Eingabedatei nicht gefunden: $input" >&2; exit 2; }
case $name in
    *[!a-z0-9_]* | '') echo "Fehler: Kartenname nur aus a-z, 0-9 und _ (höchstens 12 Zeichen)" >&2; exit 2 ;;
esac
[ "${#name}" -le 12 ] || { echo "Fehler: Kartenname höchstens 12 Zeichen" >&2; exit 2; }

root=$(cd "$(dirname "$0")/.." && pwd)
map_dir=${ETS2_MAP_DIR:-"$HOME/.local/share/Euro Truck Simulator 2/mod/user_map/map"}
mapping=${MAPPING:-"$root/writer/Osm2Ets.Writer/mapping.jsonc"}
work=${WORK_DIR:-"./build/$name"}

if [ -z "${ETS2_MAP_DIR:-}" ] && [ ! -d "$HOME/.local/share/Euro Truck Simulator 2" ]; then
    echo "Fehler: ETS2-Benutzerordner nicht gefunden. Bitte ETS2_MAP_DIR setzen." >&2
    exit 2
fi

command -v osm2ets >/dev/null 2>&1 || { echo "Fehler: osm2ets nicht installiert (pip install .)" >&2; exit 2; }

echo "== 1/2 Zwischenformat erzeugen"
osm2ets build -i "$input" -o "$work" --name "$name" "$@"

echo "== 2/2 ETS2-Karte schreiben nach $map_dir"
mkdir -p "$map_dir"
set -- write --input "$work/$name.ets.json" --out "$map_dir" --name "$name" --mapping "$mapping" --verify
[ "${FORCE:-0}" = 1 ] && set -- "$@" --force
if [ -n "${WRITER:-}" ]; then
    "$WRITER" "$@"
else
    command -v dotnet >/dev/null 2>&1 || { echo "Fehler: .NET-SDK (dotnet) nicht gefunden" >&2; exit 2; }
    dotnet run --project "$root/writer/Osm2Ets.Writer" -c Release -- "$@"
fi

echo
echo "Fertig. Kontrolle: $work/$name.geojson (z. B. in QGIS), Bericht: $work/$name.report.txt"
echo "Danach im Editor öffnen (Spielstart mit: -edit $name), 'Map > Recompute map', speichern, 'Run Map'."
