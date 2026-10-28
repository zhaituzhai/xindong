from __future__ import annotations
from pathlib import Path
from typing import Any
import yaml

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_CONFIG_PATH = ROOT / "config.yaml"

_cache: dict[str, Any] | None = None


def load_config(path: str | Path | None = None) -> dict[str, Any]:
    global _cache
    if _cache is not None and path is None:
        return _cache
    p = Path(path) if path else DEFAULT_CONFIG_PATH
    with open(p, "r", encoding="utf-8") as f:
        _cache = yaml.safe_load(f)
    return _cache


def cfg() -> dict[str, Any]:
    return load_config()


def server_cfg() -> dict[str, Any]:
    return cfg()["server"]


def storage_cfg() -> dict[str, Any]:
    return cfg()["storage"]


def ratelimit_cfg() -> dict[str, Any]:
    return cfg()["ratelimit"]


def tier_cfg() -> dict[str, Any]:
    return cfg()["tier"]


def challenge_cfg() -> dict[str, Any]:
    return cfg()["challenge"]


def webhook_cfg() -> dict[str, Any]:
    return cfg()["webhook"]


def offline_cfg() -> dict[str, Any]:
    return cfg()["offline"]