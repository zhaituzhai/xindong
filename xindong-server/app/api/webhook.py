from __future__ import annotations
from fastapi import APIRouter, Header
from app.auth import require_token
from app.models import WebhookRegisterRequest
from app.services import webhook_dispatch

router = APIRouter(prefix="/api/v1/webhook", tags=["webhook"])


@router.post("/register")
def register(req: WebhookRegisterRequest, authorization: str = Header(...)):
    device_id = require_token(authorization)
    wid = webhook_dispatch.register(device_id, req.url, req.events, req.secret)
    return {"id": wid}


@router.get("/logs")
def logs(authorization: str = Header(...)):
    device_id = require_token(authorization)
    from app import db
    rows = db.query(
        """SELECT wl.* FROM webhook_logs wl JOIN webhooks wh ON wh.id=wl.webhook_id
           WHERE wh.device_id=? ORDER BY wl.created_at DESC LIMIT 100""",
        (device_id,),
    )
    return {"logs": [dict(r) for r in rows]}