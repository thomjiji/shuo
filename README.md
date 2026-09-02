# Windows Dictation

Windows Dictation 是一个原生 WinUI 3 本地听写应用。它用 `Ctrl+Alt+\` 开始和停止录音，使用本地模型转写，然后把结果粘贴到当前前台输入框。

在另一台 Windows PC 上运行，请参阅[安装指南](docs/setup.md)。

## 当前能力

- WinUI 3 + Windows App SDK 原生应用入口，不依赖 PowerShell 或 Pi 进程。
- 底部居中的无焦点 indicator，只在录音和转写期间显示旋转进度环。
- 主窗口最小尺寸为 640 x 480，避免控件在缩小时重叠。
- 首次运行时从已有 Pi Transcribe 配置导入麦克风、模型路径、语言和可选的 autocorrect 路径；之后只读取自己的配置。
- 当前使用 `transcribe-cpp` 的 GGUF 批量转写后端。Qwen3-ASR 0.6B 的这条路径不支持实时文本流。

## 开发运行

需要 Windows、.NET SDK 10、Node.js 22+ 和一个已下载的本地 GGUF 模型。

```powershell
npm install
dotnet run
```

## 生成 .exe

> `PortableBundle` 会生成一个 x64 self-contained EXE，内置 .NET runtime、Windows App SDK、Node runtime、worker 和依赖；GGUF 模型仍在外部。构建电脑需要 Node.js 22+，目标电脑不需要 .NET 或 Node.js。

```powershell
npm ci
$node = (Get-Command node.exe -ErrorAction Stop).Source
Remove-Item -Recurse -Force .\publish\win-x64 -ErrorAction SilentlyContinue
dotnet publish --configuration Release --runtime win-x64 --output .\publish\win-x64 -p:PortableBundle=true "-p:NodeRuntimePath=$node"
.\publish\win-x64\WindowsDictation.exe
```

首次启动会将现有的 `~/.pi/agent/pi-transcribe.json` 导入到：

```text
%LOCALAPPDATA%\WindowsDictation\settings.json
```

如果没有旧配置，请按[安装指南](docs/setup.md#3-创建配置)创建该文件并填写模型和麦克风信息。可用 `WINDOWS_DICTATION_SETTINGS` 指向其他设置文件；`WINDOWS_DICTATION_NODE` 仅用于覆盖内置 Node。

## 使用

1. 启动应用并等待状态变为“准备好了”。
2. 在任意普通权限应用中按一次 `Ctrl+Alt+\` 开始录音。
3. 再按一次停止录音。indicator 会显示旋转进度环。
4. 最终文本会自动粘贴到原来的前台输入框。

普通权限应用不能向管理员权限目标发送粘贴快捷键。

## 验证

```powershell
npm test
dotnet build --configuration Debug
```

## 设计边界

本项目拥有自己的 WinUI app、Node worker 和配置文件。首次导入仅用于复用已下载模型，不需要 Pi 在运行。开发版仍需要 PATH 中的 Node.js；便携发布会内置 x64 Node runtime 和 worker 依赖，模型仍由设置中的外部路径引用。
