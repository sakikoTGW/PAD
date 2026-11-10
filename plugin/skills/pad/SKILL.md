---
name: pad
description: 用 PAD（pack-agent for DSH）管理 DSH 实例：查看/启动/停止实例、装整合包（.pack.json / .pack.zip）、把实例准备好给 agent、跑开发会话改实例、导出便携版、读 PAD 报错并照 help 行修复。用户说「用 PAD 打开」「启动实例」「装这个整合包」「导出便携版」「看看哪个在跑」「PAD 报错 PAxxx」，或任务需要给某个 DSH home 加插件、起 web/TUI 时用。不用于改 DSH 源码或一般 node/pnpm 排错。
metadata:
  version: "0.1.0"
  requires: "PAD 0.2.x+"
---

# PAD

PAD 是 DSH 的启动器：**一个二进制**，窗口给用户点，`cli` 给 agent 调。窗口和 CLI 跑同一套 Core，
所以同一个操作在两边不会有两种说法（源码注释原话）。macOS/Linux 上是 `pad-cli`，同一套 Core，没有窗口。

**你是 agent：走 CLI 或 MCP，不要让用户去窗口里点。** 窗口是给人用的；让用户点等于把确定性交给运气。

## 0. 命令怎么拼

| 形态 | 调用 |
|---|---|
| Windows 窗口版 | `PAD.exe cli <group> <cmd> …`（`cli` 不打开窗口） |
| pad-cli | `pad <group> <cmd> …`（没有 `cli` 前缀） |
| MCP | `PAD.exe cli mcp serve`（stdio，7 个 dev 工具） |

下文一律写 `pad cli …`，指"PAD 的 CLI"。先 `pad cli version` 确认能调通。

### 找不到 PAD 时

`pad` 不一定在 PATH 上，DSH 实例里通常没有。按顺序试：

```powershell
Get-Command pad                      # 1. 直接能调
$d = $env:DSH_HOME                   # 2. 从实例的 home 往上找 PAD 根（根下有 pad.json 和 instances/）
while ($d -and -not (Test-Path (Join-Path $d 'pad.json'))) { $d = Split-Path $d -Parent }
$d                                   # 可执行文件一般就在这个根下
```

都找不到就问用户 PAD 装在哪。**不要扫全盘，不要猜路径。**

## 1. 先看事实，再动手

四个只读命令，覆盖 90% 的"现在什么情况"：

```bash
pad cli where --json          # root / settings / repo / packmaker / sevenZip
pad cli instance list --json  # 有哪些实例
pad cli status --json         # Stopped | Starting | Running
pad cli ps --json             # 谁真在跑，认哪个 home / profile
```

`status` 会当场扫一遍进程现场（连双击脚本起的 TUI 都算），`ps` 只反映 PAD 账本。
两者对不上时以 `status` 为准；要 port / pid 这类细节再回 `ps`。
**不要**用 `Get-Process node`、`netstat`、翻 `instances/*/instance.json` 去重建这些事实——PAD 已经算过一遍了。

如果你自己就是被 PAD 起来的（在某个实例里干活），先跑 **`pad cli self`**：它读 PEB，告诉你
home / profile / 实例 / 谁启动的你——这些你自己看不到。新版没有就跳过。

## 2. 任务 → 命令

| 用户想做 | 跑这个 | 说明 |
|---|---|---|
| 把一个实例准备好给 agent 用 | `agent prep <instance> <profile> [spec…] [--recipe web\|tui\|gateway] [--hot]` | 一条命令：缺 profile 就按配方建、开热重载、批量装插件，返回一个 verdict（`ok` / `restartRequired` / `hot` / `reason`）。退出码 `3` = 没准备好 |
| 起一个全新实例并打开 | `instance create <name> <ver>` → `profile add …` → `run <instance> <profile>` | 也可以 `instance create --pack <id\|path>` 一次到位 |
| 装一个整合包 | `pack preflight` → `pack install` | 两步，别跳 |
| 给实例加/删一个插件做实验 | `dev start` → `dev apply` → `dev verify` → `dev commit`/`dev discard` | 改实例的唯一正确姿势 |
| 只是启动/停止 | `run` / `stop <instance> <profile>` | 停进程别用 taskkill |
| 看有什么包、市场里有什么 | `pack list` / `market plugin\|tui\|skill\|host [text]` | |
| 导出给别人 | `portable-export <dest> [instanceId]` | 需要单文件 PAD（PA117） |
| 从某条会话跳过去 | `session ref <session> --json` → `session open <instance> <session>` | `--no-exec` 只规划不执行 |
| 诊断 | `crash <instance>` / `logs <instance>` / `inspect [home]` / `sniff` | 先拿事实再下结论 |

