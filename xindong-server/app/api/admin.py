from __future__ import annotations
import json
import os
from fastapi import APIRouter, Header, Query
from app.auth import require_admin
from app import db
from app.services import rank, tier, snapshot
from app.config import ROOT, storage_cfg
from app.timeutil import now, today_str

router = APIRouter(prefix="/api/v1/admin", tags=["admin"])


def _log(action: str, target: str | None = None, detail: str | None = None):
    try:
        db.execute("INSERT INTO admin_logs(ts, action, target, detail) VALUES(?, ?, ?, ?)",
                   (now().isoformat(), action, target, detail))
    except Exception:
        pass


def _page(page: int, page_size: int) -> tuple[int, int]:
    page = max(1, page)
    page_size = max(1, min(200, page_size))
    return page, page_size


def _paged(total: int, page: int, page_size: int, items: list) -> dict:
    return {"total": total, "page": page, "page_size": page_size, "items": items}


@router.get("/overview")
def overview(x_admin_token: str | None = Header(None)):
    require_admin(x_admin_token)
    online = db.query_one("SELECT COUNT(*) AS c FROM users WHERE online=1")["c"]
    total = db.query_one("SELECT COUNT(*) AS c FROM users")["c"]
    today = db.query_one("SELECT COUNT(*) AS c FROM snapshots WHERE date(reported_at)=date('now')")["c"]
    pk_running = db.query_one("SELECT COUNT(*) AS c FROM pk_sessions WHERE status='running'")["c"]
    ch_achieved = db.query_one("SELECT COUNT(*) AS c FROM challenge_achieved WHERE day=?", (today_str(),))["c"]
    week_settled = db.query_one("SELECT COUNT(*) AS c FROM ranks WHERE week=?", (now().date().isoformat(),))["c"]
    trend = db.query(
        """SELECT date(reported_at) AS d, COUNT(*) AS c FROM snapshots
           WHERE reported_at >= datetime('now','-6 days') GROUP BY date(reported_at) ORDER BY d""",
    )
    return {
        "online": online, "total_users": total, "today_reports": today,
        "tier_distribution": tier.tier_distribution(),
        "trend_7d": [{"date": r["d"], "count": r["c"]} for r in trend],
        "pk_running": pk_running, "challenge_achieved_today": ch_achieved,
        "week_settled": week_settled,
    }


@router.get("/system/status")
def system_status(x_admin_token: str | None = Header(None)):
    require_admin(x_admin_token)
    db_path = ROOT / storage_cfg()["db_path"]
    db_size = os.path.getsize(db_path) if os.path.exists(db_path) else 0
    snap_count = db.query_one("SELECT COUNT(*) AS c FROM snapshots")["c"]
    return {
        "db_size": db_size,
        "snapshot_count": snap_count,
        "jobs": {
            "cleanup_snapshots": "00:30 daily",
            "settle_week": "Mon 00:10",
            "gen_challenge": "00:05 daily",
            "mark_offline": "every 5 min",
        },
    }


@router.get("/users")
def users(page: int = Query(1), page_size: int = Query(20),
          nickname: str | None = Query(None), tier_name: str | None = Query(None),
          online: int | None = Query(None), x_admin_token: str | None = Header(None)):
    require_admin(x_admin_token)
    page, page_size = _page(page, page_size)
    where, params = [], []
    if nickname:
        where.append("nickname LIKE ?"); params.append(f"%{nickname}%")
    if tier_name:
        where.append("tier=?"); params.append(tier_name)
    if online is not None:
        where.append("online=?"); params.append(online)
    clause = (" WHERE " + " AND ".join(where)) if where else ""
    total = db.query_one(f"SELECT COUNT(*) AS c FROM users{clause}", tuple(params))["c"]
    offset = (page - 1) * page_size
    rows = db.query(
        f"""SELECT device_id, nickname, tier, tier_score, online, last_active, first_seen_at, client_version
            FROM users{clause} ORDER BY last_active DESC LIMIT ? OFFSET ?""",
        (*params, page_size, offset),
    )
    return _paged(total, page, page_size, [dict(r) for r in rows])


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


