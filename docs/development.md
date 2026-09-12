# 开发说明

`src/Shuo` 是 WinUI 应用项目；`worker` 负责本地录音和转写；`test` 保存 Node 与 C# 测试。图标设计稿放在 `design`，应用只使用 `src/Shuo/Assets/AppIcon.ico`。

## 开发与验证

本地预览、跨平台验证、产物目录和自动启动规则见根目录 [AGENTS.md](../AGENTS.md)。Windows 预览统一输出到 artifacts/preview/，构建后附带对应架构的 Node 并直接启动应用。

按改动范围运行 npm test、test/TextCleanup.Tests 或 test/Translation.Tests；macOS 的服务端验证见[自托管部署](selfhosted.md)。WinUI 界面与宿主代码需要在 Windows 构建验证。

朗读协议验证使用 `dotnet run --project test/Reading.Tests --artifacts-path artifacts/test/reading`，覆盖长文本分段、SSE 音频及结束事件、服务错误和取消，不调用真实云端。Windows 播放测试使用 `dotnet run --project test/Reading.Playback.Tests --artifacts-path artifacts/test/reading-playback`，通过静音 PCM 验证设备播放完成、暂停和取消。系统复制事务使用 `dotnet run --project test/Reading.Input.Tests` 验证选区、空选区、富文本恢复和并发复制保护。跨应用选区另在 Windows 预览中验证。

