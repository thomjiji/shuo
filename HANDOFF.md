# Windows Dictation 交接文档

## 任务目标

将先前放在 `pi-transcribe` 插件中的 Windows 全局听写宿主，演进成一个独立的原生 Windows app。app 不需要 Pi 进程运行；它有自己的 WinUI 3 界面、全局热键、底部状态 indicator、配置文件和 Node 转写 worker。

当前优先级是继续完善这个独立 app，而不是继续修改 `C:\Users\thom\git\pi-transcribe`。

## 立即开始

```powershell
Set-Location "$HOME\git\windows-dictation"
git status
git log -1 --oneline
npm test
dotnet build --configuration Debug
```

当前 app 可能仍在运行。交接时的进程是发布版 `WindowsDictation.exe` 和它的 Node worker。若要更新 `node_modules`、重建或测试热键，请先在 app 里点“退出”，不要让两个 host 同时注册 `Ctrl+Alt+\`。

```powershell
Get-CimInstance Win32_Process -Filter "Name = 'WindowsDictation.exe' OR Name = 'node.exe'" |
  Where-Object { $_.CommandLine -match 'windows-dictation' } |
  Select-Object ProcessId, Name, CommandLine
```

## 当前状态

- 仓库：`C:\Users\thom\git\windows-dictation`
- 分支：`main`
- 最新提交：`6a5396c feat(app): bootstrap native Windows dictation`
- GitHub remote：尚未创建。不要在未确认仓库名和公开性前自行创建或推送 remote。
- 当前运行入口：`C:\Users\thom\git\windows-dictation\publish\win-x64\WindowsDictation.exe`
- 开发工具：Windows `10.0.26200`、.NET SDK `10.0.400`、Node `v22.23.1`、PowerShell `7.6.5`。

## 已实现并实际验证的能力

- 使用 WinUI 3 和 Windows App SDK `2.4.0` 的原生桌面 app；运行时不使用 PowerShell。
- `Ctrl+Alt+\` 全局热键，使用 `RegisterHotKey` 的 `Ctrl + Alt + MOD_NOREPEAT + VK_OEM_5`。
- 按第一次热键开始录音，第二次停止录音、调用本地模型，最终将文本复制并发送 `Ctrl+V` 到原前台 app。
- 不抢焦点的 WinUI 底部居中 indicator：录音和批量转写期间仅显示旋转进度环，完成或失败即隐藏。
- app 使用自己的 `%LOCALAPPDATA%\WindowsDictation\settings.json`。首次启动时仅导入一次旧 Pi 配置，之后不再读取 Pi 配置。
- worker 复用本地 Qwen3-ASR 0.6B Q8 GGUF、系统默认麦克风、OpenCC 和现有 autocorrect 可执行文件。
- 发布目录会复制 worker 和它的 `node_modules`；模型不复制，仍由设置中的本地路径引用。
- 已从发布版 exe 直接启动，确认它拉起发布目录内的 worker，并通过 UI Automation 确认状态为“准备好了”。
- 已发送合成 `Ctrl+Alt+\`，确认 app 状态变为“正在录音”；通过 app 的“退出”按钮确认 app、worker 和热键均被释放。

## 目录与职责

```text
windows-dictation/
├── MainWindow.xaml(.cs)          # Mica 主窗口、状态、按钮、热键协调
├── OverlayWindow.xaml(.cs)       # 无焦点、置顶、底部居中 indicator
├── Services/
│   ├── NativeMethods.cs           # user32/comctl32 P/Invoke、粘贴、显示位置
│   ├── GlobalHotkey.cs            # WinUI HWND 子类化和 RegisterHotKey
│   ├── DaemonClient.cs            # Node 子进程和 JSONL stdin/stdout 协议
│   └── TranscriptPaster.cs        # autocorrect、Clipboard、SendInput Ctrl+V
├── worker/dictation-daemon.mjs    # 录音、transcribe-cpp、OpenCC、设置迁移
├── test/worker.test.mjs           # worker 设置迁移和 PCM 单元测试
├── settings.example.json          # 独立配置样例
└── WindowsDictation.csproj        # WinUI、发布复制规则
```

## 架构与协议

```text
前台 app
  ↑ SendInput Ctrl+V
WinUI host
  ├── RegisterHotKey Ctrl+Alt+\
  ├── OverlayWindow
  ├── Clipboard + optional autocorrect.exe
  └── stdin/stdout JSONL
          ↓
Node worker
  ├── pvrecorder
  ├── transcribe-cpp + GGUF
  └── opencc-js