@router.get("/ledger")
def ledger(device_id: str | None = Query(None), frm: str | None = Query(None), to: str | None = Query(None),
           page: int = Query(1), page_size: int = Query(20), x_admin_token: str | None = Header(None)):
    require_admin(x_admin_token)
    page, page_size = _page(page, page_size)
    where, params = [], []
    if device_id:
        where.append("device_id=?"); params.append(device_id)
    if frm:
        where.append("date>=?"); params.append(frm)
    if to:
        where.append("date<=?"); params.append(to)
    clause = (" WHERE " + " AND ".join(where)) if where else ""
    total = db.query_one(f"SELECT COUNT(*) AS c FROM ledger_records{clause}", tuple(params))["c"]
    offset = (page - 1) * page_size
    rows = db.query(
        f"SELECT device_id, date, updated_at, payload FROM ledger_records{clause} ORDER BY date DESC LIMIT ? OFFSET ?",
        (*params, page_size, offset),
    )
    items = []
    for r in rows:
        p = json.loads(r["payload"])
        items.append({"device_id": r["device_id"], "date": r["date"], "updated_at": r["updated_at"],
                      "normal_earned": p.get("NormalEarned"), "slacking_earned": p.get("SlackingEarned"),
                      "overtime_loss": p.get("OvertimeLoss"), "worked_seconds": p.get("WorkedSeconds"),
                      "overtime_seconds": p.get("OvertimeSeconds"), "slacking_seconds": p.get("SlackingSeconds"),
                      "is_off_duty": p.get("IsOffDuty"), "auto_filled": p.get("AutoFilled")})
    return _paged(total, page, page_size, items)


@router.get("/configs")
def configs(device_id: str | None = Query(None), page: int = Query(1), page_size: int = Query(20),
            x_admin_token: str | None = Header(None)):
    require_admin(x_admin_token)
    page, page_size = _page(page, page_size)
    where, params = [], []
    if device_id:
        where.append("c.device_id=?"); params.append(device_id)
    clause = (" WHERE " + " AND ".join(where)) if where else ""
    total = db.query_one(f"SELECT COUNT(*) AS c FROM configs{clause.replace('c.','')}", tuple(params))["c"]
    offset = (page - 1) * page_size
    rows = db.query(
        f"""SELECT c.device_id, c.updated_at, c.payload, u.nickname
            FROM configs c LEFT JOIN users u ON u.device_id=c.device_id
            {clause} ORDER BY c.updated_at DESC LIMIT ? OFFSET ?""",
        (*params, page_size, offset),
    )
    items = [{"device_id": r["device_id"], "nickname": r["nickname"], "updated_at": r["updated_at"],
              "config": json.loads(r["payload"])} for r in rows]
    return _paged(total, page, page_size, items)


@router.get("/snapshots")
def snapshots(device_id: str | None = Query(None), frm: str | None = Query(None), to: str | None = Query(None),
              page: int = Query(1), page_size: int = Query(20), x_admin_token: str | None = Header(None)):
    require_admin(x_admin_token)
    page, page_size = _page(page, page_size)
    where, params = [], []
    if device_id:
        where.append("device_id=?"); params.append(device_id)
    if frm:
        where.append("reported_at>=?"); params.append(frm)
    if to:
        where.append("reported_at<=?"); params.append(to)
    clause = (" WHERE " + " AND ".join(where)) if where else ""
    total = db.query_one(f"SELECT COUNT(*) AS c FROM snapshots{clause}", tuple(params))["c"]
    offset = (page - 1) * page_size
    rows = db.query(
        f"SELECT id, device_id, reported_at, event_type FROM snapshots{clause} ORDER BY reported_at DESC LIMIT ? OFFSET ?",
        (*params, page_size, offset),
    )
    return _paged(total, page, page_size, [dict(r) for r in rows])


@router.get("/friends")
def friends(device_id: str | None = Query(None), page: int = Query(1), page_size: int = Query(20),
            x_admin_token: str | None = Header(None)):
    require_admin(x_admin_token)
    page, page_size = _page(page, page_size)
    where, params = [], []
    if device_id:
        where.append("(f.device_id=? OR f.friend_id=?)"); params += [device_id, device_id]
    clause = (" WHERE " + " AND ".join(where)) if where else ""
    total = db.query_one(f"SELECT COUNT(*) AS c FROM friends f{clause}", tuple(params))["c"]
    offset = (page - 1) * page_size
    rows = db.query(
        f"""SELECT f.device_id, f.friend_id, f.created_at, ua.nickname AS a_nick, ub.nickname AS b_nick
            FROM friends f LEFT JOIN users ua ON ua.device_id=f.device_id LEFT JOIN users ub ON ub.device_id=f.friend_id
            {clause} ORDER BY f.created_at DESC LIMIT ? OFFSET ?""",
        (*params, page_size, offset),
    )
    return _paged(total, page, page_size, [dict(r) for r in rows])


