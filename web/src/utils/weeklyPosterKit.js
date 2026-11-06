/* ─────────────────────────────────────────────────────────────────────────
   小破分站 · 周报海报共用工具箱
   ---------------------------------------------------------------------------
   两套皮肤（编辑部 / 构成主义）都用这一层：字体栈、量宽裁字、千分位、封面裁切。
   皮肤之间不许互相 import，只许各自 import 本文件——这样任一张塌了不牵连另一张。
   版面色不在这里：配色是皮肤的事，各自 export 自己的 theme()，由 weeklyPoster.js 分派。
   ───────────────────────────────────────────────────────────────────────── */

/** 海报基准宽度（px）。导出 PNG 时按这个宽度乘倍率放大。 */
export const POSTER_WIDTH = 750

// 首位 HarmonyOS Sans SC 与视频分享海报同字（utils/videoPoster.ensurePosterFont 按需注入，模块级单例）：
// 调用方开画前 await 一次，没注入成时自然降级后面几档，不阻塞不报错。
export const FF = '"HarmonyOS Sans SC","Microsoft YaHei","PingFang SC","Hiragino Sans GB",sans-serif'
export const FFM = '"Segoe UI","Helvetica Neue",Arial,sans-serif'

/**
 * 两套皮肤的入口表。WeeklyPage 的「风格」钮直接吃它，加一套皮肤走三步：
 *   1. 新写一个 weeklyPoster<名字>.js，export 四个口：theme / paint / backdrop / footer
 *   2. 在 weeklyPoster.js 的 SKINS 表里添一行
 *   3. 在这里添一条 { key, label, hint }，key 跟上一步对齐
 * key 是写进 opts.style 和存档里的稳定标识，改名只改 label，别动 key。
 */
export const POSTER_STYLES = [
  { key: 'editorial', label: '默认风格', hint: '网格 + 单强调色' },
  { key: 'construct', label: '构成主义', hint: '红黑纸 · 斜切与几何' },
]

/** 编辑部那套的三种强调色。粉蓝是分站本来的主色，琥珀是给换换口味的人留的第三档。 */
export const POSTER_ACCENTS = {
  pink: { base: '#fb7299', deep: '#e0557d', glow: 'rgba(251,114,153,.34)' },
  blue: { base: '#00a1d6', deep: '#0084b3', glow: 'rgba(0,161,214,.32)' },
  gold: { base: '#e0a13f', deep: '#bb7c22', glow: 'rgba(224,161,63,.30)' },
}

/**
 * 构成主义那套的大声色。这张海报的骨架永远是红黑米纸，accent 只换「大声」那一色，
 * 不许把米纸改成粉——所以这里不共用 POSTER_ACCENTS，另开一档，默认宣传红。
 */
export const POSTER_CONSTRUCT_ACCENTS = {
  red: { base: '#d8232a', deep: '#a5161c', name: '宣传红' },
  amber: { base: '#e0a13f', deep: '#a8741e', name: '赭黄' },
  indigo: { base: '#1f4fa8', deep: '#153a7d', name: '靛蓝' },
}

/**
 * 三种纸：白场（浅底黑字，排第一也就是默认那档）/ 夜航（深色）/ 粉版（整张浅粉底）。
 * 排在最前的是调参台默认选中的档，跟 WeeklyPage 里 posterOpts.theme 的初值对齐。
 */
export const POSTER_THEMES = [
  { key: 'paper', label: '白场', hint: '浅底黑字' },
  { key: 'ink', label: '夜航', hint: '深底银字' },
  { key: 'brand', label: '粉版', hint: '整张浅粉底' },
]

/* ── 基础画法 ─────────────────────────────────────────────────────────── */

export function roundRect(g, x, y, w, h, r) {
  const rr = Math.max(0, Math.min(r, Math.abs(w) / 2, Math.abs(h) / 2))
  g.beginPath()
  g.moveTo(x + rr, y)
  g.arcTo(x + w, y, x + w, y + h, rr)
  g.arcTo(x + w, y + h, x, y + h, rr)
  g.arcTo(x, y + h, x, y, rr)
  g.arcTo(x, y, x + w, y, rr)
  g.closePath()
}

/** 量宽裁字：超了就砍尾巴加省略号。调用前先摆好 font。 */
export function fitText(g, text, maxW) {
  const s = String(text ?? '')
  if (!s) return ''
  if (g.measureText(s).width <= maxW) return s
  let out = s
  while (out.length > 1 && g.measureText(`${out}…`).width > maxW) out = out.slice(0, -1)
  return `${out}…`
}

/** 小号英文标签：字距拉开，压在中文标题旁边当呼吸位。 */
export function kicker(g, text, x, y, size, color, spacing = '2px', align = 'left') {
  g.save()
  if ('letterSpacing' in g) g.letterSpacing = spacing
  g.fillStyle = color
  g.font = `600 ${size}px ${FFM}`
  g.textAlign = align
  g.fillText(text, x, y)
  g.restore()
}

/**
 * 抖散渐变台阶：铺一层极淡的随机噪点。
 * 8bit 画布上浅渐变容易起横纹（banding），拿噪声把量化边界打散，
 * 噪点本身肉眼几乎看不见，台阶就化开了。amount 是噪点浓度（0~255 量级）。
 */
