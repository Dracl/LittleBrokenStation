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

// B 站投稿时间的取数口径（首页卡阵与「接下来播放」共用，别再各写一份）：
// 首取导入源自带的 publishedAtUtc，其次到站归档的 publishText，两个都拿不到给 null——
// 入站时间是站长台账的概念，不该顶替投稿时间上桌（拿 createdAt 显的卡会满屏同一天）。
export function publishedDate(v) {
  if (!v) return null
  if (v.publishedAtUtc) {
    const d = new Date(v.publishedAtUtc)
    if (!Number.isNaN(d.getTime())) return d
  }
  if (v.publishText) {
    const d = new Date(String(v.publishText).replace(' ', 'T'))
    if (!Number.isNaN(d.getTime())) return d
  }
  return null
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
  // 裸月/日（连 zh-CN 都渲染成 2/5）最容易被误读成新动态：同年写中文月日，隔年的补上年
  const sameYear = d.getFullYear() === today.getFullYear()
  const datePart = sameYear
    ? `${d.getMonth() + 1}月${d.getDate()}日`
    : `${d.getFullYear()}年${d.getMonth() + 1}月${d.getDate()}日`
  return `${datePart} ${pad(d.getHours())}:${pad(d.getMinutes())}`
}
