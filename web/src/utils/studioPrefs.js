// 棚内偏好持久化（preference persistence，与 dynLikes / faceChoice 同族）：
// 周报调参台、分享海报棚的旋钮拧过一次，下次开台还是那副样子——存本机 localStorage，
// 服务器不记一笔，站内每位成员各拿各的（与本机赞、本机钦选脸同一脾气）。
// 入账的只有「旋钮原值」：抽色、主题这类算出来的派生物不进账，开台现算，免得旧账把新算法框死。

// key → 最近读出的那份对象：同一棚反复挂载共享同一引用，A 开台拧的钮 B 开台直接接着用，
// 也堵住「两份初始拷贝互相覆盖」的串写。
const MEM = new Map()

/**
 * 读棚内偏好：默认值打底，localStorage 覆盖其上。
 * 嵌套的开关组（show 那族）逐格合并而不是整组替换——往后 defaults 添新开关，
 * 旧账里没这格也能吃到新默认，不会因为存过旧版就把新开关莫名「关掉」。
 * 账本坏了（手改 JSON / 隐私模式）一律当没存过，回默认值继续跑。
 */
export function loadStudioPrefs(key, defaults) {
  const cached = MEM.get(key)
  if (cached) return cached
  let stored = null
  try { stored = JSON.parse(localStorage.getItem(key) || 'null') } catch { stored = null }
  const merged = { ...defaults, ...(stored && typeof stored === 'object' ? stored : {}) }
  for (const [k, v] of Object.entries(defaults)) {
    if (v && typeof v === 'object' && !Array.isArray(v) && stored?.[k] && typeof stored[k] === 'object') {
      merged[k] = { ...v, ...stored[k] }
    }
  }
  MEM.set(key, merged)
  return merged
}

/** 写棚内偏好：写崩（配额满/隐私模式）不拦画画，下次照样能拧。 */
export function saveStudioPrefs(key, opts) {
  try { localStorage.setItem(key, JSON.stringify(opts)) } catch { /* 本机记不住就算了，画照常画 */ }
}
