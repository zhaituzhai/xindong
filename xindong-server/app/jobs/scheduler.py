from __future__ import annotations
from apscheduler.schedulers.background import BackgroundScheduler
from apscheduler.triggers.cron import CronTrigger
from app.services import snapshot, tier, challenge
from app.timeutil import TZ

_scheduler: BackgroundScheduler | None = None


def start() -> BackgroundScheduler:
    global _scheduler
    if _scheduler is not None:
        return _scheduler
    sched = BackgroundScheduler(timezone=str(TZ))
    sched.add_job(snapshot.cleanup_snapshots, CronTrigger(hour=0, minute=30), id="cleanup_snapshots")
    sched.add_job(tier.settle_week, CronTrigger(day_of_week="mon", hour=0, minute=10), id="settle_week")
    sched.add_job(challenge.generate_daily, CronTrigger(hour=0, minute=5), id="gen_challenge")
    sched.add_job(_mark_offline, CronTrigger(minute="*/5"), id="mark_offline")
    sched.start()
    _scheduler = sched
    return sched


def _mark_offline() -> None:
    from app import db
    from app.timeutil import now
    from app.config import offline_cfg
    from datetime import timedelta
    cutoff = (now() - timedelta(seconds=offline_cfg()["heartbeat_timeout_seconds"])).isoformat()
    db.execute("UPDATE users SET online=0 WHERE last_active < ? AND online=1", (cutoff,))


def shutdown() -> None:
    global _scheduler
    if _scheduler:
        _scheduler.shutdown(wait=False)
        _scheduler = None