```

WinUI host 给 worker 的 stdin 命令只有 `toggle` 和 `shutdown`。

worker 的 stdout JSONL 事件是 `ready`、`recording`、`transcribing`、`transcript`、`empty`、`busy`、`error`、`stopped`。`ready` 带 `model` 和可选 `autocorrectPath`；`transcript` 带最终 `text`。当前协议没有 partial 或 stable/unstable 文本事件。

`MainWindow` 使用 `SetWindowSubclass` 监听自身 HWND 的 `WM_HOTKEY`。不要改成全局键盘 hook；当前 `RegisterHotKey` 是更小且已验证的实现。

`OverlayWindow` 使用 `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`，通过 `SetWindowPos(..., HWND_TOPMOST, SWP_NOACTIVATE)` 显示在当前前台窗口所在屏幕的工作区底部。不要让 overlay 激活，否则最终粘贴会送错窗口。

## 配置与本地资源

独立配置默认位置：`%LOCALAPPDATA%\WindowsDictation\settings.json`。

可用环境变量：

- `WINDOWS_DICTATION_SETTINGS`：覆盖独立设置文件路径。
- `WINDOWS_DICTATION_NODE`：覆盖 `node.exe` 路径。
- `PI_CODING_AGENT_DIR`：仅首次从旧 Pi 配置迁移时使用。
- `PI_TRANSCRIBE_AUTOCORRECT_PATH`：仅首次迁移时优先选用的 autocorrect 路径。

首次迁移源：`%USERPROFILE%\.pi\agent\pi-transcribe.json`。迁移会复制模型、语言、中文输出和麦克风设置，并把找到的 autocorrect 路径写进独立配置。后续删除或修改 Pi 配置不会影响独立 app。

交接时已生成的独立配置指向：

```text
模型：C:\Users\thom\.cache\huggingface\hub\models--handy-computer--Qwen3-ASR-0.6B-gguf\snapshots\e4e16599b900eb0cb36e524514756bb92eb092b7\Qwen3-ASR-0.6B-Q8_0.gguf
autocorrect：C:\Users\thom\.pi\agent\bin\autocorrect.exe
```

模型路径是外部资源，不会被 Git 或 publish 复制。autocorrect 是可选的；不存在或失败时，app 会粘贴原始转写文本。

## 运行、构建与发布

开发运行：

```powershell
Set-Location "$HOME\git\windows-dictation"
npm install
dotnet run
```

发布并直接启动：

```powershell
Set-Location "$HOME\git\windows-dictation"
dotnet publish --configuration Release --runtime win-x64 --self-contained false --output .\publish\win-x64
.\publish\win-x64\WindowsDictation.exe
```

`WindowsAppSDKSelfContained=true` 会把 Windows App SDK runtime 复制到 app 旁边。当前发布仍需要目标机的 .NET 10 Desktop Runtime 和 PATH 中可用的 Node.js；Node runtime 尚未打包。

不要使用 `--self-contained true`。在本机它会导致发布版启动时 `Microsoft.UI.Xaml.dll` APPCRASH。当前可靠且已验证的命令是上面的 `--self-contained false`。

`WindowsDictation.csproj` 的 `CopyWinUiResources` target 会在 publish 后复制 app 的 `*.xbf` 和 `WindowsDictation.pri`。没有它们时，发布 exe 会在 `Microsoft.UI.Xaml.dll` 内崩溃；不要删除这个 target。

`node_modules` 以 `None` 项复制，而不是 `Content` 项。使用 `Content` 会触发无意义的 PRI 资源警告。

## 验证清单

已通过：

```powershell
npm test
dotnet build --configuration Debug
dotnet publish --configuration Release --runtime win-x64 --self-contained false --output .\publish\win-x64
```

worker 协议冒烟测试：

```powershell
"shutdown" | node .\worker\dictation-daemon.mjs
```

输出应依次包含 `ready` 和 `stopped`。

手动端到端测试：启动 app，切到普通权限的文本输入框，按一次 `Ctrl+Alt+\` 说话，再按一次。确认 indicator 状态依次变化，最终文本被粘贴。普通权限 host 不能向管理员权限目标 app 粘贴。

## 当前限制与下一步

### 流式文字尚未实现

当前 `Qwen3-ASR-0.6B-Q8_0.gguf + transcribe-cpp` 路径是停止录音后的批量转写。当前 `pi-transcribe` catalog 和 transcribe.cpp 的 Qwen3-ASR 文档都将其标记为不支持 real-time streaming。

Qwen 官方模型本身支持 streaming，但官方说明 streaming 目前只通过 vLLM backend 提供。那不是当前 GGUF 后端的一个小开关：需要新的模型格式和新的运行时，并可能带来 GPU、Python、vLLM 或 Windows 支持策略的决策。

不要用反复重跑离线模型来伪造实时文字。若开始做 streaming，先做一个独立 benchmark/POC，并先扩展协议为 `partial { stable, unstable }` 和 `final { text }`。indicator 可以显示 partial，但只有 final 文本应触发粘贴。

官方参考：

- <https://github.com/QwenLM/Qwen3-ASR>
- <https://github.com/handy-computer/transcribe.cpp/blob/main/docs/models/qwen3-asr.md>

### 仍待决定的产品工作

- 选择并验证真正的流式 ASR backend。
- 为 live text 扩展 worker/host 协议和 indicator UI。
- 打包自己的 Node runtime；当前只复制 `node_modules`，仍依赖 PATH 中的 `node.exe`。
- 提供自己的设置 UI、模型选择/下载、麦克风选择和 autocorrect 安装策略。
- 设计安装器、自动启动和版本更新策略。
- 如需要后台体验，再补原生托盘图标；当前 app 应保持打开或最小化，点“退出”会停止 host。

不要在新 app 的功能稳定前删除旧仓库 `pi-transcribe/windows/`。旧实现仍在 `C:\Users\thom\git\pi-transcribe` 的提交 `b2ed515`，可作为已验证行为参考。

## 其他仓库状态

`C:\Users\thom\git\pi-transcribe` 仍保留旧 PowerShell host，且工作树中有未跟踪的 `docs/eli/` 可视化说明文件。它与新 app 无关，不要在迁移提交中误删或误提交。
