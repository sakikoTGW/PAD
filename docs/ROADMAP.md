# PAD Roadmap

按“从能用到好管”的顺序排。已完成的不删，留作验收记录。

## 当前已落地

- [x] 实例（home + 内核 + workspace）管理与收编
- [x] profile 识别 / 列表（`pad cli profile list`）
- [x] profile 改名 / 删除（`pad cli profile rename|remove`，desktop 保护、运行中拒绝）
- [x] 启动配置：一行 = (profile, bundle)，按 profile 过滤、显示 `profile <名字>`
- [x] 探测桌面端 / TUI 的 home（沿子进程 harness 归属，不再串实例）
- [x] 启动页：运行中的桌面端显示「切到窗口」+ 单独「关闭」
- [x] `pad cli self` / `running`：查自己在哪、有哪些在跑及配置
- [x] agent-preset：内置 id 从 `--dump-config` 补全（`pad cli preset list`）
- [x] agent-preset：复制 / 删除 / 绑定（`pad cli preset copy|remove|bind`）
- [x] 实验性：`desktop` profile 改名（仅 CLI `--experimental`，不进 UI；删除仍禁止）
- [x] 恢复出厂设置：`pad cli profile reset`（目录备份 + 空骨架 + 失败还原）
- [x] 锁死 profile 的插件管理：`desktop` 直写 package.json + pnpm（add/remove/update 与普通 profile 一致）
- [x] 启动行持久化：`pad.json.lastContainer`（bundle / shell / tui），启动页加载时还原
- [x] 启动反馈：等窗口出现才算成功；隐藏/最小化窗口自动恢复；超时清理进程并报失败
- [x] 热管理：识别 `dsh-hmr`（installed/mounted）、新 profile 自动开通、识别 hot-installer 的升级通道

## 下一里程碑：Agent 级别管理

目标：每个 agent（会话）有自己的**工具清单、人设、模型、权限**，PAD 能建、改、绑、换。

- [ ] **preset 库**：列出内置 id + 本地用户 preset（`<home>/.agent-presets/`），显示来源、工具数、绑定状态
- [ ] **preset 编辑器**：从当前版本的 `--dump-config` 行清单勾选工具（tool-fs / bash / web / subagent…），写 persona / agent-instructions，选模型与呈现方式（standard / ptc）
- [ ] **preset 绑定**：写 `agent-preset-registry.config.default` 到 profile 的 `cordis.patch.yml`（已有 `preset bind`，补 UI）
- [ ] **新建 preset**：生成 `<home>/.agent-presets/<id>/{agent.cordis.yml,preset.yml}` 骨架（内置源文件不在磁盘，不能复制，只能给模板）
- [ ] **导入 / 导出**：preset 目录打包，团队共享
- [ ] **启动配置三件套**：`(profile, bundle, preset)` 一起记住、一起切换，主页仍一个按钮
- [ ] **permission preset**：按会话选 `read-only` 等权限档，和工具白名单组合
- [ ] **校验**：preset 引用的工具行在目标版本是否存在（对照 dump），升级内核时提示缺失
- [ ] **子代理 preset**：确认 `tool-subagent` 是否支持指定 preset；支持则允许“审查员/规划师”分工

## 之后

- [ ] 整合包携带 preset（装完即是一套准备好的 agent）
- [ ] `desktop` profile 的只读展示与桌面端启动路由（已完成路由，展示待定）
- [ ] profile 图标 / 配图（目前跟随 bundle 图标）
- [ ] Release 便携版发布（`bin\Release\net9.0-windows\win-x64` 仍是旧版）

## 已知坑

- `desktop` profile 由桌面端独占，CLI 不能启动（`PA120`）；PAD 已改为路由到桌面端。
- 内置 agent-preset 的源文件随 DSH 打包，磁盘上没有；只能从 `--dump-config` 拿 id。
- `pad-hot-sync` 用 `link:` 安装时，pnpm 可能不建 node_modules 链接，需要手动补 junction 或 `pnpm install --force`。
