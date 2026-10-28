from __future__ import annotations
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import flet as ft
import flet.app as ft_app
from panel.api import Api
from panel.pages import overview, data, leaderboard, tier, challenge, pk

MENU = [
    ("overview", "概览", ft.Icons.DASHBOARD, None),
    ("data", "数据管理", ft.Icons.STORAGE, [
        ("data:users", "用户管理"), ("data:ledger", "账本记录"), ("data:configs", "配置管理"),
        ("data:snapshots", "快照管理"), ("data:friends", "好友关系"), ("data:invitations", "邀请码"),
        ("data:webhooks", "Webhook管理"), ("data:logs", "操作日志"),
    ]),
    ("lb", "榜单管理", ft.Icons.LIST_ALT, [
        ("lb:daily:positive", "日榜·正向"), ("lb:daily:negative", "日榜·反向"),
        ("lb:weekly:positive", "周榜·正向"), ("lb:weekly:negative", "周榜·反向"),
    ]),
    ("tier", "段位管理", ft.Icons.EMOJI_EVENTS, [
        ("tier:current", "当前分布"), ("tier:history", "历史结算"),
    ]),
    ("challenge", "挑战管理", ft.Icons.EXTENSION, [
        ("challenge:today", "今日挑战"), ("challenge:history", "历史挑战"), ("challenge:achieved", "达成榜"),
    ]),
    ("pk", "PK管理", ft.Icons.COMPARE_ARROWS, [
        ("pk:running", "进行中"), ("pk:history", "历史对局"), ("pk:white_collar", "白嫖榜"),
    ]),
]


def main(page: ft.Page):
    page.title = "薪动后台管理面板"
    try:
        page.window.width = 1280
        page.window.height = 820
    except Exception:
        pass
    page.padding = 0

    api = Api()
    state = {"expanded": set(), "selected": "overview"}

    content = ft.Column(scroll=ft.ScrollMode.AUTO, expand=True)
    nav_col = ft.Column(scroll=ft.ScrollMode.AUTO, expand=True)

    def dispatch(key: str):
        content.controls.clear()
        try:
            if key == "overview":
                overview.render(content, api)
            elif key.startswith("data:"):
                data.render_data(content, api, key.split(":", 1)[1])
            elif key.startswith("lb:"):
                _, scope, direction = key.split(":")
                leaderboard.render_leaderboard(content, api, scope, direction)
            elif key.startswith("tier:"):
                tier.render_tier(content, api, key.split(":", 1)[1])
            elif key.startswith("challenge:"):
                challenge.render_challenge(content, api, key.split(":", 1)[1])
            elif key.startswith("pk:"):
                pk.render_pk(content, api, key.split(":", 1)[1])
        except Exception as ex:
            content.controls.clear()
            content.controls.append(ft.Text(f"渲染异常: {ex}", color=ft.Colors.RED))
        content.update()

    def render_nav():
        nav_col.controls.clear()
        for top_key, label, icon, children in MENU:
            is_sel = state["selected"] == top_key
            is_exp = top_key in state["expanded"]
            row = ft.Row([
                ft.Icon(icon, color=ft.Colors.BLUE_700 if is_sel else ft.Colors.GREY_700),
                ft.Text(label, weight=ft.FontWeight.W_600 if is_sel else ft.FontWeight.NORMAL,
                        color=ft.Colors.BLUE_700 if is_sel else ft.Colors.BLACK),
            ])
            if children:
                row.controls.append(ft.Icon(ft.Icons.EXPAND_MORE if is_exp else ft.Icons.CHEVRON_RIGHT, size=18))

            top_btn = ft.Container(row, padding=ft.Padding(left=10, top=10, right=12, bottom=12), ink=True,
                                   bgcolor=ft.Colors.BLUE_50 if is_sel else None, border_radius=6)

            def on_top(e, k=top_key, has_children=bool(children)):
                if has_children:
                    if k in state["expanded"]:
                        state["expanded"].discard(k)
                    else:
                        state["expanded"].add(k)
                    render_nav()
                else:
                    state["selected"] = k
                    render_nav()
                    dispatch(k)

            top_btn.on_click = on_top
            nav_col.controls.append(top_btn)

            if children and is_exp:
                for ck, clabel in children:
                    csel = state["selected"] == ck
                    crow = ft.Container(
                        ft.Row([ft.Icon(ft.Icons.SUBDIRECTORY_ARROW_RIGHT, size=14, color=ft.Colors.GREY_500),
                                ft.Text(clabel, color=ft.Colors.BLUE_700 if csel else ft.Colors.BLACK)]),
                        padding=ft.Padding(left=6, top=6, right=12, bottom=12), margin=ft.Margin(left=18, top=0, right=0, bottom=0), ink=True,
                        bgcolor=ft.Colors.BLUE_100 if csel else None, border_radius=6)

                    def on_child(e, k=ck):
                        state["selected"] = k
                        render_nav()
                        dispatch(k)

                    crow.on_click = on_child
                    nav_col.controls.append(crow)
        nav_col.update()

    header = ft.Container(
        ft.Row([ft.Icon(ft.Icons.QUERY_STATS, color=ft.Colors.WHITE), ft.Text("薪动后台管理面板", color=ft.Colors.WHITE, size=18, weight=ft.FontWeight.BOLD)]),
        bgcolor=ft.Colors.BLUE_700, padding=14,
    )
    left = ft.Container(
        ft.Column([ft.Text("导航", size=14, color=ft.Colors.GREY_500, weight=ft.FontWeight.W_600), nav_col]),
        width=240, padding=12, bgcolor=ft.Colors.GREY_100, border=ft.Border(top=ft.BorderSide(1, ft.Colors.GREY_300), bottom=ft.BorderSide(1, ft.Colors.GREY_300), left=ft.BorderSide(1, ft.Colors.GREY_300), right=ft.BorderSide(1, ft.Colors.GREY_300)),
    )
    right = ft.Container(content, expand=True, padding=10)

    page.add(ft.Column([header, ft.Row([left, right], expand=True)], expand=True))
    render_nav()
    dispatch("overview")


if __name__ == "__main__":
    ft_app.run(main)