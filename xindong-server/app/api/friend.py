from __future__ import annotations
from fastapi import APIRouter, Header, HTTPException, status
from app import db
from app.auth import gen_invite_code, require_token
from app.models import InviteRequest, AcceptInviteRequest
from app.timeutil import now, timedelta

router = APIRouter(prefix="/api/v1/friend", tags=["friend"])


@router.post("/invite")
def invite(_: InviteRequest, authorization: str = Header(...)):
    device_id = require_token(authorization)
    ts = now()
    code = gen_invite_code()
    db.execute(
        "INSERT INTO invitations(code, issuer, created_at, expires_at) VALUES(?, ?, ?, ?)",
        (code, device_id, ts.isoformat(), (ts + __import__("datetime").timedelta(minutes=10)).isoformat()),
    )
    return {"code": code, "expires_in": 600}


@router.post("/accept")
def accept(req: AcceptInviteRequest, authorization: str = Header(...)):
    device_id = require_token(authorization)
    row = db.query_one("SELECT * FROM invitations WHERE code=?", (req.code.strip(),))
    if not row:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "code not found")
    if row["used"]:
        raise HTTPException(status.HTTP_410_GONE, "code used")
    if now().isoformat() > row["expires_at"]:
        raise HTTPException(status.HTTP_410_GONE, "code expired")
    issuer = row["issuer"]
    if issuer == device_id:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "cannot accept own code")
    ts = now().isoformat()
    with db.tx() as conn:
        conn.execute("UPDATE invitations SET used=1 WHERE code=?", (req.code.strip(),))
        conn.execute("INSERT OR IGNORE INTO friends(device_id, friend_id, created_at) VALUES(?, ?, ?)",
                     (device_id, issuer, ts))
        conn.execute("INSERT OR IGNORE INTO friends(device_id, friend_id, created_at) VALUES(?, ?, ?)",
                     (issuer, device_id, ts))
    return {"ok": True, "friend_id": issuer}


@router.get("/list")
def list_friends(authorization: str = Header(...)):
    device_id = require_token(authorization)
    rows = db.query(
        """SELECT f.friend_id, u.nickname FROM friends f JOIN users u ON u.device_id=f.friend_id
           WHERE f.device_id=?""",
        (device_id,),
    )
    return {"friends": [dict(r) for r in rows]}


@router.delete("/{friend_id}")
def remove(friend_id: str, authorization: str = Header(...)):
    device_id = require_token(authorization)
    db.execute("DELETE FROM friends WHERE (device_id=? AND friend_id=?) OR (device_id=? AND friend_id=?)",
               (device_id, friend_id, friend_id, device_id))
    return {"ok": True}