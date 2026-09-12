import fs from 'node:fs';
import path from 'node:path';

// Run from the repository root after the opt-in Reading.Comparison console app.
const directory = path.resolve('artifacts/test/reading-comparison');
const results = JSON.parse(fs.readFileSync(path.join(directory, 'results.json'), 'utf8'));
const escape = value => String(value).replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;');
const label = route => route === 'omni' ? '千问 Omni 一站式' : '专用翻译 + 豆包';
const median = values => {
  const sorted = [...values].sort((a, b) => a - b);
  return (sorted[Math.floor((sorted.length - 1) / 2)] + sorted[Math.floor(sorted.length / 2)]) / 2;
};
for (const r of results) {
  if (r.error) throw new Error(`Incomplete run: ${r.name}`);
  const wave = fs.readFileSync(path.join(directory, `${r.name}.wav`));
  if (wave.toString('ascii', 0, 4) !== 'RIFF' || wave.length !== wave.readUInt32LE(40) + 44
      || wave.readUInt32LE(24) !== 24000 || wave.readUInt16LE(34) !== 16) throw new Error(`Invalid WAV: ${r.name}`);
}
const summary = ['cascade', 'omni'].map(route => {
  const rows = results.filter(r => r.route === route);
  return `<tr><th>${label(route)}</th><td>${median(rows.map(r => r.firstAudio)).toFixed(2)} 秒</td><td>${Math.min(...rows.map(r => r.firstAudio)).toFixed(2)}–${Math.max(...rows.map(r => r.firstAudio)).toFixed(2)} 秒</td><td>${rows.length}/${rows.length}</td></tr>`;
}).join('');
const detail = results.map(r => `<tr><td>${escape(r.name)}</td><td>${r.firstText.toFixed(2)}</td><td>${r.firstAudio.toFixed(2)}</td><td>${r.playable.toFixed(2)}</td><td>${r.total.toFixed(2)}</td><td>${r.duration.toFixed(2)}</td></tr>`).join('');
const samples = [['short', '日常短文'], ['numbers', '数字与否定'], ['article', '三段说明文']].map(([id, title]) => {
  const pair = results.filter(r => r.name.startsWith(id + '-') && r.round === 1);
  return `<section><h2>${title}</h2><details><summary>英文原文</summary><p>${escape(pair[0].source)}</p></details><div class="pair">${pair.map(r => `<article><h3>${label(r.route)}</h3><audio controls preload="none" src="${r.name}.wav"></audio><p class="meta">首包 ${r.firstAudio.toFixed(2)} 秒 · 音频 ${r.duration.toFixed(1)} 秒</p><p>${escape(r.translation)}</p></article>`).join('')}</div></section>`;
}).join('');
const html = `<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>Shuo 中文译读：两条路线实测</title>
<style>body{font:16px/1.8 system-ui,sans-serif;color:#202b36;background:#f4f6f8;max-width:1100px;margin:40px auto;padding:0 24px}h1{font-size:30px;line-height:1.4}h2{font-size:22px}h3{font-size:18px}section,.intro{background:white;border:1px solid #dce2e8;border-radius:12px;padding:24px;margin:24px 0}.pair{display:grid;grid-template-columns:1fr 1fr;gap:28px}p{white-space:pre-wrap}.meta{color:#657180;font-size:14px}audio{width:100%}table{border-collapse:collapse;width:100%;font-variant-numeric:tabular-nums}th,td{border-bottom:1px solid #dce2e8;padding:10px;text-align:left}a{color:#135ba8}.scroll{overflow:auto}@media(max-width:700px){.pair{grid-template-columns:1fr}body{padding:0 12px}section{padding:16px}}</style>
<h1>英文选文，哪条路线更适合读成中文？</h1><p class="meta">2026-09-12 · Windows x64 · 百炼北京地域 · 3 篇样本，各 2 轮</p>
<div class="intro"><h2>暂定结论：优先试听 Omni，保留组合方案</h2><p>Omni 在本次样本的译文忠实度上更好，典型首包稍快，但出现一次明显延迟。组合方案延迟更稳定，并且能保留 Shuo 现有豆包音色。两者速度差距不足以单独决定产品路线，建议结合下方音频选择音色与表达。</p><table><tr><th>方案</th><th>首包音频中位数</th><th>首包范围</th><th>成功</th></tr>${summary}</table></div>
<section><h2>本次发现</h2><p>数字样本中，原文只说第二季度营收增长 12.5%，没有指定与去年同期比较。专用翻译两轮均加入“同比”；Omni 两轮均没有添加这个含义。两者都保留了 320 万美元、利润下降 8%、未下调全年预测、增速降低不等于收入下降。</p><p>日常短文两条路线均保留保存文件、五分钟和不会删除文件。三段说明文均保留关键意思和顺序，未发现整段遗漏或额外开场白。组合方案的“应尽量保留”比原文“应保留”略弱；Omni 的“听音测试”“听测结果”措辞稍生硬。这是人工核对返回文字的结果，不等于已核实音频逐字一致。</p><p>Omni 一次短文首包为 3.20 秒，其余五次为 0.45–0.51 秒。样本少，不能据此判断长期稳定性或计算可靠的高分位延迟。没有进行主观音质打分；请用下方音频判断中文听感。</p></section>
${samples}
<section><h2>测量方式与限制</h2><p>组合方案使用 qwen-mt-flash 流式翻译完整原文，中文句号、问号、感叹号或换行后，立即按顺序调用现有 DoubaoSpeechClient 合成。无需等待全文翻译完成。豆包使用当前设置的“温柔妈妈 2.0”，语速 +10。Omni 使用 qwen3.5-omni-flash、Tina 音色，一次请求流式返回中文文本与音频。音色与语速不同，音频时长不能直接作为效率或漏译指标。</p><p>首次音频指从发起任务到收到非空音频；可缓冲播放指收到至少 9600 字节 PCM，与现有播放器约 200 毫秒音频门槛一致。本次所有首包均超过门槛。没有接扬声器测量，现有播放器还包含 300 毫秒启动静音及设备延迟，因此这些数字不是实际听到第一句话的耗时。</p><p>每条路线先做过一次短文连通测试，不计入结果。正式测试第一轮先组合再 Omni，第二轮反过来；请求按顺序执行，复用 HttpClient。全部音频保存为 24 kHz、16-bit、单声道 WAV。结果为服务生成速度，无本地播放器十秒缓冲上限的反压。本次未测取消、暂停、长时间运行、重试和费用，也未修改应用交互或启动新桌面预览。</p><p>返回译文较好的结果支持把 Omni 作为下一步候选；是否取代现有朗读，仍取决于试听与接入后的真实播放验证。用户现有设置和凭据只读使用，不写入结果文件。</p></section>
<section><h2>全部记录（秒）</h2><div class="scroll"><table><tr><th>样本 / 方案 / 轮次</th><th>首文字</th><th>首音频</th><th>可缓冲播放</th><th>生成完成</th><th>音频长度</th></tr>${detail}</table></div><p><a href="results.json">原始测量数据</a></p></section>
<section><h2>来源与复现</h2><p><a href="https://www.alibabacloud.com/help/en/model-studio/qwen-omni">Qwen-Omni 官方接口说明</a> · <a href="https://www.alibabacloud.com/help/en/model-studio/qwen-mt-api">Qwen-MT 官方接口说明</a></p><p>仓库根目录执行：<code>dotnet run --project test/Reading.Comparison -- --run</code>，随后执行 <code>node scripts/reading-comparison-report.mjs</code>。前者会使用已保存凭据调用付费云服务；不加 --run 只检查配置。</p></section>
<script>document.addEventListener('play',event=>{if(event.target.tagName==='AUDIO')document.querySelectorAll('audio').forEach(player=>{if(player!==event.target)player.pause()})},true)</script></html>`;
fs.writeFileSync(path.join(directory, 'comparison.html'), html);
console.log(`Validated ${results.length} WAV files; report: ${path.join(directory, 'comparison.html')}`);
