# PAD

DeepSeek Harness 的启动器。装 DSH 版本、建隔离实例、点一下启动。

[下载 Windows 版](https://github.com/sakikoTGW/PAD/releases/latest/download/pack-agent-for-DSH.exe) · [发行说明](https://github.com/sakikoTGW/PAD/releases)

双击 `pack-agent-for DSH.exe`。第一次：

1. **下载 DSH** — 选一个发行号
2. **新建** — 一份隔离实例
3. **启动**

已经有 DSH 的文件夹，用「添加已有文件夹」。API Key 在设置里填一次。

`pad cli` 和窗口是同一个 exe：

```powershell
& '.\pack-agent-for DSH.exe' cli instance list
& '.\pack-agent-for DSH.exe' cli run <实例>
```

整合包拖进窗口即可。制作、导出、便携包见下面。

---

本仓库从 [sakikoTGW/pack-agent](https://github.com/sakikoTGW/pack-agent) 的 `agent-pack-dsh/` 拆出，保留提交历史。TS 工具链仍在主仓库。

```
pad/          .NET 9 WPF 窗 + pad cli（pack-agent-for DSH.exe）
packmaker/    整合包生成器
modpack/      读源码 / 跑 pad cli 的回归测试
docs/         启动器设计
portable/     便携包示例
```

## 构建

需要：.NET 9 SDK、Go 1.22+、Bun。

```sh
cd packmaker && go build -o bin/packmaker.exe .
dotnet build pad/Pad.csproj -c Debug
bun run modpack/pad-window.test.ts
bun run modpack/pad-pack.test.ts
```

## 便携包 / 整合包

- **便携包**：只带清单 `.pack.zip`，首次启动联网装依赖
- **整合包**：PAD + 版本库 + 实例，单文件 `.7z`
- **单文件 PAD**：`packmaker.exe` 和 7-Zip 嵌在 exe 里，发行只需要一个 `pack-agent-for DSH.exe`

## License

代码 **MIT**（[LICENSE](LICENSE)）。随仓库的 7-Zip 为 **LGPL-2.1+**。详见 [NOTICE](NOTICE)。
