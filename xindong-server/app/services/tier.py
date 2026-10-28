from __future__ import annotations
import json
from app import db
from app.config import tier_cfg
from app.timeutil import now, week_str, week_monday, iso
from app.services import rank


def _daily_salary(cfg: dict) -> float:
    amount = float(cfg.get("SalaryAmount") or 0)
    rest_d = {"None": 30.0, "Monthly2": 28.0, "Monthly4": 26.0, "BigSmall": 24.0, "DoubleWeekend": 21.75}
    d = rest_d.get(str(cfg.get("RestSystem") or ""), 21.75)
    if cfg.get("SalaryMode") == "Monthly":
        return amount / d if d else 0
    h = float(cfg.get("StandardHours") or 8) or 8
    return amount * h


def compute_score(device_id: str, week_target: str | None = None) -> float:
    cfg_row = db.query_one("SELECT payload FROM configs WHERE device_id=?", (device_id,))
    cfg = json.loads(cfg_row["payload"]) if cfg_row else {}
    scores, _ = rank.compute_all("weekly", week_target)  # type: ignore
    vals = scores.get(device_id)
    if not vals:
        return 0.0
    w = tier_cfg()["weights"]
    net = vals["net_earned"]
    full_days = vals["full_attendance"]
    daily_sal = _daily_salary(cfg)
    return net * w["weekly_net_earned"] + full_days * daily_sal * w["full_attendance_days_salary"]


def settle_week(week_target: str | None = None) -> dict:
    wk = week_target or week_str()
    users = db.query("SELECT device_id, tier FROM users")
    scored = []
    for u in users:
        s = compute_score(u["device_id"], wk)
        scored.append((u["device_id"], s, u["tier"]))
    if not scored:
        return {"week": wk, "settled": 0}
    scored.sort(key=lambda x: x[1], reverse=True)
    n = len(scored)
    pcts = tier_cfg()["percentiles"]
    names = tier_cfg()["names"]
    ts = now().isoformat()

    def tier_for(rank_pos: int) -> str:
        pct = rank_pos / n * 100
        idx = 0
        for i, cut in enumerate(pcts):
            if pct < cut:
                idx = i
                break
            idx = i + 1
        return names[min(idx, len(names) - 1)]

    with db.tx() as conn:
        for i, (dev, score, prev) in enumerate(scored, start=1):
            t = tier_for(i)
            conn.execute(
                """INSERT INTO ranks(week, device_id, tier, score, prev_tier, settled_at)
                   VALUES(?, ?, ?, ?, ?, ?)
                   ON CONFLICT(week, device_id) DO UPDATE SET tier=excluded.tier, score=excluded.score, prev_tier=excluded.prev_tier, settled_at=excluded.settled_at""",
                (wk, dev, t, score, prev, ts),
            )
            conn.execute("UPDATE users SET tier=?, tier_score=? WHERE device_id=?", (t, score, dev))
    return {"week": wk, "settled": n}


def user_tier(device_id: str) -> dict:
    row = db.query_one("SELECT tier, tier_score FROM users WHERE device_id=?", (device_id,))
    if not row:
        return {"tier": None, "score": 0}
    wk = week_str()
    last = db.query_one("SELECT * FROM ranks WHERE device_id=? AND week=? ORDER BY settled_at DESC LIMIT 1",
                        (device_id, wk))
    return {
        "tier": row["tier"],
        "score": row["tier_score"] or 0,
        "prev_tier": last["prev_tier"] if last else None,
        "week": wk,
    }


def tier_distribution() -> dict:
    rows = db.query("SELECT tier, COUNT(*) AS c FROM users WHERE tier IS NOT NULL GROUP BY tier")
    return {r["tier"]: r["c"] for r in rows}