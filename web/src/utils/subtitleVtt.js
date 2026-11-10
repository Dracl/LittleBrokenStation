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
