# 项目开发规则

## 平台与验证

Shuo 桌面端是 Windows WinUI 3 应用。先确认当前主机的操作系统、仓库路径和未提交改动，保留用户已有改动。命令从仓库根目录执行，不依赖某台电脑的绝对路径。

- Windows x64：使用 .NET SDK 10 与 x64 Node.js 22 或以上版本，构建、启动并验证桌面应用。
- macOS：可以修改代码、运行 Node 测试；Apple Silicon Mac 可以验证和运行 asr-server。不能在 Mac 上完成 WinUI 构建或运行 Windows EXE。
- 在 Mac 上修改 Windows 界面或 C# 宿主后，使用已有且可访问的 Windows 开发主机完成构建。先确认目标仓库和工作区状态，不能覆盖远端未提交改动。没有 Windows 主机时，完成当前平台可运行的检查，明确说明尚未验证 Windows 构建；不要把 Node 测试通过当成桌面验证通过，也不要为了验证擅自发布版本。
- 按实际修改运行测试。依赖未安装或锁文件变化时执行 npm ci；不要跨操作系统复制 node_modules。worker 修改运行 npm test；文本设置修改运行 dotnet run --project test/TextCleanup.Tests；翻译协议修改在 Windows 运行 dotnet run --project test/Translation.Tests。
- ASR 服务修改在 Apple Silicon Mac 使用 uv run --project asr-server --frozen python -m unittest discover -s asr-server/tests -v。Python 环境使用 uv；一次性验证环境通过 UV_PROJECT_ENVIRONMENT 放到系统临时目录，常驻服务的环境和模型使用持久目录。

## 本地预览构建

预览统一放在 artifacts/preview/，只保留一份当前可运行版本。不要再创建按功能命名的 preview、build、publish 或 v2、v3 目录。

构建前确认旧预览已经退出；需要替换时只清理 artifacts/preview/。任何递归删除前，确认目标解析后的绝对路径位于本仓库 artifacts 内，且不是符号链接或联接，不删除用户配置、凭据、模型、测试源码或无关进程的文件。

Windows 在仓库根目录执行以下命令，每一步成功后再继续。输出路径末尾保留斜杠，避免 XAML 编译文件散落到 artifacts 根目录。依赖已有且未变化时跳过 npm ci。

~~~powershell
npm ci
dotnet build src/Shuo/Shuo.csproj -c Debug -p:Platform=x64 -r win-x64 -o artifacts/preview/
if ($LASTEXITCODE -ne 0) { throw "Preview build failed" }
$previewNode = (Get-Command node.exe -ErrorAction Stop).Source
Copy-Item -LiteralPath $previewNode -Destination artifacts/preview/node.exe -Force
Start-Process -FilePath (Resolve-Path artifacts/preview/shuo.exe).Path -WorkingDirectory (Get-Location).Path
~~~

构建结果必须包含 shuo.exe、node.exe、worker、node_modules、Assets/AppIcon.ico、shuo.pri 和 MainWindow.xbf。Node 的架构必须与 win-x64 构建一致；不要通过从旧预览复制依赖来补齐新构建。

完成用户要求的桌面修改并验证后，主动直接启动本地预览，让用户立即看到应用。不要只给 EXE 链接，也不要打开文件管理器让用户自行双击。启动后核对 shuo.exe 的实际进程路径来自 artifacts/preview/，确认没有因单实例机制转到已安装的旧版。

若已有 Shuo 实例，先确认当前没有听写、翻译、粘贴或更新任务，再通过应用自身的退出入口正常退出，然后启动预览。不要仅关闭设置窗口，它会隐藏到托盘；不要按进程名称批量结束 Node 或其他应用。无法确认空闲或无法正常退出时说明具体阻碍，不强行终止正在进行的工作。

从 Mac 远程完成修复时，在负责验证的 Windows 主机交互桌面启动预览并确认窗口可见；仅从 SSH 拉起进程不能证明用户能看到窗口。

## 其他产物与清理

- artifacts/installer/：正式安装器和更新包，由 scripts/package.mjs 使用。重新打包前清理该目录，或使用 CI 的干净工作区；用户要求提交、推送、发布时才执行对应发布流程。
- artifacts/test/：临时测试程序的编译输出、截图和结果；同一个检查复用固定子目录。
- artifacts/logs/：本地构建和测试日志，复用固定文件名。
- 可持续使用的回归测试源码放到 test/，构建辅助脚本放到 scripts/，不要只保留在被 Git 忽略的 artifacts 里。
- 任务完成后清理本次过期的临时构建，保留当前 preview 和必要诊断结果。模型、下载缓存和用户提供的原始资料不作为旧预览删除；清理它们需要对应授权。
- artifacts 已由 .gitignore 排除，不提交 EXE、安装包、运行时依赖、音频样本或凭据。

## 文档

维护中文文档时，一个自然段保存为一个逻辑行。修改中文纯文本后运行可用的 autocorrect；Markdown 随后运行 convert_chinese_quotes.py，并检查 diff。HTML、代码和结构化配置不使用全文自动改写。
