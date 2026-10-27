// 动态侧图片 url 归一与角标判定（思路照 dynamic-bot-bilibili 的 toNormalizedUrlOrNull / inferImageBadge）。

// B 站 feed 偶尔吐 //i0.hdslb.com/… 协议相对链：补 https: 再画；空串/空白回 null 让调用方走兜底。
export function normUrl(u) {
  if (!u) return null
  const s = String(u).trim()
  if (!s) return null
  return s.startsWith('//') ? `https:${s}` : s
}

// 九宫格角标：url 路径（去 ?# 尾巴）以 .gif 结尾 → 「动图」；高 > 宽×2 → 「长图」。判不出回 null。
// width/height 是 feed 定格的原始尺寸（opus.pics / draw.items(+images) 带，专栏封面没有）。
export function inferImageBadge(url, width, height) {
  if (url) {
    const path = String(url).split(/[?#]/)[0].toLowerCase()
    if (path.endsWith('.gif')) return '动图'
  }
  const w = Number(width)
  const h = Number(height)
  if (w > 0 && h > w * 2) return '长图'
  return null
}
