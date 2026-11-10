# 报错与退出码

## 形状

```
error[PA026]: no such instance
 --> instances/foo
  |
  | foo
  |
  = help: pad cli instance list
```

| 段 | 含义 | 怎么用 |
|---|---|---|
| `error[PAxxx]` | code 语义**永久不变** | 查下表，或按前缀猜领域 |
| headline | 一句英文结论 | 翻成用户的话，别原样丢给他 |
| `-->` | 位置（实例 / home / profile / 包路径） | 判断是"目标不存在"还是"目标不对" |
| `\|` 块 | 细节 | 多行时只挑关键那行说 |
| `= help:` | **可执行**的下一步 | 直接跑；不要自己发明参数 |

- 错误走 **stderr**，退出码 `1`。`--json` 只保证 stdout 干净，**不改变错误通道**——所以机器判断靠退出码 + code，不靠解析 stdout。
- 意外异常（非 PadError）会多打一段 stack trace，说明是 PAD 的 bug，不是你用错了。

## 退出码

| 码 | 场景 |
|---|---|
| `0` | 成功（含 `pack install` 的 installed / incomplete） |
| `1` | `PadError`（已渲染）或未捕获异常；`credentials has` 未配置也返回 1 |
| `2` | 参数不够（stderr `usage: …`）或 `pack install` 的 `needs-decision` |
| `130` | 中断 |

**`2` 是分叉不是失败**：补参数，或者把决定交回用户。

## 常见码

### 环境 / 安装

| 码 | 含义 | 通常怎么解 |
|---|---|---|
| PA001 | 没有装任何 DSH release | `release list` → `release install <ver>` |
| PA009 | 那个 `DSH_HOME` 不存在 | 换路径，或先 `instance create` |
| PA011 | PATH 上没有 node | 让用户装 node，别自己下二进制 |
| PA040 / PA071 | 这台机器上找不到 DSH home | `sniff` / `discover` 先看，再 `adopt` |
| PA117 | 便携导出需要单文件 PAD | 用打包版 PAD，或改导 `packforge export-instance` |
| PA120 | 更新包 sha256 不符 | 停了，让用户重下；不要 `--force` 类绕过 |

### 实例 / 版本

| 码 | 含义 | 通常怎么解 |
|---|---|---|
| PA006 | 版本还被实例 pin 着，不能删 | 先 `instance pin <id> <ver>` 换版本 |
| PA020 | 另一个实例已经用了这个 home | 一个 home 只归一个实例；选别的目录 |
| PA022 / PA023 | 名字没有可用 ascii / 找不到空闲名字 | 让用户起个英文名 |
| PA025 | 桌面壳没起来 | 看 `logs`；多半是 exe 路径或用户数据目录 |
| PA026 | 实例不存在 | `instance list`；注意 `--help` 这类参数也会被当实例名 |
| PA098 / PA102 | 不是 DSH home / 未知实例 | `inspect <home>` 看构成 |
| PA121 | 这个 home 属于一个正在跑的客户端 | 先 `stop` 再操作 |

### 运行 / surface

| 码 | 含义 | 通常怎么解 |
|---|---|---|
| PA004 | 端口被占 | `procs --json` 看是谁；换 profile/端口 |
| PA079 | dsh 进程起不来 | `logs`；先确认 node 与 dsh 版本 |
| PA080 | profile 一直没报 endpoint | 多半 dsh 自己失败；转 DSH 排错 |
| PA081 / PA082 | 账本里没这个 node / 停不掉 | `ps` 核对；必要时 `node list <home>` |
| PA083 | 终端立刻退出 | 看 `--bin` 对不对、脚本内容 |
| PA084 | 未知 surface | `pad cli surfaces` |
| PA085 | 这个 surface 需要可执行文件 | 用 CLI 并传 `--exe` |
| PA093 / PA094 | 不知道 dsh CLI 在哪 | 设 `launch.dshCli`，或 `--bin <bin.js>` |
| PA095 / PA146 | app / tauri surface 缺 `--exe` | 传 `--exe` |
| PA099 | 这个 profile 已经在跑 | `stop <instance> <profile>`；别开第二个 |
| PA160 | 这个 TUI 不在 PAD 实例里 | 从 PAD 起的 TUI 才有跳转上下文 |

### 包

| 码 | 含义 | 通常怎么解 |
|---|---|---|
| PA012 | 没有这个包文件 | `pack list`；给绝对路径 |
| PA014 | 包里的本地插件目录缺了 | 包不完整，退回给作者 |
| PA027 | 找不到 pack-agent 仓库 | 设 `PACK_AGENT_REPO` |
| PA105 | 那是普通依赖，不是 profile 层 | 换成真正的插件 spec |
| PA140 | `.dspack` 没找到 | 检查路径 |
| PA145 | 包没声明 dsh 版本 | 包该补 `dsh.version` |
| PA151 | 不支持的包类型 | 看 `packforge inspect` 的 type |

### 开发会话

| 码 | 含义 | 通常怎么解 |
|---|---|---|
| PA203 | checkpoint 不存在 | `checkpoint list` |
| PA240 | 实例没有 profile | `profile add` 或传 `--profile` |
| PA241 | 没有可提交的改动 | 先 `dev apply` |
| PA242 | 验证没过，拒绝提交 | 看 `dev verify` 的三条判据；用户明确要求才 `--force` |
| PA243 | 会话已经关了 | `dev start` 重开 |
| PA244 / PA245 | 缺 spec / 没有这个待决 trial | 补参数，或 `trial list` |

### 会话跳转

| 码 | 含义 | 通常怎么解 |
|---|---|---|
| PA130 | 这个会话没有可用 surface | `surface list`；先起一个 web |
| PA132 | session id 不规范 | `session ref <session> --json` 解析后再用 |
| PA133 | 没有正在跑的 web surface | `run <instance> web` |
| PA136 | `pad-gateway.json` 缺 | 该 profile 没装 gateway |

### 实验开关

| 码 | 含义 | 通常怎么解 |
|---|---|---|
| PA096 | 实验特性没开 | `experimental enable <id>`（先问用户） |
| PA097 | 没有这个实验特性 | `experimental list` |

> 完整清单以源码 `pad/Core/*.cs` 里的 `new PadError(...)` 为准，当前共 66 个唯一 code。
> 这张表只收运行期最常见的；遇到表外的，读 `-->` 和 `= help:` 就够了。