@router.delete("/friends/{a}/{b}")
def friends_remove(a: str, b: str, x_admin_token: str | None = Header(None)):
    require_admin(x_admin_token)
    db.execute("DELETE FROM friends WHERE (device_id=? AND friend_id=?) OR (device_id=? AND friend_id=?)",
               (a, b, b, a))
    _log("friend_remove", f"{a}/{b}")
    return {"ok": True}


@router.get("/invitations")
def invitations(page: int = Query(1), page_size: int = Query(20), x_admin_token: str | None = Header(None)):
    require_admin(x_admin_token)
    page, page_size = _page(page, page_size)
    total = db.query_one("SELECT COUNT(*) AS c FROM invitations")["c"]
    offset = (page - 1) * page_size
    rows = db.query(
        """SELECT i.code, i.issuer, i.created_at, i.expires_at, i.used, u.nickname
           FROM invitations i LEFT JOIN users u ON u.device_id=i.issuer
           ORDER BY i.created_at DESC LIMIT ? OFFSET ?""",
        (page_size, offset),
    )
    return _paged(total, page, page_size, [dict(r) for r in rows])


@router.get("/webhooks")
def webhooks(page: int = Query(1), page_size: int = Query(20), x_admin_token: str | None = Header(None)):
    require_admin(x_admin_token)
    page, page_size = _page(page, page_size)
    total = db.query_one("SELECT COUNT(*) AS c FROM webhooks")["c"]
    offset = (page - 1) * page_size
    rows = db.query("SELECT * FROM webhooks ORDER BY created_at DESC LIMIT ? OFFSET ?", (page_size, offset))
    return _paged(total, page, page_size, [dict(r) for r in rows])


@router.delete("/webhooks/{wid}")
def webhooks_delete(wid: int, x_admin_token: str | None = Header(None)):
    require_admin(x_admin_token)
    db.execute("DELETE FROM webhooks WHERE id=?", (wid,))
    _log("webhook_delete", str(wid))
    return {"ok": True}


@router.get("/webhooks/{wid}/logs")
def webhooks_logs(wid: int, page: int = Query(1), page_size: int = Query(20),
                  x_admin_token: str | None = Header(None)):
    require_admin(x_admin_token)
    page, page_size = _page(page, page_size)
    total = db.query_one("SELECT COUNT(*) AS c FROM webhook_logs WHERE webhook_id=?", (wid,))["c"]
    offset = (page - 1) * page_size
    rows = db.query(
        "SELECT * FROM webhook_logs WHERE webhook_id=? ORDER BY created_at DESC LIMIT ? OFFSET ?",
        (wid, page_size, offset),
    )
    return _paged(total, page, page_size, [dict(r) for r in rows])


@router.get("/pk/running")
def pk_running(x_admin_token: str | None = Header(None)):
    require_admin(x_admin_token)
    rows = db.query("SELECT * FROM pk_sessions WHERE status='running' ORDER BY started_at DESC")
    return {"sessions": [dict(r) for r in rows]}


@router.get("/pk/list")
def pk_list(status: str | None = Query(None), page: int = Query(1), page_size: int = Query(20),
            x_admin_token: str | None = Header(None)):
    require_admin(x_admin_token)
    page, page_size = _page(page, page_size)
    where, params = [], []
    if status:
        where.append("status=?"); params.append(status)
    clause = (" WHERE " + " AND ".join(where)) if where else ""
    total = db.query_one(f"SELECT COUNT(*) AS c FROM pk_sessions{clause}", tuple(params))["c"]
    offset = (page - 1) * page_size
    rows = db.query(
        f"""SELECT s.*, ua.nickname AS a_nick, ub.nickname AS b_nick
            FROM pk_sessions s LEFT JOIN users ua ON ua.device_id=s.a_id LEFT JOIN users ub ON ub.device_id=s.b_id
            {clause} ORDER BY s.started_at DESC LIMIT ? OFFSET ?""",
        (*params, page_size, offset),
    )
    return _paged(total, page, page_size, [dict(r) for r in rows])


