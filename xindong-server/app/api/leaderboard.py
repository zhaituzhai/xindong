from __future__ import annotations
from fastapi import APIRouter, Header, Query
from app.auth import require_token, get_device_id_optional
from app.services import rank

router = APIRouter(prefix="/api/v1/leaderboard", tags=["leaderboard"])


@router.get("/daily")
def daily(metric: str = Query("normal_earned"), limit: int = Query(50, le=500),
          date: str | None = Query(None), authorization: str | None = Header(None)):
    me = get_device_id_optional(authorization)
    return rank.build_leaderboard("daily", metric, date, me, limit)


@router.get("/weekly")
def weekly(metric: str = Query("normal_earned"), limit: int = Query(50, le=500),
           date: str | None = Query(None), authorization: str | None = Header(None)):
    me = get_device_id_optional(authorization)
    return rank.build_leaderboard("weekly", metric, date, me, limit)


@router.get("/me")
def me(scope: str = Query("daily"), date: str | None = Query(None),
       authorization: str = Header(...)):
    device_id = require_token(authorization)
    if scope not in ("daily", "weekly"):
        scope = "daily"
    return rank.my_position(scope, date, device_id)