安装器构建命令见[安装指南](setup.md#1-在构建电脑生成安装包)。`scripts/package.mjs` 固定使用 `.config/dotnet-tools.json` 中的 Velopack CLI，应用依赖与 CLI 版本必须一致。独立输出目录避免旧版本文件混入新包。

## 输入流程

`MainWindow` 注册全局快捷键并启动 Node worker。worker 以 16 kHz 采集音频，停止录音后交给本地 transcribe-cpp 模型转写，按配置转换中文繁简体。宿主收到最终文本后执行可选的中英文排版整理和末尾句号处理，将最终文字追加到本地历史文件，再通过剪贴板和 Ctrl+V 输入到前台应用。

`OverlayWindow` 在连接、录音及等待最终转录期间显示，粘贴完成后收起；不激活、不接收焦点。它按前台窗口所在显示器的工作区定位；无法确定显示器时使用主屏。窗口样式中的不激活和工具窗口标志保证浮层不会夺走目标输入框的焦点。

`GlobalHotkey` 使用 `RegisterHotKey`，通过主窗口的 `WM_HOTKEY` 接收事件。主窗口最小尺寸由 WinUI presenter 约束。关闭主窗口只隐藏设置界面；托盘的“退出”会取消待粘贴任务、释放快捷键并关闭 worker。

主窗口使用 `NavigationView` 切换常规、转录服务、转录历史和文本整理区域，切换页面时取消未保存的快捷键编辑。内容宽度按右侧视口减去两侧留白计算，最大为 920 DIP，并在右侧区域内居中。浮窗使用桌面亚克力背景，监听 `UISettings.ColorValuesChanged`，在 UI 线程同步更新浅色或深色材质、边框和文字颜色；不支持亚克力时使用纯色背景。浮窗保持不激活，通过背景配置保留亚克力效果；文字通过整体字形遮罩在左侧淡出，并平滑滚动显示最新内容。蓝点随音量缩放，重音触发扩散波纹，安静时缓慢呼吸；关闭系统动画时保持静态。关闭时释放材质控制器并解除监听。

`TranscriptHistory` 将最终文字、完成时间和模型名称追加到 `%LOCALAPPDATA%\Shuo\history.jsonl`，每次写入后刷新到磁盘。读取时跳过空行，将 JSON 行紧凑序列化为可读 UTF-8，保留正文内空格，以原子替换和独立备份保留原文件，未知字段和损坏行保持原有内容；随后跳过并报告损坏行；仅在文件末尾缺少换行时补换行，避免残行吞掉新记录，每次追加恰好一行。历史页按写入顺序倒序显示，每次增加 50 条，不截断磁盘记录。保存失败会显示错误并继续粘贴；粘贴失败不删除已保存记录。`partial` 只更新浮窗，不进入历史。

## 进程协议

实时朗读由 C# 宿主直接执行：`ReadingInput` 在工作线程通过 UI Automation 查询前台选区；未获取到文字时，`ClipboardSelection` 暂存剪贴板各格式、向原前台窗口发送复制操作，核对剪贴板序号和所有者后读取文本，再有条件地恢复原剪贴板。复制结果中的空选区标记用于排除整行复制。`ReadingText` 按 UTF-8 字节数限制长请求，普通段落保留上下文；`DoubaoSpeechClient` 调用豆包 V3 SSE 接口，将 24 kHz 单声道 PCM 持续写入 `ReadingAudioBuffer`。整个会话只创建一个 WASAPI 播放器；缓冲不足时输出静音，只有明确完成后才返回 EOF。环形缓冲保留跨包采样，音量由设备读取的 PCM 计算，主线程定时更新状态指示器。未收到成功结束事件的连接按失败处理。`ReadingSettings` 保存开关、音色、语速和快捷键，独立 Key 进入凭据管理器。朗读期间禁止听写、系统音频翻译和安装更新。

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

`configure-backend` JSON 命令携带 `provider`（`local`、`doubao`、`qwen` 或 `selfhosted`）及内存中的 `config`，返回 `backend-configured` 或 `backend-error`。`test-cloud` 检查云端调用并返回 `cloud-tested` 或 `cloud-test-error`。百炼的配置使用 `apiKey` 和 `region`，豆包使用原有凭据与资源字段，固定发送 enable_ddc=true、enable_itn=true 和 enable_punc=true；旧 semanticSmoothing 字段不再读取，保存设置时移除；`transcriptionProvider` 保存当前服务，未设置时按旧版 `doubao.enabled` 读取。云端连接期间发送 `connecting`，界面禁止更改服务。凭据由 WinUI 宿主从 Windows PasswordVault 读取，经 worker 标准输入传递，不写入命令行、配置文件或事件输出。

`LocalModelDownload` 从固定 Hugging Face 修订下载 Qwen3-ASR-0.6B Q8_0，使用系统代理，在临时文件中校验长度与 SHA-256 后替换目标；取消和失败会移除临时文件。`worker/models.mjs` 始终扫描配置文件旁的 `models` 目录，并从当前模型路径确定额外扫描范围，识别 Hugging Face 的仓库与快照层级，不遍历缓存 blobs 或其他目录。切换命令再次检查候选列表；先释放旧模型，再加载新模型，成功后以临时文件替换配置，仅更新 `model` 字段。失败时保留原配置，下次听写重新加载原模型。命令队列与界面状态共同避免录音、转录和切换重叠。

本地模型在多次听写间复用，停止录音后执行转写。豆包模式通过 `worker/doubao.mjs` 建立双向流式 WebSocket，每 200 ms 发送一包 16 kHz、16-bit 单声道 PCM。`partial` 事件携带当前完整预览文本；最后一个音频包带结束标记，收到服务端最终包才发送 `transcript`。断线或超时不提交未确认文本。宿主退出时会等待 worker，超过五秒则终止子进程。

百炼根据 qwen.model 选择识别模型，旧配置默认使用 fun-asr-realtime。worker/qwen.mjs 使用 DashScope 双向 WebSocket 协议调用 Fun-ASR。录音启动后发送 run-task，连接期间的音频暂存在本机，收到 task-started 后按采集顺序发送，每 200 ms 音频组成一包二进制 PCM。按 sentence_id 排序并更新句子快照，sentence_end 确认句子完成；心跳包不进入文本。停止录音时发送 finish-task，收到 task-finished 且所有句子完成后才提交最终文字。应用固定使用北京端点与对应凭据；内部 provider 仍为 qwen。测试使用本地 WebSocket 服务验证协议，不调用真实云端。

自托管模式通过 `worker/selfhosted.mjs` 连接服务根地址对应的 `/v1/asr` WebSocket，`selfhosted.url` 和 `selfhosted.model` 分别保存地址与模型选择。客户端先启动麦克风，再建立连接并发送 `start`（协议版本 1、16 kHz、`pcm_s16le`、语言和模型）。等待 `ready` 期间的音频暂存在本机，服务就绪后先按顺序发送暂存音频，再发送后续录音，每 200 毫秒音频组成一包。连接期间也可以停止录音；客户端立即释放麦克风，待服务就绪后发送完整录音并结束会话。连接超时或失败时停止录音、清除暂存音频并报告错误。服务按会话选用预热的模型，客户端校验 `ready.model` 与选择一致。`partial` 是可替换的完整预览；停止时发完尾包，再发送 `finish`，只有收到 `final` 才提交文字。实际模型名称随最终事件传回宿主并写入历史。

`asr-server` 是独立的 macOS Python 服务，通过单个推理线程加载和调用 MLX 模型。WebRTC VAD 按停顿分段：默认等待 2 秒静音；配置更短静音时，累计语音不足 2 秒仍等待至少 2 秒静音；达到 30 秒软上限后，300 毫秒静音即可确认，35 秒硬上限强制切分。停止录音立即提交尾段，不等待静音。任务队列合并同段的旧预览，但保留确认段落的顺序；积压超过三个待识别段落时整次听写失败，不丢弃音频后继续提交。服务拒绝同时进行的第二路听写，断线和取消后释放会话。安装、使用及服务端验证见[自托管部署](selfhosted.md)。

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

转录服务选择通过 `CloudSettings.SaveProvider` 单独持久化，不要求先填写凭据。凭据修改时立即持久化，等待 500 ms 且焦点离开凭据或地址输入框后配置 worker。宿主随后配置 worker；缺少凭据时阻止听写并提示填写。清空凭据会同步清除已保存值。本地和云端共用听写试用区。

千问实时识别由 worker/qwen-realtime.mjs 接入 qwen3-asr-flash-realtime，使用 session.update 配置 16 kHz PCM 与 VAD，收到 session.updated 后开始发送 Base64 音频。临时预览由 text 与 stash 拼接，completed 的 transcript 替换对应语句；停止时发送剩余音频和 session.finish，等待最终语句及 session.finished 后提交。两种百炼模型共用该平台独立保存的转录凭据，历史记录保存实际请求的模型名称。
