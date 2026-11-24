# Changelog

## 0.1.0 - 2026-11-24

- 初始化 `Antigravity Usage Monitor Windows` 项目框架与 MIT 开源仓库。
- 实现 WSL / Windows 双端只读 Hook 采集器（`hooks/collect.py`）：支持 `transcript.jsonl` 增量去重统计、`PreInvocation` / `PostInvocation` 精确 `tok/s`、上下文占比、`conversation_summaries.db` 主控/子代理拆分及双槽原子落盘。
- 实现可选 `agy` CLI `statusLine` 配额旁路同步（`hooks/statusline-sync.py`）。
- 实现免注入 WPF 原生伴随浮层（`overlay/*.cs`）：UIA 锚定输入框工具栏空白区、深/浅主题联动、窄窗口按优先级省略、双栏详情面板、双池 `5h/7d` 窗口判定与预警、以及 UIA 可见配额文本顺手拾取。
- 提供 `install.ps1` / `uninstall.ps1` 与 Python + C# 自动化回归测试。
