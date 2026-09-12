# 自托管中文译读

Apple Silicon Mac 通过 Qwen3 8B 翻译文字，再通过 Qwen3-TTS 0.6B 生成 Serena 中文女声。Windows 上的 Shuo 负责取文和播放，正常译读不调用云端 API。Mac 需要保持联网、不休眠，并为模型留出内存；译读服务和转录服务分别运行，可以单独启动和停止。

## 在 Mac 部署

先安装 uv、登录 Tailscale，并取得 Shuo 仓库。在仓库根目录运行：

```bash
uv run --python 3.12 scripts/shuo-reading-service.py up
```

首次运行会安装锁定的依赖，下载 Qwen3-8B-4bit 和 Qwen3-TTS-12Hz-0.6B-CustomVoice-8bit，并预热模型。已有的 Hugging Face 缓存会直接复用。命令返回 `ready: true` 后即可连接。

服务仅监听 Mac 的 Tailscale IPv4 地址和 TCP 18766 端口。Tailnet 的访问规则需要允许运行 Shuo 的设备访问这个端口；转录服务的 18765 端口不变。浏览器页面不能直接调用译读 WebSocket 接口。

在 Shuo 的“实时朗读”中选择“中文译读”，将服务设为“自托管 Mac（Serena）”，填写 Mac 的 Tailscale IP，再点击“测试 Mac 连接”。若已配置自托管转录，译读主机输入框会预填同一个地址。选择服务、修改地址和播放速度后自动保存；下一次快捷键朗读使用当前设置。

本地播放速度可选 0.85、1、1.15 和 1.3 倍，默认 1 倍。变速保持音调不变。云端译读的语速档位单独保存。

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

一次只处理一路译读；其他请求会收到忙碌提示。文字按短段翻译，每段翻译完成后开始流式生成音频。暂停保留当前播放位置；播放器缓冲区填满后，Mac 等待客户端确认再继续生成。停止或断开连接会取消后续生成并释放会话。

服务不保存原文、译文或生成音频，也不把这些内容写入日志。模型可能误译或遗漏信息，跨段指代和术语也可能不一致。Mac 离线或服务不可用时会提示连接失败，不会自动切换到收费的云端服务。
