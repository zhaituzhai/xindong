from __future__ import annotations
import csv
import io
import json
from fastapi import APIRouter, Header, Query, Response
from app import db
from app.auth import require_token, get_device_id_optional
from app.services import rank

router = APIRouter(prefix="/api/v1/export", tags=["export"])


@router.get("/ledger")
def export_ledger(device_id: str = Query(...), frm: str = Query(...), to: str = Query(...),
                  authorization: str | None = Header(None)):
    get_device_id_optional(authorization)
    rows = db.query(
        "SELECT date, payload FROM ledger_records WHERE device_id=? AND date>=? AND date<=? ORDER BY date",
        (device_id, frm, to),
    )
    buf = io.StringIO()
    writer = csv.writer(buf)
    writer.writerow(["date", "NormalEarned", "SlackingEarned", "OvertimeLoss", "WorkedSeconds",
                     "OvertimeSeconds", "SlackingSeconds", "IsOffDuty", "AutoFilled"])
    for r in rows:
        p = json.loads(r["payload"])
        writer.writerow([r["date"], p.get("NormalEarned"), p.get("SlackingEarned"),
                         p.get("OvertimeLoss"), p.get("WorkedSeconds"), p.get("OvertimeSeconds"),
                         p.get("SlackingSeconds"), p.get("IsOffDuty"), p.get("AutoFilled")])
    return Response(content=buf.getvalue(), media_type="text/csv",
                    headers={"Content-Disposition": "attachment; filename=ledger.csv"})


@router.get("/leaderboard")
def export_leaderboard(scope: str = Query("daily"), metric: str = Query("normal_earned"),
                        date: str | None = Query(None)):
    lb = rank.build_leaderboard(scope, metric, date, None, 10000)
    buf = io.StringIO()
    writer = csv.writer(buf)
    writer.writerow(["rank", "device_id", "nickname", "diff_to_top", "diff_to_prev"])
    for e in lb["entries"]:
        writer.writerow([e["rank"], e["device_id"], e.get("nickname"), e.get("diff_to_top"), e.get("diff_to_prev")])
    return Response(content=buf.getvalue(), media_type="text/csv",
                    headers={"Content-Disposition": "attachment; filename=leaderboard.csv"})