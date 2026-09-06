# 在另一台 Windows PC 上运行

当前 `win-x64` 便携发布会将 .NET 10 Desktop Runtime、Windows App SDK、Node.js runtime、worker 和 `node_modules` 压进一个 `shuo.exe`。当前构建约 138 MiB，目标电脑不需要 Pi、.NET 或 Node.js；GGUF 模型仍是外部文件。

## 下载后直接使用豆包

1. 从 [最新 Release](https://github.com/thomjiji/shuo/releases/latest) 下载 `shuo.exe`，放到固定的本地目录后双击运行。
2. 选择“豆包云端”，填写火山引擎语音控制台的 API Key，点击“保存并测试”。新电脑会自动创建默认配置，不需要先下载本地模型或安装 Pi。
3. 允许桌面应用访问麦克风，在文本输入框按 `Ctrl+Alt+\` 开始说话，再按一次结束并粘贴。

API Key 需要在每台电脑上填写一次。凭据保存在该电脑的 Windows 凭据管理器中，不包含在 EXE 内。以下构建、模型和手动配置步骤用于自行构建或使用本地模型。

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

已有旧版配置的电脑会继续使用 `%LOCALAPPDATA%\WindowsDictation\settings.json`。使用本地模型时，可编辑自动创建的 `%LOCALAPPDATA%\Shuo\settings.json`：

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

启动后，在“转录服务”页选择“本地模型”并保存，可选择其他模型。普通文件夹只列出当前模型同层的 `.gguf` 文件；pi-transcribe 使用 Hugging Face 缓存时，会列出同一缓存根目录下各模型快照中的 `.gguf` 文件，不扫描其他位置。下载完成后点击刷新；Fun-ASR Nano Multilingual 对应的文件名为 `Fun-ASR-MLT-Nano-2512-*.gguf`。shuo 只保存自己的选择，不修改 pi-transcribe 配置，也不负责下载模型。

`autocorrectPath` 是可选项，用于中英文排版整理。口水词过滤和末尾句号选项可在主界面设置，不依赖 autocorrect。

## 豆包云端配置

在火山引擎开通[流式语音识别](https://www.volcengine.com/product/asr)，从语音控制台获取 API Key。打开 shuo，在“转录服务”中选择“豆包云端”，填写 API Key 后点击“保存并测试”。默认资源 ID 为流式识别 2.0 小时版的 `volc.seedasr.sauc.duration`；其他套餐须在展开项中填写对应资源 ID。旧版控制台可填写 App ID 和 Access Token，API Key 留空。

凭据保存在当前 Windows 用户的凭据管理器中，配置文件仅记录服务选择和资源 ID。云端模式需要联网，录音会上传到火山引擎并按用量计费。录音时屏幕底部指示条显示实时文字，停止后等待最终结果，再执行本地文本整理、保存历史和粘贴。完成的文字可在“转录历史”页查看、复制。历史保存在这台电脑的 `%LOCALAPPDATA%\Shuo\history.jsonl`，更新 EXE 后保留，不随 EXE 同步到其他电脑。详细协议见[官方文档](https://www.volcengine.com/docs/6561/1354869)。

## 4. 启动并验证

双击 `shuo.exe`。首次启动可能比后续启动慢，因为 app 会自动解压内置运行时。Windows 需要允许桌面应用访问默认麦克风。

启动后，可在“常规”页点击“录音触发快捷键”右侧的铅笔图标修改快捷键；默认 `Ctrl+Alt+\`。在普通权限的文本输入框中按一次已设置的快捷键开始录音，再按一次停止并转写。完成后文字会粘贴到原先的前台输入框。

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
