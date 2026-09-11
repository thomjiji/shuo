# 自托管 CosyVoice 朗读

Mac 在本机通过 MLX 运行 Fun-CosyVoice 3，Windows 上的 Shuo 通过 Tailscale 发送短段文字并播放返回的音频。模型和声音参考资料只需在 Mac 安装一次，几台 Windows 可以轮流使用。服务需要 Apple Silicon Mac；Mac 需保持供电、联网且不休眠。

项目使用 `mlx-audio-plus` 提供的 CosyVoice 3 MLX 移植版、`mlx-community/Fun-CosyVoice3-0.5B-2512-4bit` 权重和配套的 S3TokenizerV3。上游 CosyVoice 代码和模型采用 Apache-2.0，MLX 移植包采用 MIT。它不收取按次调用费，但两组模型首次下载合计约 2.2 GB，还会占用统一内存和本机电力。官方 PyTorch 实现在 macOS 上只会使用 CPU；这里使用 MLX 是为了让 Apple Silicon GPU 参与推理。

## 准备声音

CosyVoice 使用零样本声音克隆，不需要训练或微调。每个音色由一段参考录音和完全对应的逐字稿组成。只有得到声音所有者明确许可后才能注册和使用；不得把公开音频或他人的语音当作默认授权。

录制 3 到 10 秒自然说话最稳妥，服务接受的范围为 3 到 30 秒。录音中只保留一个人声，避免背景音乐、其他说话者、明显回声、降噪伪影和长静音。逐字稿应与实际发音逐字一致，包括口头词；错字、漏字和多余文字都会降低相似度和发音稳定性。参考内容本身会保存在 Mac 上，因此不要录入密码或其他不必要的敏感信息。

先把参考音频放到 Mac，然后在项目根目录安装独立环境：

```bash
uv venv --python 3.12 "$HOME/.local/share/shuo-tts/venv"
uv pip install --python "$HOME/.local/share/shuo-tts/venv/bin/python" ./tts-server
```

注册音色时选择一个只含小写字母、数字、连字符或下划线的 ID。命令会检查许可确认、时长、采样率和有效采样，并在服务目录保存一份 24 kHz 单声道 WAV；不会修改原文件。

```bash
"$HOME/.local/share/shuo-tts/venv/bin/shuo-tts-voice" \
  --voices-dir "$HOME/.local/share/shuo-tts/voices" \
  --id my-voice \
  --name "我的声音" \
  --audio "/参考音频/reference.wav" \
  --text "这里填写与参考音频完全一致的逐字稿。" \
  --confirm-consent
```

参考音频与主要输出语言不同时，在注册命令中增加 `--cross-lingual`。例如，用日文参考音频生成中文或英文时必须启用；服务仍保存逐字稿用于核对，但推理时不会把日文文本和语音 token 送入语言模型，以免输出内容跟随参考语言。该设置属于音色资料，新增后需要重启服务。

## 启动和验证

首次前台启动会从 Hugging Face 下载量化模型。`HF_HOME` 指向持久目录，避免模型进入临时缓存；以后启动直接复用。服务默认只监听 `127.0.0.1:18766`，一次只处理一个合成请求。

```bash
HF_HOME="$HOME/.local/share/shuo-tts/huggingface" \
  "$HOME/.local/share/shuo-tts/venv/bin/shuo-tts" \
  --voices-dir "$HOME/.local/share/shuo-tts/voices"
```

另开终端检查健康状态：

```bash
curl http://127.0.0.1:18766/health
```

返回 `ready: true` 且 `voices` 中有刚注册的音色后，通过 Tailscale 发布到私网：

```bash
tailscale serve --bg --tcp=18766 tcp://127.0.0.1:18766
tailscale serve status
```

这里使用 Serve 的 TCP 转发，不使用 Funnel。服务仍只监听回环地址，Windows 与 Mac 之间的 HTTP 流量由 Tailscale 隧道加密，公共互联网无法访问。tailnet 使用限制策略时，只需允许需要朗读的设备访问 Mac 的 TCP 18766；新规则应使用 grant，例如：

```json
{
  "grants": [
    {
      "src": ["你的登录邮箱"],
      "dst": ["Mac 的 Tailscale IP"],
      "ip": ["tcp:18766"]
    }
  ]
}
```

不要用示例覆盖现有策略；把这一条合并到已有 `grants`。如果 tailnet 仍使用默认的全设备互通策略，连接已经被允许，无需增加规则。

在 Windows 上验证：

```powershell
curl.exe --max-time 10 http://<Mac的Tailscale名称>:18766/health
```

在 Shuo 的“实时朗读”中选择“Mac 上的 CosyVoice 3”，填写 Mac 的 Tailscale 名称或 IP 与音色 ID，点击“测试 CosyVoice 服务”，成功后保存设置。

## 常驻运行

前台服务随终端退出而停止。常驻部署时使用 macOS LaunchAgent，并把模型缓存、环境、音色和日志放在持久目录。`~/Library/LaunchAgents/ai.shuo.tts.plist` 可以使用以下结构；将用户名路径改为当前 Mac 的实际路径。

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>ai.shuo.tts</string>
  <key>ProgramArguments</key>
  <array>
    <string>/Users/你的用户名/.local/share/shuo-tts/venv/bin/shuo-tts</string>
    <string>--voices-dir</string><string>/Users/你的用户名/.local/share/shuo-tts/voices</string>
    <string>--port</string><string>18766</string>
  </array>
  <key>EnvironmentVariables</key>
  <dict>
    <key>HF_HOME</key><string>/Users/你的用户名/.local/share/shuo-tts/huggingface</string>
    <key>HF_HUB_OFFLINE</key><string>1</string>
  </dict>
  <key>WorkingDirectory</key><string>/Users/你的用户名/.local/share/shuo-tts</string>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><true/>
  <key>ThrottleInterval</key><integer>15</integer>
  <key>StandardOutPath</key><string>/Users/你的用户名/Library/Logs/ShuoTTS/stdout.log</string>
  <key>StandardErrorPath</key><string>/Users/你的用户名/Library/Logs/ShuoTTS/stderr.log</string>
</dict>
</plist>
```

`HF_HUB_OFFLINE=1` 让常驻服务只使用已经校验并缓存的模型，避免重启时受模型站网络影响；首次下载模型时不要在前台启动命令中设置它。创建日志目录后，用 `launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/ai.shuo.tts.plist` 加载。新增音色后重新启动 LaunchAgent，服务才会读取新资料。Mac 休眠时无法提供朗读；客户端断开不会立即中止已经进入 MLX 的当前短段，但不会继续发送后续段落。

停用私网发布：

```bash
tailscale serve --tcp=18766 off
```

## 当前限制

模型支持中文、英文、日文、韩文、德文、西班牙文、法文、意大利文、俄文以及多种中文方言，也能进行跨语言克隆。当前 MLX 包会先完整生成一个短段，再向 Shuo 返回音频；它不是上游所说的 150 毫秒双流式实现。Shuo 因此按不超过 240 个 UTF-8 字节切分，并在同一个播放器中连接各段。实际首段等待和实时倍率取决于 Mac 型号、文本与参考声音，应以本机热启动测试为准。

## 开发验证

服务端单元测试不会下载或加载模型：

```bash
uv run --project tts-server --frozen python -m unittest discover -s tts-server/tests -v
```

Windows 客户端协议测试使用 `dotnet run --project test/Reading.Tests`，不连接真实服务。真实端到端验证应同时检查 `/health`、首段等待、连续播放、停止、未知音色和服务忙碌提示。
