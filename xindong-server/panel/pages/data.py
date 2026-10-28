from __future__ import annotations
import flet as ft
from panel.api import Api
from panel.widgets import paged_table, val, short


def render_data(content: ft.Column, api: Api, kind: str):
    content.controls.clear()

    def refresh():
        render_data(content, api, kind)

    if kind == "users":
        f_nick = ft.TextField(label="昵称", width=160, dense=True, height=42)
        f_tier = ft.Dropdown(label="段位", width=120, dense=True, height=42, options=[
            ft.dropdown.Option(k) for k in ["青铜", "白银", "黄金", "铂金", "钻石", "王者"]])
        f_online = ft.Dropdown(label="状态", width=100, dense=True, height=42, options=[
            ft.dropdown.Option("1", "在线"), ft.dropdown.Option("0", "离线")])

        def fetch(page, ps):
            return api.get("/api/v1/admin/users", page=page, page_size=ps,
                           nickname=f_nick.value, tier_name=f_tier.value, online=f_online.value)

        def row_map(it):
            revoke = ft.FilledButton("吊销", on_click=lambda e, d=it["device_id"]: (api.post(f"/api/v1/admin/revoke/{d}"), refresh()))
            dele = ft.FilledButton("注销", on_click=lambda e, d=it["device_id"]: (api.post(f"/api/v1/admin/delete_user/{d}"), refresh()))
            return [short(it.get("nickname")), short(it.get("device_id"), 12), it.get("tier") or "-",
                    f"{it.get('tier_score') or 0:.1f}", "在线" if it.get("online") else "离线",
                    short(it.get("last_active"), 19), ft.Row([revoke, dele])]
        content.controls.append(paged_table("用户管理", ["昵称", "设备ID", "段位", "综合分", "状态", "最近活跃", "操作"],
                                            fetch, row_map, filters=[f_nick, f_tier, f_online]))

    elif kind == "ledger":
        f_dev = ft.TextField(label="设备ID", width=200, dense=True, height=42)
        f_from = ft.TextField(label="起(YYYY-MM-DD)", width=150, dense=True, height=42)
        f_to = ft.TextField(label="止(YYYY-MM-DD)", width=150, dense=True, height=42)

        def fetch(page, ps):
            return api.get("/api/v1/admin/ledger", device_id=f_dev.value or None,
                           frm=f_from.value or None, to=f_to.value or None, page=page, page_size=ps)

        def row_map(it):
            return [short(it.get("device_id"), 10), it.get("date"), f"{val(it,'normal_earned',default=0):.2f}",
                    f"{val(it,'slacking_earned',default=0):.2f}", f"{val(it,'overtime_loss',default=0):.2f}",
                    f"{val(it,'worked_seconds',default=0)/3600:.1f}h", f"{val(it,'overtime_seconds',default=0)/3600:.1f}h",
                    f"{val(it,'slacking_seconds',default=0)/60:.0f}m",
                    "是" if it.get("is_off_duty") else "否", "是" if it.get("auto_filled") else "否"]
        content.controls.append(paged_table("账本记录管理",
                                            ["用户", "日期", "已赚", "摸鱼赚", "加班亏损", "有效工时", "加班", "摸鱼", "下班", "自动"],
                                            fetch, row_map, filters=[f_dev, f_from, f_to]))

    elif kind == "configs":
        f_dev = ft.TextField(label="设备ID", width=200, dense=True, height=42)

        def fetch(page, ps):
            return api.get("/api/v1/admin/configs", device_id=f_dev.value or None, page=page, page_size=ps)

        def row_map(it):
            c = it.get("config") or {}
            return [short(it.get("device_id"), 10), it.get("nickname") or "-", c.get("SalaryMode", "-"),
                    str(c.get("SalaryAmount", "-")), c.get("RestSystem", "-"), short(it.get("updated_at"), 19)]
        content.controls.append(paged_table("配置管理", ["用户", "昵称", "薪资模式", "金额", "休息制度", "更新时间"],
                                            fetch, row_map, filters=[f_dev]))

    elif kind == "snapshots":
        f_dev = ft.TextField(label="设备ID", width=200, dense=True, height=42)
        f_from = ft.TextField(label="起", width=150, dense=True, height=42)
        f_to = ft.TextField(label="止", width=150, dense=True, height=42)

        def fetch(page, ps):
            return api.get("/api/v1/admin/snapshots", device_id=f_dev.value or None,
                           frm=f_from.value or None, to=f_to.value or None, page=page, page_size=ps)

        def row_map(it):
            return [it.get("id"), short(it.get("device_id"), 10), short(it.get("reported_at"), 19), it.get("event_type") or "-"]
        content.controls.append(paged_table("快照管理", ["ID", "用户", "上报时间", "事件"],
                                            fetch, row_map, filters=[f_dev, f_from, f_to]))

    elif kind == "friends":
        f_dev = ft.TextField(label="设备ID", width=200, dense=True, height=42)

        def fetch(page, ps):
            return api.get("/api/v1/admin/friends", device_id=f_dev.value or None, page=page, page_size=ps)

        def row_map(it):
            rm = ft.FilledButton("解除", on_click=lambda e, a=it["device_id"], b=it["friend_id"]: (api.delete(f"/api/v1/admin/friends/{a}/{b}"), refresh()))
            return [short(it.get("device_id"), 10), it.get("a_nick") or "-", short(it.get("friend_id"), 10),
                    it.get("b_nick") or "-", short(it.get("created_at"), 19), rm]
        content.controls.append(paged_table("好友关系管理", ["A", "A昵称", "B", "B昵称", "建立时间", "操作"],
                                            fetch, row_map, filters=[f_dev]))

    elif kind == "invitations":
        def fetch(page, ps):
            return api.get("/api/v1/admin/invitations", page=page, page_size=ps)

        def row_map(it):
            return [it.get("code"), short(it.get("issuer"), 10), it.get("nickname") or "-",
                    short(it.get("created_at"), 19), short(it.get("expires_at"), 19), "已用" if it.get("used") else "未用"]
        content.controls.append(paged_table("邀请码管理", ["码", "签发者", "昵称", "创建", "过期", "状态"],
                                            fetch, row_map))

    elif kind == "webhooks":
        def fetch(page, ps):
            return api.get("/api/v1/admin/webhooks", page=page, page_size=ps)

        def row_map(it):
            dele = ft.FilledButton("删除", on_click=lambda e, i=it["id"]: (api.delete(f"/api/v1/admin/webhooks/{i}"), refresh()))
            return [it.get("id"), short(it.get("url"), 40), it.get("events"), short(it.get("created_at"), 19), dele]
        content.controls.append(paged_table("Webhook管理", ["ID", "URL", "事件", "创建", "操作"],
                                            fetch, row_map))
    elif kind == "logs":
        f_action = ft.TextField(label="操作类型", width=160, dense=True, height=42)

        def fetch(page, ps):
            return api.get("/api/v1/admin/logs", action=f_action.value or None, page=page, page_size=ps)

        def row_map(it):
            return [short(it.get("ts"), 19), it.get("action") or "-", it.get("target") or "-", it.get("detail") or "-"]
        content.controls.append(paged_table("管理操作日志", ["时间", "操作", "目标", "详情"],
                                            fetch, row_map, filters=[f_action]))