# 自托管朗读与译读

Apple Silicon Mac 使用 Qwen3-TTS 0.6B 或 1.7B CustomVoice 和 Serena 或 Vivian 音色生成语音。原文朗读直接使用原文，模型自动判断语言；中文译读先由 Qwen3 8B 翻译成中文，再生成语音。Windows 上的 Shuo 负责取文和播放，两种方式均不调用云端 API。Mac 需要保持联网、不休眠，并为模型留出内存；朗读服务和转录服务分别运行，可以单独启动和停止。

## 在 Mac 部署

先安装 uv、登录 Tailscale，并取得 Shuo 仓库。在仓库根目录运行：

```bash
uv run --python 3.12 scripts/shuo-reading-service.py up
```

首次运行会安装锁定的依赖，下载 Qwen3-8B-4bit 和默认的 Qwen3-TTS 0.6B，并分别预热模型。首次请求 1.7B 时才会下载并加载该模型；已有的 Hugging Face 缓存会直接复用。服务一次只保留一个 TTS 模型，模型切换与朗读共用同一个推理队列，第一次切换会比普通朗读慢。状态中的 `capabilities.translation.ready` 和 `capabilities.speech.ready` 分别表示文字翻译与语音合成可用，`translation_model` 表示固定的翻译模型，`speech_models` 列出当前服务支持的 TTS 模型，`speech_model` 表示当前加载的模型，`voices` 列出可选音色，`speech_instruct` 表示服务接受每次请求的朗读提示词；顶层 `ready: true` 表示至少一项可用。一个能力加载失败不会阻止另一个能力提供服务：原文朗读只需要语音合成，使用豆包声音服务的中文译读只需要文字翻译，本地中文译读需要两项。Shuo 的连接测试会在对应模型左侧显示结果，并按当前设置检查模型、音色与朗读提示词兼容性。

服务仅监听 Mac 的 Tailscale IPv4 地址和 TCP 18766 端口。Tailnet 的访问规则需要允许运行 Shuo 的设备访问这个端口；转录服务的 18765 端口不变。浏览器页面不能直接调用译读 WebSocket 接口。

在“设置 -> 自托管 Mac”中填写主机名或 IP，选择“语音合成模型”，根据需要修改“朗读提示词”，点击“测试连接与可用能力”，再保存服务设置。朗读提示词最多 300 个字符，同时作用于 0.6B 和 1.7B；旧设置默认继续使用 `mlx-community/Qwen3-TTS-12Hz-0.6B-CustomVoice-8bit`。然后在“文字朗读”页选择原文朗读或中文译读，在“朗读设置”中将声音服务设为自托管 Mac，并选择 Serena 或 Vivian 音色。声音服务下方会显示当前 TTS 模型，无需重启页面。已有服务升级后需要执行 `up` 安装当前版本，才能接受新版应用发送的模型、音色和朗读提示词参数。

不修改设置时，两个模型和两种音色均使用以下默认朗读提示词：

> 请用平实、专业、克制、清晰的中文文章朗读方式。句间停顿应简短自然，只在段落边界或语义确有需要时稍作停顿；不要为了制造情绪、悬念或起承转合刻意延长停顿，不要戏剧化表演。准确读出否定、数字与结论，不要逐字播报。

本地播放速度可选 0.85、1、1.15 和 1.3 倍，默认 1 倍。变速保持音调不变。豆包的合成语速单独保存。

中文译读也可以选择豆包作为声音服务：复用同一服务的 `/v1/translation` 文字翻译接口，每段译文完成后由 Windows 发送给豆包 TTS 2.0。该方式不经过 ASR，也不调用 Mac 的语音生成；需要豆包语音凭据，音色和语速与豆包原文朗读共用。原文只发给 Mac，中文译文会发给火山引擎。

## 管理服务

```bash
uv run --python 3.12 scripts/shuo-reading-service.py status
uv run --python 3.12 scripts/shuo-reading-service.py restart
uv run --python 3.12 scripts/shuo-reading-service.py logs
uv run --python 3.12 scripts/shuo-reading-service.py down
```

`up` 安装当前仓库版本，`restart` 重启已安装版本，`down` 停止服务并关闭登录时自动启动。服务正在译读或无法确认已运行服务的状态时，管理脚本不会中断它；先停止播放，再更新或重启。

运行环境保存在 `~/.local/share/shuo-reading/venv`，模型缓存保存在 `~/.cache/huggingface`，登录启动项是 `ai.shuo.reading`，日志位于 `~/Library/Logs/ShuoReading`。停止服务不会删除模型。

## 播放与限制

译读和实时字幕共用 Qwen3 翻译模型，同一时间只处理一个请求；其他请求会收到忙碌提示。实时字幕通过 `/v1/translation` 请求纯文字翻译，不生成音频。译读按短段翻译，每段完成后开始流式生成音频。暂停保留当前播放位置；播放器缓冲区填满后，Mac 等待客户端确认再继续生成。停止或断开连接会取消后续生成并释放会话。

服务不保存原文、译文或生成音频，也不把这些内容写入日志。模型可能误译或遗漏信息，跨段指代和术语也可能不一致。Mac 离线或服务不可用时会提示连接失败，不会自动切换到收费的云端服务。
