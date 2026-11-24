# Changelog

## 0.2.0 - 2026-11-24

- **无感双池配额自动同步**：基于模型按钮 `ExpandCollapsePattern` + 无光标移动 `WM_MOUSEMOVE` 读取「查看用量」子菜单 `RawView`（`0.7s` 内完成读取并自动收起），新增面板「刷新额度」与右键「刷新双池配额」按钮，以及带键鼠空闲（≥45s）与新增消耗（≥300万 Token）护栏的空闲自动刷新开关（5 / 10 / 20 分钟可选）。
- **燃尽速率与耗尽预测（Burn Rate / Runway）**：本地维护 `quota-history.json`（保留 30 天），自动计算每小时消耗斜率并对比重置时间预测是否能安全撑到重置。
- **切号 / 新周期分界自动隔离**：检测到剩余比例回升或重置时间跳变（>18 分钟）时自动打分界点，避免上一账号的消耗斜率混算到新账号。
- **Prompt Cache 塌陷预警**：`hooks/collect.py` 逐轮对比缓存命中率，当热缓存会话（≥65%）在较大输入（≥30K）上骤降至 <35% 时，在状态条和面板高亮「缓存塌陷」警告。
- **配额阈值里程碑桌面提醒**：5h 跌破 `20% / 10%`、7d 跌破 `10% / 5%` 时弹出一次性 Windows 气泡提醒（同重置周期去重不刷屏，可在设置中开关）。
- **近 14 天本地 Token 趋势图**：`usage-history.json` 持久化 31 天纯数字日账本，展开面板底部新增可开关的 14 天迷你趋势柱图。

## 0.1.0 - 2026-11-24

- 初始化 `Antigravity Usage Monitor Windows` 项目框架与 MIT 开源仓库。
- 实现 WSL / Windows 双端只读 Hook 采集器（`hooks/collect.py`）：支持 `transcript.jsonl` 增量去重统计、`PreInvocation` / `PostInvocation` 精确 `tok/s`、上下文占比、`conversation_summaries.db` 主控/子代理拆分及双槽原子落盘。
- 实现可选 `agy` CLI `statusLine` 配额旁路同步（`hooks/statusline-sync.py`）。
- 实现免注入 WPF 原生伴随浮层（`overlay/*.cs`）：UIA 锚定输入框工具栏空白区、深/浅主题联动、窄窗口按优先级省略、双栏详情面板、双池 `5h/7d` 窗口判定与预警、以及 UIA 可见配额文本顺手拾取。
- 提供 `install.ps1` / `uninstall.ps1` 与 Python + C# 自动化回归测试。
