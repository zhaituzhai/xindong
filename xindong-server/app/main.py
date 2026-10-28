from __future__ import annotations
import logging
from logging.handlers import TimedRotatingFileHandler
from contextlib import asynccontextmanager
from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse
from app.config import server_cfg, storage_cfg
from app.db import init_db, run_dir
from app.jobs import scheduler


def _setup_logging():
    log_dir = run_dir() / "logs"
    log_dir.mkdir(parents=True, exist_ok=True)
    keep = storage_cfg().get("log_keep_days", 30)
    fmt = logging.Formatter("%(asctime)s %(levelname)s %(name)s %(message)s")
    fh = TimedRotatingFileHandler(str(log_dir / "server.log"), when="midnight", backupCount=keep, encoding="utf-8")
    fh.setFormatter(fmt)
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s", handlers=[fh, logging.StreamHandler()])


_setup_logging()
logger = logging.getLogger("xindong.server")


@asynccontextmanager
async def lifespan(app: FastAPI):
    init_db()
    logger.info("server starting, run_dir=%s", run_dir())
    sched = scheduler.start()
    yield
    scheduler.shutdown()
    logger.info("server stopped")


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

    @app.middleware("http")
    async def access_log(request: Request, call_next):
        logger.info("%s %s", request.method, request.url.path)
        try:
            resp = await call_next(request)
        except Exception as ex:
            logger.exception("request error: %s", ex)
            return JSONResponse({"error": "internal error"}, status_code=500)
        if resp.status_code >= 400:
            logger.warning("%s %s -> %s", request.method, request.url.path, resp.status_code)
        return resp

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