from __future__ import annotations
import json
from datetime import timedelta
from app import db
from app.timeutil import now, today_str, parse_dt
from app.config import storage_cfg


def store_report(device_id: str, payload: dict, event_type: str = "report") -> None:
    reported_at = payload.get("reported_at") or now().isoformat()
    config = payload.get("config") or {}
    ledger = payload.get("ledger") or {}
    records = list(ledger.get("Records") or [])
    ts = now().isoformat()

    with db.tx() as conn:
        conn.execute(
            """INSERT INTO users(device_id, first_seen_at, last_active, online, created_at)
               VALUES(?, ?, ?, 1, ?)
               ON CONFLICT(device_id) DO UPDATE SET last_active=excluded.last_active, online=1""",
            (device_id, ts, ts, ts),
        )
        if payload.get("client_version"):
            conn.execute("UPDATE users SET client_version=? WHERE device_id=?",
                         (payload.get("client_version"), device_id))
        if payload.get("client_tz_offset") is not None:
            conn.execute("UPDATE users SET client_tz_offset=? WHERE device_id=?",
                         (payload.get("client_tz_offset"), device_id))

        if config:
            conn.execute(
                """INSERT INTO configs(device_id, payload, updated_at)
                   VALUES(?, ?, ?)
                   ON CONFLICT(device_id) DO UPDATE SET payload=excluded.payload, updated_at=excluded.updated_at""",
                (device_id, json.dumps(config, ensure_ascii=False), ts),
            )

        for rec in records:
            d = str(rec.get("Date") or "")
            if not d:
                continue
            conn.execute(
                """INSERT INTO ledger_records(device_id, date, payload, updated_at)
                   VALUES(?, ?, ?, ?)
                   ON CONFLICT(device_id, date) DO UPDATE SET payload=excluded.payload, updated_at=excluded.updated_at""",
                (device_id, d, json.dumps(rec, ensure_ascii=False), ts),
            )

        conn.execute(
            "INSERT INTO snapshots(device_id, reported_at, event_type, payload) VALUES(?, ?, ?, ?)",
            (device_id, reported_at, event_type, json.dumps(payload, ensure_ascii=False)),
        )


def touch_active(device_id: str) -> None:
    ts = now().isoformat()
    db.execute("UPDATE users SET last_active=?, online=1 WHERE device_id=?", (ts, device_id))


def cleanup_snapshots() -> int:
    full_days = storage_cfg()["snapshot_full_days"]
    cutoff = (now() - timedelta(days=full_days)).date()
    conn = db.get_conn()
    rows = conn.execute(
        """SELECT device_id, date(reported_at) AS d, MAX(id) AS max_id
           FROM snapshots WHERE date(reported_at) < ?
           GROUP BY device_id, date(reported_at)""",
        (cutoff.isoformat(),),
    ).fetchall()
    keep_ids = {r["max_id"] for r in rows}
    if not keep_ids:
        cur = conn.execute("DELETE FROM snapshots WHERE date(reported_at) < ?", (cutoff.isoformat(),))
    else:
        placeholders = ",".join("?" * len(keep_ids))
        cur = conn.execute(
            f"DELETE FROM snapshots WHERE date(reported_at) < ? AND id NOT IN ({placeholders})",
            (cutoff.isoformat(), *keep_ids),
        )
    return cur.rowcount or 0


def list_snapshots(device_id: str, limit: int = 50) -> list[dict]:
    rows = db.query(
        "SELECT id, reported_at, event_type FROM snapshots WHERE device_id=? ORDER BY reported_at DESC LIMIT ?",
        (device_id, limit),
    )
    return [dict(r) for r in rows]