from __future__ import annotations
import sys
import json
import httpx
import flet as ft
import flet.app as ft_app

API = "http://127.0.0.1:8770"
ADMIN_TOKEN = "change-me-admin-token"


def api_get(path: str, token: str | None = None) -> dict:
    headers = {"X-Admin-Token": ADMIN_TOKEN} if token == "admin" else ({"Authorization": f"Bearer {token}"} if token else {})
    r = httpx.get(f"{API}{path}", headers=headers, timeout=10.0)
    return r.json() if r.status_code == 200 else {"error": r.text}


def main(page: ft.Page):
    page.title = "薪动后台管理面板"
    page.scroll = ft.ScrollMode.AUTO

    content = ft.Column()

    def render_overview(_=None):
        data = api_get("/api/v1/admin/overview", "admin")
        content.controls.clear()
        content.controls.append(ft.Text("概览", size=24, weight=ft.FontWeight.BOLD))
        if "error" in data:
            content.controls.append(ft.Text(f"错误: {data['error']}", color=ft.Colors.RED))
        else:
            content.controls.append(ft.Text(f"在线用户: {data.get('online')}"))
            content.controls.append(ft.Text(f"总用户数: {data.get('total_users')}"))
            content.controls.append(ft.Text(f"今日上报: {data.get('today_reports')}"))
            content.controls.append(ft.Text("段位分布:"))
            for k, v in (data.get("tier_distribution") or {}).items():
                content.controls.append(ft.Text(f"  {k}: {v}"))
        page.update()

    def render_users(_=None):
        data = api_get("/api/v1/admin/users?limit=200", "admin")
        content.controls.clear()
        content.controls.append(ft.Text("用户列表", size=24, weight=ft.FontWeight.BOLD))
        users = data.get("users", []) if isinstance(data, dict) else []
        rows = []
        for u in users[:200]:
            rows.append(ft.DataRow(cells=[
                ft.DataCell(ft.Text(u.get("nickname") or "-")),
                ft.DataCell(ft.Text(u.get("device_id", "")[:8])),
                ft.DataCell(ft.Text(u.get("tier") or "-")),
                ft.DataCell(ft.Text(str(u.get("tier_score") or 0)[:8])),
                ft.DataCell(ft.Text("在线" if u.get("online") else "离线")),
                ft.DataCell(ft.Text(u.get("last_active") or "-")),
            ]))
        content.controls.append(ft.DataTable(
            columns=[ft.DataColumn(ft.Text("昵称")), ft.DataColumn(ft.Text("ID")),
                     ft.DataColumn(ft.Text("段位")), ft.DataColumn(ft.Text("综合分")),
                     ft.DataColumn(ft.Text("状态")), ft.DataColumn(ft.Text("最近活跃"))],
            rows=rows,
        ))
        page.update()

    def render_leaderboard(_=None):
        content.controls.clear()
        content.controls.append(ft.Text("日榜 - 今日已赚", size=24, weight=ft.FontWeight.BOLD))
        data = api_get("/api/v1/leaderboard/daily?metric=normal_earned&limit=50")
        rows = []
        for e in data.get("entries", []):
            rows.append(ft.DataRow(cells=[
                ft.DataCell(ft.Text(str(e["rank"]))),
                ft.DataCell(ft.Text(e.get("nickname") or "匿名")),
                ft.DataCell(ft.Text(f"{e.get('diff_to_top') or 0:.2f}")),
            ]))
        content.controls.append(ft.DataTable(
            columns=[ft.DataColumn(ft.Text("排名")), ft.DataColumn(ft.Text("昵称")),
                     ft.DataColumn(ft.Text("与榜首差"))],
            rows=rows,
        ))
        page.update()

    def render_tier(_=None):
        data = api_get("/api/v1/tier/distribution")
        content.controls.clear()
        content.controls.append(ft.Text("段位分布", size=24, weight=ft.FontWeight.BOLD))
        for k, v in (data.get("distribution") or {}).items():
            content.controls.append(ft.Text(f"{k}: {v}"))
        page.update()

    def render_challenge(_=None):
        data = api_get("/api/v1/challenge/today")
        content.controls.clear()
        content.controls.append(ft.Text("今日挑战", size=24, weight=ft.FontWeight.BOLD))
        ch = data.get("challenge") or {}
        content.controls.append(ft.Text(f"描述: {ch.get('description')}"))
        content.controls.append(ft.Text(f"类型: {ch.get('kind')}"))
        content.controls.append(ft.Text("达成榜:"))
        for a in (data.get("achieved") or []):
            content.controls.append(ft.Text(f"  {a.get('nickname') or a.get('device_id')[:8]} @ {a.get('achieved_at')}"))
        page.update()

    def render_pk(_=None):
        data = api_get("/api/v1/admin/pk/running", "admin")
        content.controls.clear()
        content.controls.append(ft.Text("进行中 PK", size=24, weight=ft.FontWeight.BOLD))
        for s in (data.get("sessions") or []):
            content.controls.append(ft.Text(f"{s['session_id'][:8]}... A={s['a_id'][:8]} B={s['b_id'][:8]} mode={s['mode']}"))
        page.update()

    nav = ft.NavigationBar(
        destinations=[
            ft.NavigationBarDestination(icon=ft.Icons.DASHBOARD, label="概览"),
            ft.NavigationBarDestination(icon=ft.Icons.PEOPLE, label="用户"),
            ft.NavigationBarDestination(icon=ft.Icons.LIST_ALT, label="日榜"),
            ft.NavigationBarDestination(icon=ft.Icons.EMOJI_EVENTS, label="段位"),
            ft.NavigationBarDestination(icon=ft.Icons.EXTENSION, label="挑战"),
            ft.NavigationBarDestination(icon=ft.Icons.COMPARE_ARROWS, label="PK"),
        ],
        on_change=lambda e: [
            render_overview, render_users, render_leaderboard,
            render_tier, render_challenge, render_pk,
        ][e.control.selected_index](),
    )
    render_overview()
    page.add(nav, content)


if __name__ == "__main__":
    ft_app.run(main)