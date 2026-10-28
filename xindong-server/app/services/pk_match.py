from __future__ import annotations
import json
import uuid
from app import db
from app.timeutil import now
from app.services import rank, tier


def _online_users() -> list[str]:
    rows = db.query("SELECT device_id FROM users WHERE online=1")
    return [r["device_id"] for r in rows]


def match(device_id: str, mode: str = "random", friend_id: str | None = None,
          anonymous: bool = False) -> dict:
    candidates: list[str] = []
    if mode == "friend":
        if not friend_id:
            raise ValueError("friend_id required for friend mode")
        fr = db.query_one("SELECT 1 FROM friends WHERE device_id=? AND friend_id=?",
                          (device_id, friend_id))
        if not fr:
            raise ValueError("not a friend")
        candidates = [friend_id]
    elif mode == "tier":
        me = db.query_one("SELECT tier FROM users WHERE device_id=?", (device_id,))
        my_tier = me["tier"] if me else None
        rows = db.query("SELECT device_id FROM users WHERE online=1 AND device_id!=? AND tier=?",
                        (device_id, my_tier))
        candidates = [r["device_id"] for r in rows]
    else:
        candidates = [u for u in _online_users() if u != device_id]

    if not candidates:
        raise ValueError("no available opponent")
    import secrets
    opponent = secrets.choice(candidates)
    session_id = uuid.uuid4().hex
    ts = now().isoformat()
    db.execute(
        """INSERT INTO pk_sessions(session_id, a_id, b_id, mode, anonymous, status, started_at)
           VALUES(?, ?, ?, ?, ?, 'running', ?)""",
        (session_id, device_id, opponent, mode, 1 if anonymous else 0, ts),
    )
    return {"session_id": session_id, "opponent": None if anonymous else opponent,
            "anonymous": anonymous, "started_at": ts}


def live_diff(session_id: str, me_id: str) -> dict:
    row = db.query_one("SELECT * FROM pk_sessions WHERE session_id=?", (session_id,))
    if not row:
        raise ValueError("session not found")
    a, b = row["a_id"], row["b_id"]
    today = now().date().isoformat()
    scores, _ = rank.compute_all("daily", today)  # type: ignore
    a_vals = scores.get(a, {})
    b_vals = scores.get(b, {})
    anon = bool(row["anonymous"])
    metrics = ["normal_earned", "net_earned", "slacking_earned"]
    diff = {}
    for m in metrics:
        av = a_vals.get(m, 0.0)
        bv = b_vals.get(m, 0.0)
        if me_id == a:
            diff[m] = av - bv
        else:
            diff[m] = bv - av
    return {
        "session_id": session_id,
        "status": row["status"],
        "anonymous": anon,
        "opponent": None if anon else (b if me_id == a else a),
        "diff": diff,
    }


def settle(session_id: str, me_id: str) -> dict:
    row = db.query_one("SELECT * FROM pk_sessions WHERE session_id=?", (session_id,))
    if not row:
        raise ValueError("session not found")
    if row["status"] != "running":
        return json.loads(row["report"]) if row["report"] else {}
    a, b = row["a_id"], row["b_id"]
    today = now().date().isoformat()
    scores, _ = rank.compute_all("daily", today)  # type: ignore
    a_net = scores.get(a, {}).get("net_earned", 0.0)
    b_net = scores.get(b, {}).get("net_earned", 0.0)
    winner = a if a_net > b_net else (b if b_net > a_net else None)
    ts = now().isoformat()
    report = {
        "session_id": session_id,
        "winner": winner,
        "a_net": a_net,
        "b_net": b_net,
        "diff": abs(a_net - b_net),
        "started_at": row["started_at"],
        "ended_at": ts,
    }
    db.execute("UPDATE pk_sessions SET status='settled', ended_at=?, winner=?, report=? WHERE session_id=?",
               (ts, winner, json.dumps(report, ensure_ascii=False), session_id))
    return report


def white_collar_leaderboard(week_target: str | None = None, limit: int = 50) -> dict:
    scores, dates = rank.compute_all("weekly", week_target)  # type: ignore
    items = []
    for dev, vals in scores.items():
        white = vals["overtime_loss"] + vals["emergency_call_loss"]
        items.append((dev, white))
    items.sort(key=lambda x: x[1], reverse=True)
    nick_map = {u["device_id"]: u["nickname"] for u in db.query("SELECT device_id, nickname FROM users")}
    top = items[0][1] if items else None
    entries = []
    for i, (dev, val) in enumerate(items[:limit], start=1):
        entries.append({"rank": i, "nickname": nick_map.get(dev), "diff_to_top": (val - top) if top is not None else None})
    return {"title": "老板白嫖榜", "period": dates, "entries": entries}