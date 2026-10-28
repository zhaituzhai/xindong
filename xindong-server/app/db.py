from __future__ import annotations
import sqlite3
import threading
from contextlib import contextmanager
from pathlib import Path
from typing import Iterator
from app.config import storage_cfg, ROOT

_lock = threading.Lock()
_conn: sqlite3.Connection | None = None

SCHEMA = """
CREATE TABLE IF NOT EXISTS users (
    device_id TEXT PRIMARY KEY,
    nickname TEXT,
    token TEXT UNIQUE,
    token_issued_at TEXT,
    first_seen_at TEXT,
    last_active TEXT,
    online INTEGER DEFAULT 0,
    tier TEXT,
    tier_score REAL DEFAULT 0,
    client_version TEXT,
    client_tz_offset INTEGER,
    created_at TEXT
);
CREATE TABLE IF NOT EXISTS configs (
    device_id TEXT PRIMARY KEY,
    payload TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    FOREIGN KEY(device_id) REFERENCES users(device_id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS ledger_records (
    device_id TEXT NOT NULL,
    date TEXT NOT NULL,
    payload TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY(device_id, date),
    FOREIGN KEY(device_id) REFERENCES users(device_id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS snapshots (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    device_id TEXT NOT NULL,
    reported_at TEXT NOT NULL,
    event_type TEXT,
    payload TEXT NOT NULL,
    FOREIGN KEY(device_id) REFERENCES users(device_id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_snapshots_dev_time ON snapshots(device_id, reported_at);

CREATE TABLE IF NOT EXISTS friends (
    device_id TEXT NOT NULL,
    friend_id TEXT NOT NULL,
    created_at TEXT NOT NULL,
    PRIMARY KEY(device_id, friend_id)
);
CREATE TABLE IF NOT EXISTS invitations (
    code TEXT PRIMARY KEY,
    issuer TEXT NOT NULL,
    created_at TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    used INTEGER DEFAULT 0
);

CREATE TABLE IF NOT EXISTS pk_sessions (
    session_id TEXT PRIMARY KEY,
    a_id TEXT NOT NULL,
    b_id TEXT NOT NULL,
    mode TEXT NOT NULL,
    anonymous INTEGER DEFAULT 0,
    status TEXT DEFAULT 'running',
    started_at TEXT NOT NULL,
    ended_at TEXT,
    winner TEXT,
    report TEXT
);

CREATE TABLE IF NOT EXISTS challenges (
    day TEXT PRIMARY KEY,
    kind TEXT NOT NULL,
    params TEXT NOT NULL,
    created_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS challenge_achieved (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    day TEXT NOT NULL,
    device_id TEXT NOT NULL,
    achieved_at TEXT NOT NULL,
    UNIQUE(day, device_id)
);

CREATE TABLE IF NOT EXISTS ranks (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    week TEXT NOT NULL,
    device_id TEXT NOT NULL,
    tier TEXT NOT NULL,
    score REAL NOT NULL,
    prev_tier TEXT,
    settled_at TEXT NOT NULL,
    UNIQUE(week, device_id)
);

CREATE TABLE IF NOT EXISTS webhooks (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    device_id TEXT,
    url TEXT NOT NULL,
    events TEXT NOT NULL,
    secret TEXT,
    created_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS webhook_logs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    webhook_id INTEGER NOT NULL,
    event TEXT NOT NULL,
    payload TEXT NOT NULL,
    status INTEGER,
    attempts INTEGER DEFAULT 0,
    created_at TEXT NOT NULL
);
"""


def _new_conn() -> sqlite3.Connection:
    db_path = storage_cfg()["db_path"]
    p = Path(db_path)
    if not p.is_absolute():
        p = ROOT / p
    conn = sqlite3.connect(str(p), check_same_thread=False, isolation_level=None)
    conn.row_factory = sqlite3.Row
    conn.execute("PRAGMA journal_mode=WAL")
    conn.execute("PRAGMA foreign_keys=ON")
    return conn


def init_db() -> None:
    global _conn
    with _lock:
        if _conn is None:
            _conn = _new_conn()
        _conn.executescript(SCHEMA)


def get_conn() -> sqlite3.Connection:
    if _conn is None:
        init_db()
    assert _conn is not None
    return _conn


@contextmanager
def tx() -> Iterator[sqlite3.Connection]:
    conn = get_conn()
    with _lock:
        conn.execute("BEGIN IMMEDIATE")
        try:
            yield conn
            conn.execute("COMMIT")
        except Exception:
            conn.execute("ROLLBACK")
            raise


def query(sql: str, params: tuple = ()) -> list[sqlite3.Row]:
    return list(get_conn().execute(sql, params).fetchall())


def query_one(sql: str, params: tuple = ()) -> sqlite3.Row | None:
    return get_conn().execute(sql, params).fetchone()


def execute(sql: str, params: tuple = ()) -> sqlite3.Cursor:
    return get_conn().execute(sql, params)