# Windows Dictation

本项目仅供我个人使用，它的生命周期已定，豆包输入法推出 Windows 版后将停止维护。

Windows 原生 WinUI 3 本地听写应用。按全局快捷键开始录音，再按一次停止；本地模型转写完成后，文本会粘贴到原前台输入框。

在另一台 Windows PC 上运行，请参阅[安装指南](docs/setup.md)。

## 使用

1. 启动应用。
2. 如需修改快捷键，点击“录音触发快捷键”右侧的铅笔图标，在卡片内按下新组合键并保存。默认 `Ctrl+Alt+\`；清除会禁用全局快捷键。
3. 在普通权限应用的输入框中按一次快捷键开始录音，再按一次停止。
4. 转写完成后，文本会自动粘贴。

转写在停止录音后进行，不提供实时文本。普通权限应用不能向管理员权限目标粘贴文本。

## 配置

配置文件位于 `%LOCALAPPDATA%\WindowsDictation\settings.json`。首次开始听写时，如果该文件不存在，应用会从 `~/.pi/agent/pi-transcribe.json` 导入模型、麦克风、语言和可选的 autocorrect 路径；之后只读取自身配置。

GGUF 模型不包含在 EXE 中。没有可导入配置时，按[安装指南](docs/setup.md#3-创建配置)创建配置。

## 开发

需要 Windows、.NET SDK 10、Node.js 22+ 和本地 GGUF 模型。

```powershell
npm ci
dotnet run
```

## 发布

按[安装指南](docs/setup.md#1-在构建电脑生成一个-exe)构建 x64 单文件 EXE。

## 验证

```powershell
npm test
dotnet build --configuration Debug
```
