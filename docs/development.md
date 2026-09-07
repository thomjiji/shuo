# 开发说明

`src/Shuo` 是 WinUI 应用项目；`worker` 负责本地录音和转写；`test` 保存 Node 与 C# 测试。图标设计稿放在 `design`，应用只使用 `src/Shuo/Assets/AppIcon.ico`。

## 开发与验证

在仓库根目录执行：

```powershell
npm ci
dotnet run --project src/Shuo/Shuo.csproj
```

验证命令：

```powershell
npm test
dotnet run --project test/TextCleanup.Tests/TextCleanup.Tests.csproj
dotnet build Shuo.slnx --configuration Debug
```

安装器构建命令见[安装指南](setup.md#1-在构建电脑生成安装包)。`scripts/package.mjs` 固定使用 `.config/dotnet-tools.json` 中的 Velopack CLI，应用依赖与 CLI 版本必须一致。独立输出目录避免旧版本文件混入新包。

## 输入流程

`MainWindow` 注册全局快捷键并启动 Node worker。worker 以 16 kHz 采集音频，停止录音后交给本地 transcribe-cpp 模型转写，按配置转换中文繁简体。宿主收到最终文本后执行可选的中英文排版整理、口水词过滤和末尾句号处理，将最终文字追加到本地历史文件，再通过剪贴板和 Ctrl+V 输入到前台应用。

`OverlayWindow` 在连接、录音及等待最终转录期间显示，粘贴完成后收起；不激活、不接收焦点。它按前台窗口所在显示器的工作区定位；无法确定显示器时使用主屏。窗口样式中的不激活和工具窗口标志保证浮层不会夺走目标输入框的焦点。

`GlobalHotkey` 使用 `RegisterHotKey`，通过主窗口的 `WM_HOTKEY` 接收事件。主窗口最小尺寸由 WinUI presenter 约束。关闭主窗口只隐藏设置界面；托盘的“退出”会取消待粘贴任务、释放快捷键并关闭 worker。

主窗口使用 `NavigationView` 切换常规、转录服务、转录历史和文本整理区域，切换页面时取消未保存的快捷键编辑。内容宽度按右侧视口减去两侧留白计算，最大为 920 DIP，并在右侧区域内居中。浮窗使用桌面亚克力背景，监听 `UISettings.ColorValuesChanged`，在 UI 线程同步更新浅色或深色材质、边框和文字颜色；不支持亚克力时使用纯色背景。浮窗保持不激活，通过背景配置保留亚克力效果；文字通过整体字形遮罩在左侧淡出，并平滑滚动显示最新内容。蓝点随音量缩放，重音触发扩散波纹，安静时缓慢呼吸；关闭系统动画时保持静态。关闭时释放材质控制器并解除监听。

`TranscriptHistory` 将最终文字、完成时间和模型名称追加到 `%LOCALAPPDATA%\Shuo\history.jsonl`，每次写入后刷新到磁盘。读取时先将旧 JSON 行重新序列化为可读 UTF-8，以原子替换和独立备份保留原文件，未知字段和损坏行保持原有内容；随后跳过并报告损坏行；追加前补换行，避免上一次中断留下的残行吞掉新记录。历史页按写入顺序倒序显示，每次增加 50 条，不截断磁盘记录。保存失败会显示错误并继续粘贴；粘贴失败不删除已保存记录。`partial` 只更新浮窗，不进入历史。

## 进程协议

宿主向 worker 的标准输入逐行发送 `toggle`、`shutdown` 或用于刷新模型列表的 `models`。切换模型使用 JSON 行 `{"type":"select-model","path":"模型绝对路径"}`。worker 的标准输出只发送 JSONL 事件，诊断写入标准错误。

| 事件 | 含义 |
| --- | --- |
| `ready` | 服务已准备好，携带模型标识和可选的 `autocorrectPath`。 |
| `models` | 可选模型位于 `models` 数组，当前路径位于 `modelPath`。 |
| `model-changed` | 模型已加载并保存，返回当前 `model` 和 `modelPath`。 |
| `model-error` | 切换失败，`message` 说明原因，`modelPath` 仍指向原选择。 |
| `model-list-error` | 模型目录无法读取。 |
| `recording` | 已开始录音。 |
| `audio-level` | 录音期间每约 64 ms 返回归一化音量 `level`（0 到 1），驱动浮窗波纹。 |
| `transcribing` | 录音结束，正在转写。 |
| `transcript` | 最终文本位于 `text`，可粘贴。 |
| `empty` | 没有可输入的文本。 |
| `busy` | 当前操作尚未结束。 |
| `error` | 失败原因位于 `message`。 |
| `stopped` | worker 已完成关闭。 |

`configure-backend` JSON 命令携带 `provider`（`local` 或 `doubao`）及内存中的 `config`，返回 `backend-configured` 或 `backend-error`。`test-cloud` 检查云端调用并返回 `cloud-tested` 或 `cloud-test-error`。云端连接期间发送 `connecting`，界面禁止更改服务。凭据由 WinUI 宿主从 Windows PasswordVault 读取，经 worker 标准输入传递，不写入命令行、配置文件或事件输出。

`worker/models.mjs` 从当前模型路径确定扫描范围，识别 Hugging Face 的仓库与快照层级，不遍历缓存 blobs 或其他目录。切换命令再次检查候选列表；先释放旧模型，再加载新模型，成功后以临时文件替换配置，仅更新 `model` 字段。失败时保留原配置，下次听写重新加载原模型。命令队列与界面状态共同避免录音、转录和切换重叠。

本地模型在多次听写间复用，停止录音后执行转写。豆包模式通过 `worker/doubao.mjs` 建立双向流式 WebSocket，每 200 ms 发送一包 16 kHz、16-bit 单声道 PCM。`partial` 事件携带当前完整预览文本；最后一个音频包带结束标记，收到服务端最终包才发送 `transcript`。断线或超时不提交未确认文本。宿主退出时会等待 worker，超过五秒则终止子进程。

## 配置与发布

`SHUO_SETTINGS` 可覆盖配置路径，`SHUO_NODE` 可覆盖 Node 路径；旧的 `WINDOWS_DICTATION_SETTINGS`、`WINDOWS_DICTATION_NODE` 仍作为后备。默认路径优先使用 `%LOCALAPPDATA%\Shuo\settings.json`，新文件不存在时继续使用已有的 `%LOCALAPPDATA%\WindowsDictation\settings.json`，保留旧用户的全部偏好。首次配置导入 Pi 的规则见 [README](../README.md#配置)。

应用与 worker 必须采用相同的配置路径优先级。设置保存保留不属于当前界面的字段；文本整理选项按每次录音快照使用。

项目显式链接仓库根目录的 worker、node_modules 和配置样例，发布时保持它们相对于 EXE 的路径。安装包包含 .NET、所需的 Windows App SDK 组件和 Node；模型仍是外部文件。

WinUI 的 XBF 和 PRI 资源通过项目中的发布 targets 纳入输出。调整这些规则后，应从新的安装目录启动程序，确认界面和包内 Node worker 都能启动。

## 安装与应用内更新

`Program.Main` 在 WinUI 初始化前调用 `VelopackApp.Run()`，安装和更新钩子因此不会启动窗口或 worker。普通启动再初始化 WinUI 的 COM 包装器、应用线程和同步上下文，创建 `App`。安装标识为 `ShuoDesktop`，与 `%LOCALAPPDATA%\Shuo` 数据目录分开；配置路径和凭据键保持原有规则。

`MainWindow.Updates.cs` 从公开 GitHub Releases 的稳定版本读取更新清单，启动及每 6 小时检查。用户点击后才下载，Velopack 校验包完整性，再启动等待当前进程退出的更新器；现有退出流程停止 worker 后退出，更新器完成文件替换并重启。启动时自动应用已下载更新被禁用，始终由用户点击。检查和安装各自防止重入，录音、转录、粘贴及模型切换期间不允许安装。

标签版本传给 MSBuild 的 `Version` 和 Velopack 的包版本，安装器文件名包含同一版本。Release 同时上传安装器、`releases.win.json` 和完整 `.nupkg`；更新客户端下载完整包，不依赖历史版本或额外服务。`assets.win.json` 是打包工具内部索引，不上传。未配置代码签名，安装器会显示未知发布者。

更新源验证：`dotnet run --project test/Update.Tests/Update.Tests.csproj -- <releases目录> <版本> ShuoDesktop`，检查版本发现、真实包下载校验、待重启状态和禁止降级。发布工作流在上传前运行它。
