from __future__ import annotations
import hashlib
import hmac
import json
import time
import httpx
from app import db
from app.config import webhook_cfg
from app.timeutil import now


def register(device_id: str | None, url: str, events: list[str], secret: str | None) -> int:
    ts = now().isoformat()
    cur = db.execute(
        "INSERT INTO webhooks(device_id, url, events, secret, created_at) VALUES(?, ?, ?, ?, ?)",
        (device_id, url, json.dumps(events, ensure_ascii=False), secret, ts),
    )
    return cur.lastrowid or 0


def _sign(secret: str | None, body: bytes) -> str:
    if not secret:
        return ""
    return hmac.new(secret.encode(), body, hashlib.sha256).hexdigest()


def dispatch(event: str, payload: dict) -> None:
    rows = db.query("SELECT * FROM webhooks")
    cfg = webhook_cfg()
    max_retries = cfg["max_retries"]
    backoff = cfg["backoff_base"]
    ts = now().isoformat()
    for wh in rows:
        events = json.loads(wh["events"])
        if event not in events:
            continue
        body = json.dumps({"event": event, "payload": payload, "ts": ts}, ensure_ascii=False).encode()
        sig = _sign(wh["secret"], body)
        headers = {"Content-Type": "application/json"}
        if sig:
            headers["X-Signature"] = sig
        status_code = None
        for attempt in range(1, max_retries + 1):
            try:
                resp = httpx.post(wh["url"], content=body, headers=headers, timeout=10.0)
                status_code = resp.status_code
                if 200 <= status_code < 300:
                    break
            except Exception:
                status_code = None
            time.sleep(backoff ** attempt)
        db.execute(
            "INSERT INTO webhook_logs(webhook_id, event, payload, status, attempts, created_at) VALUES(?, ?, ?, ?, ?, ?)",
            (wh["id"], event, body.decode(), status_code, attempt, ts),
        )