# SPDX-License-Identifier: GPL-3.0-or-later
"""Länderprofile laden und validieren."""
from __future__ import annotations

import re
from dataclasses import dataclass, field
from importlib import resources
from pathlib import Path
from typing import Optional

import yaml


class ConfigError(ValueError):
    pass


@dataclass(frozen=True)
class CoreRule:
    highway: frozenset
    ref_regex: Optional[re.Pattern] = None

    def matches(self, tags: dict) -> bool:
        if tags.get("highway") not in self.highway:
            return False
        if self.ref_regex is None:
            return True
        return bool(self.ref_regex.search(tags.get("ref", "")))


@dataclass(frozen=True)
class LodConfig:
    name: str
    description: str
    core: tuple
    links: frozenset
    connector_classes: frozenset
    connector_depth: int
    # None = Vorgabe des Profils (network.exclude_service / network.exclude_access)
    exclude_service: Optional[frozenset] = None
    exclude_access: Optional[frozenset] = None

    def is_core(self, tags: dict) -> bool:
        return any(rule.matches(tags) for rule in self.core)

    @property
    def core_classes(self) -> frozenset:
        out = set()
        for rule in self.core:
            out |= rule.highway
        return frozenset(out)

    @property
    def all_classes(self) -> frozenset:
        """Alle Straßenklassen, die für diese Stufe gelesen werden müssen.

        Dazu gehören auch die Klassen der Anschlussstraßen: Sie bestimmen,
        wo die "nächste Kreuzung" liegt.
        """
        return self.core_classes | self.links | self.connector_classes


@dataclass
class Profile:
    name: str
    description: str
    crs: str
    origin_lonlat: tuple
    scale_h: float
    scale_v: float
    densify_m: float
    simplify_tolerance_game_m: float
    vertical_tolerance_game_m: float
    min_segment_game_m: float
    max_segment_game_m: float
    smoothing_window_m: float
    bridge_clearance_m: float
    max_ramp_grade: float
    terrain_max_grade: float
    max_bridge_levels: int
    min_bridge_length_m: float
    implied_oneway: frozenset
    exclude_access: frozenset
    exclude_service: frozenset
    lods: dict = field(default_factory=dict)
    industry: dict = field(default_factory=dict)
    extras: dict = field(default_factory=dict)

    def lod(self, name: str) -> LodConfig:
        try:
            return self.lods[name]
        except KeyError:
            raise ConfigError(
                f"Detailstufe {name!r} nicht im Profil {self.name!r}; "
                f"vorhanden: {', '.join(sorted(self.lods))}") from None


def _req(d: dict, key: str, ctx: str):
    if key not in d:
        raise ConfigError(f"{ctx}: Pflichtfeld {key!r} fehlt")
    return d[key]


def _float(d: dict, key: str, ctx: str, default=None, positive=True) -> float:
    value = d.get(key, default)
    if value is None:
        raise ConfigError(f"{ctx}: Pflichtfeld {key!r} fehlt")
    try:
        value = float(value)
    except (TypeError, ValueError):
        raise ConfigError(f"{ctx}.{key}: Zahl erwartet, bekommen {value!r}") from None
    if positive and value <= 0:
        raise ConfigError(f"{ctx}.{key}: muss > 0 sein, ist {value}")
    return value


def _str_set(value, ctx: str) -> frozenset:
    if value is None:
        return frozenset()
    if not isinstance(value, (list, tuple)):
        raise ConfigError(f"{ctx}: Liste erwartet, bekommen {value!r}")
    out = []
    for v in value:
        if isinstance(v, bool):
            # YAML 1.1 macht aus unquotiertem no/yes ein bool – das ist fast
            # immer ein Tippfehler im Profil.
            raise ConfigError(f"{ctx}: {v!r} ist ein Wahrheitswert; Wert in Anführungszeichen setzen")
        out.append(str(v))
    return frozenset(out)


