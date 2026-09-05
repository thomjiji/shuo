# 在另一台 Windows PC 上运行

当前 `win-x64` 便携发布会将 .NET 10 Desktop Runtime、Windows App SDK、Node.js runtime、worker 和 `node_modules` 压进一个 `shuo.exe`。当前构建约 138 MiB，目标电脑不需要 Pi、.NET 或 Node.js；GGUF 模型仍是外部文件。

## 1. 在构建电脑生成一个 EXE

构建电脑需要 x64 Windows、.NET SDK 10 和 x64 Node.js 22 或更高版本。先退出正在运行的 shuo，再执行：

```powershell
Set-Location "$HOME\git\shuo"
npm ci
$node = (Get-Command node.exe -ErrorAction Stop).Source
Remove-Item -Recurse -Force .\publish\win-x64 -ErrorAction SilentlyContinue
dotnet publish src/Shuo/Shuo.csproj --configuration Release --runtime win-x64 --output .\publish\win-x64 -p:PortableBundle=true "-p:NodeRuntimePath=$node"
```

输出目录中只有 `publish\win-x64\shuo.exe`。将这一个文件复制到目标电脑的本地目录即可；它在首次启动时会自动解压运行所需的原生文件。

## 2. 放置模型

模型不会包含在 EXE 中。最快的方法是从原电脑复制已有的 `Qwen3-ASR-0.6B-Q8_0.gguf` 文件到目标电脑，例如 `D:\Models\Qwen3-ASR-0.6B-Q8_0.gguf`。

如果没有现成模型，可下载 [Qwen3-ASR-0.6B-Q8_0.gguf](https://huggingface.co/handy-computer/Qwen3-ASR-0.6B-gguf/resolve/main/Qwen3-ASR-0.6B-Q8_0.gguf)。

## 3. 创建配置

已有旧版配置的电脑会继续使用 `%LOCALAPPDATA%\WindowsDictation\settings.json`。新电脑没有可导入配置时，请创建 `%LOCALAPPDATA%\Shuo\settings.json`：

```powershell
$settingsDir = Join-Path $env:LOCALAPPDATA "Shuo"
New-Item -ItemType Directory -Force $settingsDir
notepad (Join-Path $settingsDir "settings.json")
```

粘贴下面内容，并将模型路径改成实际位置。Windows JSON 路径中的反斜杠必须写成 `\\`：

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

启动后可在“转录模型”中选择其他模型。普通文件夹只列出当前模型同层的 `.gguf` 文件；pi-transcribe 使用 Hugging Face 缓存时，会列出同一缓存根目录下各模型快照中的 `.gguf` 文件，不扫描其他位置。下载完成后点击刷新；Fun-ASR Nano Multilingual 对应的文件名为 `Fun-ASR-MLT-Nano-2512-*.gguf`。shuo 只保存自己的选择，不修改 pi-transcribe 配置，也不负责下载模型。

`autocorrectPath` 是可选项，用于中英文排版整理。口水词过滤和末尾句号选项可在主界面设置，不依赖 autocorrect。

## 4. 启动并验证

双击 `shuo.exe`。首次启动可能比后续启动慢，因为 app 会自动解压内置运行时。Windows 需要允许桌面应用访问默认麦克风。

启动后，可点击“录音触发快捷键”右侧的铅笔图标修改快捷键；默认 `Ctrl+Alt+\`。在普通权限的文本输入框中按一次已设置的快捷键开始录音，再按一次停止并转写。完成后文字会粘贴到原先的前台输入框。

## 常见问题

| 现象 | 处理方式 |
| --- | --- |
| 双击后无法启动或马上退出 | 确认是 x64 Windows，并将 EXE 复制到本地目录后再试。目标电脑不需要安装 .NET 或 Node.js。 |
| 状态显示无法启动听写服务 | 重新复制完整的 EXE；可用 `SHUO_NODE` 指向其他 `node.exe` 进行排错，但正常发布不需要它。 |
| 状态提示模型文件缺失 | 检查当前配置文件中的 `model.path`，以及 JSON 转义后的实际路径。 |
| 没有听到声音 | 在 Windows 设置中确认系统默认输入设备和麦克风隐私权限；配置默认使用系统默认麦克风。 |
| 文字没有粘贴进管理员权限应用 | 听写 app 与目标应用需要相同权限级别。普通权限 app 不能向管理员权限窗口发送粘贴操作。 |

## 更新

右键托盘图标选择“退出”后，用新的 `shuo.exe` 覆盖旧文件即可。关闭主窗口只会隐藏窗口，应用仍在运行。用户配置和模型位于 EXE 之外，不会被覆盖。
