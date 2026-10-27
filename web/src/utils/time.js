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

// 进站类时间人性化：一小时内报分钟，一天内报小时，昨天点名，更早给日期。
// UTC 串（…Z）由 Date.parse 认时区；认不出的一律「—」，不编数。
export function friendlyTime(iso) {
  if (!iso) return '—'
  const t = Date.parse(iso)
  if (!Number.isFinite(t)) return '—'
  const pad = (n) => String(n).padStart(2, '0')
  const diff = Date.now() - t
  if (diff < 0) return new Date(t).toLocaleString('zh-CN', { hour12: false })
  const min = Math.floor(diff / 60000)
  if (min < 1) return '刚刚'
  if (min < 60) return `${min} 分钟前`
  const hr = Math.floor(min / 60)
  if (hr < 24) return `${hr} 小时前`
  const d = new Date(t)
  const key = (x) => `${x.getFullYear()}-${x.getMonth()}-${x.getDate()}`
  const today = new Date()
  const yest = new Date()
  yest.setDate(today.getDate() - 1)
  if (key(d) === key(yest)) return `昨天 ${pad(d.getHours())}:${pad(d.getMinutes())}`
  // 跨年日期裸显月/日会被当成今年——穿越错觉根源：隔年的补上年
  const sameYear = d.getFullYear() === today.getFullYear()
  return d.toLocaleString('zh-CN', sameYear
    ? { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit', hour12: false }
    : { year: 'numeric', month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit', hour12: false })
}
