// 弹幕高能条的取数与算料：与弹幕列表同源（馆藏 .ass），归档即真相。
// 官方那份走 dm/web/view 服务端下发，本站不碰网络接口：本地 .ass 前端数桶。
// 参数是站长在对照卡上定的稿：峰高 32px、白 15%、平滑 5s（高斯 σ）。
// playedAlpha 是播放头扫过后的点亮叠层：同一路径 clip 到已播段再叠一层白，
// 与底层合成后已播处明显更亮（左亮右暗），跟 B 站高能条一个观感。
export const HEAT = { peakPx: 32, alpha: 0.15, smooth: 5, playedAlpha: 0.4 }

// 同页同文件只 fetch 一次：弹幕引擎（DanmakuLayer）与高能条共用这一份文本缓存。
// 失败不记账（剔掉缓存），下次进页还能重试，不把一次抖动冻成整场哑巴。
// 容量封顶：整页翻下来 .ass 文本能攒好几 MB，按插入序淘汰最旧的一份（弹幕文本重复度高，命中率不受影响）。
const cache = new Map()
const CACHE_LIMIT = 100

export function loadAssText(url) {
  if (!url) return Promise.resolve(null)
  let p = cache.get(url)
  if (!p) {
    p = fetch(url)
      .then((r) => (r.ok ? r.text() : null))
      .catch(() => null)
    p.then((t) => { if (t === null) cache.delete(url) }) // 只留成功结果：没成形的不配占席
    if (cache.size >= CACHE_LIMIT) cache.delete(cache.keys().next().value)
    cache.set(url, p)
  }
  return p
}

// 1 秒/桶数 Dialogue 出现时刻（高能看的是「这一刻弹幕多不多」，不是停留时长），
// 再用高斯核抹坡：box 均值出的是「平台+陡崖」，高斯的长尾巴才是 B 站那种缓丘；
// sigma 即站长定的平滑秒数。全空给 null（调用方收起整条，零视觉存在）。
export function buildHeat(text, durationSec, smooth = HEAT.smooth) {
  if (!text || !(durationSec > 0) || !Number.isFinite(durationSec)) return null
  const n = Math.max(1, Math.ceil(durationSec))
  const bins = new Array(n).fill(0)
  for (const line of text.split(/\r?\n/)) {
    if (!line.startsWith('Dialogue: ')) continue
    const parts = line.slice(10).split(',')
    if (parts.length < 10) continue
    const m = parts[1].match(/(\d+):(\d{1,2}):(\d{1,2})/)
    if (!m) continue
    const t = Number(m[1]) * 3600 + Number(m[2]) * 60 + Number(m[3])
    if (!(t >= 0)) continue
    bins[Math.min(n - 1, Math.floor(t))] += 1
  }
  const sigma = Math.max(1, smooth)
  const radius = Math.max(1, Math.ceil(sigma * 2.5))
  const kernel = new Array(radius * 2 + 1)
  for (let k = -radius; k <= radius; k++) kernel[k + radius] = Math.exp(-(k * k) / (2 * sigma * sigma))
  let max = 0
  const sm = bins.map((_, i) => {
    let s = 0
    let cs = 0
    for (let k = -radius; k <= radius; k++) {
      const j = i + k
      if (j >= 0 && j < n) {
        const w = kernel[k + radius]
        s += bins[j] * w
        cs += w
      }
    }
    const v = s / cs
    if (v > max) max = v
    return v
  })
  if (!max) return null
  return sm.map((v) => v / max)
}
