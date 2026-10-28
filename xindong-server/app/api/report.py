from __future__ import annotations
import json
from fastapi import APIRouter, Header, Request, HTTPException, status
from app import db
from app.auth import gen_token, rate_limit, require_token
from app.models import ReportPayload, HeartbeatPayload, NicknameRequest, LogoutRequest
from app.services import snapshot, challenge, webhook_dispatch
from app.timeutil import now

router = APIRouter(prefix="/api/v1", tags=["report"])


@router.post("/report")
def report(payload: ReportPayload, request: Request, authorization: str | None = Header(None)):
    rate_limit(request, "default")
    device_id = payload.device_id
    if not device_id or len(device_id) < 8:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "invalid device_id")

    row = db.query_one("SELECT token FROM users WHERE device_id=?", (device_id,))
    is_first = not row or not row["token"]
    if not is_first:
        require_token(authorization)

    snapshot.store_report(device_id, payload.model_dump(), payload.event_type or "report")

    if is_first:
        token = gen_token()
        ts = now().isoformat()
        db.execute("UPDATE users SET token=?, token_issued_at=? WHERE device_id=?",
                   (token, ts, device_id))
    else:
        token = row["token"]

    if challenge.check_achieved(device_id):
        webhook_dispatch.dispatch("challenge.achieved", {"device_id": device_id, "day": challenge.get_daily()["day"]})

    nick_row = db.query_one("SELECT nickname FROM users WHERE device_id=?", (device_id,))
    return {"device_id": device_id, "token": token, "nickname": nick_row["nickname"] if nick_row else None}


@router.post("/heartbeat")
def heartbeat(payload: HeartbeatPayload, request: Request, authorization: str = Header(...)):
    rate_limit(request, "heartbeat")
    device_id = require_token(authorization)
    full = {
        "device_id": device_id,
        "reported_at": payload.reported_at or now().isoformat(),
        "config": payload.config,
        "ledger": payload.ledger,
        "event_type": "heartbeat",
    }
    snapshot.store_report(device_id, full, "heartbeat")
    snapshot.touch_active(device_id)
    return {"ok": True}


@router.post("/bind/nickname")
def bind_nickname(req: NicknameRequest, authorization: str = Header(...)):
    device_id = require_token(authorization)
    nick = req.nickname.strip()
    if not nick or len(nick) > 32:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "invalid nickname")
    occ = db.query_one("SELECT device_id FROM users WHERE nickname=? AND device_id!=?", (nick, device_id))
    if occ:
        raise HTTPException(status.HTTP_409_CONFLICT, "nickname taken")
    db.execute("UPDATE users SET nickname=? WHERE device_id=?", (nick, device_id))
    return {"ok": True, "nickname": nick}


@router.post("/logout")
def logout(req: LogoutRequest, authorization: str = Header(...)):
    if not req.confirm:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "confirm required")
    device_id = require_token(authorization)
    with db.tx() as conn:
        for t in ["configs", "ledger_records", "snapshots"]:
            conn.execute(f"DELETE FROM {t} WHERE device_id=?", (device_id,))
        conn.execute("DELETE FROM friends WHERE device_id=? OR friend_id=?", (device_id, device_id))
        conn.execute("DELETE FROM challenge_achieved WHERE device_id=?", (device_id,))
        conn.execute("DELETE FROM ranks WHERE device_id=?", (device_id,))
        conn.execute("DELETE FROM users WHERE device_id=?", (device_id,))
    return {"ok": True}