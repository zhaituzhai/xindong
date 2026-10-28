from __future__ import annotations
import argparse
import json
import random
import uuid
from datetime import date, timedelta
import httpx

API = "http://127.0.0.1:8770"

REST_SYSTEMS = ["DoubleWeekend", "BigSmall", "Monthly4", "Monthly2", "None"]
NICKNAMES = ["卷王", "摸鱼大师", "加班侠", "穷鬼", "白嫖之王", "按时下班", "夺命Call常客",
             "身价暴跌", "抹零达人", "全勤标兵", "老六", "打工魂", "搬砖工", "咸鱼", "奋斗逼"]


def gen_config(i: int) -> dict:
    salary = random.choice([4000, 6000, 8000, 12000, 15000, 20000])
    return {
        "Version": 1,
        "SalaryMode": "Monthly",
        "SalaryAmount": salary,
        "RestSystem": random.choice(REST_SYSTEMS),
        "WorkStart": "09:00",
        "WorkEnd": "18:00",
        "LunchStart": "12:00",
        "LunchEnd": "13:00",
        "StandardHours": 8,
        "WorkDays": ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"],
        "Holidays": [],
        "MakeupDays": [],
        "HolidayCalendar": {"Holidays": {}, "Workdays": [], "RegularRestDays": [],
                             "BigSmallWeekAnchorMonday": None, "BigSmallWeekAnchorIsBig": True},
        "TodayOff": False,
        "MonthlyRent": random.choice([1500, 2000, 2500, 3000, 4000]),
        "DailyCommuteFood": random.choice([30, 50, 60, 80, 100]),
        "PayDay": 15,
        "MonthlyToleranceLimit": 800,
        "OvertimeRateNormal": 1.0, "OvertimeRateNormalLate": 1.5,
        "OvertimeRateHoliday": 2.0, "OvertimeRateHolidayLate": 3.0,
        "NormalOvertimeStageHours": 2, "HolidayOvertimeStageHours": 8,
        "OvertimeBaseMonthly": 0, "UseCustomOvertimeBase": False, "OvertimeBaseSalary": 2360,
        "BossKeyMode": "Stealth", "BossKeyActive": False, "StartWithWindows": False,
        "FirstRunDate": "2026-10-28T00:00:00+08:00", "BossHotKey": "Ctrl + Alt + Z",
        "ShowIslandOnStart": True, "ShowCalendarLegend": True, "CloseToTray": True,
    }


def gen_record(d: date, cfg: dict) -> dict:
    salary = cfg["SalaryAmount"]
    rest_d = {"None": 30.0, "Monthly2": 28.0, "Monthly4": 26.0, "BigSmall": 24.0, "DoubleWeekend": 21.75}
    dd = rest_d.get(cfg["RestSystem"], 21.75)
    daily_salary = salary / dd
    h = cfg["StandardHours"]
    worked_sec = h * 3600
    normal = daily_salary
    ot_sec = random.choices([0, 1800, 3600, 5400, 7200], weights=[50, 20, 15, 10, 5])[0]
    ot_loss = (ot_sec / 3600.0) * (daily_salary / h) * 1.0 if ot_sec else 0
    slack_sec = random.choices([0, 300, 900, 1800], weights=[40, 30, 20, 10])[0]
    slack_earned = slack_sec / 3600.0 * (daily_salary / h)
    slack_count = random.randint(0, 6) if slack_sec else 0
    rd_loss = random.choice([0, 0, 0, 2, 5])
    em_count = random.choices([0, 1, 2], weights=[80, 15, 5])[0]
    em_loss = em_count * random.uniform(20, 100)
    return {
        "Date": d.isoformat(),
        "NormalEarned": normal,
        "SlackingEarned": slack_earned,
        "OvertimeLoss": ot_loss,
        "EmergencyCallLoss": em_loss,
        "EmergencyCallEarned": 0,
        "EmergencyCallSeconds": 0,
        "EmergencyCallCount": em_count,
        "EmergencyCallMode": "Unpaid",
        "EmergencyCallRate": 0,
        "EmergencyCallActive": False,
        "EmergencyCallStartedAt": None,
        "EmergencyCallEndedAt": None,
        "EmergencyCallFixedSubsidy": 0,
        "EmergencyCallHourlyRate": 0,
        "RoundingLoss": rd_loss,
        "WorkedSeconds": worked_sec,
        "SlackingSeconds": slack_sec,
        "OvertimeSeconds": ot_sec,
        "LockedHourlyAmount": 0,
        "SlackingCount": slack_count,
        "RoundingLostSeconds": 0,
        "IsHoliday": False,
        "IsOffDuty": True,
        "AutoFilled": False,
        "ClockOutAt": f"{d.isoformat()}T18:00:00+08:00",
        "HolidayOvertime": False,
        "HolidayOvertimeStart": None,
        "SlackingStartedAt": None,
        "ActiveSlackingType": "Phone",
        "SlackingStats": [],
        "LastUpdated": f"{d.isoformat()}T18:00:00+08:00",
    }


def seed(users: int, days: int) -> None:
    today = date(2026, 10, 28)
    dates = [today - timedelta(days=i) for i in range(days)]
    for i in range(users):
        device_id = str(uuid.uuid4())
        cfg = gen_config(i)
        records = [gen_record(d, cfg) for d in dates]
        payload = {
            "device_id": device_id,
            "client_version": "1.0.0",
            "client_tz_offset": 480,
            "reported_at": f"{today.isoformat()}T21:00:00+08:00",
            "config": cfg,
            "ledger": {"Version": 1, "Records": records},
            "event_type": "report",
        }
        r = httpx.post(f"{API}/api/v1/report", json=payload, timeout=10.0)
        if r.status_code != 200:
            print(f"[{i}] report failed: {r.text}")
            continue
        token = r.json()["token"]
        nick = f"{random.choice(NICKNAMES)}{i:02d}"
        httpx.post(f"{API}/api/v1/bind/nickname", json={"nickname": nick},
                   headers={"Authorization": f"Bearer {token}"}, timeout=10.0)
        print(f"[{i}] {nick} ok")
    print(f"seeded {users} users x {days} days")


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("--users", type=int, default=50)
    p.add_argument("--days", type=int, default=7)
    p.add_argument("--api", type=str, default=API)
    args = p.parse_args()
    API = args.api
    seed(args.users, args.days)