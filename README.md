# 说（shuo）

> 本项目仅供我个人使用，它的生命周期直到豆包输入法推出 Windows 版的那一天为止。因为那时候我就跑去用豆包输入法 Windows 版了。

Windows 原生 WinUI 3 本地听写应用。按全局快捷键开始录音，再按一次停止；本地模型转写完成后，文本会粘贴到原前台输入框。

在另一台 Windows PC 上运行，请参阅[安装指南](docs/setup.md)。

## 使用

1. 启动应用。
2. 如需修改快捷键，点击“录音触发快捷键”右侧的铅笔图标，在卡片内按下新组合键并保存。默认 `Ctrl+Alt+\`；清除会禁用全局快捷键。
3. 在普通权限应用的输入框中按一次快捷键开始录音，再按一次停止。
4. 转写完成后，文本会自动粘贴。

关闭主窗口后，应用继续在系统托盘运行，不占用任务栏。点击托盘图标可打开设置；右键图标可选择“打开设置”或“退出”。需要完全停止应用时，选择“退出”。

“文本整理”提供两个独立开关，默认关闭，自动保存并从下一次听写生效：

- “去除口水词”：清除停顿中独立出现的“嗯、呃”，并整理相邻分隔符。保留单独回应的“嗯”、正常语气和引号内容；边界不明确时保留原文。
- “去掉末尾句号”：去掉整次输入最后的句号，长段落也适用。保留句中标点、问号、感叹号、省略号，以及常见英文缩写等有歧义的句点。

文本整理在本地完成，不需要额外模型或 API。

转写在停止录音后进行，不提供实时文本。普通权限应用不能向管理员权限目标粘贴文本。

## 配置

新安装的配置位于 `%LOCALAPPDATA%\Shuo\settings.json`。如果新文件不存在，应用会继续使用已有的 `%LOCALAPPDATA%\WindowsDictation\settings.json`，保留原有快捷键和偏好。两者都不存在时，首次启动服务会从 `~/.pi/agent/pi-transcribe.json` 导入模型、麦克风、语言和可选的 autocorrect 路径。

GGUF 模型不包含在 EXE 中。没有可导入配置时，按[安装指南](docs/setup.md#3-创建配置)创建配置。

## 开发

需要 Windows、.NET SDK 10、Node.js 22+ 和本地 GGUF 模型。

```powershell
npm ci
dotnet run --project src/Shuo/Shuo.csproj
```

项目结构和进程协议见[开发说明](docs/development.md)。

## 发布

按[安装指南](docs/setup.md#1-在构建电脑生成一个-exe)构建 x64 单文件 EXE。

## 验证

```powershell
npm test
dotnet run --project test/TextCleanup.Tests/TextCleanup.Tests.csproj
dotnet build --configuration Debug
```
