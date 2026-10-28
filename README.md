# PAD — pack-agent for DSH

PAD 是 DeepSeek Harness 的桌面启动器（.NET 9 / WPF）和 `pad cli`：探测本机 DSH、建隔离实例、导入/制作整合包、导出便携包。

本仓库是从 `sakikoTGW/pack-agent` 的 `agent-pack-dsh/` 子树拆出来的独立仓库（保留提交历史）；TS 工具链（`packagent dsh`、投影编译器、插件管理口）仍在主仓库。

```
pad/          PAD 启动器：.NET 9 WPF 窗 + pad cli（pack-agent-for DSH.exe）
packmaker/    Go 写的整合包生成器（packmaker make --spec ...）
modpack/      PAD 相关的 TS 回归测试（读源码/跑 pad cli）
docs/         启动器设计（launcher-design.md / launcher-architecture.md）
portable/     便携包示例
```

## 构建

需要：.NET 9 SDK、Go 1.22+、Bun（跑测试）。

```sh
# packmaker（Pad.csproj 会把它拷到输出目录）
cd packmaker && go build -o bin/packmaker.exe .

# PAD
dotnet build pad/Pad.csproj -c Debug

# 回归测试（Windows）
bun run modpack/pad-window.test.ts
bun run modpack/pad-pack.test.ts
bun run modpack/pad-discover.test.ts
bun run modpack/pad-depth.test.ts
```

`pad-pack` 的端到端部分需要主仓库的 `bin/packagent.js`（`PACK_AGENT_REPO`）；本仓库单独跑时会跳过实跑、只跑源码回归。

## 便携包 / 整合包

- **便携包**：只带清单 `.pack.zip`，首次启动联网装依赖；
- **整合包**：PAD + 版本库 + 实例，单文件 `.7z`（内置 7-Zip，`-snl` 保留 junction，收件方启动时自动重接链接）；
- 导出支持断点续传（固定暂存目录，跳过已复制文件/已建链接）与暂停/继续；
- **单文件 PAD**：`packmaker.exe` 和 7-Zip（`7z.exe`/`7z.dll`）都嵌在 PAD 里，首次使用时释放到 `%LOCALAPPDATA%\pack-agent-dsh\tools\`，所以发行只需要一个 `pack-agent-for DSH.exe`。

## 7-Zip

`pad/Assets/7z/` 随仓库附带 7-Zip 的 `7z.exe`/`7z.dll`（来自 https://www.7-zip.org/ ，LGPL，`License.txt` 已一并附带），用于整合包压缩；找不到时回退 .NET 压缩。

## 相关仓库

- 主仓库（TS 工具链 / 投影 / 插件）：https://github.com/sakikoTGW/pack-agent

## License

代码采用 **MIT License**（见 [LICENSE](LICENSE)）。

随仓库附带的 7-Zip 二进制（`pad/Assets/7z/`）为 **LGPL-2.1+**，DeepSeek 名称/鲸鱼标识归其所有者所有（仅用于识别 DSH 运行时）；导出的整合包/便携包里可能包含第三方软件，按各自的许可证分发。详见 [NOTICE](NOTICE)。
