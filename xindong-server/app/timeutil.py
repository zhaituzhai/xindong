from __future__ import annotations
from datetime import datetime, date, timedelta
from zoneinfo import ZoneInfo

TZ = ZoneInfo("Asia/Shanghai")


def now() -> datetime:
    return datetime.now(TZ)


def today_str() -> str:
    return now().date().isoformat()


def parse_dt(s: str | None) -> datetime | None:
    if not s:
        return None
    try:
        return datetime.fromisoformat(s).astimezone(TZ)
    except Exception:
        return None


def week_monday(d: date | None = None) -> date:
    d = d or now().date()
    return d - timedelta(days=d.weekday())


def week_str(d: date | None = None) -> str:
    return week_monday(d).isoformat()


def week_dates(d: date | None = None) -> list[date]:
    m = week_monday(d)
    return [m + timedelta(days=i) for i in range(7)]


def date_range(start: date, end: date) -> list[date]:
    out = []
    cur = start
    while cur <= end:
        out.append(cur)
        cur += timedelta(days=1)
    return out


def iso(d: date) -> str:
    return d.isoformat()