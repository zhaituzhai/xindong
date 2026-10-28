from __future__ import annotations
from typing import Callable
import flet as ft


def paged_table(title: str, columns: list[str], fetch: Callable[[int, int], dict],
                row_map: Callable[[dict], list], page_size: int = 20,
                filters: list[ft.Control] | None = None,
                on_refresh: Callable | None = None) -> ft.Column:
    """通用分页表格组件。fetch(page, page_size) -> {total, page, page_size, items}"""
    state = {"page": 1}

    title_row = ft.Row([ft.Text(title, size=20, weight=ft.FontWeight.BOLD)], alignment=ft.MainAxisAlignment.SPACE_BETWEEN)

    filter_row = ft.Row(filters or [], wrap=True) if filters else ft.Container()

    table = ft.DataTable(
        columns=[ft.DataColumn(ft.Text(c)) for c in columns],
        rows=[],
    )
    info = ft.Text("")
    prev_btn = ft.FilledButton("上一页")
    next_btn = ft.FilledButton("下一页")
    refresh_btn = ft.FilledButton("刷新", icon=ft.Icons.REFRESH)

    def render():
        data = fetch(state["page"], page_size)
        if "error" in data:
            info.value = f"错误: {data['error']}"
            table.rows = []
            return
        total = data.get("total", 0)
        items = data.get("items", [])
        table.rows = [ft.DataRow(cells=[ft.DataCell(c if isinstance(c, ft.Control) else ft.Text(str(c))) for c in row_map(it)]) for it in items]
        start = (state["page"] - 1) * page_size + 1 if total else 0
        end = min(state["page"] * page_size, total)
        info.value = f"第 {state['page']} 页 / 共 {(total + page_size - 1) // page_size} 页　显示 {start}-{end} / 共 {total} 条"
        prev_btn.disabled = state["page"] <= 1
        next_btn.disabled = state["page"] * page_size >= total
        try:
            table.update(); info.update(); prev_btn.update(); next_btn.update()
        except Exception:
            pass

    def on_prev(_):
        if state["page"] > 1:
            state["page"] -= 1
            render()

    def on_next(_):
        state["page"] += 1
        render()

    def on_refresh_click(_):
        render()
        if on_refresh:
            on_refresh()

    prev_btn.on_click = on_prev
    next_btn.on_click = on_next
    refresh_btn.on_click = on_refresh_click

    for f in (filters or []):
        def _on_change(e):
            state["page"] = 1
            render()
        if isinstance(f, ft.TextField):
            f.on_change = _on_change
        elif isinstance(f, ft.Dropdown):
            f.on_select = _on_change

    render()
    return ft.Column([
        title_row,
        filter_row,
        ft.Row([refresh_btn, prev_btn, next_btn, info]),
        ft.Column([table], scroll=ft.ScrollMode.AUTO),
    ], scroll=ft.ScrollMode.AUTO)


def val(d: dict, *keys, default="-"):
    for k in keys:
        if k in d and d[k] is not None:
            v = d[k]
            return v
    return default


def short(s, n=8):
    if s is None:
        return "-"
    s = str(s)
    return s if len(s) <= n else s[:n] + "…"