def _parse_lod(name: str, d: dict) -> LodConfig:
    ctx = f"lods.{name}"
    core_raw = _req(d, "core", ctx)
    if not isinstance(core_raw, list) or not core_raw:
        raise ConfigError(f"{ctx}.core: nicht-leere Liste erwartet")
    rules = []
    for i, r in enumerate(core_raw):
        rctx = f"{ctx}.core[{i}]"
        hw = _str_set(_req(r, "highway", rctx), rctx + ".highway")
        rx = r.get("ref_regex")
        try:
            pattern = re.compile(rx) if rx else None
        except re.error as exc:
            raise ConfigError(f"{rctx}.ref_regex: ungültiger regulärer Ausdruck: {exc}") from None
        rules.append(CoreRule(highway=hw, ref_regex=pattern))
    conn = d.get("connectors") or {}
    depth = conn.get("depth", 0)
    if not isinstance(depth, int) or depth < 0:
        raise ConfigError(f"{ctx}.connectors.depth: ganze Zahl >= 0 erwartet")
    return LodConfig(
        name=name,
        description=str(d.get("description", "")).strip(),
        core=tuple(rules),
        links=_str_set(d.get("links"), ctx + ".links"),
        connector_classes=_str_set(conn.get("highway"), ctx + ".connectors.highway"),
        connector_depth=depth,
        exclude_service=(_str_set(d["exclude_service"], ctx + ".exclude_service")
                         if "exclude_service" in d else None),
        exclude_access=(_str_set(d["exclude_access"], ctx + ".exclude_access")
                        if "exclude_access" in d else None),
    )


def load_profile(name_or_path: str) -> Profile:
    """Lädt ein Profil per Name (eingebaut, z. B. "de") oder Dateipfad."""
    path = Path(name_or_path)
    if path.suffix in (".yaml", ".yml") or path.exists():
        text = path.read_text(encoding="utf-8")
        source = str(path)
    else:
        try:
            text = resources.files("osm2ets").joinpath("profiles", f"{name_or_path}.yaml").read_text(encoding="utf-8")
        except FileNotFoundError:
            raise ConfigError(f"Profil {name_or_path!r} nicht gefunden") from None
        source = f"<eingebaut:{name_or_path}>"
    data = yaml.safe_load(text)
    if not isinstance(data, dict):
        raise ConfigError(f"{source}: Profil muss ein YAML-Objekt sein")
    return parse_profile(data, source)


