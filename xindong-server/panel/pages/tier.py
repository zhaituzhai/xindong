from __future__ import annotations
import flet as ft
from panel.api import Api
from panel.widgets import paged_table, val, short


def render_tier(content: ft.Column, api: Api, sub: str):
    content.controls.clear()
    if sub == "current":
        content.controls.append(ft.Text("段位管理 · 当前分布", size=24, weight=ft.FontWeight.BOLD))
        data = api.get("/api/v1/tier/distribution")
        dist = data.get("distribution") or {}
        names = ["青铜", "白银", "黄金", "铂金", "钻石", "王者"]
        if not dist:
            content.controls.append(ft.Text("暂无段位数据（需周一结算或手动结算）"))
        else:
            row = ft.Row(wrap=True)
            list_area = ft.Column()

            def load_tier(tier_name: str):
                list_area.controls.clear()
                list_area.controls.append(ft.Text(f"{tier_name} 段位用户", size=16, weight=ft.FontWeight.BOLD))

                def fetch(page, ps):
                    return api.get("/api/v1/admin/users", tier_name=tier_name, page=page, page_size=ps)

                def row_map(it):
                    return [short(it.get("nickname")), short(it.get("device_id"), 12),
                            it.get("tier") or "-", f"{val(it,'tier_score',default=0):.1f}",
                            "在线" if it.get("online") else "离线", short(it.get("last_active"), 19)]
                list_area.controls.append(paged_table(
                    f"{tier_name} 用户列表", ["昵称", "设备ID", "段位", "综合分", "状态", "最近活跃"],
                    fetch, row_map))
                try:
                    list_area.update()
                except Exception:
                    pass

            for n in names:
                card = ft.Container(
                    ft.Column([ft.Text(n, size=14, color=ft.Colors.GREY_600), ft.Text(str(dist.get(n, 0)), size=22, weight=ft.FontWeight.BOLD)]),
                    padding=15, border_radius=8, bgcolor=ft.Colors.BLUE_50, width=120, ink=True)
                card.on_click = lambda e, tn=n: load_tier(tn)
                row.controls.append(card)
            content.controls.append(row)
            content.controls.append(list_area)
        content.controls.append(ft.FilledButton("立即结算本周段位",
            on_click=lambda e: (api.post("/api/v1/admin/settle_week"), render_tier(content, api, "current"), content.update())))
    elif sub == "history":
        f_week = ft.TextField(label="周(YYYY-MM-DD，留空全部)", width=220, dense=True, height=42)

        def fetch(page, ps):
            return api.get("/api/v1/admin/ranks", week=f_week.value or None, page=page, page_size=ps)

        def row_map(it):
            return [it.get("week"), short(it.get("device_id"), 10), it.get("nickname") or "-",
                    it.get("tier") or "-", it.get("prev_tier") or "-", f"{val(it,'score',default=0):.1f}",
                    short(it.get("settled_at"), 19)]
        content.controls.append(paged_table("段位历史结算", ["周", "用户", "昵称", "段位", "上周段位", "综合分", "结算时间"],
                                            fetch, row_map, filters=[f_week]))