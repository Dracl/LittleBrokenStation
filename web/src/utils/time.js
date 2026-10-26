// 引擎存的时长是「2m19s」这种，B 站卡片用冒号制：分:秒，过小时补成 时:分:秒。
// 认不出 h/m/s 字样的（已经是冒号制或乱码）原样放行，不编数。
export function colonTime(text) {
  if (!text) return text
  const h = /(\d+)\s*h/i.exec(text)
  const m = /(\d+)\s*m/i.exec(text)
  const s = /(\d+)\s*s/i.exec(text)
  if (!h && !m && !s) return text
  const pad = (n) => String(n).padStart(2, '0')
  const hh = h ? +h[1] : 0
  const mm = m ? +m[1] : 0
  const ss = s ? +s[1] : 0
  return hh > 0 ? `${hh}:${pad(mm)}:${pad(ss)}` : `${mm}:${pad(ss)}`
}
