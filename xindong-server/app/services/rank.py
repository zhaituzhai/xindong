from __future__ import annotations
import json
from datetime import date
from typing import Any
from app import db
from app.timeutil import today_str, week_dates, iso, parse_dt, now

REST_D = {
    "NoRest": 30.0,
    "Rest2Days": 28.0,
    "Rest4Days": 26.0,
    "BigSmallWeek": 24.0,
    "DoubleWeekend": 21.75,
}

METRICS: dict[str, dict[str, Any]] = {
    "normal_earned":      {"label": "今日已赚",       "kind": "money",    "dir": "desc", "money": True},
    "net_earned":         {"label": "净赚",           "kind": "money",    "dir": "desc", "money": True},
    "slacking_earned":    {"label": "摸鱼赚",         "kind": "money",    "dir": "desc", "money": True},
    "worked_seconds":     {"label": "有效工时",       "kind": "seconds",  "dir": "desc", "money": False},
    "total_seconds":      {"label": "总工时",         "kind": "seconds",  "dir": "desc", "money": False},
    "ontime_days":        {"label": "按时下班天数",   "kind": "count",    "dir": "desc", "money": False},
    "full_attendance":    {"label": "全勤天数",       "kind": "count",    "dir": "desc", "money": False},
    "overtime_loss":      {"label": "义务加班亏损",   "kind": "money",    "dir": "desc", "money": True},
    "emergency_call_loss":{"label": "夺命Call亏损",   "kind": "money",    "dir": "desc", "money": True},
    "rounding_loss":      {"label": "抹零损失",       "kind": "money",    "dir": "desc", "money": True},
    "depreciation_pct":   {"label": "身价贬值",       "kind": "percent",  "dir": "desc", "money": False},
    "slacking_seconds":   {"label": "摸鱼时长",       "kind": "seconds",  "dir": "desc", "money": False},
    "slacking_count":     {"label": "摸鱼次数",       "kind": "count",    "dir": "desc", "money": False},
    "single_slack_max":   {"label": "单次最长摸鱼",   "kind": "seconds",  "dir": "desc", "money": False},
    "emergency_call_count":{"label": "夺命Call次数",  "kind": "count",    "dir": "desc", "money": False},
    "poorest":            {"label": "穷鬼榜(净赚倒排)","kind": "money",   "dir": "asc",  "money": True},
}

POSITIVE = ["normal_earned", "net_earned", "slacking_earned", "worked_seconds",
            "total_seconds", "ontime_days", "full_attendance"]
NEGATIVE = ["overtime_loss", "emergency_call_loss", "rounding_loss", "depreciation_pct",
            "slacking_seconds", "slacking_count", "single_slack_max",
            "emergency_call_count", "poorest"]


def _d_from_config(cfg: dict) -> float:
    return REST_D.get(str(cfg.get("RestSystem") or ""), 21.75)


def _parse_hhmm(s) -> float | None:
    if not s:
        return None
    try:
        h, m = str(s).split(":")
        v = int(h) + int(m) / 60.0
        return v if 0 <= v < 24 else None
    except Exception:
        return None


def _standard_hours(cfg: dict) -> float:
    """复刻 app UserConfig.ComputedStandardHours：下班-上班-重叠午休，异常回退旧 StandardHours。"""
    start = _parse_hhmm(cfg.get("WorkStart"))
    end = _parse_hhmm(cfg.get("WorkEnd"))
    if start is None or end is None or end <= start:
        return max(0.5, float(cfg.get("StandardHours") or 8))
    hours = end - start
    ls = _parse_hhmm(cfg.get("LunchStart"))
    le = _parse_hhmm(cfg.get("LunchEnd"))
    if ls is not None and le is not None and le > ls:
        ov_s = max(ls, start)
        ov_e = min(le, end)
        if ov_e > ov_s:
            hours -= ov_e - ov_s
    return max(0.5, round(hours, 4))


def _survival_cost(cfg: dict) -> float:
    rent = float(cfg.get("MonthlyRent") or 0)
    daily = float(cfg.get("DailyCommuteFood") or 0)
    return rent / 30.0 + daily


def _standard_hourly(cfg: dict) -> float:
    mode = cfg.get("SalaryMode")
    amount = float(cfg.get("SalaryAmount") or 0)
    d = _d_from_config(cfg)
    h = _standard_hours(cfg)
    if mode == "Monthly":
        daily_salary = amount / d if d else 0
        return daily_salary / h if h else 0
    return amount


def _daily_salary(cfg: dict) -> float:
    amount = float(cfg.get("SalaryAmount") or 0)
    d = _d_from_config(cfg)
    if cfg.get("SalaryMode") == "Monthly":
        return amount / d if d else 0
    h = _standard_hours(cfg)
    return amount * h