def parse_profile(data: dict, source: str = "<profil>") -> Profile:
    proj = _req(data, "projection", source)
    origin = _req(proj, "origin_lonlat", "projection")
    if not (isinstance(origin, (list, tuple)) and len(origin) == 2):
        raise ConfigError("projection.origin_lonlat: [Längengrad, Breitengrad] erwartet")
    lon0, lat0 = float(origin[0]), float(origin[1])
    if not (-180 <= lon0 <= 180 and -90 <= lat0 <= 90):
        raise ConfigError("projection.origin_lonlat: außerhalb des gültigen Bereichs")

    scale = data.get("scale") or {}
    scale_h = _float(scale, "horizontal", "scale", default=1.0)
    scale_v = scale.get("vertical")
    scale_v = scale_h if scale_v is None else _float(scale, "vertical", "scale")

    geo = _req(data, "geometry", source)
    ele = data.get("elevation") or {}
    net = data.get("network") or {}

    lods_raw = _req(data, "lods", source)
    if not isinstance(lods_raw, dict) or not lods_raw:
        raise ConfigError("lods: mindestens eine Detailstufe erwartet")
    lods = {str(k): _parse_lod(str(k), v or {}) for k, v in lods_raw.items()}

    ext = data.get("extras") or {}
    if not isinstance(ext, dict):
        raise ConfigError("extras: YAML-Objekt erwartet")
    drivable_default = ["motorway", "trunk", "primary", "secondary", "tertiary", "motorway_link",
                        "trunk_link", "primary_link", "secondary_link", "tertiary_link", "unclassified",
                        "residential", "living_street", "service", "road"]
    extras = {
        "same_level_max_m": _float(ext, "same_level_max_m", "extras", default=500.0, positive=False),
        "same_level_ratio": _float(ext, "same_level_ratio", "extras", default=1.5),
        "rest_loop_max_m": _float(ext, "rest_loop_max_m", "extras", default=1500.0, positive=False),
        "escape": bool(ext.get("escape", True)),
        "end_extension_m": _float(ext, "end_extension_m", "extras", default=50.0, positive=False),
        "via_highway": _str_set(ext.get("via_highway", drivable_default), "extras.via_highway"),
    }

    profile = Profile(
        name=str(data.get("name", "profil")),
        description=str(data.get("description", "")),
        crs=str(_req(proj, "crs", "projection")),
        origin_lonlat=(lon0, lat0),
        scale_h=scale_h,
        scale_v=scale_v,
        densify_m=_float(geo, "densify_m", "geometry"),
        simplify_tolerance_game_m=_float(geo, "simplify_tolerance_game_m", "geometry", positive=False),
        vertical_tolerance_game_m=_float(geo, "vertical_tolerance_game_m", "geometry", positive=False),
        min_segment_game_m=_float(geo, "min_segment_game_m", "geometry"),
        max_segment_game_m=_float(geo, "max_segment_game_m", "geometry"),
        smoothing_window_m=_float(ele, "smoothing_window_m", "elevation", default=120.0),
        bridge_clearance_m=_float(ele, "bridge_clearance_m", "elevation", default=6.5),
        max_ramp_grade=_float(ele, "max_ramp_grade", "elevation", default=0.05),
        terrain_max_grade=_float(ele, "terrain_max_grade", "elevation", default=0.06),
        max_bridge_levels=int(_float(ele, "max_bridge_levels", "elevation", default=4)),
        min_bridge_length_m=_float(ele, "min_bridge_length_m", "elevation", default=8.0, positive=False),
        implied_oneway=_str_set(net.get("implied_oneway", ["motorway", "motorway_link"]), "network.implied_oneway"),
        exclude_access=_str_set(net.get("exclude_access", []), "network.exclude_access"),
        exclude_service=_str_set(net.get("exclude_service", []), "network.exclude_service"),
        lods=lods,
        extras=extras,
        industry={
            "enabled": bool((data.get("industry") or {}).get("enabled", True)),
            "highway": _str_set((data.get("industry") or {}).get("highway", drivable_default), "industry.highway"),
            "max_distance_m": float((data.get("industry") or {}).get("max_distance_m", 300.0)),
            "inside_m": float((data.get("industry") or {}).get("inside_m", 150.0)),
        },
    )
    validate_geometry(profile)
    return profile


# Grenzen aus dem Spiel bzw. TruckLib (Road.MinLength bei HighPoly, Road.MaxLength).
GAME_MIN_SEGMENT_M = 1.25
GAME_MAX_SEGMENT_M = 1000.0


def validate_geometry(p: Profile) -> None:
    """Prüft die Segmentgrenzen. Sie gelten in SPIELmetern (nach Maßstab)."""
    if p.min_segment_game_m < GAME_MIN_SEGMENT_M:
        raise ConfigError(
            f"geometry.min_segment_game_m={p.min_segment_game_m} liegt unter dem Spielminimum "
            f"von {GAME_MIN_SEGMENT_M} m")
    if p.max_segment_game_m > GAME_MAX_SEGMENT_M:
        raise ConfigError(
            f"geometry.max_segment_game_m={p.max_segment_game_m} liegt über dem Spielmaximum "
            f"von {GAME_MAX_SEGMENT_M:.0f} m")
    if p.max_segment_game_m <= 2 * p.min_segment_game_m:
        raise ConfigError("geometry.max_segment_game_m muss größer als 2 × min_segment_game_m sein")
    if p.densify_m >= p.max_segment_game_m * p.scale_h:
        raise ConfigError("geometry.densify_m muss kleiner als die maximale Segmentlänge (in Echtmetern) sein")
