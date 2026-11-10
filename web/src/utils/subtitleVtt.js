// 字幕到货加工：浏览器 <track> 只认 WebVTT。
// 分站台账里的字幕九成是引擎写盘的 .srt（无 WEBVTT 头、时间戳用逗号），原样挂上 <track>
// 会被 VTT 解析器整文件判死：轨加载出 0 条 cue，面板写着「显示中」画面上却没有一个字——
// 站长 11-11 实机抓到的「字幕不显示」就是这一条。到货路上 fetch 下来就地转 VTT，
// 盘上的原始文件一个字节不动（导出包里躺着的仍是原字幕）。
// 认三形：.vtt（原样）/ .srt（补头 + 逗号换点）/ .json（B 站字幕那形 [{from,to,content}]，
// 可能裹在 body 里；认不出形就交空，绝不把 JSON 当 srt 硬转）。全部纯函数，node --test 直测。

// SRT 时间戳唯一要紧的病灶是那枚逗号：00:00:01,500 → 00:00:01.500。
// 不能整文件盲替：台词里真出现「3,000」会被误伤——所以按行锚定，
// 逗号只换在含 --> 的那一行（时间戳行）上，台词里的逗号一个不碰。
export function srtToVtt(text) {
  let body = String(text ?? '').replace(/^\uFEFF/, '').replace(/\r\n?/g, '\n').trim()
  if (!body) return ''
  if (/^WEBVTT/i.test(body)) return body // 挂着 .srt 的名却装着 VTT 的货：原样放行
  return (
    'WEBVTT\n\n' +
    body
      .split('\n')
      .map((line) => (line.includes('-->') ? line.replace(/(\d),(\d)/g, '$1.$2') : line))
      .join('\n')
  )
}

function pad(n, width) {
  return String(Math.max(0, Math.floor(n))).padStart(width, '0')
}

// 秒 → 00:00:00.000（B 站字幕 json 的 from/to 是浮点秒）。to 比 from 小的脏数据按 from 收平。
function secToStamp(sec) {
  const total = Number.isFinite(sec) && sec > 0 ? sec : 0
  const h = Math.floor(total / 3600)
  const m = Math.floor(total / 60) % 60
  const s = Math.floor(total) % 60
  const ms = Math.round((total - Math.floor(total)) * 1000)
  return `${pad(h, 2)}:${pad(m, 2)}:${pad(s, 2)}.${pad(ms, 3)}`
}

// B 站字幕 json：正身是 [{from, to, content}, ...]，也见过裹在 {body: [...]} 里的。
// 认不出形（不是数组、没有 body、一条 content 都没有）交空串——调用方拿空当「转不动」处置。
export function bilibiliJsonToVtt(raw) {
  let doc
  try {
    doc = JSON.parse(raw)
  } catch {
    return ''
  }
  const body = Array.isArray(doc) ? doc : Array.isArray(doc?.body) ? doc.body : null
  if (!body) return ''
  const cues = []
  for (const item of body) {
    const content = typeof item?.content === 'string' ? item.content.trim() : ''
    if (!content) continue
    const from = Number.isFinite(item.from) ? item.from : 0
    const to = Number.isFinite(item.to) && item.to > from ? item.to : from
    cues.push(`${secToStamp(from)} --> ${secToStamp(to)}\n${content}`)
  }
  if (!cues.length) return ''
  return 'WEBVTT\n\n' + cues.join('\n\n')
}

// 分派口：拿 URL 认后缀、拿正文定形。认不出的交空串（调用方退回原始地址碰运气，
// 坏轨与空轨在 <track> 眼里一个样，反正不比现状更坏）。
export function subtitleTextToVtt(url, raw) {
  const text = String(raw ?? '')
  const head = text.replace(/^\uFEFF/, '').trimStart()
  if (/^WEBVTT/i.test(head)) return head // 已是 VTT：原样（浏览器就吃这个）
  if (/\.json(\?|#|$)/i.test(String(url ?? ''))) return bilibiliJsonToVtt(text) // json 只走 json 的路
  return srtToVtt(text) // .srt 与没后缀的：按 srt 转
}

// ——— cue 台账（11-11 深夜自绘改造的第二段）———
// 原生 <track> 的 ::cue 染不了色、定不了位、更拖不动——站长要的 B 站式设置面板与可拖浮窗，
// 只能分站自己摆字：到货路上转完 VTT 顺手把 cue 解析成 [{start,end,text}] 交出去。
// 全部纯函数，node --test 直测。

// 时间戳 → 秒：认 hh:mm:ss.mmm / mm:ss.mmm / ss.mmm 三形；逗号也顺手认（SRT 逗号偶漏时别饿死整轨）。
function stampToSec(raw) {
  const s = String(raw ?? '').trim().replace(/,/, '.')
  if (!s) return NaN
  const parts = s.split(':')
  if (parts.length > 3) return NaN
  let acc = 0
  for (const p of parts) {
    const n = Number(p)
    if (!Number.isFinite(n) || n < 0) return NaN
    acc = acc * 60 + n
  }
  return acc
}

// 行内标记（<c>/<v>/<b>/<ruby> 那族）剥字留形；实体解包 &amp; 垫底，
// 不然 &amp;lt; 会被错解回 <。台词里的换行原样保留（前端 white-space 管摆法）。
function cueTextOf(bodyLines) {
  return bodyLines
    .join('\n')
    .replace(/<[^>]*>/g, '')
    .replace(/&nbsp;/g, ' ')
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&#39;/g, "'")
    .replace(/&amp;/g, '&')
    .trim()
}

// VTT 正文 → cues。只认「时间戳行打头、空行收尾」的块；
// 头部区（WEBVTT 行到首个空行）、cue 序号、NOTE/STYLE 那族记账行全数跳过；
// 脏块（时间戳认不出、to<=from、正文空）静默略过——一行坏账不配拖垮整轨。
export function parseVttCues(vttText) {
  const text = String(vttText ?? '').replace(/^\uFEFF/, '').replace(/\r\n?/g, '\n')
  const lines = text.split('\n')
  let i = 0
  while (i < lines.length && lines[i].trim() !== '') i++
  const cues = []
  while (i < lines.length) {
    while (i < lines.length && !lines[i].includes('-->')) i++
    if (i >= lines.length) break
    const [rawA, rawB = ''] = lines[i].split('-->')
    const start = stampToSec(rawA)
    const end = stampToSec(rawB.trim().split(/\s/)[0]) // 时间戳行尾挂的 position/align 设置不进秒表
    i++
    const body = []
    while (i < lines.length && lines[i].trim() !== '' && !lines[i].includes('-->')) {
      body.push(lines[i])
      i++
    }
    if (!Number.isFinite(start) || !Number.isFinite(end) || end <= start) continue
    const t = cueTextOf(body)
    if (!t) continue
    cues.push({ start, end, text: t })
  }
  return cues
}

// 此刻该摆哪句：前闭后开（t==start 算数、t==end 交班）；重叠多句拼成一段换行。
// 不靠「有序就提前 break」：cue 才几十条，全扫一趟的成本忽略不计，换来脏序账也不漏摆。
export function activeCueAt(cues, t) {
  if (!Array.isArray(cues) || !cues.length || !Number.isFinite(t)) return ''
  const hits = []
  for (const c of cues) {
    if (c.start <= t && t < c.end) hits.push(c.text)
  }
  return hits.join('\n')
}
