# 在另一台 Windows PC 上运行

下载 [最新 Release](https://github.com/thomjiji/shuo/releases/latest) 中的 `Shuo-版本-win-x64-Setup.exe`，双击安装后从开始菜单打开“说”。安装包包含所需运行时，电脑不需要另外安装 Pi、.NET 或 Node.js；本地模型可在应用内下载。

## 从便携版迁移与更新

先从托盘退出旧版，再运行安装器。程序安装在 `%LOCALAPPDATA%\ShuoDesktop`，在 Windows“已安装的应用”中可查看版本、卸载。已有配置、Windows 凭据管理器中的凭据和转录历史继续使用，更新或卸载程序不会删除这些独立的数据文件。原来固定在任务栏的便携版入口需取消，再固定安装后的应用。

标题栏和“常规”页显示当前版本。安装版启动时及每 6 小时检查更新，窗口和托盘会提示新版本；点击“更新并重启”后下载并安装。下载完成后应用会先退出听写服务，再替换程序并重启。录音、转录、粘贴和模型切换期间不能安装更新。网络失败后可在“版本与更新”中重试。

安装器目前未签名，Windows 可能显示发布者未知。安装器名称和文件属性均包含版本号；安装后可以删除下载的安装器。

## 下载后直接使用豆包

1. 安装并打开“说”。
2. 在“转录服务”中选择“火山引擎 - 豆包流式语音识别模型 2.0”，填写火山引擎语音控制台的 API Key，配置自动保存。新电脑会自动创建默认配置，不需要先下载本地模型或安装 Pi。
3. 允许桌面应用访问麦克风，在文本输入框按 `Ctrl+Alt+\` 开始说话，再按一次结束并粘贴。

API Key 需要在每台电脑上填写一次，保存在该电脑的 Windows 凭据管理器中。历史记录保存在各自电脑上，不会随安装包或更新同步。

## 1. 在构建电脑生成安装包

构建电脑需要 x64 Windows、.NET SDK 10 和 x64 Node.js 22 或更高版本。在仓库根目录运行：

```powershell
npm ci
node scripts/package.mjs 0.4.0 artifacts/installer-0.4.0
```

替换命令中的版本号，每次使用新的输出目录。安装器位于输出目录的 `releases` 子目录，名称包含版本号。构建会同时生成应用内更新所需的包和清单；公开发布由 GitHub Actions 完成。

## 2. 下载本地模型

在“转录服务”页选择“本地模型”，服务选择会自动保存。在模型选择框右侧点击“下载”下载约 811 MB 的 Q8_0 模型，选择框下方显示下载进度，可随时取消。应用使用 Windows 系统代理，下载完成后校验文件大小和 SHA-256，失败可重试。

模型保存到配置文件旁的 `models` 目录，默认是 `%LOCALAPPDATA%\Shuo\models`；旧版配置对应 `%LOCALAPPDATA%\WindowsDictation\models`。下载完成后自动刷新模型列表，空闲且仍使用本地服务时自动加载并选用。无需安装 Pi 或手工修改配置。选择好模型后，可在下方“试用听写”区域使用快捷键测试。

## 3. 使用已有模型

如需使用自行准备的 GGUF 文件，可将其放入配置旁的 `models` 目录后重新打开转录服务页，或者在配置中指定路径。已有旧版配置的电脑会继续使用 `%LOCALAPPDATA%\WindowsDictation\settings.json`。使用本地模型时，可编辑自动创建的 `%LOCALAPPDATA%\Shuo\settings.json`：

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

模型列表包含应用下载目录和当前模型同层的 `.gguf` 文件。已有 Hugging Face 缓存路径会继续识别同一缓存根目录下各模型快照中的 `.gguf` 文件。选择加载成功后自动保存，不修改原插件配置。

`autocorrectPath` 是可选项，用于中英文排版整理。末尾句号选项可在主界面设置，不依赖 autocorrect。

## 豆包云端配置

在火山引擎开通[流式语音识别](https://www.volcengine.com/product/asr)，从语音控制台获取 API Key。打开 shuo，在“转录服务”中选择“火山引擎 - 豆包流式语音识别模型 2.0”，填写 API Key 后自动保存。默认资源 ID 为流式识别 2.0 小时版的 `volc.seedasr.sauc.duration`；其他套餐须在展开项中填写对应资源 ID。旧版控制台可填写 App ID 和 Access Token，API Key 留空。

凭据保存在当前 Windows 用户的凭据管理器中，配置文件仅记录服务选择和资源 ID。云端模式需要联网，录音会上传到火山引擎并按用量计费。录音时屏幕底部指示条显示实时文字，停止后等待最终结果，再执行本地文本整理、保存历史和粘贴。完成的文字可在“转录历史”页查看、复制。历史保存在这台电脑的 `%LOCALAPPDATA%\Shuo\history.jsonl`，更新程序后保留，不随安装包同步到其他电脑。详细协议见[官方文档](https://www.volcengine.com/docs/6561/1354869)。

## 4. 启动并验证

双击 `shuo.exe`。首次启动可能比后续启动慢，因为 app 会自动解压内置运行时。Windows 需要允许桌面应用访问默认麦克风。

启动后，可在“常规”页点击“录音触发快捷键”右侧的铅笔图标修改快捷键；默认 `Ctrl+Alt+\`。在普通权限的文本输入框中按一次已设置的快捷键开始录音，再按一次停止并转写。完成后文字会粘贴到原先的前台输入框。

## 常见问题

| 现象 | 处理方式 |
| --- | --- |
| 双击后无法启动或马上退出 | 确认是 x64 Windows，重新运行相同或更新版本的安装器。目标电脑不需要安装 .NET 或 Node.js。 |
| 状态显示无法启动听写服务 | 重新运行安装器；可用 `SHUO_NODE` 指向其他 `node.exe` 进行排错，但正常发布不需要它。 |
| 状态提示模型文件缺失 | 检查当前配置文件中的 `model.path`，以及 JSON 转义后的实际路径。 |
| 没有听到声音 | 在 Windows 设置中确认系统默认输入设备和麦克风隐私权限；配置默认使用系统默认麦克风。 |
| 文字没有粘贴进管理员权限应用 | 听写 app 与目标应用需要相同权限级别。普通权限 app 不能向管理员权限窗口发送粘贴操作。 |

## 更新

安装版在“常规”的“版本与更新”中点击“更新并重启”即可。也可以从托盘退出后运行更新版本的安装器。配置、模型和历史位于安装目录之外，不会被更新覆盖。
