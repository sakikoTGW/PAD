# PAD CLI 命令地图

> 权威来源是二进制自己的 `pad cli help`。这份文件是**补全版**：源码里 49 个 group，`help` 只印了 35 个；
> 漏掉的 14 个是 `agent` `bundles` `discover` `hmr` `node` `packforge` `pack-runtime` `pad-update`
> `procs` `project` `relations` `status` `usage` `version`。仓库里 `scripts/check-skill-drift.ps1` 每次改完跑一遍。
> 两者冲突时以二进制为准，并顺手把这里补上。

约定：`--json` 表示有结构化输出；`--out <file>` 表示可写文件（Windows WinExe 的 stdout 不可靠，优先用它）。

## 事实（只读，先跑这些）

| 命令 | 作用 |
|---|---|
| `where` | root / settings / repo / packmaker / 7z 的位置 |
| `version` | `{id, name, version}`，JSON |
| `status [--json]` | 每个实例的 `Stopped / Starting / Running` |
| `ps [--json]` | 运行账本里的实例与 profile |
| `procs [--json] [--pid N]` | 进程现场（PEB / `ps`），带 home、profile、命令行 |
| `self` | **我在哪**：pid / home / profile / 实例 / 谁启动的 / 父进程链。给"被 PAD 起来的 agent"用，读 PEB 才看得到自己的启动参数（新版） |
| `running` | 谁在跑 **+ 它跑的什么配置**（PAD runs + 探测到的桌面端/TUI），比 `ps` 全（新版） |
| `instance list` / `profile list <instance>` / `plugin list <instance> [profile]` | 清单 |
| `release list` | 已装的 DSH 版本 |
| `pack list [instance] [--json] [--out f] [--roots a;b] [--deep <dir>]` | 包目录 + 每实例账本 |
| `surfaces`（别名 `bundles`；dev 树改名叫 `bundle`） | surface 注册表：`id/kind/label/shows/exclusive/needsEndpoint/requires` |
| `inspect [home] [--json] [--out f]` | 一个 home 的构成 |
| `sniff` | 探当前机器的 DSH 安装（dsh home / TUI 包） |
| `discover` | 扫描本机已有的 DSH 安装 |
| `relations` | 实例/包/插件的关系图 |
| `usage <home>` | 该 home 的 token 与工具调用统计（只报事实，不给建议） |
| `logs <instance>` | 日志目录 |
| `crash <instance>` | 崩溃分析 |
| `selftest` | 自检 |

## 生命周期

| 命令 | 作用 |
|---|---|
| `release list \| install <ver> \| pin <bin.js> \| remove <ver>` | DSH 版本 |
| `instance list \| create <name> <ver> \| create --pack <id\|path> [--name n] [--profile p] \| pin <id> <ver> \| clone <id> <name> \| remove <id>` | 实例 |
| `instance workspace isolate <id> \| share <id> <dir>` | 工作区隔离或共享 |
| `profile add <instance> <profile> <tui\|web\|gateway>` | 给实例加 profile |
| `run <instance> [profile]` | 启动并记录 |
| `script <instance> [profile]` | 生成启动脚本 |
| `rewrite-scripts` | 重写全部脚本（迁移后） |
| `stop <instance> <profile>` | 停止（**唯一正确的停法**） |
| `shortcut <instance> [profile]` | 桌面快捷方式 |
| `adopt [home] [version]` | 接管一个已有 home |
| `import <pack.zip>` | 导入整合包 |
| `portable-export <dest> [instanceId]` | 导出便携版（需单文件 PAD，否则 PA117） |
| `update` | 自更新 |

## 包

| 命令 | 作用 |
|---|---|
| `pack spec \| validate <path>` | 校验包规范 |
| `pack preflight <instance> --pack <id\|path> [--profile p] [--json] [--out f]` | **装前预检**：插件状态、账本、管理器、警告 |
| `pack install <instance> --pack <id\|path> [--profile p] [--accept-risk] [--skip-missing] [--dry-run] [--json] [--out f]` | 安装；`0`=已应用，`2`=needs-decision |
| `pack project <instance> <pack>` / `allow` / `deny <instance> <pack-id>` | 投影与放行 |
| `pack scan` | 扫描可打包的内容 |
| `packforge inspect \| market \| registry \| install \| import \| import-auto \| export-instance \| export-workspace` | PackForge `.dspack` 生态；`help` 里没有这一组 |
| `market plugin \| tui \| skill \| host [text]` | 市场搜索 |
| `trial list \| add <instance> <profile> <spec> \| commit \| drop` | 试装 |

