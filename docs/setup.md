# 在另一台 Windows PC 上运行

本指南适用于当前的 `win-x64` 发布版。目标电脑不需要安装 Pi、PowerShell 脚本或 npm 包，但需要 x64 Windows、.NET 10 Desktop Runtime、Node.js 22 或更高版本，以及本地 GGUF 模型。

## 1. 在构建电脑打包

先退出正在运行的 Windows Dictation，再执行：

```powershell
Set-Location "$HOME\git\windows-dictation"
dotnet publish --configuration Release --runtime win-x64 --self-contained false --output .\publish\win-x64
Compress-Archive -Path .\publish\win-x64\* -DestinationPath .\WindowsDictation-win-x64.zip -Force
```

将 `WindowsDictation-win-x64.zip` 整个复制到目标电脑。不要只复制 `WindowsDictation.exe`：同目录的 DLL、`worker`、`node_modules`、`.pri`、`.xbf` 和 `settings.example.json` 都是运行所需文件。

## 2. 安装目标电脑依赖

在目标电脑安装以下 x64 组件：

1. [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)。不要只安装 .NET Runtime；需要 Desktop Runtime。
2. [Node.js](https://nodejs.org/en/download) 22 或更高版本，并让 `node.exe` 位于 `PATH`。
3. 可用的默认麦克风，以及 Windows 允许桌面应用访问麦克风。

Windows App SDK runtime 已随发布目录携带，无需另行安装。当前发布版只支持 x64 Windows。

安装后可在 PowerShell 中检查：

```powershell
node --version
dotnet --list-runtimes | Select-String Microsoft.WindowsDesktop.App
```

## 3. 放置模型

模型不会包含在发布包中。最快的方法是从原电脑复制已有的 `Qwen3-ASR-0.6B-Q8_0.gguf` 文件到目标电脑，例如 `D:\Models\Qwen3-ASR-0.6B-Q8_0.gguf`。

如果没有现成模型，可下载 [Qwen3-ASR-0.6B-Q8_0.gguf](https://huggingface.co/handy-computer/Qwen3-ASR-0.6B-gguf/resolve/main/Qwen3-ASR-0.6B-Q8_0.gguf)。

## 4. 创建配置

将 ZIP 解压到一个本地可写目录，例如 `$HOME\Apps\WindowsDictation`，然后创建用户配置：

```powershell
$install = Join-Path $HOME "Apps\WindowsDictation"
$settingsDir = Join-Path $env:LOCALAPPDATA "WindowsDictation"
New-Item -ItemType Directory -Force $settingsDir
Copy-Item "$install\settings.example.json" "$settingsDir\settings.json"
notepad "$settingsDir\settings.json"
```

将配置中的模型路径改为实际位置。Windows JSON 路径中的反斜杠必须写成 `\\`：

```json
{
  "version": 1,
  "backend": { "type": "transcribe-cpp" },
  "model": {
    "id": "Qwen3-ASR-0.6B",
    "path": "D:\\Models\\Qwen3-ASR-0.6B-Q8_0.gguf"
  },
  "transcriptionLanguage": "auto",
  "chineseOutput": "simplified",
  "microphone": { "type": "system-default" }
}
```

`autocorrectPath` 是可选项。目标电脑没有 `autocorrect.exe` 时，可将其值改为空字符串 `""`；转写仍会正常粘贴原始文本。

## 5. 启动并验证

```powershell
Set-Location "$HOME\Apps\WindowsDictation"
.\WindowsDictation.exe
```

等待主窗口显示“准备好了”。在普通权限的文本输入框中按一次 `Ctrl+Alt+\` 开始录音，再按一次停止并转写。完成后文字会粘贴到原先的前台输入框。

## 常见问题

| 现象 | 处理方式 |
| --- | --- |
| 主窗口无法启动或马上退出 | 确认解压了完整发布目录，并已安装 x64 .NET 10 Desktop Runtime。 |
| 状态显示无法启动听写服务 | 在新 PowerShell 中运行 `node --version`；若 Node 不在 `PATH`，设置 `WINDOWS_DICTATION_NODE` 为 `node.exe` 的完整路径后重启 app。 |
| 状态提示模型文件缺失 | 检查 `%LOCALAPPDATA%\WindowsDictation\settings.json` 中的 `model.path`，以及 JSON 转义后的实际路径。 |
| 没有听到声音 | 在 Windows 设置中确认系统默认输入设备和麦克风隐私权限；配置默认使用系统默认麦克风。 |
| 文字没有粘贴进管理员权限应用 | 听写 app 与目标应用需要相同权限级别。普通权限 app 不能向管理员权限窗口发送粘贴操作。 |

## 更新

在 app 中点击“退出”后，用新发布包覆盖安装目录即可。用户配置和模型都位于安装目录之外，不会被覆盖。