def _rec_metrics(rec: dict, cfg: dict) -> dict[str, float]:
    normal = float(rec.get("NormalEarned") or 0)
    slack_e = float(rec.get("SlackingEarned") or 0)
    ot_loss = float(rec.get("OvertimeLoss") or 0)
    em_loss = float(rec.get("EmergencyCallLoss") or 0)
    rd_loss = float(rec.get("RoundingLoss") or 0)
    worked = float(rec.get("WorkedSeconds") or 0)
    ot_sec = float(rec.get("OvertimeSeconds") or 0)
    slack_sec = float(rec.get("SlackingSeconds") or 0)
    slack_cnt = float(rec.get("SlackingCount") or 0)
    em_cnt = float(rec.get("EmergencyCallCount") or 0)
    survival = _survival_cost(cfg)
    net = normal - survival - ot_loss - rd_loss - em_loss

    std_hourly = _standard_hourly(cfg)
    h = _standard_hours(cfg)
    ot_hours = ot_sec / 3600.0
    daily_sal = _daily_salary(cfg)
    actual_hourly = daily_sal / (h + ot_hours) if (h + ot_hours) > 0 else 0
    depr = (std_hourly - actual_hourly) / std_hourly if std_hourly > 0 else 0.0

    single_max = 0.0
    for s in (rec.get("SlackingStats") or []):
        try:
            single_max = max(single_max, float(s.get("Duration") or s.get("Seconds") or 0))
        except Exception:
            pass

    is_off = bool(rec.get("IsOffDuty"))
    auto = bool(rec.get("AutoFilled"))
    ontime = 1.0 if (is_off and ot_sec == 0 and not auto) else 0.0
    full_att = 1.0 if (worked > 0 and not auto) else 0.0

    return {
        "normal_earned": normal,
        "net_earned": net,
        "slacking_earned": slack_e,
        "worked_seconds": worked,
        "total_seconds": worked + ot_sec,
        "ontime_days": ontime,
        "full_attendance": full_att,
        "overtime_loss": ot_loss,
        "emergency_call_loss": em_loss,
        "rounding_loss": rd_loss,
        "depreciation_pct": depr,
        "slacking_seconds": slack_sec,
        "slacking_count": slack_cnt,
        "single_slack_max": single_max,
        "emergency_call_count": em_cnt,
        "poorest": net,
    }


def _aggregate(recs: list[dict], cfg: dict) -> dict[str, float]:
    if not recs:
        return {k: 0.0 for k in METRICS}
    if len(recs) == 1:
        return _rec_metrics(recs[0], cfg)
    agg = {k: 0.0 for k in METRICS}
    depr_sum = 0.0
    for rec in recs:
        m = _rec_metrics(rec, cfg)
        for k in agg:
            agg[k] += m[k]
        depr_sum += m["depreciation_pct"]
    agg["depreciation_pct"] = depr_sum / len(recs)
    return agg


def _load_cfg(device_id: str) -> dict:
    row = db.query_one("SELECT payload FROM configs WHERE device_id=?", (device_id,))
    if not row:
        return {}
    try:
        return json.loads(row["payload"])
    except Exception:
        return {}


def _load_records(device_id: str, dates: list[str]) -> list[dict]:
    if not dates:
        return []
    placeholders = ",".join("?" * len(dates))
    rows = db.query(
        f"SELECT date, payload FROM ledger_records WHERE device_id=? AND date IN ({placeholders}) ORDER BY date",
        (device_id, *dates),
    )
    out = []
    for r in rows:
        try:
            out.append(json.loads(r["payload"]))
        except Exception:
            pass
    return out


def _scope_dates(scope: str, target: str | None) -> list[str]:
    if scope == "daily":
        return [target or today_str()]
    d = parse_dt(target) if target else None
    wd = (d.date() if d else now().date())
    return [iso(x) for x in week_dates(wd)]


def compute_all(scope: str, target: str | None) -> dict[str, dict[str, float]]:
    dates = _scope_dates(scope, target)
    users = db.query("SELECT device_id, nickname FROM users")
    result: dict[str, dict[str, float]] = {}
    for u in users:
        cfg = _load_cfg(u["device_id"])
        recs = _load_records(u["device_id"], dates)
        if not recs:
            continue
        result[u["device_id"]] = _aggregate(recs, cfg)
    return result, dates  # type: ignore


def build_leaderboard(scope: str, metric: str, target: str | None,
                      me_id: str | None, limit: int = 50) -> dict:
    if metric not in METRICS:
        raise ValueError(f"unknown metric {metric}")
    meta = METRICS[metric]
    scores, dates = compute_all(scope, target)  # type: ignore
    items = [(dev, vals.get(metric, 0.0)) for dev, vals in scores.items()]
    reverse = meta["dir"] == "desc"
    items.sort(key=lambda x: x[1], reverse=reverse)

    nick_map = {u["device_id"]: u["nickname"] for u in db.query("SELECT device_id, nickname FROM users")}
    top_val = items[0][1] if items else None
    entries = []
    prev_val = None
    for i, (dev, val) in enumerate(items[:limit], start=1):
        entries.append({
            "rank": i,
            "device_id": dev,
            "nickname": nick_map.get(dev),
            "is_me": dev == me_id,
            "diff_to_top": (val - top_val) if top_val is not None else None,
            "diff_to_prev": (val - prev_val) if prev_val is not None else None,
            "absolute": val if (dev == me_id or not meta["money"]) else None,
            "is_money": meta["money"],
        })
        prev_val = val

    return {
        "scope": scope,
        "metric": metric,
        "metric_label": meta["label"],
        "date": dates[0] if scope == "daily" else dates[0],
        "period": dates if scope == "weekly" else dates[0],
        "total": len(items),
        "entries": entries,
    }


def my_position(scope: str, target: str | None, me_id: str) -> dict:
    result: dict[str, dict] = {}
    for metric in METRICS:
        try:
            lb = build_leaderboard(scope, metric, target, me_id, limit=10000)
        except Exception:
            continue
        full = lb["entries"]
        pos = next((e for e in full if e["device_id"] == me_id), None)
        result[metric] = {
            "label": METRICS[metric]["label"],
            "rank": pos["rank"] if pos else None,
            "total": lb["total"],
            "absolute": pos["absolute"] if pos else None,
            "percentile": (1 - (pos["rank"] - 1) / lb["total"]) if (pos and lb["total"]) else None,
        }
    return result