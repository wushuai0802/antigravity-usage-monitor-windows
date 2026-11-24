# Antigravity Usage Monitor Windows

> 非官方伴随工具，与 Google 没有关联，也未获其认可或支持。“Antigravity”与“Gemini”是 Google 的商标，此处仅用于说明适用对象。

在 Windows 版 **Antigravity 2.0** 桌面应用输入框下方的工具栏空白区（模型按钮右侧、权限选择器左侧）显示一行实时用量：

```text
48.2 tok/s | 184.5万 | 78.4% | G 25% · C 12% | 上下文 31%
```

**不注入 Antigravity、不开调试端口、不修改安装包、默认零第三方网络请求。**

---

## 核心特性

- **免注入 WPF 原生伴随浮层**：使用 Windows 内置 `.NET Framework 4.x` 编译器本地构建，零额外 SDK 依赖；基于 Windows UI Automation 精准锚定在输入框工具栏空白区，自动跟随 Antigravity 深/浅主题，窗口拖动、缩放或展开菜单时自动隐身，窄窗口按优先级自动省略指标。
- **零焦点交互面板**：点击状态条展开双栏详情（左栏「本会话 + 主控/子代理拆分」，右栏「官方双池配额 + 今日汇总」），支持显示项勾选、窄窗口保留优先级调整与浅色/深色/自动主题切换，点击面板外部自动收起且不抢占输入焦点。
- **WSL + Windows 双端无缝采集**：通过 Antigravity 官方 `hooks.json`（`SessionStart`、`PreInvocation`、`PostInvocation`、`Stop`）在回合边界记录毫秒级耗时并只读解析本地 `transcript.jsonl` 与 `conversation_summaries.db`，以双槽原子 JSON 落盘至 `%LOCALAPPDATA%\AntigravityUsageMonitor\`，浮层仅读本机 NTFS，无跨系统 I/O 抖动。
- **官方双池配额与 5h/7d 预警**：
  - 区分 **Gemini 池**（`G`）与 **Claude / 第三方池**（`C`），自动高亮当前选中模型所属池；
  - 根据重置时间距今是否超过 6 小时自动判定 `5h` / `7d` 窗口；`5h ≤ 10%` 橙色提示可切另一模型家族，`7d ≤ 5%` 红色提示准备切号；
  - 支持两种零侵入同步源：**桌面端 UIA 顺手拾取**（打开含配额文本的界面时只读缓存）与 **可选 `agy` CLI `statusLine` 旁路同步**。

---

## 快速安装

需要 Windows 10/11（64 位）、Python 3.10+（用于只读采集脚本）及已安装的 Antigravity 2.0 桌面应用。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
```

安装脚本会：
1. 使用系统内置 `csc.exe` 编译 `AntigravityUsageOverlay.exe` 到 `%LOCALAPPDATA%\AntigravityUsageMonitor\bin\`；
2. 将采集脚本同步到 `%LOCALAPPDATA%\AntigravityUsageMonitor\scripts\`；
3. 自动合并配置到 Antigravity 的 `hooks.json`（支持 Windows 本机及 WSL 默认发行版，可用 `-SkipHooks` 跳过）；
4. 通过 `explorer.exe` 拉起单例浮层（脱离当前终端进程树）。

> 提示：已打开的 Antigravity 会话需新开对话或重启后才会加载新增的 Hook。即使尚未触发 Hook，采集器也可直接只读本地会话记录计算 Token、缓存命中率与上下文占比。

---

## 指标口径

| 指标 | 说明 |
| --- | --- |
| `tok/s` | 最近一次模型响应的 `output_tokens ÷ 回合耗时`。有 `PreInvocation` / `PostInvocation` 精确毫秒记录时优先使用，否则基于相邻步骤时间估算；空闲无样本时显示 `--`。 |
| 今日 Token | 当天本机所有活跃会话 `PLANNER_RESPONSE` 的 `input_tokens + cache_read_tokens + output_tokens`（按会话与 `step_index` 严格去重）。 |
| 缓存命中 | `cache_read_tokens ÷ (input_tokens + cache_read_tokens)`。 |
| 上下文 | 当前会话最新一步 `input_tokens + cache_read_tokens` 占该模型上下文窗口的估算百分比。 |
| 主控 / 子代理拆分 | 读取 `conversation_summaries.db` 的 `parent_conversation_id` 关联，分别统计主对话与派生子代理（Subagent / Swarm）的 Token 消耗。 |
| `G XX% · C XX%` | Gemini 池与 Claude/第三方池的剩余配额百分比；按重置时间距今判断 `5h`（≤ 6h）或 `7d`（> 6h）窗口；过期未刷新时显示“已重置”。 |

---

## 双池配额同步

Antigravity App 的 Hook 载荷目前不包含云端剩余配额，本工具采用**纯官方 UIA 无感读取 + 可选 CLI 旁路**写入标准双槽文件：

1. **桌面端自动 & 一键刷新（默认开启，零凭据、零 Token、不移动鼠标）**：
   - **手动随时刷新**：点击展开面板右上角「刷新额度」或右键状态条选择「刷新双池配额」，通过原生 UIA `ExpandCollapsePattern` + 无光标移动 `WM_MOUSEMOVE` 读取模型菜单「查看用量」子浮卡中的 **Gemini（5h + 7d）与 Claude / GPT（5h + 7d）** 四条配额并立即收起（约 0.7 秒）。
   - **空闲自动刷新**：在「显示设置」中可开关与调整周期（5 / 10 / 20 分钟）。仅当键鼠连续空闲 ≥ 45 秒、无菜单展开、且达到周期或新增消耗 ≥ 300 万 Token 时才自动快闪同步，绝不打断输入。
   - **顺手被动拾取**：平时自己滑开模型菜单「查看用量」或打开「模型额度」页时也会即时同步最新数值。
2. **`agy` CLI `statusLine` 旁路同步（可选）**：在 `~/.gemini/antigravity-cli/settings.json` 中配置 `statusLine` 指向 `hooks/statusline-sync.py`，每次使用 `agy` 时会自动把官方 JSON 里的 `quota` 字段同步到桌面浮层。

详细数据结构见 [架构与数据契约](docs/architecture.md)。

---

## 隐私与安全

- 默认**零网络请求**、无遥测、不注入宿主进程、不读取或保存任何账号 Token / Cookie / 密钥。
- 只读解析 `transcript.jsonl` 中的用量数字字段（`model`、`input_tokens`、`cache_read_tokens`、`output_tokens`、`created_at`、`step_index`）与 `conversation_summaries.db` 的会话元数据，**不保存也不展示对话正文**。
- 所有运行状态、设置与限长日志仅保存在 `%LOCALAPPDATA%\AntigravityUsageMonitor\`。

---

## 开发与测试

```powershell
.\tests\run-tests.ps1
```

---

## 卸载

```powershell
.\uninstall.ps1
# 如需一并清理本地状态、缓存与设置：
.\uninstall.ps1 -RemoveData
```

---

## 许可证

[MIT](LICENSE)
