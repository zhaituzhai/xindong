from __future__ import annotations
import secrets
import time
from collections import defaultdict, deque
from fastapi import Header, HTTPException, Request, status
from app import db
from app.config import ratelimit_cfg, server_cfg

_rate_buckets: dict[tuple[str, str], deque[float]] = defaultdict(deque)


def _check_rate(key: tuple[str, str], limit: int) -> None:
    now = time.monotonic()
    bucket = _rate_buckets[key]
    while bucket and now - bucket[0] > 60.0:
        bucket.popleft()
    if len(bucket) >= limit:
        raise HTTPException(status.HTTP_429_TOO_MANY_REQUESTS, "rate limited")
    bucket.append(now)


def rate_limit(request: Request, channel: str = "default") -> None:
    cfg = ratelimit_cfg()
    limit = cfg["heartbeat_per_minute"] if channel == "heartbeat" else cfg["default_per_minute"]
    ip = request.client.host if request.client else "unknown"
    _check_rate(("ip", ip), limit)


def gen_token() -> str:
    return secrets.token_hex(32)


def gen_invite_code() -> str:
    alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"
    return "".join(secrets.choice(alphabet) for _ in range(6))


def require_token(authorization: str | None) -> str:
    if not authorization or not authorization.lower().startswith("bearer "):
        raise HTTPException(status.HTTP_401_UNAUTHORIZED, "missing token")
    token = authorization.split(" ", 1)[1].strip()
    row = db.query_one("SELECT device_id FROM users WHERE token=? AND token IS NOT NULL", (token,))
    if not row:
        raise HTTPException(status.HTTP_401_UNAUTHORIZED, "invalid token")
    return row["device_id"]


def require_admin(x_admin_token: str | None) -> None:
    expected = server_cfg().get("admin_token")
    if not expected or x_admin_token != expected:
        raise HTTPException(status.HTTP_401_UNAUTHORIZED, "admin token required")


def get_device_id_optional(authorization: str | None) -> str | None:
    if not authorization or not authorization.lower().startswith("bearer "):
        return None
    token = authorization.split(" ", 1)[1].strip()
    row = db.query_one("SELECT device_id FROM users WHERE token=?", (token,))
    return row["device_id"] if row else None