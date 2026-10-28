from __future__ import annotations
import json
import random
from app import db
from app.config import challenge_cfg
from app.timeutil import now, today_str


def _gen_params(tmpl: dict) -> dict:
    kind = tmpl["kind"]
    if kind in ("slack_minutes", "single_slack_minutes", "slack_count"):
        return {"kind": kind, "threshold": random.randint(tmpl["min"], tmpl["max"])}
    if kind in ("net_earned_pct", "net_over_median_pct"):
        return {"kind": kind, "threshold": random.randint(tmpl["min"], tmpl["max"])}
    return {"kind": kind}


def _describe(params: dict) -> str:
    k = params["kind"]
    t = params.get("threshold")
    if k == "slack_minutes":
        return f"今日摸鱼满 {t} 分钟"
    if k == "net_earned_pct":
        return f"今日净赚超过日薪 {t}%"
    if k == "zero_overtime":
        return "今日零加班下班"
    if k == "slack_count":
        return f"今日摸鱼次数达 {t} 次"
    if k == "single_slack_minutes":
        return f"单次摸鱼超过 {t} 分钟"
    if k == "net_over_median_pct":
        return f"今日净赚超过全服中位数 {t}%"
    return k


def generate_daily(day: str | None = None) -> dict:
    day = day or today_str()
    existing = db.query_one("SELECT day FROM challenges WHERE day=?", (day,))
    if existing:
        return get_daily(day)
    cfg = challenge_cfg()
    templates = cfg["templates"]
    nondup = cfg["nondup_days"]
    recent = db.query(
        "SELECT kind FROM challenges WHERE day >= date(?, ?) ORDER BY day DESC",
        (day, f"-{nondup} days"),
    )
    recent_kinds = {r["kind"] for r in recent}
    candidates = [t for t in templates if t["kind"] not in recent_kinds] or templates
    tmpl = random.choice(candidates)
    params = _gen_params(tmpl)
    ts = now().isoformat()
    db.execute(
        "INSERT OR IGNORE INTO challenges(day, kind, params, created_at) VALUES(?, ?, ?, ?)",
        (day, params["kind"], json.dumps(params, ensure_ascii=False), ts),
    )
    return get_daily(day)


def get_daily(day: str | None = None) -> dict | None:
    day = day or today_str()
    row = db.query_one("SELECT * FROM challenges WHERE day=?", (day,))
    if not row:
        return None
    params = json.loads(row["params"])
    return {"day": day, "kind": row["kind"], "params": params, "description": _describe(params)}


def check_achieved(device_id: str, day: str | None = None) -> bool:
    ch = get_daily(day)
    if not ch:
        return False
    params = ch["params"]
    kind = params["kind"]
    threshold = params.get("threshold", 0)
    rec_row = db.query_one("SELECT payload FROM ledger_records WHERE device_id=? AND date=?",
                           (device_id, ch["day"]))
    if not rec_row:
        return False
    rec = json.loads(rec_row["payload"])
    cfg_row = db.query_one("SELECT payload FROM configs WHERE device_id=?", (device_id,))
    cfg = json.loads(cfg_row["payload"]) if cfg_row else {}

    achieved = False
    if kind == "slack_minutes":
        achieved = (rec.get("SlackingSeconds") or 0) / 60.0 >= threshold
    elif kind == "single_slack_minutes":
        mx = max((s.get("Duration") or s.get("Seconds") or 0) for s in (rec.get("SlackingStats") or [0])) or 0
        achieved = mx / 60.0 >= threshold
    elif kind == "slack_count":
        achieved = (rec.get("SlackingCount") or 0) >= threshold
    elif kind == "zero_overtime":
        achieved = (rec.get("OvertimeSeconds") or 0) == 0 and bool(rec.get("IsOffDuty"))
    elif kind == "net_earned_pct":
        normal = rec.get("NormalEarned") or 0
        survival = (cfg.get("MonthlyRent") or 0) / 30.0 + (cfg.get("DailyCommuteFood") or 0)
        net = normal - survival - (rec.get("OvertimeLoss") or 0) - (rec.get("RoundingLoss") or 0) - (rec.get("EmergencyCallLoss") or 0)
        rest_d = {"None": 30.0, "Monthly2": 28.0, "Monthly4": 26.0, "BigSmall": 24.0, "DoubleWeekend": 21.75}
        d = rest_d.get(str(cfg.get("RestSystem") or ""), 21.75)
        daily_sal = (cfg.get("SalaryAmount") or 0) / d if cfg.get("SalaryMode") == "Monthly" else (cfg.get("SalaryAmount") or 0) * (cfg.get("StandardHours") or 8)
        achieved = daily_sal > 0 and net >= daily_sal * threshold / 100.0
    elif kind == "net_over_median_pct":
        normal = rec.get("NormalEarned") or 0
        all_normal = [json.loads(r["payload"]).get("NormalEarned") or 0 for r in db.query("SELECT payload FROM ledger_records WHERE date=?", (ch["day"],))]
        if not all_normal:
            return False
        med = sorted(all_normal)[len(all_normal) // 2]
        achieved = med > 0 and normal >= med * (1 + threshold / 100.0)

    if achieved:
        ts = now().isoformat()
        try:
            db.execute("INSERT INTO challenge_achieved(day, device_id, achieved_at) VALUES(?, ?, ?)",
                       (ch["day"], device_id, ts))
            return True
        except Exception:
            return False
    return False


def achieved_leaderboard(day: str | None = None) -> list[dict]:
    day = day or today_str()
    rows = db.query(
        """SELECT ca.device_id, ca.achieved_at, u.nickname
           FROM challenge_achieved ca JOIN users u ON u.device_id=ca.device_id
           WHERE ca.day=? ORDER BY ca.achieved_at ASC""",
        (day,),
    )
    return [dict(r) for r in rows]