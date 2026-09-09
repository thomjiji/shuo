# 自托管语音识别

Mac 在本机通过 MLX 运行 Qwen3-ASR，Windows 上的 shuo 通过 Tailscale 发送录音并显示实时文字。模型只需在 Mac 安装一次，几台 Windows 可以轮流使用。服务需要 Apple Silicon Mac；Mac 需保持供电、联网且不休眠。

## 在 Mac 启动服务

安装 uv 后，在项目根目录执行：

```bash
uv run --project asr-server --frozen shuo-asr
```

首次运行会下载 `mlx-community/Qwen3-ASR-1.7B-8bit`，权重约 2.46 GB。模型加载和预热完成后，服务监听 `127.0.0.1:18765`。已有模型可通过 `--model /模型目录` 指定；识别在 Mac 上完成，正常请求不会将音频发送给模型下载站。

通过 Tailscale 将服务发布到私网：

```bash
tailscale serve --bg --http=18765 http://127.0.0.1:18765
tailscale serve status
```

Windows 到 Mac 的 **TCP 18765** 必须被 tailnet 的访问规则允许。源选择需要使用听写的 Windows 设备，目标选择 Mac；地址使用 `tailscale serve status` 显示的主机名，例如 `http://<Mac主机名>:18765`。不要直接替换成 `100.x` IP：当前 HTTP 发布按主机名匹配，IP 请求可能返回 404。HTTP 和 WebSocket 流量由 Tailscale 隧道加密。

在 Windows 上验证：

```powershell
curl.exe --max-time 5 http://<Mac的Tailscale名称>:18765/health
```

返回 `ready: true` 后，在 shuo 的“转录服务”中选择“自托管识别”，填写同一个服务根地址，点击“测试连接”。随后可按快捷键听写，浮窗显示实时预览，停止后才粘贴最终文字。地址自动保存，切换服务后保留。

在“识别模型”中选择 Qwen3-ASR 1.7B 或 0.6B，选择自动保存并在下一次听写时生效。两个选项都使用 Mac 上的 MLX 服务和相同的实时分段预览流程。0.6B 的实际速度与识别效果可用自己的录音比较；服务端未安装所选模型时会明确报错。

服务正在处理另一台电脑的听写时，新连接会提示忙碌。断线、服务退出或最终结果超时会提示失败，未确认的预览不会被粘贴或写入历史。

## 分段与识别行为

服务先检测语音，再约每秒识别当前段落并更新预览。通常停顿约 1 秒后确认该段；当前段累计语音不足 2 秒时，等待至少 2 秒静音，减少开头短暂犹豫造成的切分。确认后只识别新的段落，已确认文字不再重新计算。按停止键不等待静音，会立即补齐不足一包的尾音，并等待识别完成。

段落达到 30 秒软上限后，遇到至少 300 毫秒的静音就确认；一直没有停顿则在 35 秒硬上限切分，避免推理开销随整次听写增长。切分依据语音活动检测，不理解句意，也不保证找到词语边界。硬切仍可能影响边界处的词语或标点。单次听写最长 30 分钟，当前服务每次只处理一路录音。短于约 120 毫秒的零碎语音会被过滤。

服务不保存录音或转写文字到磁盘。最终文本仍按 shuo 的现有规则保存在发起听写的 Windows 电脑上。

需要调整分段时，可在启动命令中增加参数，例如 `--max-segment-seconds 30 --hard-segment-seconds 35 --silence-seconds 1 --preview-seconds 1`。这些也是默认值；未指定硬上限时，它等于软上限加 5 秒。短句所需静音取 2 秒与配置静音时长中的较大值。`/health` 返回的 `segmentation` 显示当前参数。使用 LaunchAgent 时，将参数和对应值写入 `ProgramArguments` 后重新加载服务。

## 安装两个模型

服务默认只加载 1.7B。要允许客户端切换 0.6B，在 Mac 的启动命令中增加 `--small-model`：

```bash
uv run --project asr-server --frozen shuo-asr --small-model mlx-community/Qwen3-ASR-0.6B-8bit
```

0.6B 的 8bit 权重约 1.01 GB；已有模型可传入本地目录。服务启动时加载并预热两个模型，随后每次听写按客户端选择使用其中一个，仍只允许一路录音。两个模型同时驻留会增加内存占用。使用 LaunchAgent 时，将 `--small-model` 和模型目录追加到 `ProgramArguments`。

## 常驻运行

前台运行的服务随终端退出而停止。常驻部署时，将服务安装到独立环境，并由 macOS LaunchAgent 启动。下面是环境安装步骤，须在项目根目录执行：

```bash
uv venv --python 3.12 "$HOME/.local/share/shuo-asr/venv"
uv pip install --python "$HOME/.local/share/shuo-asr/venv/bin/python" ./asr-server
```

LaunchAgent 的 `ProgramArguments` 使用环境中的 `bin/shuo-asr`，设置 `RunAtLoad` 和 `KeepAlive`，并用 `--model` 指向持久保存的模型目录。不要将模型或环境放在 `/tmp` 中。LaunchAgent 随用户登录启动；Mac 休眠时无法提供识别服务。

停用私网发布：

```bash
tailscale serve --http=18765 off
```

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
