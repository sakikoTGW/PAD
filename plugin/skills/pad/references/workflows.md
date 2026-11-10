# 常用流程

每个流程都以"怎么验证它真的成了"结尾。**没验证就别说成功。**

## 0. 先试 `agent prep`（一条命令）

```bash
pad cli agent prep myapp web --recipe web --hot
pad cli agent prep myapp web @scope/a @scope/b --hot     # 顺带批量装插件
```

它按顺序做：profile 不在就按配方建（`web` / `tui` / `gateway`）→ 装并挂官方热重载 →
把 specs 作为一个批次装上去 → 返回 `pad.agent-prep/v1`。

- `ok=false`（退出 `3`）：看 `failedSpec` 和 `message`，**停**，别当成功往下走。
- `restartRequired=true`：要重启一次才生效 → `pad cli stop myapp web` 再 `pad cli run myapp web`。
- `hot` / `reason`：这台机器到底能不能热；不能热就老实说"要重启"。

**验证：** `pad cli hmr status myapp web --json`、`pad cli plugin list myapp web`。

下面的 A–F 是它没覆盖到、或需要分步控制时用的。

## A. 起一个全新实例并打开

```bash
pad cli release list                 # 有哪些已装版本
pad cli instance create myapp 0.2.1  # 名字 + DSH 版本
pad cli profile list myapp           # 看它自带哪些 profile
pad cli profile add myapp web web    # 没有 web 就加一个（第三个参数是 tui|web|gateway）
pad cli run myapp web                # 启动并记账
```

`run` 打印的是 JSON（surface / port / pid / url 一类）。**验证：**

```bash
pad cli ps --json                    # 应有 myapp / web 一行且 pid 活着
pad cli status --json                # Running
```

也有一条命令到位的写法：`pad cli instance create --pack <id|path> --name myapp --profile web`。

## A2. 打开一个已经存在的实例

先 `pad cli status --json`。已经在跑就别再 `run` 同一 profile——会撞 PA099（同一个 home+profile
不允许两个进程，否则账本会孤儿化）。要第二个窗口，就加/换一个 profile。

## B. 装一个整合包

```bash
pad cli pack preflight myapp --pack .\foo.pack.json --json --out preflight.json
pad cli pack install   myapp --pack .\foo.pack.json --dry-run
pad cli pack install   myapp --pack .\foo.pack.json --accept-risk --skip-missing
```

- `preflight` 会列出：插件逐项状态（不是 `Ok` 的才打）、已装账本、管理器是否缺、警告。
  **先把不是 Ok 的项和警告讲给用户听**，再说装不装。
- `install` 的退出码是契约：`0` = 已应用；`2` = needs-decision（缺 `--accept-risk` 或 `--skip-missing`）。
  拿到 `2` 不要重跑同一条命令，补齐选项或去问用户。
- `--pack` 既吃目录行里的 id，也吃 `.pack.json` / `.pack.zip` 的路径；找不到是 PA012。

**验证：** `pad cli pack list myapp` 里该包账本状态变成已装；`pad cli plugin list myapp <profile>` 能看到新插件。

## C. 改实例（dev 会话）

```bash
pad cli dev start   myapp --profile web
pad cli dev apply   myapp --spec @scope/pkg --action add
pad cli dev verify  myapp
pad cli dev commit  myapp --dry-run    # 看会写什么
pad cli dev commit  myapp
```

- `apply` 之后改动**只在本次运行生效**，实例文件没动——这正是可以放心试的原因。
- 想先看行为：`pad cli run myapp web --dev`，启动时带试装覆盖层。
- `verify` 三条判据：`config-compiles`、`plugins-resolve`、`source-untouched`。
- `commit` 验证失败默认拒绝（PA242）。只有用户明说"知道没过也要提交"才 `--force`，并在回复里写明。
- 不想改：`pad cli dev discard myapp`。**实例保持原样，不需要回滚。**

**验证：** `dev status` 应无 pending、会话 `committed`；`checkpoint list myapp` 多一条。

## D. 导出给别人 / 换机器

```bash
pad cli portable-export .\out\pad-portable myapp
```

需要单文件 PAD，否则 PA117。整包只想导配置：`pad cli packforge export-instance <home> --file out.dspack`。
**验证：** 目标目录里有可执行 + 实例目录，直接跑一次 `PAD.exe cli status`。

## E. 跳到一个已有会话

```bash
pad cli session ref <session> --json
pad cli session open myapp <session> --no-exec   # 只规划，看它准备做什么
pad cli session open myapp <session>             # 真执行
```

先 `--no-exec`。计划不对就停，别硬跑。找不到可用 surface 是 PA130。

## F. 排障顺序

| 症状 | 先看 | 再动 |
|---|---|---|
| 起不来 / 秒退 | `pad cli crash myapp`、`pad cli logs myapp` | 日志里的第一处错误，不要从最后一行倒推 |
| 说已经在跑（PA099） | `pad cli ps --json` | `pad cli stop myapp web`，或 `node close <home> --profile web` |
| 端口被占（PA004） | `pad cli procs --json` | 换 profile/端口，别杀无关进程 |
| 不知道 dsh CLI 在哪（PA093/94） | `pad cli where --json`、`pad cli inspect <home>` | 设 `launch.dshCli` 或 `--bin` |
| profile 没起 endpoint（PA080） | `pad cli logs` | 多半是 dsh 本身起不来，转去看 DSH |
| 包找不到（PA012） | `pad cli pack list` | 给绝对路径 |
| 验证不过（PA242） | `pad cli dev verify` 的输出 | 修 spec，或明确 `--force` |

排障时**不要**：`taskkill /IM node.exe`、手改 `instance.json`、删 `instances/*/home`。
这三件事都会让账本和现实脱节，而 PAD 的下一句话就是基于账本的。
