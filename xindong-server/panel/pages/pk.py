from __future__ import annotations
import flet as ft
from panel.api import Api
from panel.widgets import paged_table, val, short


def render_pk(content: ft.Column, api: Api, sub: str):
    content.controls.clear()
    if sub == "running":
        content.controls.append(ft.Text("PK管理 · 进行中", size=24, weight=ft.FontWeight.BOLD))
        data = api.get("/api/v1/admin/pk/running")
        sessions = data.get("sessions") or []
        if not sessions:
            content.controls.append(ft.Text("暂无进行中对局"))
            return
        table = ft.DataTable(columns=[ft.DataColumn(ft.Text(c)) for c in ["对局ID", "A", "B", "模式", "匿名", "开始时间"]], rows=[])
        for s in sessions:
            table.rows.append(ft.DataRow(cells=[
                ft.DataCell(ft.Text(short(s.get("session_id"), 8))),
                ft.DataCell(ft.Text(short(s.get("a_id"), 8))),
                ft.DataCell(ft.Text(short(s.get("b_id"), 8))),
                ft.DataCell(ft.Text(s.get("mode") or "-")),
                ft.DataCell(ft.Text("是" if s.get("anonymous") else "否")),
                ft.DataCell(ft.Text(short(s.get("started_at"), 19))),
            ]))
        content.controls.append(table)
    elif sub == "history":
        f_status = ft.Dropdown(label="状态", width=120, dense=True, height=42, options=[
            ft.dropdown.Option("running", "进行中"), ft.dropdown.Option("settled", "已结算")])

        def fetch(page, ps):
            return api.get("/api/v1/admin/pk/list", status=f_status.value or None, page=page, page_size=ps)

        def row_map(it):
            return [short(it.get("session_id"), 8), it.get("a_nick") or short(it.get("a_id"), 8),
                    it.get("b_nick") or short(it.get("b_id"), 8), it.get("mode") or "-",
                    it.get("status") or "-", it.get("winner") or "-",
                    short(it.get("started_at"), 19), short(it.get("ended_at"), 19)]
        content.controls.append(paged_table("PK历史对局", ["对局ID", "A", "B", "模式", "状态", "胜者", "开始", "结束"],
                                            fetch, row_map, filters=[f_status]))
    elif sub == "white_collar":
        content.controls.append(ft.Text("PK管理 · 老板白嫖榜", size=24, weight=ft.FontWeight.BOLD))
        data = api.get("/api/v1/pk/white_collar", limit=100)
        if "error" in data:
            content.controls.append(ft.Text(f"错误: {data['error']}", color=ft.Colors.RED))
            return
        entries = data.get("entries") or []
        table = ft.DataTable(columns=[ft.DataColumn(ft.Text(c)) for c in ["排名", "昵称", "与榜首差"]], rows=[])
        for e in entries:
            table.rows.append(ft.DataRow(cells=[
                ft.DataCell(ft.Text(str(e.get("rank")))),
                ft.DataCell(ft.Text(e.get("nickname") or "匿名")),
                ft.DataCell(ft.Text(f"{val(e,'diff_to_top',default=0):.2f}")),
            ]))
        content.controls.append(ft.Text(f"周期: {data.get('period')}"))
        content.controls.append(table)