命令全集见 [references/cli.md](references/cli.md)；多步流程见 [references/workflows.md](references/workflows.md)。

## 3. 五条铁律

1. **改实例只走 dev 会话。** `dev start` 之后 `dev apply` 的改动**只影响本次运行，不写实例**；验证过了
   `dev commit` 才落盘（自动打 checkpoint）。直接编辑 `instances/<id>/home/**` 会绕过验证和打点，
   `source-untouched` 判据也会因此失败。
2. **装包先 preflight。** `pack preflight` 给出插件状态、已装账本、管理器缺失、警告；`pack install` 才动实例。
   退出码 `2` = "需要用户决定但你没带对应选项"，带上 `--accept-risk` / `--skip-missing` **或** 去问用户，
   不要原样重试、更不要循环重试。
3. **停进程用 `pad cli stop`。** 直接杀进程会留下账本里"还活着"的行，下次 `run` 撞 PA099。
4. **结构化输出用 `--json`。** Windows 窗口版是 WinExe，stdout 不可靠，脚本里再配 `--out <file>`，
   读文件而不是读控制台。
5. **报错里 `= help:` 就是命令。** PAD 的错误是 rustc 形状，help 行是能直接跑的命令——照抄，别自己发明参数。

## 4. 退出码

| 码 | 含义 |
|---|---|
| `0` | 成功（`pack install` 的 `installed` / `incomplete` 都算 0） |
| `1` | 已渲染的 `PadError`（有 code / location / help），或意外异常 |
| `2` | 用法错误（参数不够，stderr 打 `usage: …`）或 `pack install` 的 `needs-decision` |
| `3` | `agent prep` 跑完了但 `ok=false`（看 `failedSpec` / `message`） |
| `130` | 用户中断 |

`2` 不是一个错误码，是一个**分叉**：要么补参数，要么把决定交回用户。

## 5. 报错长这样

```
error[PA026]: no such instance
 --> instances/foo
  = help: pad cli instance list
```

- `error[PAxxx]`：code 含义永久不变，可以先查 [references/errors.md](references/errors.md)。
- `-->`：出问题的位置（实例、home、profile、包路径）。
- `= help:`：**可执行**的下一步，一行一条。

读完这三段再说话。不要复述整段错误给用户，也不要吞掉 help 行。

## 6. dev 会话（改实例的唯一姿势）

```bash
pad cli dev start   <instance> [--profile p]        # 返回 session + sourceHash
pad cli dev apply   <instance> --spec <pkg> [--action add|remove]
pad cli dev verify  <instance>                      # config-compiles / plugins-resolve / source-untouched
pad cli dev commit  <instance> [--dry-run] [--force]  # 验证失败默认拒绝（PA242）
pad cli dev discard <instance>                      # 丢掉会话，实例不变
```

- `dev status` 看未固化改动 + 运行事实；`dev list` 看历史。
- `--force` 只在用户明确说"知道验证没过也要提交"时用，并在回复里说清楚。
- commit 会自动打 checkpoint；`checkpoint list` / `checkpoint prune --keep 20`。
- 有 MCP 时优先 MCP（`dev_start` / `dev_status` / `dev_apply` / `dev_verify` / `dev_commit` / `dev_discard` / `dev_list`），
  确定性、无 shell 引号问题。

## 7. 什么时候别用 PAD

- 只是跑 `dsh` 命令、改 DSH 源码、装 npm 依赖 → 直接做，别包一层启动器。
- 用户想自己点窗口 → 告诉他点哪儿，不要替他执行 `run`。
- 要"顺便修一下实例配置"→ 那是 `dev` 的事，不是编辑器的事。

## 参考

| 文件 | 什么时候读 |
|---|---|
| [references/cli.md](references/cli.md) | 不确定某个 group 存不存在、有哪些 flag |
| [references/workflows.md](references/workflows.md) | 多步流程：起实例、装包、改实例、导出、排障 |
| [references/errors.md](references/errors.md) | 拿到 PAxxx，想知道原因和修法 |
