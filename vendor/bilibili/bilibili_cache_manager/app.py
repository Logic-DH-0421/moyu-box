from __future__ import annotations

import logging
import queue
import tkinter as tk
from pathlib import Path
from tkinter import messagebox, ttk
from typing import Any

from .config_store import AppConfig, app_data_dir, load_config, save_config
from .database import (
    counts,
    init_db,
    invert_selected,
    query_videos,
    reset_failed_selected,
    set_all_selected,
    set_selected,
    update_quality_for_pending,
)
from .worker import AutomationWorker


QUALITY_OPTIONS = ["最高可用", "4K", "1080P高码率", "1080P", "720P", "480P"]
STATUS_OPTIONS = ["全部", "未缓存", "已选择", "正在打开", "等待客户端", "已提交", "正在缓存", "已完成", "失败", "不支持缓存", "已暂停", "已跳过"]

COLORS = {
    "background": "#fff9fb",
    "surface": "#ffffff",
    "sidebar": "#fff3f7",
    "primary": "#fb4b7a",
    "primary_hover": "#eb3d6d",
    "primary_pressed": "#d92f60",
    "title": "#25252d",
    "body": "#565663",
    "muted": "#9a96a2",
    "border": "#f0dfe5",
    "soft_blue": "#fff0f5",
    "soft_slate": "#fff6f8",
    "success": "#24a866",
    "warning": "#7c64e8",
    "danger": "#e5484d",
    "violet": "#8b75e8",
}


def setup_logging() -> Path:
    path = app_data_dir() / "diagnostic.log"
    logging.basicConfig(
        filename=path,
        level=logging.INFO,
        format="%(asctime)s %(levelname)s %(message)s",
        encoding="utf-8",
    )
    return path