## 准备好给 agent 用（`help` 里没有）

| 命令 | 作用 |
|---|---|
| `agent prep <instance> <profile> [spec…] [--recipe web\|tui\|gateway] [--hot]` | 一条命令：缺 profile 按配方建 → 开官方热重载 → 批量装插件 → 一个 verdict。返回 `pad.agent-prep/v1`：`ok` / `restartRequired` / `hot` / `reason` / `failedSpec` / `checkpoint`；`ok=false` 退出 3 |
| `hmr enable <instance> <profile> [--json]` | 装/挂官方热重载，返回 `pad.hmr/v1`（`installed` / `mounted` / `changed` / `restartRequired` / `hot` / `reason`） |
| `hmr status <instance> <profile> [--json]` | 只探测能力，不改 |

`agent prep` 是"把实例变成 agent 工作台"的入口，优先用它，别手搓 `profile add` + `plugin add` + `hmr enable` 三步。

## 开发会话

| 命令 | 作用 |
|---|---|
| `dev start <instance> [--profile p] [--json]` | 开会话（不写实例） |
| `dev status <instance> [--json]` | 会话 + 未固化改动 + 运行事实 |
| `dev apply <instance> --spec <pkg> [--action add\|remove] [--json]` | 应用一项改动（只影响本次运行） |
| `dev verify <instance> [--json]` | `config-compiles` / `plugins-resolve` / `source-untouched` |
| `dev commit <instance> [--dry-run] [--force]` | 固化 + 自动 checkpoint；验证失败默认 PA242 |
| `dev discard <instance>` | 丢弃 |
| `dev list <instance>` | 历史 |
| `checkpoint list <instance>` / `prune <instance> [--keep 20] [--yes]` | 打点管理 |

## 会话跳转与 surface

| 命令 | 作用 |
|---|---|
| `session ref <session> [--json]` | 解析会话引用 |
| `session open <instance> <session> [--bundle s] [--no-exec] [--json]` | 跳到该会话（`--no-exec` 只规划） |
| `surface list [--json]` / `validate [--json]` | surface 能力注册表 |
| `surface commit <instance> <profile> <spec>` / `drop …` | 固化/撤销 surface 注册 |
| `node list <instance>` / `node open <home> --bundle <tui\|web\|app\|tauri> [--bin <bin.js>] [--exe <exe>] [--profile p] [--port N]` / `node close <home> --profile p` | 起/查/关一个 surface 进程（比 `run` 低一层） |

## 预设与账本（新版 dev 树，release 可能还没有）

| 命令 | 作用 |
|---|---|
| `preset lib list \| add <dir> [id] \| from-instance <instance> <preset> [as] \| remove <id> \| install <id> <instance>` | 预设库（跨实例复用 agent preset） |
| `preset list <instance>` / `copy <instance> <from> <to>` / `remove <instance> <id>` / `bind <instance> <profile> <id>` | 实例里的 agent preset |
| `refresh` | 清掉账本里已经死掉的 run / node |

## 系统、凭据与机器接口

| 命令 | 作用 |
|---|---|
| `mcp serve` | MCP over stdio（7 个 dev 工具，stdout 只有 JSON-RPC） |
| `credentials list \| has [instance] \| get <name> \| set <name> <file.yaml> \| remove <name>` | 凭据；`has` 未配置时退出 1 |
| `gateway <instance> [rpc-method]` | 管理侧门 |
| `task run <instance> --input <f> --out <f> [--timeout s]` | 无头任务 |
| `queue submit \| submit-mesh \| list \| status \| cancel \| retry \| worker` | 任务队列 |
| `project <cmd>` | 项目定义（`project` 组不在 `help` 里） |
| `system new <name>` / `layer list\|open <system> <layer>` | 实验：分层系统 |
| `experimental list \| enable <id> \| disable <id>` | 实验开关；关闭时 PA096 |
| `pack-runtime stage <dest>` | 装配 pack-runtime |
| `pad-update check \| fetch \| pending \| apply` | 启动器自更新 |

## 全局规律

- **缺参数** = `usage: …` 打到 stderr，退出 `2`。所以 `pad cli <group>` 空跑一次，就能看到该组的用法。
- **不存在 `--help`。** `run --help` 会把 `--help` 当成实例名。要用法就空跑该组。
- `--json` 把日志挤到 stderr（保持 stdout 纯净）；`--out` 直接写文件。
