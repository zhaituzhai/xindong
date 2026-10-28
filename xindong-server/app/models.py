from __future__ import annotations
from typing import Any, Optional
from pydantic import BaseModel, Field


class ReportPayload(BaseModel):
    device_id: str
    client_version: Optional[str] = None
    client_tz_offset: Optional[int] = None
    reported_at: str
    config: dict[str, Any] = Field(default_factory=dict)
    ledger: dict[str, Any] = Field(default_factory=dict)
    event_type: str = "report"


class HeartbeatPayload(BaseModel):
    config: dict[str, Any] = Field(default_factory=dict)
    ledger: dict[str, Any] = Field(default_factory=dict)
    reported_at: str


class NicknameRequest(BaseModel):
    nickname: str


class LogoutRequest(BaseModel):
    confirm: bool


class PkMatchRequest(BaseModel):
    mode: str = "random"
    friend_id: Optional[str] = None
    anonymous: bool = False


class InviteRequest(BaseModel):
    pass


class AcceptInviteRequest(BaseModel):
    code: str


class WebhookRegisterRequest(BaseModel):
    url: str
    events: list[str]
    secret: Optional[str] = None


class TokenResponse(BaseModel):
    device_id: str
    token: str
    nickname: Optional[str] = None


class LeaderboardEntry(BaseModel):
    rank: int
    nickname: Optional[str]
    is_me: bool = False
    diff_to_top: Optional[float] = None
    diff_to_prev: Optional[float] = None
    absolute: Optional[float] = None
    is_money: bool = True


class LeaderboardResponse(BaseModel):
    scope: str
    metric: str
    date: str
    total: int
    entries: list[LeaderboardEntry]


def normalize_ledger_records(ledger: dict[str, Any]) -> list[dict[str, Any]]:
    if not ledger:
        return []
    return list(ledger.get("Records") or [])


def get_record_date(rec: dict[str, Any]) -> str:
    return str(rec.get("Date") or "")