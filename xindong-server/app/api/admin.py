from __future__ import annotations
import json
from fastapi import APIRouter, Header, Query
from app.auth import require_admin
from app import db
from app.services import rank, tier, snapshot

router = APIRouter(prefix="/api/v1/admin", tags=["admin"])


@router.get("/overview")
def overview(x_admin_token: str | None = Header(None)):
    require_admin(x_admin_token)
    online = db.query_one("SELECT COUNT(*) AS c FROM users WHERE online=1")["c"]
    total = db.query_one("SELECT COUNT(*) AS c FROM users")["c"]
    today = db.query_one("SELECT COUNT(*) AS c FROM snapshots WHERE date(reported_at)=date('now')")["c"]
    return {"online": online, "total_users": total, "today_reports": today,
            "tier_distribution": tier.tier_distribution()}


@router.get("/users")
def users(limit: int = Query(100, le=1000), x_admin_token: str | None = Header(None)):
    require_admin(x_admin_token)
    rows = db.query(
        "SELECT device_id, nickname, tier, tier_score, online, last_active, first_seen_at FROM users ORDER BY last_active DESC LIMIT ?",
        (limit,),
    )
    return {"users": [dict(r) for r in rows]}


@router.get("/user/{device_id}")
def user_detail(device_id: str, x_admin_token: str | None = Header(None)):
    require_admin(x_admin_token)
    u = db.query_one("SELECT * FROM users WHERE device_id=?", (device_id,))
    if not u:
        return {"error": "not found"}
    cfg = db.query_one("SELECT payload, updated_at FROM configs WHERE device_id=?", (device_id,))
    recs = db.query("SELECT date, payload, updated_at FROM ledger_records WHERE device_id=? ORDER BY date DESC LIMIT 60", (device_id,))
    snaps = snapshot.list_snapshots(device_id, 20)
    return {
        "user": dict(u),
        "config": json.loads(cfg["payload"]) if cfg else None,
        "config_updated_at": cfg["updated_at"] if cfg else None,
        "records": [{"date": r["date"], "payload": json.loads(r["payload"]), "updated_at": r["updated_at"]} for r in recs],
        "snapshots": snaps,
    }


@router.get("/pk/running")
def pk_running(x_admin_token: str | None = Header(None)):
    require_admin(x_admin_token)
    rows = db.query("SELECT * FROM pk_sessions WHERE status='running' ORDER BY started_at DESC")
    return {"sessions": [dict(r) for r in rows]}


@router.get("/tier/distribution")
def tier_distribution(x_admin_token: str | None = Header(None)):
    require_admin(x_admin_token)
    return {"distribution": tier.tier_distribution()}


@router.get("/webhooks")
def webhooks(x_admin_token: str | None = Header(None)):
    require_admin(x_admin_token)
    rows = db.query("SELECT * FROM webhooks ORDER BY created_at DESC")
    return {"webhooks": [dict(r) for r in rows]}


@router.post("/revoke/{device_id}")
def revoke(device_id: str, x_admin_token: str | None = Header(None)):
    require_admin(x_admin_token)
    db.execute("UPDATE users SET token=NULL, token_issued_at=NULL WHERE device_id=?", (device_id,))
    return {"ok": True}