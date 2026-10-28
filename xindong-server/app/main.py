from __future__ import annotations
import logging
from contextlib import asynccontextmanager
from fastapi import FastAPI
from app.config import server_cfg, storage_cfg
from app.db import init_db
from app.jobs import scheduler

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")


@asynccontextmanager
async def lifespan(app: FastAPI):
    init_db()
    sched = scheduler.start()
    yield
    scheduler.shutdown()


def create_app() -> FastAPI:
    app = FastAPI(title="XinDong-Server", version="1.0.0", lifespan=lifespan)

    from app.api import report, leaderboard, pk, friend, export, webhook, admin
    app.include_router(report.router)
    app.include_router(leaderboard.router)
    app.include_router(pk.router)
    app.include_router(friend.router)
    app.include_router(export.router)
    app.include_router(webhook.router)
    app.include_router(admin.router)

    @app.get("/health")
    def health():
        return {"ok": True}

    @app.get("/")
    def root():
        return {"service": "xindong-server", "docs": "/docs"}

    return app


app = create_app()


if __name__ == "__main__":
    import uvicorn
    cfg = server_cfg()
    uvicorn.run("app.main:app", host=cfg["host"], port=int(cfg["port"]), reload=False)