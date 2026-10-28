from __future__ import annotations
import flet as ft
from panel.api import Api
from panel.widgets import paged_table, val, short


def render_challenge(content: ft.Column, api: Api, sub: str):
    content.controls.clear()
    if sub == "today":
        content.controls.append(ft.Text("挑战管理 · 今日挑战", size=24, weight=ft.FontWeight.BOLD))
        data = api.get("/api/v1/challenge/today")
        if "error" in data:
            content.controls.append(ft.Text(f"错误: {data['error']}", color=ft.Colors.RED))
            return
        ch = data.get("challenge") or {}
        content.controls.append(ft.Text(f"日期: {ch.get('day')}"))
        content.controls.append(ft.Text(f"类型: {ch.get('kind')}"))
        content.controls.append(ft.Text(f"描述: {ch.get('description')}"))
        content.controls.append(ft.Text("达成榜:", size=16, weight=ft.FontWeight.BOLD))
        for a in (data.get("achieved") or []):
            content.controls.append(ft.Text(f"  {a.get('nickname') or short(a.get('device_id'),8)} @ {short(a.get('achieved_at'),19)}"))
        if not (data.get("achieved") or []):
            content.controls.append(ft.Text("  暂无达成"))
    elif sub == "history":
        f_from = ft.TextField(label="起", width=150, dense=True, height=42)
        f_to = ft.TextField(label="止", width=150, dense=True, height=42)

        def fetch(page, ps):
            return api.get("/api/v1/admin/challenges", frm=f_from.value or None, to=f_to.value or None, page=page, page_size=ps)

        def row_map(it):
            return [it.get("day"), it.get("kind"), str(it.get("params")), short(it.get("created_at"), 19)]
        content.controls.append(paged_table("历史挑战", ["日期", "类型", "参数", "创建时间"],
                                            fetch, row_map, filters=[f_from, f_to]))
    elif sub == "achieved":
        f_day = ft.TextField(label="日期(YYYY-MM-DD)", width=180, dense=True, height=42)
        table = ft.DataTable(columns=[ft.DataColumn(ft.Text(c)) for c in ["排名", "昵称", "达成时间"]], rows=[])
        info = ft.Text("")

        def load():
            day = f_day.value or ""
            if not day:
                info.value = "请输入日期"
                table.rows = []
                return
            data = api.get(f"/api/v1/admin/challenges/{day}/achieved")
            if "error" in data:
                info.value = f"错误: {data['error']}"
                table.rows = []
                return
            ach = data.get("achieved") or []
            table.rows = [ft.DataRow(cells=[ft.DataCell(ft.Text(str(i + 1))),
                                            ft.DataCell(ft.Text(a.get("nickname") or short(a.get("device_id"), 8))),
                                            ft.DataCell(ft.Text(short(a.get("achieved_at"), 19)))]) for i, a in enumerate(ach)]
            info.value = f"共 {len(ach)} 人达成"
        f_day.on_change = lambda e: load()
        load()
        content.controls.append(ft.Text("挑战管理 · 达成榜", size=24, weight=ft.FontWeight.BOLD))
        content.controls.append(ft.Row([f_day]))
        content.controls.append(info)
        content.controls.append(ft.Column([table], scroll=ft.ScrollMode.AUTO))