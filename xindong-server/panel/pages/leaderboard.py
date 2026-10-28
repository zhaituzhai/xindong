from __future__ import annotations
import flet as ft
from panel.api import Api
from panel.widgets import paged_table, val, short

POSITIVE = [
    ("normal_earned", "今日已赚"), ("net_earned", "净赚"), ("slacking_earned", "摸鱼赚"),
    ("worked_seconds", "有效工时"), ("total_seconds", "总工时"),
    ("ontime_days", "按时下班天数"), ("full_attendance", "全勤天数"),
]
NEGATIVE = [
    ("overtime_loss", "义务加班亏损"), ("emergency_call_loss", "夺命Call亏损"), ("rounding_loss", "抹零损失"),
    ("depreciation_pct", "身价贬值"), ("slacking_seconds", "摸鱼时长"), ("slacking_count", "摸鱼次数"),
    ("single_slack_max", "单次最长摸鱼"), ("emergency_call_count", "夺命Call次数"), ("poorest", "穷鬼榜"),
]


def render_leaderboard(content: ft.Column, api: Api, scope: str, direction: str):
    content.controls.clear()
    title = f"{'日榜' if scope == 'daily' else '周榜'}·{'正向' if direction == 'positive' else '反向'}"
    metrics = POSITIVE if direction == "positive" else NEGATIVE

    f_metric = ft.Dropdown(label="指标", width=200, dense=True, height=42,
                           options=[ft.dropdown.Option(k, v) for k, v in metrics], value=metrics[0][0])
    f_date = ft.TextField(label="日期(YYYY-MM-DD，留空取当天)", width=220, dense=True, height=42)

    table = ft.DataTable(columns=[ft.DataColumn(ft.Text(c)) for c in ["排名", "昵称", "与榜首差", "与上名差", "绝对值(仅自己)"]], rows=[])
    info = ft.Text("")

    def load():
        data = api.get(f"/api/v1/leaderboard/{scope}", metric=f_metric.value, date=f_date.value or None, limit=100)
        if "error" in data:
            info.value = f"错误: {data['error']}"
            table.rows = []
            return
        entries = data.get("entries", [])
        table.rows = []
        for e in entries:
            is_me = e.get("is_me")
            row = ft.DataRow(
                cells=[ft.DataCell(ft.Text(str(e["rank"]))),
                       ft.DataCell(ft.Text(e.get("nickname") or "匿名")),
                       ft.DataCell(ft.Text(f"{val(e,'diff_to_top',default=0):.2f}")),
                       ft.DataCell(ft.Text(f"{val(e,'diff_to_prev',default=0):.2f}")),
                       ft.DataCell(ft.Text(f"{val(e,'absolute',default='-')}" if e.get("absolute") is not None else "-"))],
                color=ft.Colors.BLUE_100 if is_me else None,
            )
            table.rows.append(row)
        info.value = f"指标: {data.get('metric_label')}　共 {data.get('total')} 人　周期: {data.get('period')}"

    f_metric.on_select = lambda e: load()
    f_date.on_change = lambda e: load()
    load()
    content.controls.append(ft.Text(title, size=24, weight=ft.FontWeight.BOLD))
    content.controls.append(ft.Row([f_metric, f_date]))
    content.controls.append(info)
    content.controls.append(ft.Column([table], scroll=ft.ScrollMode.AUTO))