let noiseTile = null
function makeNoiseTile(size = 180) {
  if (noiseTile && noiseTile.width === size) return noiseTile
  const c = document.createElement('canvas')
  c.width = size
  c.height = size
  const g = c.getContext('2d')
  const img = g.createImageData(size, size)
  for (let i = 0; i < img.data.length; i += 4) {
    const v = Math.floor(Math.random() * 256)
    img.data[i] = v
    img.data[i + 1] = v
    img.data[i + 2] = v
    img.data[i + 3] = 26
  }
  g.putImageData(img, 0, 0)
  noiseTile = c
  return c
}

/** 在指定区域上铺噪点。只在渐变大块地上用，贴到字上就脏了。 */
export function dither(g, x, y, w, h, amount = 0.05) {
  if (!(w > 0) || !(h > 0) || !(amount > 0)) return
  const tile = makeNoiseTile()
  const pat = g.createPattern(tile, 'repeat')
  if (!pat) return
  g.save()
  g.globalAlpha = Math.min(0.3, amount)
  g.fillStyle = pat
  g.fillRect(x, y, w, h)
  g.restore()
}

/** 千分位：大数读起来不费劲。 */
export function fmtNum(n) {
  const v = Number(n ?? 0)
  return String(v).replace(/\B(?=(\d{3})+(?!\d))/g, ',')
}

/**
 * 明信片抖数：从种子（bvid）折出一枚稳定的小转角与纵向错位（FNV-1a 32 位）。
 * 同一条片子每次导出都歪在同一个角度——与周报页新货墙「洗牌也不跳家」同一条脾气。
 * rot ±1.2°（弧度）、dy ±3px：够看出是手摆的，不够看出是乱丢的。
 */
export function posterJitter(seed) {
  const s = String(seed ?? '')
  let h = 2166136261
  for (let i = 0; i < s.length; i += 1) {
    h ^= s.charCodeAt(i)
    h = Math.imul(h, 16777619) >>> 0
  }
  const a = (h % 1000) / 1000
  const b = ((h >>> 10) % 1000) / 1000
  return { rot: (a - 0.5) * 0.042, dy: Math.round((b - 0.5) * 6) }
}

/**
 * 封面按 cover 方式裁切填进框里（跟 <img object-fit:cover> 一个意思）。
 * opts.clip：自己建裁切路径（不给就 8px 圆角矩形），皮肤要斜切平行四边形就从这儿塞。
 * opts.overlay：图落好之后再叠一层（网点、色罩都走它），仍在 clip 内，不会糊到框外。
 */
export function drawCover(g, img, x, y, w, h, T, label, opts = {}) {
  g.save()
  if (typeof opts.clip === 'function') opts.clip(g, x, y, w, h)
  else roundRect(g, x, y, w, h, opts.radius ?? 8)
  g.clip()
  if (img && img.width > 0 && img.height > 0) {
    const ir = img.width / img.height
    const tr = w / h
    let sw
    let sh
    let sx
    let sy
    if (ir > tr) {
      sh = img.height
      sw = sh * tr
      sx = (img.width - sw) / 2
      sy = 0
    } else {
      sw = img.width
      sh = sw / tr
      sx = 0
      sy = (img.height - sh) / 2
    }
    g.drawImage(img, sx, sy, sw, sh, x, y, w, h)
  } else {
    // 没有封面就摆一块有质感的灰渐变，不摆黑方块糊弄人
    const grad = g.createLinearGradient(x, y, x + w, y + h)
    grad.addColorStop(0, T.phA)
    grad.addColorStop(1, T.phB)
    g.fillStyle = grad
    g.fillRect(x, y, w, h)
    g.fillStyle = T.phFg
    g.font = `500 11px ${FFM}`
    g.textAlign = 'center'
    g.fillText(fitText(g, label || '', w - 16), x + w / 2, y + h / 2 + 4)
    g.textAlign = 'left'
  }
  if (typeof opts.overlay === 'function') opts.overlay(g, x, y, w, h)
  g.restore()
}

/**
 * 落款二维码（11-06 站长点单，当日二改）：海报最底下居中一枚大码，内容 = 本周周报地址。
 * 二改缘由：首版码挂在发丝线上方，中间一条空白带被站长打回「空空的好难看」——
 * 现在码紧跟正文，发丝线与日期降到码下压底（renderPosterOn 把整高递给 footer，不再扣码带）。
 * 白盘位置从发丝线往上算：hair 对齐各皮肤 footer 的发丝线（H-62），gap 是盘底到发丝线的呼吸。
 * 码体由调用方（WeeklyPage 拿 qrcodejs 现成组件）生成好后递 canvas，画笔只管摆盘。
 */
export const POSTER_QR = { size: 124, pad: 14, band: 178, ring: 10, hair: 62, gap: 28 }

export function posterQr(g, W, H, qrCanvas) {
  const S = POSTER_QR.size
  const box = S + POSTER_QR.pad * 2
  const bx = Math.round((W - box) / 2)
  // 盘底从发丝线往上留 gap 呼吸（站长 11-06 两轮下挪钦定：35→25）；发丝线位与各皮肤 footer 的 H-62 对齐
  const by = H - POSTER_QR.hair - POSTER_QR.gap - box
  g.save()
  g.fillStyle = '#ffffff'
  roundRect(g, bx, by, box, box, POSTER_QR.ring)
  g.fill()
  if (qrCanvas && qrCanvas.width > 0) {
    g.drawImage(qrCanvas, bx + POSTER_QR.pad, by + POSTER_QR.pad, S, S)
  }
  g.restore()
}
