# 架构与数据契约

## 1. 总体数据流

```text
Antigravity 2.0 (Electron UI on Windows)
   │  ├── UIA 只读扫描（锚点坐标、当前模型名、深/浅主题、窗口标题、可选配额文本拾取）
   │  └── 后端会话（WSL ~/.gemini/antigravity 或 Windows %USERPROFILE%\.gemini\antigravity）
   │         ├── hooks.json (SessionStart / PreInvocation / PostInvocation / Stop)
   │         │      └── hooks/collect.py ──▶ 双槽原子写入 metrics.slot{0,1}.json
   │         └── (可选) agy CLI statusLine
   │                └── hooks/statusline-sync.py ──▶ 双槽原子写入 quota.slot{0,1}.json
   ▼
%LOCALAPPDATA%\AntigravityUsageMonitor\
   ├── bin\AntigravityUsageOverlay.exe   (纯 .NET Framework 4.x WPF 无焦点浮层)
   ├── metrics.slot0.json / metrics.slot1.json
   ├── quota.slot0.json / quota.slot1.json
   ├── timing.json                       (Pre/PostInvocation 回合毫秒时间戳)
   ├── settings.json                     (显示项勾选、窄窗口优先级、主题偏好)
   └── overlay.log                       (限长 1MB 滚动日志)
```

## 2. UIA 锚点策略（Antigravity 2.0 实测）

- 进程名：`Antigravity`（排除无主窗口子进程）。
- 主题识别：顶层 `Group` 的 `ClassName` 若含 `dark-theme` / `theme-dark` 判定为深色，含 `light-theme` / `theme-light` 判定为浅色；否则回退像素采样。
- 输入框定位：`ControlType.ComboBox`（或 `Edit`），`Name` 为 `Message input`（或底部大宽输入区）。
- 工具栏锚点：位于输入框下方一行，左侧定位模型按钮（`Name` 匹配 `Select model` 或含 `Gemini` / `Claude` / `GPT` / `Orchestrator`），右侧定位同水平行相邻按钮（如权限预设选择器、语音按钮），在模型按钮右侧与右侧控件之间计算最大连续空白带（FreeRange），浮层居中或右对齐贴合该空白带。
- 隐藏保护：窗口最小化、拖动/缩放中、任一菜单/弹层展开（`ExpandCollapseState.Expanded` 或检测到 `Menu` / `List` 弹层）或可用宽度小于最低显示阈值时自动隐藏，稳定后平滑淡入。

## 3. 双槽原子文件契约

所有跨进程共享 JSON 采用双槽轮换写入（`sequence % 2`）：写入方先落盘到 `.tmp` 或直接写入非活跃槽，读取方同时读 `slot0` 与 `slot1` 并选取合法 JSON 中 `sequence` 最大的一份。

### 3.1 `metrics.slot{0,1}.json`

```json
{
  "schemaVersion": 1,
  "sequence": 42,
  "updatedAt": "2026-11-24T03:05:00Z",
  "sourceEnv": "wsl",
  "activeConversationId": "0f55576c-12b6-4264-a07c-dcfcd907affe",
  "today": {
    "date": "2026-11-24",
    "totalTokens": 1845200,
    "inputTokens": 392000,
    "cacheReadTokens": 1418000,
    "outputTokens": 35200,
    "cacheHitRate": 0.7834,
    "sessionsCount": 6
  },
  "sessions": {
    "0f55576c-12b6-4264-a07c-dcfcd907affe": {
      "conversationId": "0f55576c-12b6-4264-a07c-dcfcd907affe",
      "title": "Antigravity Usage Monitor",
      "model": "gemini-4-neon-high",
      "modelFamily": "gemini",
      "status": "CASCADE_RUN_STATUS_IDLE",
      "lastModifiedAt": "2026-11-24T03:05:00Z",
      "sessionTokens": 248100,
      "inputTokens": 48200,
      "cacheReadTokens": 191500,
      "outputTokens": 8400,
      "cacheHitRate": 0.7989,
      "lastTurnTokens": 52100,
      "lastTurnOutputTokens": 920,
      "lastTurnDurationMs": 18900,
      "tokPerSec": 48.68,
      "contextTokens": 52220,
      "contextWindowSize": 1048576,
      "contextPct": 4.98,
      "subagentCount": 1,
      "subagentTokens": 41200,
      "executionDurationMs": 142000
    }
  }
}
```

### 3.2 `quota.slot{0,1}.json`

```json
{
  "schemaVersion": 1,
  "sequence": 7,
  "updatedAt": "2026-11-24T03:05:00Z",
  "source": "agy-statusline",
  "planTier": "Pro",
  "pools": {
    "gemini": {
      "remainingPct": 25.0,
      "resetAt": "2026-11-24T06:06:36Z"
    },
    "claude": {
      "remainingPct": 12.0,
      "resetAt": "2026-11-24T06:48:53Z"
    }
  }
}
```

### 3.3 窗口判定与预警阈值

- 计算 `delta = resetAt - now`：
  - `delta <= 0`：标记为“已重置”（`resetPassed = true`）；
  - `0 < delta <= 6h`：判定为 **`5h` 窗口**；`remainingPct <= 10` 触发橙色预警（可切另一模型家族）；
  - `delta > 6h`：判定为 **`7d` 窗口**；`remainingPct <= 5` 触发红色预警（建议准备切号）。
