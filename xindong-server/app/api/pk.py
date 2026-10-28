from __future__ import annotations
from fastapi import APIRouter, Header, Query
from app.auth import require_token
from app.models import PkMatchRequest
from app.services import pk_match, challenge, tier

router = APIRouter(prefix="/api/v1", tags=["pk"])


@router.post("/pk/match")
def pk_match_route(req: PkMatchRequest, authorization: str = Header(...)):
    device_id = require_token(authorization)
    return pk_match.match(device_id, req.mode, req.friend_id, req.anonymous)


@router.get("/pk/{session_id}")
def pk_detail(session_id: str, authorization: str = Header(...)):
    device_id = require_token(authorization)
    return pk_match.live_diff(session_id, device_id)


@router.post("/pk/{session_id}/end")
def pk_end(session_id: str, authorization: str = Header(...)):
    device_id = require_token(authorization)
    report = pk_match.settle(session_id, device_id)
    from app.services import webhook_dispatch
    webhook_dispatch.dispatch("pk.settled", report)
    return report


@router.get("/pk/white_collar")
def white_collar(limit: int = Query(50, le=500), date: str | None = Query(None)):
    return pk_match.white_collar_leaderboard(date, limit)


@router.get("/challenge/today")
def challenge_today():
    ch = challenge.get_daily()
    if not ch:
        ch = challenge.generate_daily()
    return {"challenge": ch, "achieved": challenge.achieved_leaderboard(ch["day"])}


@router.get("/tier/me")
def tier_me(authorization: str = Header(...)):
    device_id = require_token(authorization)
    return tier.user_tier(device_id)


@router.get("/tier/distribution")
def tier_dist():
    return {"distribution": tier.tier_distribution()}