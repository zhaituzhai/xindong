from __future__ import annotations
import httpx

API = "http://127.0.0.1:8770"
ADMIN_TOKEN = "change-me-admin-token"


class Api:
    def __init__(self, base: str = API, token: str = ADMIN_TOKEN):
        self.base = base.rstrip("/")
        self.token = token

    def _h(self) -> dict:
        return {"X-Admin-Token": self.token}

    def get(self, path: str, **params) -> dict:
        q = {k: v for k, v in params.items() if v is not None}
        r = httpx.get(f"{self.base}{path}", params=q, headers=self._h(), timeout=15.0)
        return r.json() if r.status_code == 200 else {"error": f"{r.status_code}: {r.text}"}

    def post(self, path: str) -> dict:
        r = httpx.post(f"{self.base}{path}", headers=self._h(), timeout=15.0)
        return r.json() if r.status_code == 200 else {"error": f"{r.status_code}: {r.text}"}

    def delete(self, path: str) -> dict:
        r = httpx.delete(f"{self.base}{path}", headers=self._h(), timeout=15.0)
        return r.json() if r.status_code == 200 else {"error": f"{r.status_code}: {r.text}"}