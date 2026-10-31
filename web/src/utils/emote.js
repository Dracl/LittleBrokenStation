import { mentionHtml } from './mention'

// 评论正文渲染公共管线：转义 → @高亮 → [表情] 换图 → 失效表情压胶囊。
// 视频评论区与动态评论区共用一份，别复制第二份（CollapsibleRichText 就是走这里）。
//
// emoteMap：后端定格好的「转义符 → 图片地址」（本地 /emotes/ 优先，下载失败为远程热链）。
// emoteMeta：同一批表情的留档（转义符 → { jumpTitle, packageId, id, type }），归档侧 2026-10-29 起才写，
//            老快照没这个键，缺省即可 —— 它只用来把失效表情的名字念对，念不出来回退到转义符本身。
//
// 盒子的尺寸与对齐全站只有 style.css 的 .emote-token / .emote-dead 一处定义，这里只挂类名：
// 图与胶囊同高 18px、vertical-align:middle 再 translateY(-2px)，改口径只改 CSS，别再在字符串里补内联样式。
// 也别再挂 loading="lazy"：这些是十几 KB 的本地图，懒加载省不下什么，未进视口时盒子宽度是 0，
// 等到加载完才撑开，整条评论的文字跟着抖一次；量过一次现场，正是「表情被顶到单独一行」的放大器。
const TOKEN_RE = /\[[^\[\]\n]{1,40}\]/g
// UP 主专属转义符 [UP_<mid>_名]：B 停发 URL 之后，这是唯一还能认出「它本来是枚表情」的形状。
const UP_TOKEN_RE = /^\[UP_\d+_[^\[\]]{1,24}\]$/

function escAttr(s) {
  return String(s)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
}

// 已知表情包的前缀（[七禾表情包_suki] → '[七禾表情包_'）：同一包里没被下发的成员，形状与兄弟一致，
// 认这个前缀才敢压胶囊。认不出前缀、又不是 UP 专属形状的方括号（[233]、[1.2万]、【】里的口语）一律原样留着。
function knownPrefixes(emoteMap) {
  const set = new Set()
  for (const seq of Object.keys(emoteMap)) {
    if (!seq || seq.charCodeAt(0) !== 91) continue // '['
    const cut = seq.indexOf('_')
    if (cut > 1 && seq.endsWith(']')) set.add(seq.slice(0, cut + 1))
  }
  return set
}

// 胶囊里的字：优先用 B 站自己的 jump_title（「打call」这种人类叫法），没有就剥转义符的壳取末段。
function deadLabel(token, meta) {
  const titled = meta && (meta.jumpTitle ?? meta.jump_title)
  if (typeof titled === 'string' && titled.trim()) return titled.trim()
  const body = token.slice(1, -1)
  const segs = body.split('_')
  const last = segs[segs.length - 1]
  return last && !/^\d+$/.test(last) ? last : body
}

export function richHtml(text, emoteMap, emoteMeta) {
  const map = emoteMap ?? {}
  const meta = emoteMeta ?? {}
  const prefixes = knownPrefixes(map)
  // 一趟扫完：先换图再补胶囊会被自己拼出来的 alt="[UP_…]" 二次命中，合成一次 replace 就没这麻烦。
  return mentionHtml(text).replace(TOKEN_RE, (token) => {
    const loc = map[token]
    if (loc) {
      const safeSrc = String(loc).replace(/"/g, '&quot;')
      const alt = escAttr(token)
      return `<img src="${safeSrc}" alt="${alt}" title="${alt}" referrerpolicy="no-referrer" class="emote-token" />`
    }
    if (!UP_TOKEN_RE.test(token) && !prefixes.has(token.slice(0, token.indexOf('_') + 1))) return token
    return `<span class="emote-dead" title="${escAttr(token)} · B 站未下发图片">${escAttr(deadLabel(token, meta[token]))}</span>`
  })
}