@router.get("/ranks")
def ranks(week: str | None = Query(None), page: int = Query(1), page_size: int = Query(20),
          x_admin_token: str | None = Header(None)):
    require_admin(x_admin_token)
    page, page_size = _page(page, page_size)
    where, params = [], []
    if week:
        where.append("week=?"); params.append(week)
    clause = (" WHERE " + " AND ".join(where)) if where else ""
    total = db.query_one(f"SELECT COUNT(*) AS c FROM ranks{clause}", tuple(params))["c"]
    offset = (page - 1) * page_size
    rows = db.query(
        f"""SELECT r.*, u.nickname FROM ranks r LEFT JOIN users u ON u.device_id=r.device_id
            {clause} ORDER BY r.score DESC LIMIT ? OFFSET ?""",
        (*params, page_size, offset),
    )
    return _paged(total, page, page_size, [dict(r) for r in rows])


@router.get("/challenges")
def challenges(frm: str | None = Query(None), to: str | None = Query(None),
               page: int = Query(1), page_size: int = Query(20), x_admin_token: str | None = Header(None)):
    require_admin(x_admin_token)
    page, page_size = _page(page, page_size)
    where, params = [], []
    if frm:
        where.append("day>=?"); params.append(frm)
    if to:
        where.append("day<=?"); params.append(to)
    clause = (" WHERE " + " AND ".join(where)) if where else ""
    total = db.query_one(f"SELECT COUNT(*) AS c FROM challenges{clause}", tuple(params))["c"]
    offset = (page - 1) * page_size
    rows = db.query(
        f"SELECT day, kind, params, created_at FROM challenges{clause} ORDER BY day DESC LIMIT ? OFFSET ?",
        (*params, page_size, offset),
    )
    items = [{"day": r["day"], "kind": r["kind"], "params": json.loads(r["params"]), "created_at": r["created_at"]} for r in rows]
    return _paged(total, page, page_size, items)


@router.get("/challenges/{day}/achieved")
def challenges_achieved(day: str, x_admin_token: str | None = Header(None)):
    require_admin(x_admin_token)
    rows = db.query(
        """SELECT ca.device_id, ca.achieved_at, u.nickname
           FROM challenge_achieved ca LEFT JOIN users u ON u.device_id=ca.device_id
           WHERE ca.day=? ORDER BY ca.achieved_at ASC""",
        (day,),
    )
    return {"day": day, "achieved": [dict(r) for r in rows]}


@router.post("/revoke/{device_id}")
def revoke(device_id: str, x_admin_token: str | None = Header(None)):
    require_admin(x_admin_token)
    db.execute("UPDATE users SET token=NULL, token_issued_at=NULL WHERE device_id=?", (device_id,))
    _log("revoke_token", device_id)
    return {"ok": True}


@router.post("/delete_user/{device_id}")
def delete_user(device_id: str, x_admin_token: str | None = Header(None)):
    require_admin(x_admin_token)
    with db.tx() as conn:
        for t in ["configs", "ledger_records", "snapshots"]:
            conn.execute(f"DELETE FROM {t} WHERE device_id=?", (device_id,))
        conn.execute("DELETE FROM friends WHERE device_id=? OR friend_id=?", (device_id, device_id))
        conn.execute("DELETE FROM challenge_achieved WHERE device_id=?", (device_id,))
        conn.execute("DELETE FROM ranks WHERE device_id=?", (device_id,))
        conn.execute("DELETE FROM users WHERE device_id=?", (device_id,))
    _log("delete_user", device_id)
    return {"ok": True}


@router.post("/settle_week")
def settle_week(x_admin_token: str | None = Header(None)):
    require_admin(x_admin_token)
    res = tier.settle_week()
    _log("settle_week", str(res.get("week")), f"settled={res.get('settled')}")
    return res


@router.get("/logs")
def admin_logs(action: str | None = Query(None), page: int = Query(1), page_size: int = Query(20),
               x_admin_token: str | None = Header(None)):
    require_admin(x_admin_token)
    page, page_size = _page(page, page_size)
    where, params = [], []
    if action:
        where.append("action=?"); params.append(action)
    clause = (" WHERE " + " AND ".join(where)) if where else ""
    total = db.query_one(f"SELECT COUNT(*) AS c FROM admin_logs{clause}", tuple(params))["c"]
    offset = (page - 1) * page_size
    rows = db.query(
        f"SELECT id, ts, action, target, detail FROM admin_logs{clause} ORDER BY ts DESC LIMIT ? OFFSET ?",
        (*params, page_size, offset),
    )
    return _paged(total, page, page_size, [dict(r) for r in rows])