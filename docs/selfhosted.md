# 自托管语音识别

Mac 在本机通过 MLX 运行 Qwen3-ASR，Windows 上的 shuo 通过 Tailscale 发送录音并显示实时文字。模型只需在 Mac 安装一次，几台 Windows 可以轮流使用。服务需要 Apple Silicon Mac；Mac 需保持供电、联网且不休眠。

## 在 Mac 部署服务

Mac 需要安装 uv 和 Tailscale，并先登录需要访问该服务的 tailnet。在项目根目录执行统一的部署入口：

```bash
./scripts/shuo-services up
```

`up` 会读取 Mac 当前的 Tailscale IPv4 地址，根据 `asr-server/uv.lock` 将服务安装到 `~/.local/share/shuo-asr/venv`，生成并加载 `ai.shuo.asr` LaunchAgent。服务直接监听 Mac 的 Tailscale IPv4 地址和 18765 端口，不使用 Tailscale Serve，也不会监听 Wi-Fi 或以太网地址。

已有的 `~/.local/share/shuo-asr/model` 和 `model-0.6b` 会被直接复用。没有这两个本地模型目录时，首次启动会从 Hugging Face 下载 `mlx-community/Qwen3-ASR-1.7B-8bit` 和 `mlx-community/Qwen3-ASR-0.6B-8bit`，并将缓存保存在 `~/.local/share/shuo-asr/huggingface`。模型加载和预热完成后，`up` 才会返回健康状态。识别在 Mac 上完成，正常请求不会将音频发送给模型下载站。

日常管理统一使用以下命令：

```bash
./scripts/shuo-services up
./scripts/shuo-services down
./scripts/shuo-services restart
./scripts/shuo-services status
./scripts/shuo-services logs
```

`up` 会同步并部署当前仓库中的服务代码，然后启用并启动 LaunchAgent；`restart` 只重新生成配置并重启已经安装的服务；`down` 会停用 LaunchAgent，使其在下次登录时也保持停止；`status` 同时检查 LaunchAgent、旧 Serve 配置和健康接口；`logs` 持续显示标准输出和错误日志。`up`、`restart` 和 `down` 检测到正在听写时不会中断服务。Tailscale 地址变化后再次运行 `up` 或 `restart`，LaunchAgent 就会改用新地址。

Windows 到 Mac 的 **TCP 18765** 必须被 tailnet 的访问规则允许。源选择需要使用听写的 Windows 设备，目标选择 Mac。客户端可以使用 Mac 的 Tailscale IP 或主机名，HTTP 和 WebSocket 流量由 Tailscale 隧道加密。服务只绑定 Tailscale 地址，因此 tailnet 外的本地网络不能连接该端口。

在 Windows 上验证：

```powershell
curl.exe --max-time 5 http://<Mac的Tailscale名称>:18765/health
```

返回 `ready: true` 后，在 shuo 的“转录服务”中选择“自托管识别”，在“主机 IP”中填写 Mac 的 Tailscale IP，点击“测试连接”。随后可按快捷键听写，浮窗显示实时预览，停止后才粘贴最终文字。应用自动补上 HTTP 协议和默认端口 18765，也兼容主机名及已有的完整地址。地址在输入时自动保存，离开输入框后应用配置，切换服务后保留。

在“识别模型”中选择 Qwen3-ASR 1.7B 或 0.6B，选择自动保存并在下一次听写时生效。两个选项都使用 Mac 上的 MLX 服务和相同的实时分段预览流程。0.6B 的实际速度与识别效果可用自己的录音比较；服务端未安装所选模型时会明确报错。

服务正在处理另一台电脑的听写时，新连接会提示忙碌。断线、服务退出或最终结果超时会提示失败，未确认的预览不会被粘贴或写入历史。

## 分段与识别行为

服务先检测语音，再约每秒识别当前段落并更新预览。通常停顿约 2 秒后确认该段，减少思考时短暂停顿造成的切分。确认后只识别新的段落，已确认文字不再重新计算。按停止键不等待静音，会立即补齐不足一包的尾音，并等待识别完成。

段落达到 30 秒软上限后，遇到至少 300 毫秒的静音就确认；一直没有停顿则在 35 秒硬上限切分，避免推理开销随整次听写增长。切分依据语音活动检测，不理解句意，也不保证找到词语边界。硬切仍可能影响边界处的词语或标点。单次听写最长 30 分钟，当前服务每次只处理一路录音。短于约 120 毫秒的零碎语音会被过滤。

服务不保存录音或转写文字到磁盘。最终文本仍按 shuo 的现有规则保存在发起听写的 Windows 电脑上。

当前部署使用 `--max-segment-seconds 30 --silence-seconds 2 --preview-seconds 1`，未指定的硬上限等于软上限加 5 秒。若将静音时长调低到 2 秒以下，当前段累计语音不足 2 秒时仍会等待 2 秒静音。`/health` 返回的 `segmentation` 显示当前参数。需要调整时修改 `scripts/shuo-services` 中生成的 `ProgramArguments`，再运行 `up` 部署。

## 两个识别模型

部署入口会同时配置 1.7B 和 0.6B 两个模型，以便客户端切换。1.7B 的 8bit 权重约 2.46 GB，0.6B 约 1.01 GB。服务启动时加载并预热两个模型，随后每次听写按客户端选择使用其中一个，仍只允许一路录音。两个模型同时驻留会增加内存占用。

## 常驻运行

`up` 生成的 LaunchAgent 设置了 `RunAtLoad` 和 `KeepAlive`，随用户登录启动，并在异常退出后重试。程序、模型、缓存和日志都位于持久目录，不使用 `/tmp`。如果登录时 Tailscale 地址尚未就绪，服务可能先启动失败；LaunchAgent 会每隔 15 秒重试。Mac 休眠时无法提供识别服务。

## 开发验证

Mac 服务端测试不加载模型：

```bash
uv run --project asr-server --frozen python -m unittest discover -s asr-server/tests -v
```

在 Windows 用正式客户端检查实际服务，输入文件需为 16 kHz、16-bit、小端、单声道的中文 PCM：

```powershell
node scripts/test-selfhosted.mjs http://<Mac的Tailscale名称>:18765 C:/音频/sample.pcm
```

脚本按录音速度发包，依次验证静音、提前停止、中文识别、停顿分段和连续录音，输出首字及最终结果等待时间。单元测试通过 `npm test` 运行，不连接真实服务。
