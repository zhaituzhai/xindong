from __future__ import annotations
import flet as ft
from panel.api import Api
from panel.widgets import paged_table, val, short


def render(content: ft.Column, api: Api):
    content.controls.clear()
    content.controls.append(ft.Text("系统概览", size=24, weight=ft.FontWeight.BOLD))
    ov = api.get("/api/v1/admin/overview")
    if "error" in ov:
        content.controls.append(ft.Text(f"错误: {ov['error']}", color=ft.Colors.RED))
        return

    cards = ft.Row(wrap=True, spacing=10)
    def card(title, value):
        return ft.Container(
            ft.Column([ft.Text(title, size=12, color=ft.Colors.GREY_600), ft.Text(str(value), size=22, weight=ft.FontWeight.BOLD)]),
            padding=15, border_radius=8, bgcolor=ft.Colors.GREY_100, width=180,
        )
    cards.controls.append(card("在线用户", ov.get("online")))
    cards.controls.append(card("总用户数", ov.get("total_users")))
    cards.controls.append(card("今日上报", ov.get("today_reports")))
    cards.controls.append(card("进行中PK", ov.get("pk_running")))
    cards.controls.append(card("今日挑战达成", ov.get("challenge_achieved_today")))
    cards.controls.append(card("本周段位结算", ov.get("week_settled")))
    content.controls.append(cards)

    content.controls.append(ft.Text("段位分布", size=16, weight=ft.FontWeight.BOLD))
    tier_row = ft.Row(wrap=True)
    for k, v in (ov.get("tier_distribution") or {}).items():
        tier_row.controls.append(ft.Container(ft.Text(f"{k}: {v}"), padding=8, border_radius=6, bgcolor=ft.Colors.BLUE_50))
    content.controls.append(tier_row)

    content.controls.append(ft.Text("近 7 天上报趋势", size=16, weight=ft.FontWeight.BOLD))
    trend = ov.get("trend_7d") or []
    if trend:
        maxc = max((t["count"] for t in trend), default=1) or 1
        for t in trend:
            bar_w = int(300 * t["count"] / maxc)
            content.controls.append(ft.Row([
                ft.Text(t["date"], width=110),
                ft.Container(width=bar_w, height=16, bgcolor=ft.Colors.BLUE_400, border_radius=4),
                ft.Text(str(t["count"])),
            ]))
    else:
        content.controls.append(ft.Text("无数据"))

    content.controls.append(ft.Text("系统状态", size=16, weight=ft.FontWeight.BOLD))
    st = api.get("/api/v1/admin/system/status")
    if "error" not in st:
        size_kb = (st.get("db_size") or 0) / 1024
        content.controls.append(ft.Text(f"数据库大小: {size_kb:.1f} KB　快照总数: {st.get('snapshot_count')}"))
        for k, v in (st.get("jobs") or {}).items():
            content.controls.append(ft.Text(f"定时任务 {k}: {v}"))