class CacheManagerApp(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        init_db()
        self.log_path = setup_logging()
        self.config_data = load_config()
        self.worker = AutomationWorker(self.config_data)
        self.visible_ids: list[int] = []
        self.operation_running = False

        self.title("B站稍后再看管理器")
        self.geometry("1600x920")
        self.minsize(1280, 760)
        self.configure(background=COLORS["background"])
        self.protocol("WM_DELETE_WINDOW", self.on_close)

        self.quality_var = tk.StringVar(value=self.config_data.quality)
        self.cache_all_var = tk.BooleanVar(value=self.config_data.cache_all)
        self.fallback_var = tk.BooleanVar(value=self.config_data.auto_fallback)
        self.keyword_var = tk.StringVar()
        self.uploader_var = tk.StringVar()
        self.status_filter_var = tk.StringVar(value="全部")
        self.view_mode_var = tk.StringVar(value="queue")
        self.min_duration_var = tk.StringVar()
        self.max_duration_var = tk.StringVar()
        self.status_var = tk.StringVar(value="准备就绪")
        self.current_var = tk.StringVar(value="当前任务：无")
        self.stats_var = tk.StringVar(value="队列 0｜已选择 0｜已完成 0｜失败 0")
        self.metric_vars = {
            "total": tk.StringVar(value="0"),
            "selected": tk.StringVar(value="0"),
            "active": tk.StringVar(value="0"),
            "completed": tk.StringVar(value="0"),
            "failed": tk.StringVar(value="0"),
        }

        self._build_style()
        self._build_ui()
        self.refresh_table()
        self.after(120, self.poll_events)
        self.after(700, self.refresh_queue)

    def _build_style(self) -> None:
        style = ttk.Style(self)
        try:
            style.theme_use("clam")
        except tk.TclError:
            pass
        font = ("Microsoft YaHei UI", 9)
        style.configure(".", font=font, background=COLORS["background"], foreground=COLORS["body"])
        style.configure("App.TFrame", background=COLORS["background"])
        style.configure("Surface.TFrame", background=COLORS["surface"])
        style.configure("Header.TLabel", background=COLORS["background"], foreground=COLORS["title"], font=("Microsoft YaHei UI", 17, "bold"))
        style.configure("Subtitle.TLabel", background=COLORS["background"], foreground=COLORS["muted"], font=("Microsoft YaHei UI", 9))
        style.configure("Section.TLabel", background=COLORS["surface"], foreground=COLORS["title"], font=("Microsoft YaHei UI", 11, "bold"))
        style.configure("Body.TLabel", background=COLORS["surface"], foreground=COLORS["body"])
        style.configure("Muted.TLabel", background=COLORS["surface"], foreground=COLORS["muted"], font=("Microsoft YaHei UI", 8))
        style.configure("Status.TLabel", background="#eefbf3", foreground="#16844e", padding=(10, 5), font=("Microsoft YaHei UI", 9, "bold"))
        style.configure(
            "Primary.TButton",
            background=COLORS["primary"],
            foreground="#ffffff",
            bordercolor=COLORS["primary"],
            lightcolor=COLORS["primary"],
            darkcolor=COLORS["primary"],
            padding=(14, 8),
            font=("Microsoft YaHei UI", 9, "bold"),
        )
        style.map(
            "Primary.TButton",
            background=[("disabled", "#f8b5c8"), ("pressed", COLORS["primary_pressed"]), ("active", COLORS["primary_hover"])],
            foreground=[("disabled", "#ffffff")],
            bordercolor=[("disabled", "#f8b5c8"), ("pressed", COLORS["primary_pressed"]), ("active", COLORS["primary_hover"])],
        )
        style.configure(
            "Secondary.TButton",
            background=COLORS["surface"],
            foreground=COLORS["body"],
            bordercolor=COLORS["border"],
            lightcolor=COLORS["surface"],
            darkcolor=COLORS["surface"],
            padding=(11, 7),
        )
        style.map(
            "Secondary.TButton",
            background=[("pressed", COLORS["soft_slate"]), ("active", COLORS["soft_blue"])],
            foreground=[("active", COLORS["primary_hover"])],
            bordercolor=[("active", "#f5b6c9")],
        )
        style.configure(
            "Danger.TButton",
            background=COLORS["surface"],
            foreground=COLORS["danger"],
            bordercolor="#fecaca",
            lightcolor=COLORS["surface"],
            darkcolor=COLORS["surface"],
            padding=(11, 7),
        )
        style.map("Danger.TButton", background=[("active", "#fef2f2")], bordercolor=[("active", "#fca5a5")])
        style.configure(
            "Tab.TButton",
            background="#ffffff",
            foreground=COLORS["body"],
            bordercolor=COLORS["border"],
            padding=(16, 7),
            font=("Microsoft YaHei UI", 9),
        )
        style.configure(
            "TabActive.TButton",
            background=COLORS["primary"],
            foreground="#ffffff",
            bordercolor=COLORS["primary"],
            padding=(16, 7),
            font=("Microsoft YaHei UI", 9, "bold"),
        )
        style.map("Tab.TButton", background=[("active", COLORS["soft_blue"])], foreground=[("active", COLORS["primary_hover"])])
        style.map("TabActive.TButton", background=[("active", COLORS["primary_hover"])])
        style.configure("TEntry", fieldbackground="#ffffff", bordercolor=COLORS["border"], padding=6)
        style.configure("TCombobox", fieldbackground="#ffffff", background="#ffffff", bordercolor=COLORS["border"], padding=5, arrowsize=13)
        style.map("TCombobox", fieldbackground=[("readonly", "#ffffff")], selectbackground=[("readonly", "#ffffff")], selectforeground=[("readonly", COLORS["title"])])
        style.configure("TCheckbutton", background=COLORS["surface"], foreground=COLORS["body"])
        style.map("TCheckbutton", background=[("active", COLORS["surface"])])
        style.configure(
            "Treeview",
            rowheight=40,
            font=font,
            background="#ffffff",
            fieldbackground="#ffffff",
            foreground=COLORS["title"],
            bordercolor=COLORS["border"],
            lightcolor=COLORS["border"],
            darkcolor=COLORS["border"],
        )
        style.map("Treeview", background=[("selected", "#fff0f5")], foreground=[("selected", "#b92255")])
        style.configure(
            "Treeview.Heading",
            background="#fff5f8",
            foreground=COLORS["body"],
            bordercolor=COLORS["border"],
            padding=(6, 8),
            font=("Microsoft YaHei UI", 9, "bold"),
        )
        style.map("Treeview.Heading", background=[("active", "#ffedf3")])
        style.configure("TScrollbar", background="#e7cdd6", troughcolor=COLORS["soft_slate"], bordercolor=COLORS["soft_slate"], arrowcolor=COLORS["body"])

    def _build_ui(self) -> None:
        shell = tk.Frame(self, background=COLORS["background"])
        shell.pack(fill="both", expand=True)
        sidebar = tk.Frame(shell, width=118, background=COLORS["sidebar"], highlightbackground=COLORS["border"], highlightthickness=1)
        sidebar.pack(side="left", fill="y")
        sidebar.pack_propagate(False)
        brand = tk.Frame(sidebar, background=COLORS["sidebar"], pady=18)
        brand.pack(fill="x")
        tk.Label(brand, text="▣", background=COLORS["primary"], foreground="#ffffff", font=("Microsoft YaHei UI", 17, "bold"), width=2, pady=5).pack()
        tk.Label(brand, text="稍后再看", background=COLORS["sidebar"], foreground=COLORS["title"], font=("Microsoft YaHei UI", 10, "bold"), pady=7).pack()
        self.sidebar_buttons: dict[str, tk.Button] = {}
        for key, icon, label in (
            ("queue", "▤", "列表管理"),
            ("downloading", "⇩", "下载管理"),
            ("completed", "◉", "弹幕管理"),
        ):
            button = tk.Button(
                sidebar,
                text=f"{icon}\n{label}",
                command=lambda value=key: self.set_view(value),
                relief="flat",
                borderwidth=0,
                background=COLORS["sidebar"],
                foreground=COLORS["body"],
                activebackground="#ffe5ee",
                activeforeground=COLORS["primary_pressed"],
                font=("Microsoft YaHei UI", 9),
                cursor="hand2",
                pady=11,
            )
            button.pack(fill="x", padx=8, pady=3)
            self.sidebar_buttons[key] = button
        tk.Label(sidebar, text="⚙\n设置中心", background=COLORS["sidebar"], foreground=COLORS["muted"], font=("Microsoft YaHei UI", 9), pady=13).pack(fill="x", padx=8, pady=(8, 0))
        tk.Label(sidebar, text="●  后台运行", background="#fffafa", foreground=COLORS["success"], font=("Microsoft YaHei UI", 8), padx=8, pady=8).pack(side="bottom", fill="x", padx=10, pady=12)

        outer = ttk.Frame(shell, padding=(18, 14, 18, 10), style="App.TFrame")
        outer.pack(side="left", fill="both", expand=True)

        header = ttk.Frame(outer, style="App.TFrame")
        header.pack(fill="x", pady=(0, 14))
        title_box = ttk.Frame(header, style="App.TFrame")
        title_box.pack(side="left")
        ttk.Label(title_box, text="B站稍后再看管理器", style="Header.TLabel").pack(anchor="w")
        ttk.Label(title_box, text="列表管理 · 下载管理 · 弹幕维护", style="Subtitle.TLabel").pack(anchor="w", pady=(2, 0))
        ttk.Label(header, textvariable=self.status_var, style="Status.TLabel").pack(side="right", padx=(14, 0), pady=5)

        settings_card = tk.Frame(outer, background=COLORS["surface"], highlightbackground=COLORS["border"], highlightthickness=1)
        settings_card.pack(fill="x", pady=(0, 12))
        settings = ttk.Frame(settings_card, padding=(16, 12), style="Surface.TFrame")
        settings.pack(fill="x")
        settings.columnconfigure(2, weight=1)
        ttk.Label(settings, text="缓存设置", style="Section.TLabel").grid(row=0, column=0, sticky="w", padx=(0, 18))
        ttk.Label(settings, text="客户端", style="Body.TLabel").grid(row=0, column=1, sticky="e")
        path_entry = ttk.Entry(settings, width=45)
        path_entry.insert(0, self.config_data.bilibili_exe)
        path_entry.configure(state="readonly")
        path_entry.grid(row=0, column=2, sticky="ew", padx=(7, 18))
        ttk.Label(settings, text="默认画质", style="Body.TLabel").grid(row=0, column=3, sticky="e")
        quality = ttk.Combobox(settings, values=QUALITY_OPTIONS, textvariable=self.quality_var, state="readonly", width=14)
        quality.grid(row=0, column=4, padx=(7, 14))
        quality.bind("<<ComboboxSelected>>", self.on_quality_change)
        ttk.Checkbutton(settings, text="缓存全部", variable=self.cache_all_var, command=self.on_cache_all_change).grid(row=0, column=5, padx=(0, 12))
        ttk.Checkbutton(settings, text="画质不可用时自动降级", variable=self.fallback_var, command=self.save_settings).grid(row=0, column=6)

        action_card = tk.Frame(outer, background=COLORS["surface"], highlightbackground=COLORS["border"], highlightthickness=1)
        action_card.pack(fill="x", pady=(0, 12))
        actions = ttk.Frame(action_card, padding=(16, 12), style="Surface.TFrame")
        actions.pack(fill="x")
        actions_top = ttk.Frame(actions, style="Surface.TFrame")
        actions_top.pack(fill="x")
        ttk.Label(actions_top, text="任务操作", style="Section.TLabel").pack(side="left", padx=(0, 16))
        self.refresh_btn = ttk.Button(actions_top, text="刷新稍后再看", command=self.refresh_queue, style="Secondary.TButton")
        self.refresh_btn.pack(side="left")
        self.cache_btn = ttk.Button(actions_top, text="缓存选中项", command=self.start_cache, style="Primary.TButton")
        self.cache_btn.pack(side="left", padx=(7, 0))
        self.pause_btn = ttk.Button(actions_top, text="暂停", command=self.pause_resume, state="disabled", style="Secondary.TButton")
        self.pause_btn.pack(side="left", padx=(7, 0))
        self.stop_btn = ttk.Button(actions_top, text="停止", command=self.stop_task, state="disabled", style="Danger.TButton")
        self.stop_btn.pack(side="left", padx=(7, 0))
        self.delete_btn = ttk.Button(actions_top, text="删除选中缓存", command=self.delete_cache, style="Danger.TButton", state="disabled")
        self.delete_btn.pack(side="left", padx=(7, 0))
        self.danmaku_btn = ttk.Button(actions_top, text="更新选中项弹幕", command=self.update_danmaku, style="Secondary.TButton", state="disabled")
        self.danmaku_btn.pack(side="left", padx=(7, 0))
        self.skip_btn = ttk.Button(actions_top, text="跳过当前", command=self.worker.skip, state="disabled", style="Secondary.TButton")
        self.skip_btn.pack(side="left", padx=(7, 0))
        actions_bottom = ttk.Frame(actions, style="Surface.TFrame")
        actions_bottom.pack(fill="x", pady=(9, 0))
        ttk.Label(actions_bottom, textvariable=self.current_var, style="Body.TLabel").pack(side="left")
        ttk.Button(actions_bottom, text="全选可见项", command=self.select_visible, style="Secondary.TButton").pack(side="left", padx=(16, 0))
        ttk.Button(actions_bottom, text="反选", command=self.invert_visible, style="Secondary.TButton").pack(side="left", padx=(7, 0))
        ttk.Button(actions_bottom, text="清空选择", command=self.clear_visible, style="Secondary.TButton").pack(side="left", padx=(7, 0))
        ttk.Button(actions_bottom, text="重试失败项", command=self.retry_failed, style="Secondary.TButton").pack(side="right")
        ttk.Button(actions_bottom, text="同步缓存状态", command=self.worker.start_status_sync, style="Secondary.TButton").pack(side="right", padx=(0, 7))
        ttk.Button(actions_bottom, text="打开离线缓存", command=self.worker.open_offline_cache, style="Secondary.TButton").pack(side="right", padx=(0, 7))
        ttk.Button(actions_bottom, text="显示B站客户端", command=self.worker.show_client, style="Secondary.TButton").pack(side="right", padx=(0, 7))

        metrics = ttk.Frame(outer, style="App.TFrame")
        metrics.pack(fill="x", pady=(0, 12))
        metric_specs = (
            ("稍后再看", "total", COLORS["primary"], "#fff0f5"),
            ("已选择", "selected", COLORS["violet"], "#f7f3ff"),
            ("正在缓存", "active", COLORS["warning"], "#f4f2ff"),
            ("已完成", "completed", COLORS["success"], "#effaf4"),
            ("失败", "failed", COLORS["danger"], "#fff2f2"),
        )
        for index, (label, key, accent, fill) in enumerate(metric_specs):
            metrics.columnconfigure(index, weight=1, uniform="metric")
            card = tk.Frame(metrics, background=fill, highlightbackground=COLORS["border"], highlightthickness=1)
            card.grid(row=0, column=index, sticky="ew", padx=(0 if index == 0 else 5, 0 if index == len(metric_specs) - 1 else 5))
            content = tk.Frame(card, background=fill, padx=14, pady=10)
            content.pack(fill="x")
            tk.Label(content, text=label, background=fill, foreground=accent, font=("Microsoft YaHei UI", 8, "bold")).pack(anchor="w")
            value_row = tk.Frame(content, background=fill)
            value_row.pack(fill="x", pady=(2, 0))
            tk.Label(value_row, textvariable=self.metric_vars[key], background=fill, foreground=COLORS["title"], font=("Microsoft YaHei UI", 17, "bold")).pack(side="left")
            tk.Label(value_row, text="条", background=fill, foreground=COLORS["muted"], font=("Microsoft YaHei UI", 8)).pack(side="left", padx=(4, 0), pady=(7, 0))

        filters_card = tk.Frame(outer, background=COLORS["surface"], highlightbackground=COLORS["border"], highlightthickness=1)
        filters_card.pack(fill="x", pady=(0, 12))
        filters = ttk.Frame(filters_card, padding=(16, 10), style="Surface.TFrame")
        filters.pack(fill="x")
        ttk.Label(filters, text="筛选视频", style="Section.TLabel").pack(side="left", padx=(0, 16))
        ttk.Label(filters, text="关键词", style="Body.TLabel").pack(side="left")
        keyword = ttk.Entry(filters, textvariable=self.keyword_var, width=22)
        keyword.pack(side="left", padx=(5, 10))
        keyword.bind("<Return>", lambda _event: self.refresh_table())
        ttk.Label(filters, text="UP主", style="Body.TLabel").pack(side="left")
        uploader = ttk.Entry(filters, textvariable=self.uploader_var, width=17)
        uploader.pack(side="left", padx=(5, 10))
        uploader.bind("<Return>", lambda _event: self.refresh_table())
        ttk.Label(filters, text="状态", style="Body.TLabel").pack(side="left")
        ttk.Combobox(filters, values=STATUS_OPTIONS, textvariable=self.status_filter_var, state="readonly", width=12).pack(side="left", padx=(5, 10))
        ttk.Label(filters, text="时长 ≥", style="Body.TLabel").pack(side="left")
        ttk.Entry(filters, textvariable=self.min_duration_var, width=7).pack(side="left", padx=(5, 8))
        ttk.Label(filters, text="≤", style="Body.TLabel").pack(side="left")
        ttk.Entry(filters, textvariable=self.max_duration_var, width=7).pack(side="left", padx=(5, 10))
        ttk.Label(filters, text="秒", style="Body.TLabel").pack(side="left", padx=(0, 10))
        ttk.Button(filters, text="应用筛选", command=self.refresh_table, style="Primary.TButton").pack(side="left")
        ttk.Button(filters, text="重置", command=self.reset_filters, style="Secondary.TButton").pack(side="left", padx=(7, 0))

        table_card = tk.Frame(outer, background=COLORS["surface"], highlightbackground=COLORS["border"], highlightthickness=1)
        table_card.pack(fill="both", expand=True)
        table_header = tk.Frame(table_card, background=COLORS["surface"], padx=14, pady=9)
        table_header.pack(fill="x")
        self.table_title_label = tk.Label(table_header, text="视频列表", background=COLORS["surface"], foreground=COLORS["title"], font=("Microsoft YaHei UI", 11, "bold"))
        self.table_title_label.pack(side="left")
        self.view_buttons: dict[str, ttk.Button] = {}
        for key, label in reversed((("queue", "稍后再看"), ("downloading", "正在缓存"), ("completed", "已缓存完成"))):
            button = ttk.Button(table_header, text=label, command=lambda value=key: self.set_view(value), style="Tab.TButton")
            button.pack(side="right", padx=(7, 0))
            self.view_buttons[key] = button
        tk.Label(table_header, text="视图", background=COLORS["surface"], foreground=COLORS["muted"], font=("Microsoft YaHei UI", 8)).pack(side="right", padx=(0, 3))
        self._refresh_view_buttons()
        table_frame = ttk.Frame(table_card, style="Surface.TFrame")
        table_frame.pack(fill="both", expand=True, padx=1, pady=(0, 1))
        columns = (
            "selected", "queue", "title", "uploader", "duration", "bvid", "parts",
            "status", "quality", "actual_quality", "progress", "error", "updated",
        )
        self.tree = ttk.Treeview(table_frame, columns=columns, show="headings", selectmode="extended", height=7)
        headings = {
            "selected": "选择", "queue": "序号", "title": "视频名称", "uploader": "UP主",
            "duration": "时长", "bvid": "BV号", "parts": "分P", "status": "状态",
            "quality": "目标画质", "actual_quality": "实际画质", "progress": "进度",
            "error": "说明/失败原因", "updated": "最后处理时间",
        }
        widths = {
            "selected": 44, "queue": 46, "title": 280, "uploader": 105, "duration": 58,
            "bvid": 105, "parts": 44, "status": 78, "quality": 80, "actual_quality": 80,
            "progress": 58, "error": 180, "updated": 115,
        }
        for column in columns:
            self.tree.heading(column, text=headings[column])
            self.tree.column(column, width=widths[column], minwidth=45, anchor="w" if column in {"title", "uploader", "error"} else "center")
        yscroll = ttk.Scrollbar(table_frame, orient="vertical", command=self.tree.yview)
        xscroll = ttk.Scrollbar(table_frame, orient="horizontal", command=self.tree.xview)
        self.tree.configure(yscrollcommand=yscroll.set, xscrollcommand=xscroll.set)
        self.tree.grid(row=0, column=0, sticky="nsew")
        yscroll.grid(row=0, column=1, sticky="ns")
        xscroll.grid(row=1, column=0, sticky="ew")
        table_frame.rowconfigure(0, weight=1)
        table_frame.columnconfigure(0, weight=1)
        self.tree.bind("<Button-1>", self.on_tree_click, add=True)
        self.tree.bind("<Double-1>", self.on_tree_double_click)
        self.tree.tag_configure("已完成", foreground=COLORS["success"])
        self.tree.tag_configure("失败", foreground=COLORS["danger"])
        self.tree.tag_configure("不支持缓存", foreground="#c2410c")
        self.tree.tag_configure("正在缓存", foreground=COLORS["primary_hover"])
        self.tree.tag_configure("已提交", foreground=COLORS["violet"])
        self.tree.tag_configure("已选择", background=COLORS["soft_blue"])
        self.tree.tag_configure("间隔行", background="#fffafb")

        footer = ttk.Frame(outer, padding=(2, 7, 2, 0), style="App.TFrame")
        footer.pack(fill="x")
        ttk.Label(footer, text=f"日志：{self.log_path}", style="Subtitle.TLabel").pack(side="left")
        ttk.Label(footer, text="Ctrl / Shift 多选列表行 · 点击第一列勾选", style="Subtitle.TLabel").pack(side="right")

    def save_settings(self) -> None:
        self.config_data.quality = self.quality_var.get()
        self.config_data.cache_all = bool(self.cache_all_var.get())
        self.config_data.auto_fallback = bool(self.fallback_var.get())
        save_config(self.config_data)
        self.worker.config = self.config_data

    def on_quality_change(self, _event: Any = None) -> None:
        self.save_settings()
        update_quality_for_pending(self.quality_var.get())
        self.refresh_table()

    def on_cache_all_change(self) -> None:
        enabled = bool(self.cache_all_var.get())
        self.save_settings()
        set_all_selected(enabled)
        self.status_var.set("已开启缓存全部；刷新发现的新视频也会自动选择" if enabled else "已关闭缓存全部，请在列表中手动选择")
        self.refresh_table()

    def _filters(self) -> dict[str, Any] | None:
        try:
            return {
                "view": self.view_mode_var.get(),
                "keyword": self.keyword_var.get(),
                "uploader": self.uploader_var.get(),
                "status": self.status_filter_var.get(),
                "min_duration": int(self.min_duration_var.get() or 0),
                "max_duration": int(self.max_duration_var.get() or 0),
            }
        except ValueError:
            messagebox.showerror("筛选错误", "时长必须填写整数秒")
            return None

    def refresh_table(self) -> None:
        filters = self._filters()
        if filters is None:
            return
        rows = query_videos(filters)
        selected_rows = set(self.tree.selection())
        self.tree.delete(*self.tree.get_children())
        self.visible_ids = []
        for row_index, row in enumerate(rows):
            video_id = int(row["id"])
            self.visible_ids.append(video_id)
            selected = bool(row["selected"])
            status = str(row["status"])
            tags = [status]
            if row_index % 2:
                tags.append("间隔行")
            if selected and status not in {"已完成", "正在缓存"}:
                tags.append("已选择")
            self.tree.insert(
                "",
                "end",
                iid=str(video_id),
                values=(
                    "☑" if selected else "☐",
                    row["queue_index"],
                    row["title"],
                    row["uploader"],
                    row["duration_text"],
                    row["bvid"],
                    row["part_count"],
                    status,
                    row["target_quality"],
                    row["actual_quality"],
                    f"{row['progress']}%",
                    row["error"],
                    row["updated_at"],
                ),
                tags=tuple(tags),
            )
            if str(video_id) in selected_rows:
                self.tree.selection_add(str(video_id))
        stats = counts()
        self.stats_var.set(
            f"队列 {stats['total']}｜已选择 {stats['selected']}｜缓存中 {stats['active']}｜已完成 {stats['completed']}｜失败 {stats['failed']}"
        )
        for key, variable in self.metric_vars.items():
            variable.set(str(stats[key]))
        self._refresh_view_buttons()
        self._refresh_management_buttons()

    def _refresh_view_buttons(self) -> None:
        current = self.view_mode_var.get()
        stats = counts()
        labels = {
            "queue": f"稍后再看 ({stats['total']})",
            "downloading": f"正在缓存 ({stats['active']})",
            "completed": f"已缓存完成 ({stats['completed']})",
        }
        titles = {"queue": "稍后再看列表", "downloading": "正在缓存任务", "completed": "已缓存视频"}
        if hasattr(self, "table_title_label"):
            self.table_title_label.configure(text=titles.get(current, "视频列表"))
        for key, button in getattr(self, "view_buttons", {}).items():
            button.configure(text=labels[key], style="TabActive.TButton" if key == current else "Tab.TButton")
        for key, button in getattr(self, "sidebar_buttons", {}).items():
            selected = key == current
            button.configure(
                background="#ffe3ec" if selected else COLORS["sidebar"],
                foreground=COLORS["primary_pressed"] if selected else COLORS["body"],
                font=("Microsoft YaHei UI", 9, "bold" if selected else "normal"),
            )

    def _refresh_management_buttons(self) -> None:
        if not hasattr(self, "delete_btn"):
            return
        view = self.view_mode_var.get()
        idle = not self.operation_running
        self.delete_btn.configure(
            text="移出稍后再看" if view == "queue" else "删除选中缓存",
            state="normal" if idle else "disabled",
        )
        self.danmaku_btn.configure(state="normal" if idle and view == "completed" else "disabled")

    def set_view(self, view: str) -> None:
        self.view_mode_var.set(view)
        self.status_filter_var.set("全部")
        self._refresh_view_buttons()
        self.refresh_table()

    def reset_filters(self) -> None:
        self.keyword_var.set("")
        self.uploader_var.set("")
        self.status_filter_var.set("全部")
        self.min_duration_var.set("")
        self.max_duration_var.set("")
        self.refresh_table()

    def on_tree_click(self, event: tk.Event[Any]) -> None:
        if self.tree.identify_region(event.x, event.y) != "cell":
            return
        if self.tree.identify_column(event.x) != "#1":
            return
        item = self.tree.identify_row(event.y)
        if not item:
            return
        values = self.tree.item(item, "values")
        set_selected([int(item)], not (values and values[0] == "☑"))
        self.after_idle(self.refresh_table)

    def on_tree_double_click(self, event: tk.Event[Any]) -> None:
        item = self.tree.identify_row(event.y)
        if item:
            values = self.tree.item(item, "values")
            set_selected([int(item)], not (values and values[0] == "☑"))
            self.refresh_table()

    def select_visible(self) -> None:
        set_all_selected(True, only_ids=self.visible_ids)
        self.refresh_table()

    def invert_visible(self) -> None:
        invert_selected(self.visible_ids)
        self.refresh_table()

    def clear_visible(self) -> None:
        set_all_selected(False, only_ids=self.visible_ids)
        self.cache_all_var.set(False)
        self.save_settings()
        self.refresh_table()

    def refresh_queue(self) -> None:
        self.save_settings()
        if self.worker.start_refresh():
            self.status_var.set("正在刷新稍后再看…")
            self._set_running(True, operation="refresh")

    def start_cache(self) -> None:
        native_selection = [int(value) for value in self.tree.selection()]
        if self.cache_all_var.get():
            set_all_selected(True)
        elif native_selection:
            set_selected(native_selection, True)
        self.refresh_table()
        if counts()["selected"] == 0:
            messagebox.showinfo("请选择视频", "请在列表中多选视频，或开启“缓存全部”。")
            return
        self.save_settings()
        if self.worker.start_cache():
            self.status_var.set("正在提交缓存任务…")
            self.pause_btn.configure(text="暂停")
            self._set_running(True, operation="cache")

    def pause_resume(self) -> None:
        if self.pause_btn.cget("text") == "暂停":
            self.worker.pause()
            self.pause_btn.configure(text="继续")
        else:
            self.worker.resume()
            self.pause_btn.configure(text="暂停")

    def stop_task(self) -> None:
        self.worker.stop()

    def retry_failed(self) -> None:
        count = reset_failed_selected()
        self.status_var.set(f"已将 {count} 条失败/暂停任务重新加入选择")
        self.refresh_table()

    def _native_selected_ids(self) -> list[int]:
        return [int(value) for value in self.tree.selection()]

    def delete_cache(self) -> None:
        ids = self._native_selected_ids()
        if not ids:
            messagebox.showinfo("请选择视频", "请先在当前列表中多选需要删除的视频。")
            return
        view = self.view_mode_var.get()
        if view == "queue":
            approved = messagebox.askyesno(
                "确认移出稍后再看",
                f"将通过B站客户端卡片菜单把选中的 {len(ids)} 条视频移出稍后再看。\n\n已有缓存不会被删除，是否继续？",
            )
            if approved and self.worker.start_delete_watch_later(ids):
                self.status_var.set("正在从B站稍后再看队列移除选中视频…")
                self._set_running(True, operation="delete_queue")
            return
        approved = messagebox.askyesno(
            "确认删除缓存",
            f"将通过B站客户端卡片右下角菜单删除选中的 {len(ids)} 条缓存。\n\n该操作会取消缓存任务或删除离线缓存文件，是否继续？",
        )
        if approved and self.worker.start_delete_cache(ids):
            self.status_var.set("正在通过B站客户端删除选中缓存…")
            self._set_running(True, operation="delete")

    def update_danmaku(self) -> None:
        ids = self._native_selected_ids()
        if not ids:
            messagebox.showinfo("请选择视频", "请先在“已缓存完成”列表中多选需要更新弹幕的视频。")
            return
        if self.worker.start_update_danmaku(ids):
            self.status_var.set("正在通过B站客户端更新选中视频弹幕…")
            self._set_running(True, operation="danmaku")

    def _set_running(self, running: bool, *, operation: str = "") -> None:
        self.operation_running = running
        self.refresh_btn.configure(state="disabled" if running else "normal")
        self.cache_btn.configure(state="disabled" if running else "normal")
        cache_controls = "normal" if running and operation == "cache" else "disabled"
        self.pause_btn.configure(state=cache_controls)
        self.stop_btn.configure(state="normal" if running else "disabled")
        self.skip_btn.configure(state=cache_controls)
        self._refresh_management_buttons()
        if not running:
            self.pause_btn.configure(text="暂停")
            self.current_var.set("当前任务：无")

    def poll_events(self) -> None:
        try:
            while True:
                event = self.worker.events.get_nowait()
                self.handle_event(event)
        except queue.Empty:
            pass
        self.after(120, self.poll_events)

    def handle_event(self, event: dict[str, Any]) -> None:
        kind = event.get("kind")
        if kind == "log":
            logging.info("%s", event.get("text", ""))
            self.status_var.set(str(event.get("text", "")))
        elif kind == "status":
            self.status_var.set(str(event.get("text", "")))
        elif kind == "current":
            self.current_var.set(
                f"当前任务 {event.get('index')}/{event.get('total')}：{event.get('title', '')}"
            )
        elif kind in {"table_refresh", "cache_done", "refresh_done"}:
            self.refresh_table()
        elif kind == "operation":
            self._set_running(bool(event.get("running")), operation=str(event.get("operation", "")))
        elif kind == "attention":
            self.status_var.set(str(event.get("text", "")))
            if event.get("action") == "restart":
                approved = messagebox.askyesno(
                    "需要重启B站客户端",
                    f"{event.get('text', '')}\n\n是否由本程序关闭B站客户端后台进程并重新启动？登录数据不会被删除。",
                )
                if approved:
                    self.after(500, self.worker.start_restart_refresh)
            else:
                approved = messagebox.askyesno(
                    "需要人工处理",
                    f"{event.get('text', '')}\n\n是否现在打开B站客户端进行处理？",
                )
                if approved:
                    self.worker.show_client()
        elif kind == "error":
            text = str(event.get("text", ""))
            logging.error("%s", text)
            self.status_var.set(text)
            messagebox.showerror("操作失败", text)
        elif kind == "item_error":
            logging.warning("%s: %s", event.get("title", ""), event.get("text", ""))

    def on_close(self) -> None:
        if self.worker.running and not messagebox.askyesno("退出", "任务仍在运行，确定停止并退出吗？"):
            return
        self.worker.close()
        self.destroy()


def run() -> None:
    app = CacheManagerApp()
    app.mainloop()
