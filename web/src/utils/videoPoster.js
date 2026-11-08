/* ─────────────────────────────────────────────────────────────────────────
   小破分站 · 视频分享海报画笔（观看页那颗「分享海报」用）
   ---------------------------------------------------------------------------
   版式照 dynamic-bot 的 default 布局、经站长 11-04 逐版验收定稿：设计基准 1000dp、
   四周 scenePadding 20、整底左上→右下渐变，头卡 150 高五层垫底 + 一张卡贴边（封面 16:9
   / 角标 / 底部遮罩行 / 标题 / 简介；套娃的内容卡/媒体卡已拆平，站长 v8 定）。
   配色不从头像取、从**封面**取：extractPosterColors 在中心内接圆里按 size/96 网格采样，
   36×5×5 三维桶 + 四因子权重选至多 3 枚主色，再交 themeFromColors 按主色亮度判 LIGHT/DARK
   双模式派生（暗封面走深色渐变底 + 白字，浅封面走糖果底 + 黑字），逐行照搬原件。

   与周报海报（weeklyPoster*.js）是两套独立东西：这里只借 weeklyPosterKit 的
   fitText 一把尺子，不 import 任何一套周报皮肤，免得周报的纸样哪天改了把这
   张卡一起带歪。

   字体纪律：HarmonyOS Sans SC（4MB woff2）不进首屏——ensurePosterFont() 只在开画前被
   await（模块级单例，只下载一次），拿不到就照 FONT_FAMILY 降级链画，海报不许因为字体塌。

   图源纪律：封面与头像只认同源那一份（/media、/api、/avatars 开头或 data: 内联）。B 站图床
   是热链，画进 canvas 就是脏像素，toDataURL 当场抛 SecurityError —— 热链一
   律不画，交兜底（灰渐变 +「封面」占位字 / 深底圆 + UP 名首字）。
   ───────────────────────────────────────────────────────────────────────── */

import { fitText } from './weeklyPosterKit.js'

/** 海报基准宽度（dp）。导出 PNG 时按这个宽度乘倍率放大。 */
export const POSTER_WIDTH = 1000
/** 导出倍率：2 倍 = 宽 2000px，发群里够清楚又不肥。 */
export const POSTER_EXPORT_SCALE = 2

/** 字体栈：首位 HarmonyOS Sans SC 由 ensurePosterFont() 按需注入（模块级单例），后面几档是降级链。 */
export const FONT_FAMILY = '"HarmonyOS Sans SC","Microsoft YaHei","PingFang SC","Noto Sans CJK SC",sans-serif'

/* ── 版式尺子（与 dynamic-bot default 布局逐条对齐）─────────────────────── */

const PAD = 20 // scenePadding
const CARD_R = 15 // 卡片圆角
const CARD_BORDER = 3
const HEADER_H = 150 // 有二维码那一档的作者卡
const CONTENT_GAP = 20 // 头卡与内容卡之间（contentSpacing）
const CONTENT_PAD = 20 // 内容卡内边距（cardPadding）
const CARD_W = POSTER_WIDTH - PAD * 2

const AV = { size: 112, left: 30, top: 18, textSpacing: 18, ring: 4 }
// UP 名不叠头像（站长 v5 裁定）：开头与日期共用一条竖线（文本列起点）
const NAME = { size: 50, lineHeight: 80, top: 2, left: 0 }
const TIME = { size: 36, lineHeight: 42, topSpacing: 10 }
const TEXT_RIGHT = 12 // rightSpacing
// 码缩一档并整体内缩，与头卡边缘留呼吸位、中心不挂任何标（站长 v6/v7 定稿）
const QR = { size: 122, bleedLeft: 68, inset: 14 }

const COVER_R = 10
const COVER_RATIO = 16 / 9
const BADGE = { fontSize: 30, lineHeight: 46, paddingX: 30, maxTextW: 200, cornerR: 6 }
const MASK_H = 75 // 60 放二成五（站长 11-05 定：文字要多一层雾托），过渡曲线照旧余弦缓降
const PILL = { marginLeft: 40, paddingX: 15, fontSize: 26, lineHeight: 38, radius: 10, bottom: 14 }
const INFO = { fontSize: 28, marginLeft: 15, maxWidth: 600, bottom: 14 }
const TITLE = { size: 34, lineHeight: 46, maxLines: 2, marginTop: 15, marginBottom: 10, marginX: 15 }
const DESC = { size: 24, lineHeight: 32, maxLines: 3, marginBottom: 15, marginX: 15 }
// 调参台「落款」那一行：画在卡外地带右对齐，不开则整块高度不计
 const SIGN = { size: 20, lineHeight: 28, marginTop: 14 }

/** 调参台选项的出厂态：normalizeOpts 把外部传进来的半成品补成完整体。 */
const DEFAULT_SHOW = { qr: true, badge: true, duration: true, views: true, danmaku: true, likes: true, title: true, desc: true }

export function normalizeOpts(raw) {
  if (!raw) return null
  if (raw.norm) return raw // 幂等：已补成完整体的直接放行（renderVideoPoster 与 paintPoster 会先后拿同一份）
  return {
    norm: true,
    title: String(raw.titleOverride ?? '').trim(),
    desc: String(raw.descOverride ?? '').trim(),
    badgeText: String(raw.badgeText ?? '').trim() || '视频',
    show: { ...DEFAULT_SHOW, ...(raw.show || {}) },
    mode: raw.mode === 'light' || raw.mode === 'dark' ? raw.mode : 'auto',
    accent: /^#[0-9a-fA-F]{6}$/.test(String(raw.accent || '')) ? String(raw.accent) : null,
    sign: String(raw.sign ?? '').trim(),
    // 简介显示行数（调参台）：1~10（站长 11-05 要拉条自由拉），缺省照 DESC.maxLines
    descMaxLines: Number.isFinite(raw.descMaxLines) ? Math.min(10, Math.max(1, Math.round(raw.descMaxLines))) : null,
  }
}

/** Material 三档投影（skiko 的 Shadow.ELEVATION_3 / _2 与头像那一档 #29000000）。 */
const E3 = [
  { x: 0, y: 3, blur: 5, color: 'rgba(0,0,0,0.2)' },
  { x: 0, y: 6, blur: 10, color: 'rgba(0,0,0,0.141)' },
  { x: 0, y: 1, blur: 18, color: 'rgba(0,0,0,0.122)' },
]
// 封面自己的那一档（skiko Shadow.ELEVATION_2）：v8 拆平时被我拆过头丢了，站长 11-04 对照原件红框要求补回
const E2 = [
  { x: 0, y: 3, blur: 1, color: 'rgba(0,0,0,0.2)' },
  { x: 0, y: 2, blur: 2, color: 'rgba(0,0,0,0.141)' },
  { x: 0, y: 1, blur: 4, color: 'rgba(0,0,0,0.122)' },
]
const AV_SHADOW = [{ x: 0, y: 2, blur: 4, color: 'rgba(0,0,0,0.161)' }]

/* ── 颜色小工具 ─────────────────────────────────────────────────────────── */

const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v))

/** [r,g,b] + alpha → css 串。 */
function rgba(c, a = 1) {
  return a >= 1 ? `rgb(${c[0]},${c[1]},${c[2]})` : `rgba(${c[0]},${c[1]},${c[2]},${a})`
}

/** 把抽出来的色递给画笔外的调用方（二维码点阵色要交给 qrcode 库，它只认 css 串）。 */
export function rgbCss(c, a = 1) {
  return rgba(c, a)
}

function hsvToRgb(h, s, v) {
  const hue = ((h % 360) + 360) % 360 / 60
  const c = 255 * v * s
  const x = c * (1 - Math.abs((hue % 2) - 1))
  const m = 255 * v * (1 - s)
  const seg = [
    [c, x, 0], [x, c, 0], [0, c, x], [0, x, c], [x, 0, c], [c, 0, x],
  ][Math.floor(hue) % 6]
  return [seg[0] + m, seg[1] + m, seg[2] + m].map((n) => Math.round(clamp(n, 0, 255)))
}

function hslToRgb(h, s, l) {
  const hue = (((h % 360) + 360) % 360) / 360
  const q = l < 0.5 ? l * (1 + s) : l + s - l * s
  const p = 2 * l - q
  const edge = (t) => {
    let tt = t
    if (tt < 0) tt += 1
    if (tt > 1) tt -= 1
    if (tt < 1 / 6) return p + (q - p) * 6 * tt
    if (tt < 1 / 2) return q
    if (tt < 2 / 3) return p + (q - p) * (2 / 3 - tt) * 6
    return p
  }
  return [edge(hue + 1 / 3), edge(hue), edge(hue - 1 / 3)].map((n) => Math.round(clamp(n * 255, 0, 255)))
}

function rgbToHsb(r, g, b) {
  const rr = r / 255
  const gg = g / 255
  const bb = b / 255
  const max = Math.max(rr, gg, bb)
  const min = Math.min(rr, gg, bb)
  const d = max - min
  let h = 0
  if (d !== 0) {
    if (max === rr) h = ((gg - bb) / d) % 6
    else if (max === gg) h = (bb - rr) / d + 2
    else h = (rr - gg) / d + 4
    h *= 60
    if (h < 0) h += 360
  }
  return [h / 360, max === 0 ? 0 : d / max, max]
}

/** WCAG 相对亮度与对比度：二维码点阵能不能在白底上认出来，就看这个数。 */
function luminance(c) {
  const ch = (v) => {
    const x = v / 255
    return x <= 0.03928 ? x / 12.92 : ((x + 0.055) / 1.055) ** 2.4
  }
  return 0.2126 * ch(c[0]) + 0.7152 * ch(c[1]) + 0.0722 * ch(c[2])
}

function contrast(a, b) {
  const la = luminance(a)
  const lb = luminance(b)
  return (Math.max(la, lb) + 0.05) / (Math.min(la, lb) + 0.05)
}

/** 往白里并色（照 AuthorStyle.blendWithWhite）：ratio 越小越接近本色。 */
function blendWhite(c, ratio) {
  return c.map((v) => Math.round(clamp(255 + (v - 255) * ratio, 0, 255)))
}

/* ── 主题抽取：只吃封面 ─────────────────────────────────────────────────── */

// —— 照 dynamic-bot 的 AvatarThemeExtractor + DrawThemeFactory 逐行移植，源图从头像换成封面（站长 11-04 定案）——
// ① 抽取：中心内接圆、36×5×5 三维桶，四因子权重（中心距/饱和/亮度峰 0.62/浅色系加成），
//    肤色不剔除只 ×0.38 降权，桶样本下限滤噪，选出至多 3 枚加权平均真彩；
// ② 派生：detectMode 量主色亮度判 LIGHT/DARK —— 暗封面走深色渐变底 + 深色半透卡 + 白字，
//    浅色走糖果底 + 白卡 + 黑字（此前写死 LIGHT，暗封面被强套糖果底就是这个 bug）。
const NEUTRAL_SAT = 0.08 // 饱和低于此算中性色，色相落 DEFAULT_THEME_HUE
const DEFAULT_THEME_HUE = 335
const FALLBACK_PALETTE = ['#808080'] // 抽色一场空/图读不动：中性灰进派生厂，色相自然落 335，海报不塌

// 抽取端常量（AvatarThemeExtractor.kt）
const HUE_BUCKETS = 36
const SAT_BUCKETS = 5
const VAL_BUCKETS = 5
const MAX_EXTRACTED = 3
const MIN_BUCKET_SAMPLES = 12
const MIN_BUCKET_SAMPLE_RATIO = 0.004
const MIN_SECONDARY_HUE_DISTANCE = 24
const SECONDARY_SCORE_RATIO = 0.34
const SKIN_FACTOR = 0.38

function rgbToHsl(r, g, b) {
  const rr = r / 255
  const gg = g / 255
  const bb = b / 255
  const max = Math.max(rr, gg, bb)
  const min = Math.min(rr, gg, bb)
  const l = (max + min) / 2
  const d = max - min
  if (d === 0) return [0, 0, l]
  const s = l > 0.5 ? d / (2 - max - min) : d / (max + min)
  let h
  if (max === rr) h = ((gg - bb) / d) % 6
  else if (max === gg) h = (bb - rr) / d + 2
  else h = (rr - gg) / d + 4
  h *= 60
  if (h < 0) h += 360
  return [h, s, l]
}

function isUsefulPixel(s, v) {
  if (s < 0.1 || v < 0.08 || v > 0.985) return false
  // 高亮低饱和（皮肤/高光/白底）直接纳入会让画面偏灰
  return !(s < 0.16 && v > 0.86)
}

function pixelWeight(s, v, dist) {
  const center = 0.78 + clamp(1 - dist, 0, 1) * 0.38
  const sw = 0.55 + s * 1.15
  const vw = 0.78 + (1 - Math.min(1, Math.abs(v - 0.62) / 0.62)) * 0.22
  const pastel = v >= 0.82 && s >= 0.16 && s <= 0.45 ? 1.18 : 1
  return center * sw * vw * pastel
}

function isSkinLike(hueDeg, s, v) {
  return hueDeg >= 12 && hueDeg <= 48 && s >= 0.16 && s <= 0.58 && v >= 0.38
}

/** 扫一遍封面：抽至多 3 枚主色 + 量全屏亮度分布（V 直方图）；读不动/一场空给 FALLBACK_PALETTE。 */
export function scanPosterImage(img) {
  const iw = img?.naturalWidth || img?.width || 0
  const ih = img?.naturalHeight || img?.height || 0
  if (!(iw > 0) || !(ih > 0)) return { palette: [...FALLBACK_PALETTE], darkFrac: 0, medianV: 1 }
  const size = Math.max(1, Math.min(iw, ih))
  const left = Math.max(0, Math.floor((iw - size) / 2))
  const top = Math.max(0, Math.floor((ih - size) / 2))
  // 原件是在原尺寸位图上跳格取像素（step=size/96），不经任何重采样。
  // 早先这里缩到 128×128 再采，浏览器高质量降采样把霓光抹成大片粉彩像素，
  // 把暗封面的主色盘洗成粉系误判 —— 必须原尺寸 1:1 取图。
  let data
  try {
    const c = document.createElement('canvas')
    c.width = size
    c.height = size
    const g = c.getContext('2d', { willReadFrequently: true })
    g.imageSmoothingEnabled = false
    g.drawImage(img, left, top, size, size, 0, 0, size, size)
    data = g.getImageData(0, 0, size, size).data
  } catch {
    return { palette: [...FALLBACK_PALETTE], darkFrac: 0, medianV: 1 } // 热链脏像素/解码失败：fallback 接着画，不炸
  }
  const step = Math.max(1, Math.floor(size / 96)) // 原件：Int 除法 floor(size/96)，至少 1
  const center = size / 2
  const buckets = new Map()
  let circleCount = 0
  // 全屏亮度分布（站长 11-04 定案的对质补充）：不只看 3 枚主色，还看整体观感。
  // 百分之一一档的 V 直方图，只统计不透明像素；暗字判定与中位数都从它算。
  const vHist = new Int32Array(100)
  let vCount = 0
  for (let y = 0; y < size; y += step) {
    for (let x = 0; x < size; x += step) {
      const dx = (x + 0.5 - center) / center
      const dy = (y + 0.5 - center) / center
      const dist = Math.hypot(dx, dy)
      if (dist > 1) continue
      circleCount += 1
      const i = (y * size + x) * 4
      if (data[i + 3] < 200) continue
      const r0 = data[i]
      const g0 = data[i + 1]
      const b0 = data[i + 2]
      const [h01, s, v] = rgbToHsb(r0, g0, b0)
      vHist[Math.min(99, Math.floor(v * 100))] += 1
      vCount += 1
      if (!isUsefulPixel(s, v)) continue
      const hueDeg = h01 * 360
      const key = (clamp(Math.floor(h01 * HUE_BUCKETS), 0, HUE_BUCKETS - 1) << 8)
        | (clamp(Math.floor(s * SAT_BUCKETS), 0, SAT_BUCKETS - 1) << 4)
        | clamp(Math.floor(v * VAL_BUCKETS), 0, VAL_BUCKETS - 1)
      const cw = pixelWeight(s, v, dist)
      const sw = cw * (isSkinLike(hueDeg, s, v) ? SKIN_FACTOR : 1)
      let bk = buckets.get(key)
      if (!bk) {
        bk = { r: 0, g: 0, b: 0, cw: 0, score: 0, hs: 0, hc: 0, n: 0 }
        buckets.set(key, bk)
      }
      bk.r += r0 * cw
      bk.g += g0 * cw
      bk.b += b0 * cw
      bk.cw += cw
      bk.score += sw
      bk.hs += Math.sin((hueDeg * Math.PI) / 180) * cw
      bk.hc += Math.cos((hueDeg * Math.PI) / 180) * cw
      bk.n += 1
    }
  }
  const minSamples = Math.max(MIN_BUCKET_SAMPLES, Math.ceil(circleCount * MIN_BUCKET_SAMPLE_RATIO))
  const cands = [...buckets.values()].filter((bk) => bk.n >= minSamples && bk.score > 0)
  cands.sort((a, b) => b.score - a.score)
  const avgHue = (bk) => {
    const d = Math.max(0.0001, bk.cw)
    const h = (Math.atan2(bk.hs / d, bk.hc / d) * 180) / Math.PI
    return h < 0 ? h + 360 : h
  }
  const hueDist = (a, b) => {
    const d = Math.abs(avgHue(a) - avgHue(b))
    return Math.min(d, 360 - d)
  }
  const toHex = (bk) => {
    const d = Math.max(0.0001, bk.cw)
    const hex = (v) => Math.round(clamp(v / d, 0, 255)).toString(16).padStart(2, '0')
    return `#${hex(bk.r)}${hex(bk.g)}${hex(bk.b)}`
  }
  const sel = []
  for (const bk of cands) {
    if (sel.length >= MAX_EXTRACTED) break
    if (sel.length && bk.score < cands[0].score * SECONDARY_SCORE_RATIO) continue
    if (sel.length && sel.some((s) => hueDist(bk, s) < MIN_SECONDARY_HUE_DISTANCE)) continue
    sel.push(bk)
  }
  if (!sel.length) return { palette: [...FALLBACK_PALETTE], darkFrac: 0, medianV: 1 }
  let dark = 0
  for (let b = 0; b < 35; b += 1) dark += vHist[b] // V<0.35 算暗像素
  let medianV = 1
  if (vCount > 0) {
    let acc = 0
    for (let b = 0; b < 100; b += 1) {
      acc += vHist[b]
      if (acc * 2 >= vCount) {
        medianV = (b + 0.5) / 100
        break
      }
    }
  }
  return {
    palette: sel.map(toHex),
    darkFrac: vCount > 0 ? dark / vCount : 0,
    medianV,
  }
}

/** 只要主色盘（theme 冒烟与外部探针用）。 */
export function extractPosterColors(img) {
  return scanPosterImage(img).palette
}

/* 派生段：DrawThemeFactory.fromColors 的 JS 对应。 */

const parseHex = (hx) => {
  const s = String(hx).replace('#', '')
  return [parseInt(s.slice(0, 2), 16), parseInt(s.slice(2, 4), 16), parseInt(s.slice(4, 6), 16)]
}

function themeHueOf(rgbArr) {
  const [h01, s] = rgbToHsb(rgbArr[0], rgbArr[1], rgbArr[2])
  return s < NEUTRAL_SAT ? DEFAULT_THEME_HUE : h01 * 360
}

function detectMode(colors) {
  const avgL = colors.reduce((acc, c) => acc + rgbToHsl(c[0], c[1], c[2])[2], 0) / colors.length
  const avgV = colors.reduce((acc, c) => acc + rgbToHsb(c[0], c[1], c[2])[2], 0) / colors.length
  return avgL >= 0.42 || avgV >= 0.58 ? 'LIGHT' : 'DARK'
}

function pickMode(colors, stats) {
  const base = detectMode(colors)
  if (!stats) return base
  const darkLook = (stats.darkFrac ?? 0) >= 0.1 || (stats.medianV ?? 1) < 0.65
  return darkLook ? 'DARK' : base
}

function normalizeBg(rgbArr, mode) {
  const [, s, v] = rgbToHsb(rgbArr[0], rgbArr[1], rgbArr[2])
  const hue = themeHueOf(rgbArr)
  if (mode === 'LIGHT') return hsvToRgb(hue, s < NEUTRAL_SAT ? 0.12 : 0.3, 1.0)
  const sat = s < NEUTRAL_SAT ? 0.22 : clamp(s, 0.34, 0.52)
  return hsvToRgb(hue, sat, clamp(v * 0.32 + 0.16, 0.16, 0.34))
}

function gradientSingle(rgbArr, mode) {
  const [, s, v] = rgbToHsb(rgbArr[0], rgbArr[1], rgbArr[2])
  const hue = themeHueOf(rgbArr)
  const hues = [hue - 30, hue, hue + 30]
  if (mode === 'LIGHT') {
    const sat = s < NEUTRAL_SAT ? 0.12 : 0.3
    return hues.map((nh) => hsvToRgb(nh, sat, 1.0))
  }
  const sat = s < NEUTRAL_SAT ? 0.22 : clamp(s, 0.34, 0.52)
  const bri = clamp(v * 0.32 + 0.16, 0.16, 0.34)
  return hues.map((nh) => hsvToRgb(nh, sat, bri))
}

function primaryAccent(rgbArr, mode) {
  const [h, s] = rgbToHsl(rgbArr[0], rgbArr[1], rgbArr[2])
  const sat = s < NEUTRAL_SAT ? 0.92 : clamp(s, 0.72, 1.0)
  return hslToRgb(themeHueOf(rgbArr), sat, mode === 'LIGHT' ? 0.7 : 0.76)
}

/** 保对比度的小循环：按步进提亮/压暗，最多 12 档，还不行给 fallback（原件 ensureContrast 语义）。 */
function ensureContrast(rgbArr, background, minRatio, lighten, fallback) {
  const [h, s, l] = rgbToHsl(rgbArr[0], rgbArr[1], rgbArr[2])
  for (let i = 0; i <= 12; i += 1) {
    const cand = hslToRgb(h, s, clamp(l + (lighten ? 1 : -1) * 0.04 * i, 0, 1))
    if (contrast(cand, background) >= minRatio) return cand
  }
  return fallback
}

function readableAccentColor(rgbArr, mode, cardBase) {
  const [h, s] = rgbToHsl(rgbArr[0], rgbArr[1], rgbArr[2])
  const sat = s < NEUTRAL_SAT ? 0.55 : clamp(s, 0.45, 0.72)
  const candidate = hslToRgb(themeHueOf(rgbArr), sat, mode === 'LIGHT' ? 0.36 : 0.72)
  return ensureContrast(
    candidate,
    cardBase,
    4.5,
    mode === 'DARK',
    mode === 'LIGHT' ? [0, 93, 153] : [145, 216, 255],
  )
}

function readableTextOnAccent(rgbArr) {
  const white = contrast([255, 255, 255], rgbArr)
  if (white >= 2.6) return '#ffffff'
  const black = contrast([0, 0, 0], rgbArr)
  if (black >= 2.6) return '#111111'
  return white >= black ? '#ffffff' : '#111111'
}

function qrPointColor(primary, readableAccent) {
  // 点阵实际坐在 paintQr 的奶白 bar 上（blendWhite(primary,.08~.24) 的浅渐变），
  // 两种模式都按白底判对比。原件 dynamic 卡把码坐深底、DARK 按深底放行浅色点阵；
  // 站长定稿改成奶白 bar 后点阵得跟着白底走，否则锁屏式深主题出一脸浅橙码洗白。
  const bg = [255, 255, 255]
  const minRatio = 2.4
  if (contrast(primary, bg) >= minRatio) return primary
  if (contrast(readableAccent, bg) >= minRatio) return readableAccent
  return ensureContrast(primary, bg, minRatio, false, readableAccent)
}

/** 至多 3 枚 '#rrggbb' → 完整 theme（明暗双模式，字段消费点见 paintPoster）。
 * stats（scanPosterImage 的亮度分布）可选：给了就在原件三均值判定之外加一条
 * 整体观感判 —— 暗像素占比 ≥10% 或中位 V<0.65 就落 DARK。原件为方形头像而设，
 * 封面中心常坐一个亮色角色把三均值抬进 LIGHT（锁那张粉发即此症）；两判 OR，
 * 哪边喊暗就暗（站长 11-04 定案）。不给 stats 则纯原件语义，冒烟对照用。
 * modeOverride 给 'light'/'dark' 时直接接管判式（调参台「恒浅/恒深」），'auto' 照旧。 */
export function themeFromColors(palette, stats, modeOverride) {
  const hexes = palette?.length ? palette : FALLBACK_PALETTE
  const colors = hexes.map(parseHex)
  const mode = modeOverride === 'light' ? 'LIGHT' : modeOverride === 'dark' ? 'DARK' : pickMode(colors, stats)
  const first = colors[0]
  const cardBase = mode === 'LIGHT' ? [255, 255, 255] : [30, 34, 42]
  const backgroundColors = colors.length === 1
    ? gradientSingle(first, mode)
    : colors.map((c) => normalizeBg(c, mode))
  const primary = primaryAccent(first, mode)
  const readableAccent = readableAccentColor(first, mode, cardBase)
  const qrPoint = qrPointColor(primary, readableAccent)
  return {
    mode,
    backgroundColors,
    primary,
    readableAccent,
    onPrimary: readableTextOnAccent(primary),
    qrPoint,
    cardColor: mode === 'LIGHT' ? 'rgba(255,255,255,0.65)' : 'rgba(19,23,31,0.3)',
    borderColor: mode === 'LIGHT' ? 'rgba(255,255,255,0.88)' : 'rgba(255,255,255,0.2)',
    textColor: mode === 'LIGHT' ? 'rgba(0,0,0,0.85)' : 'rgba(255,255,255,0.85)',
    secondaryTextColor: mode === 'LIGHT' ? 'rgba(0,0,0,0.72)' : 'rgba(255,255,255,0.78)',
  }
}

function extractPosterTheme(img, opts) {
  const scan = scanPosterCached(img)
  const palette = opts?.accent ? [opts.accent] : scan.palette
  return themeFromColors(palette, scan, opts?.mode === 'auto' || !opts ? undefined : opts.mode)
}

// 同一张封面的扫描结果很贵（原尺寸取像素），按图缓存；调参改色不改像素，重算 theme 不重扫。
const SCAN_CACHE = new WeakMap()
function scanPosterCached(img) {
  if (!img) return { palette: [...FALLBACK_PALETTE], darkFrac: 0, medianV: 1 }
  const hit = SCAN_CACHE.get(img)
  if (hit) return hit
  const scan = scanPosterImage(img)
  SCAN_CACHE.set(img, scan)
  return scan
}

// 同一张封面在预览里要画两遍（探针量身高 + 正文开画），主题只算一次（无 opts 路径）。
const THEME_CACHE = new WeakMap()

export function posterThemeOf(img, opts) {
  if (opts) return extractPosterTheme(img, opts) // 调参台：拨一下重算一次，不进缓存
  if (!img) return extractPosterTheme(null)
  const hit = THEME_CACHE.get(img)
  if (hit) return hit
  const t = extractPosterTheme(img)
  THEME_CACHE.set(img, t)
  return t
}

/* ── 画图小件 ───────────────────────────────────────────────────────────── */

const CAN_FILTER = (() => {
  try {
    const c = document.createElement('canvas')
    const g = c.getContext('2d')
    return typeof g.filter === 'string'
  } catch {
    return false
  }
})()

/** 圆角矩形路径。r 给数字（四角同值）或 [左上,右上,右下,左下]。 */
function rr(g, x, y, w, h, r) {
  const [tl, tr, br, bl] = Array.isArray(r)
    ? r.map((v) => Math.max(0, v))
    : [r, r, r, r].map((v) => Math.max(0, v))
  const cap = Math.min(Math.abs(w) / 2, Math.abs(h) / 2)
  const k = (v) => Math.min(v, cap)
  g.beginPath()
  g.moveTo(x + k(tl), y)
  g.lineTo(x + w - k(tr), y)
  if (tr) g.arcTo(x + w, y, x + w, y + k(tr), k(tr))
  g.lineTo(x + w, y + h - k(br))
  if (br) g.arcTo(x + w, y + h, x + w - k(br), y + h, k(br))
  g.lineTo(x + k(bl), y + h)
  if (bl) g.arcTo(x, y + h, x, y + h - k(bl), k(bl))
  g.lineTo(x, y + k(tl))
  if (tl) g.arcTo(x, y, x + k(tl), y, k(tl))
  g.closePath()
}

/**
 * 铺一层层落影。canvas 的 shadow* 一次只能挂一层，同一形状描三遍又会把半透明
 * 底色叠深（cardColor 本来 .65，三遍就糊成不白了），所以这里改用 filter 直接
 * 画「影子的形状」：每档单独 blur + translate + 上色，形状本体最后只填一次。
 * 没有 filter 的浏览器（老 Safari）退成一档 ctx.shadow，聊胜于无。
 */
function shadows(g, list, pathFn) {
  if (!CAN_FILTER || !list?.length) return
  for (const s of list) {
    g.save()
    try {
      g.filter = `blur(${(s.blur / 2).toFixed(2)}px)`
    } catch {
      /* 认不出 filter 串就当没 blur */
    }
    g.translate(s.x, s.y)
    g.fillStyle = s.color
    pathFn()
    g.fill()
    g.restore()
  }
}

/** 带影的形状：先落影，再由 fillFn 画本体（本体只画一次，半透明色不会叠深）。
 * 影要先用 evenodd 把形状自己的足迹挖掉再铺：否则卡底那层黑影会从半透卡面
 * （.65 白 / DARK .3 深）透上来，白卡洗成脏灰卡 —— canvas 形状影的通病，补刀。 */
function elevated(g, list, pathFn, fillFn) {
  if (CAN_FILTER) {
    g.save()
    pathFn() // rr 自带 beginPath，先开形状；大矩形用 rect 追加子路径（不清路径），evenodd 才圈得出「环带」
    g.rect(-80, -80, POSTER_WIDTH + 160, 120000)
    g.clip('evenodd')
    shadows(g, list, pathFn)
    g.restore()
    fillFn()
    return
  }
  const s = list?.[0]
  if (!s) {
    fillFn()
    return
  }
  g.save()
  g.shadowOffsetX = s.x
  g.shadowOffsetY = s.y
  g.shadowBlur = s.blur
  g.shadowColor = s.color
  fillFn()
  g.restore()
}

/** aspect-fill 裁切（与 <img object-fit:cover> 同义）。调用方负责已经 clip 好。 */
function fillImage(g, img, x, y, w, h) {
  if (!img || !(img.width > 0) || !(img.height > 0)) return false
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
  return true
}

/** 一行文字：影子（filter 版，可多档）→ 描边 → 填色。假粗靠 strokeText，不写 bold。
    maxW 给了就在本行字号下量宽裁字（裁字必须已经摆好 font，不然量的是别人的字宽）。 */
function paintText(g, raw, x, y, o) {
  g.save()
  g.font = o.font
  g.textAlign = o.align || 'left'
  g.textBaseline = 'middle'
  const text = o.maxW ? fitText(g, raw, o.maxW) : String(raw ?? '')
  const stroke = (color, width) => {
    if (!(width > 0) || !color) return
    g.lineWidth = width
    g.lineJoin = 'round'
    g.strokeStyle = color
    g.strokeText(text, x, y)
  }
  if (CAN_FILTER) {
    for (const s of o.shadows || []) {
      g.save()
      try {
        g.filter = `blur(${(s.blur / 2).toFixed(2)}px)`
      } catch {
        /* 同上 */
      }
      g.translate(s.x, s.y)
      stroke(s.color, o.strokeWidth)
      g.fillStyle = s.color
      g.fillText(text, x, y)
      g.restore()
    }
  } else if (o.shadows?.length) {
    const s = o.shadows[0]
    g.shadowOffsetX = s.x
    g.shadowOffsetY = s.y
    g.shadowBlur = s.blur
    g.shadowColor = s.color
  }
  stroke(o.stroke, o.strokeWidth)
  g.fillStyle = o.fill
  g.fillText(text, x, y)
  g.restore()
}

/** 折行 + 末行省略号。\n 算硬换行，段内再按宽度折；超过 maxLines 的行整段砍掉，末行补 …。 */
function wrapLines(g, text, maxW, maxLines, font) {
  const paras = String(text ?? '')
    .split(/\r?\n+/)
    .map((p) => p.replace(/\s+/g, ' ').trim())
    .filter(Boolean)
  if (!paras.length || !(maxLines > 0)) return []
  g.save()
  if (font) g.font = font
  const lines = []
  let overflow = false
  for (let p = 0; p < paras.length; p += 1) {
    if (lines.length >= maxLines) {
      overflow = true
      break
    }
    const chars = Array.from(paras[p])
    let buf = ''
    let i = 0
    while (i < chars.length && lines.length < maxLines) {
      const next = buf + chars[i]
      if (buf && g.measureText(next).width > maxW) {
        lines.push(buf)
        buf = ''
      } else {
        buf = next
        i += 1
      }
    }
    if (buf && lines.length < maxLines) {
      lines.push(buf)
      buf = ''
      i = chars.length
    }
    // 这段还有字没吃完（被行数上限砍在半路）才算截断；后面整段丢不丢由循环头的行满判定管
    if (i < chars.length || buf) {
      overflow = true
      break
    }
  }
  if (lines.length) {
    const last = lines[lines.length - 1]
    lines[lines.length - 1] = overflow ? fitText(g, `${last}…`, maxW) : fitText(g, last, maxW)
  }
  g.restore()
  return lines.filter(Boolean)
}

/* ── 图源闸门 ───────────────────────────────────────────────────────────── */

/**
 * 只放行同源图：/media、/api、/avatars 开头（站内馆藏）或 data: 内联。
 * 头像馆藏就在 /avatars/<hash>.jpg（video_meta.up_face 存的这条），当年漏了它真头像被当热链拦下，v5 补上。
 * B 站图床是热链，画进 canvas 就是脏像素，toDataURL 当场炸（同 weeklyPoster 的 posterCoverSrc 口径）。
 */
export function sameOriginSrc(url) {
  if (!url) return ''
  const s = String(url).trim()
  if (s.startsWith('data:')) return s
  if (s.startsWith('/media') || s.startsWith('/api') || s.startsWith('/avatars')) return s
  return ''
}

function loadImage(src) {
  if (!src) return Promise.resolve(null)
  return new Promise((resolve) => {
    const img = new Image()
    img.crossOrigin = 'anonymous'
    img.onload = () => resolve(img)
    img.onerror = () => resolve(null)
    img.src = src
  })
}

/** 拉海报要用的两张图（封面 + UP 头）。热链与缺图一律不拉，直接 null 交给兜底。 */
export function loadPosterAssets(model) {
  const cover = loadImage(sameOriginSrc(model?.coverUrl))
  const face = loadImage(sameOriginSrc(model?.upFace))
  return Promise.all([cover, face]).then(([c, f]) => ({ cover: c, face: f }))
}

/**
 * 时长归一（照后端 DurationText 的脾气）：库里存着「4m3s」「03:09」「243」三种方言，
 * 上桌统一收成 B 站同款 —— 一小时内 m:ss，一小时以上 h:mm:ss。认不出的原文照放。
 */
export function normalizeDurationText(raw) {
  const s = String(raw ?? '').trim()
  if (!s) return ''
  const hms = /^(?:(\d+)h)?(?:(\d+)m)?(?:(\d+)s)?$/.exec(s)
  let h = 0
  let m = 0
  let sec = 0
  if (hms && (hms[1] || hms[2] || hms[3])) {
    h = Number(hms[1] || 0)
    m = Number(hms[2] || 0)
    sec = Number(hms[3] || 0)
  } else if (/^\d+$/.test(s)) {
    sec = Number(s)
  } else if (/^\d{1,2}(:\d{1,2}){1,2}$/.test(s)) {
    const parts = s.split(':').map(Number)
    if (parts.length === 2) {
      m = parts[0]
      sec = parts[1]
    } else {
      h = parts[0]
      m = parts[1]
      sec = parts[2]
    }
  } else {
    return s
  }
  const total = h * 3600 + m * 60 + sec
  if (!Number.isFinite(total) || total <= 0) return ''
  const hh = Math.floor(total / 3600)
  const mm = Math.floor((total % 3600) / 60)
  const ss = total % 60
  const pad = (n) => String(n).padStart(2, '0')
  return hh > 0 ? `${hh}:${pad(mm)}:${pad(ss)}` : `${mm}:${pad(ss)}`
}

/* ── 正片：量版式 + 开画 ────────────────────────────────────────────────── */

function infoLine(model, show) {
  const parts = []
  const view = String(model?.viewCountText ?? '').trim()
  if (view && (!show || show.views)) parts.push(`${view}播放`)
  const dm = model?.danmakuCount
  if (dm != null && String(dm).trim() !== '' && (!show || show.danmaku)) parts.push(`${dm}弹幕`)
  const like = model?.statLike
  if (like != null && String(like).trim() !== '' && (!show || show.likes)) parts.push(`${like}点赞`)
  return parts.join(' / ')
}

/**
 * 量出这张海报的身高（标题/简介是折行数决定的，必须拿真字体量）。
 * 与 paintPoster 共用同一份布局函数，量出来的高度与画出来的逐像素对齐。
 */
function layout(g, model, opts) {
  // 调参台覆写：留空才回原文；开关关掉的那行连高度一起退场
  const title = (opts?.title || String(model?.title ?? '')).trim()
  const desc = (opts?.desc || String(model?.description ?? '')).trim()
  const show = opts?.show
  const duration = show && !show.duration ? '' : normalizeDurationText(model?.durationText)
  const info = infoLine(model, show)

  // v8 拆平（站长 11-04 定）：只剩一张卡。封面贴满卡顶（通宽 CARD_W），
  // 文字绝对位置不变：原先是「内卡边+margin」，现在「卡边+CONTENT_PAD+margin」同一根线。
  const coverX = PAD
  const coverW = CARD_W
  const coverH = coverW / COVER_RATIO
  const textW = CARD_W - (CONTENT_PAD + TITLE.marginX) * 2

  const titleFont = `${TITLE.size}px ${FONT_FAMILY}`
  const descFont = `${DESC.size}px ${FONT_FAMILY}`
  const titleLines = title && (!show || show.title) ? wrapLines(g, title, textW, TITLE.maxLines, titleFont) : []
  const descLines = desc && (!show || show.desc) ? wrapLines(g, desc, textW, opts?.descMaxLines ?? DESC.maxLines, descFont) : []

  const cardTop = PAD + HEADER_H + CONTENT_GAP
  const textBlockH = (titleLines.length ? TITLE.marginTop + titleLines.length * TITLE.lineHeight + TITLE.marginBottom : 0)
    + (descLines.length ? descLines.length * DESC.lineHeight + DESC.marginBottom : 0)
  const contentH = coverH + textBlockH + CONTENT_PAD
  const signH = opts?.sign ? SIGN.marginTop + SIGN.lineHeight : 0
  const totalH = PAD + HEADER_H + CONTENT_GAP + contentH + signH + PAD

  return {
    model, totalH,
    header: { x: PAD, y: PAD, w: CARD_W, h: HEADER_H },
    content: { x: PAD, y: cardTop, w: CARD_W, h: contentH },
    cover: { x: coverX, y: cardTop, w: coverW, h: coverH },
    title, desc, titleLines, descLines, titleFont, descFont, textW,
    duration, info,
    sign: opts?.sign || '',
  }
}

/** 头卡那五层垫底（base 横渐变 / 虚焦封面 / 同图清晰一层 / 色相 tint / 白洗）。 */
function paintHeaderBackdrop(g, L, T, cover) {
  const { x, y, w, h } = L.header
  g.save()
  rr(g, x, y, w, h, CARD_R)
  g.clip()

  const base = g.createLinearGradient(x, y, x + w, y)
  const bg = T.backgroundColors
  bg.forEach((c, i) => base.addColorStop(bg.length === 1 ? 0 : i / (bg.length - 1), rgba(c)))
  g.fillStyle = base
  g.fillRect(x, y, w, h)

  if (cover) {
    // 虚焦层：blur(3.2px) + alpha .40。老 Safari 没有 ctx.filter，跳过模糊只画 .40 那层。
    g.save()
    g.globalAlpha = 0.4
    if (CAN_FILTER) {
      try {
        g.filter = 'blur(3.2px)'
      } catch {
        g.filter = 'none'
      }
    }
    // 影子/模糊会从边上啃进来一圈，往外多铺 8dp 让裁切线以内是满的
    fillImage(g, cover, x - 8, y - 8, w + 16, h + 16)
    g.restore()

    g.save()
    g.globalAlpha = 0.08
    fillImage(g, cover, x, y, w, h)
    g.restore()
  }

  const tint = g.createLinearGradient(x, y, x + w, y)
  tint.addColorStop(0, rgba(T.primary, 0.09))
  tint.addColorStop(0.5, rgba(T.readableAccent, 0.07))
  tint.addColorStop(1, rgba(T.primary, 0.07))
  g.fillStyle = tint
  g.fillRect(x, y, w, h)

  g.fillStyle = 'rgba(255,255,255,0.22)'
  g.fillRect(x, y, w, h)
  g.restore()
}

function paintAvatar(g, L, T, face, upName) {
  const cx = L.header.x + AV.left + AV.size / 2
  const cy = L.header.y + AV.top + AV.size / 2
  const r = AV.size / 2
  const path = () => {
    g.beginPath()
    // 白描边 4：图本体保持 112 直径，白盘往外包 4dp（影也照这颗大圆落）
    g.arc(cx, cy, r + AV.ring, 0, Math.PI * 2)
    g.closePath()
  }
  elevated(g, AV_SHADOW, path, () => {
    path()
    g.fillStyle = '#ffffff'
    g.fill()
  })
  g.save()
  g.beginPath()
  g.arc(cx, cy, r, 0, Math.PI * 2)
  g.closePath()
  g.clip()
  if (!fillImage(g, face, cx - r, cy - r, AV.size, AV.size)) {
    // 热链/缺图：深底圆 + UP 名首字（脏像素防线，同 weeklyPoster 的 posterCoverSrc 口径）
    const deep = g.createLinearGradient(cx - r, cy - r, cx + r, cy + r)
    deep.addColorStop(0, rgba(T.readableAccent, 1))
    deep.addColorStop(1, rgba([18, 22, 30], 1))
    g.fillStyle = deep
    g.fillRect(cx - r, cy - r, AV.size, AV.size)
    const ch = String(upName ?? '').trim().charAt(0) || '站'
    g.font = `500 ${Math.round(AV.size * 0.46)}px ${FONT_FAMILY}`
    g.textAlign = 'center'
    g.textBaseline = 'middle'
    g.fillStyle = 'rgba(255,255,255,0.92)'
    g.fillText(ch, cx, cy + 2)
  }
  g.restore()
}

function paintQr(g, L, T, qrCanvas) {
  const { x: cx, y: cy, w: cw, h: chh } = L.header
  const barW = QR.size + QR.inset + QR.bleedLeft
  const barX = cx + cw - barW
  const barPath = () => rr(g, barX, cy, barW, chh, [0, CARD_R, CARD_R, 0])
  barPath()
  const grad = g.createLinearGradient(barX, cy, barX + barW, cy)
  grad.addColorStop(0, rgba(blendWhite(T.primary, 0.08), 0))
  grad.addColorStop(0.55, rgba(blendWhite(T.primary, 0.12), 0.55))
  grad.addColorStop(1, rgba(blendWhite(T.primary, 0.24), 1))
  g.fillStyle = grad
  g.fill()
  // 站长定稿：中心不放任何 logo
  if (qrCanvas && qrCanvas.width > 0) {
    g.save()
    g.imageSmoothingEnabled = true
    g.drawImage(qrCanvas, cx + cw - QR.inset - QR.size, cy + (chh - QR.size) / 2, QR.size, QR.size)
    g.restore()
  }
}

function paintBadge(g, cover, T, label) {
  const font = `${BADGE.fontSize}px ${FONT_FAMILY}`
  g.save()
  g.font = font
  const text = fitText(g, label, BADGE.maxTextW)
  const tw = g.measureText(text).width
  const w = tw + BADGE.paddingX * 2
  const x = cover.x + cover.w - w
  const y = cover.y
  g.save()
  rr(g, cover.x, cover.y, cover.w, cover.h, [COVER_R, COVER_R, 0, 0])
  g.clip()
  // 贴纸式角标贴死封面角：右上圆角与卡/封面顶弧同值同心不露缝；左下自带小圆角
  g.fillStyle = rgba(T.primary)
  rr(g, x, y, w, BADGE.lineHeight, [0, COVER_R, 0, BADGE.cornerR])
  g.fill()
  // 只描左、下两条亮边（上、右藏进弧线与封面边缘，整圈描会出毛边）
  g.lineWidth = 2
  g.strokeStyle = rgba([255, 255, 255], 0.62)
  g.lineJoin = 'round'
  g.beginPath()
  g.moveTo(x, y)
  g.lineTo(x, y + BADGE.lineHeight - BADGE.cornerR)
  g.arcTo(x, y + BADGE.lineHeight, x + BADGE.cornerR, y + BADGE.lineHeight, BADGE.cornerR)
  g.lineTo(x + w, y + BADGE.lineHeight)
  g.stroke()
  g.restore()
  const onWhite = T.onPrimary
  g.font = font
  g.textAlign = 'center'
  g.textBaseline = 'middle'
  g.fillStyle = onWhite
  g.fillText(text, x + w / 2, y + BADGE.lineHeight / 2)
  g.restore()
}

function paintCoverMaskRow(g, cover, T, L) {
  const x = cover.x
  const w = cover.w
  const y = cover.y + cover.h - MASK_H
  // 遮罩不写死：照 MediaCardColors 随模式变，浓度在原件基础上再收一档（站长 11-05 追柔）
  const overlayAlpha = T.mode === 'DARK' ? 0.52 : 0.42
  // 时长片要半透不实心（站长 11-05 点名）：压到遮罩的七折以下，白字靠底下遮罩托住仍可读
  const pillAlpha = Math.round(overlayAlpha * 0.72 * 100) / 100
  g.save()
  // 封面四角圆 10：遮罩贴在封面底，底两角随封面圆弧收边（原件 Row radius [0,0,10,10] 的clip等价）
  rr(g, cover.x, cover.y, cover.w, cover.h, COVER_R)
  g.clip()
  // 渐变收柔（站长 11-05 四轮打磨）：余弦缓降曲线（cos²）采样——处处圆滑无陡段，
  // 底缘浓度即顶，往上每寸都在褪，顶缘归零无缝
  const a = (t) => Math.round(overlayAlpha * Math.cos((Math.PI / 2) * t) ** 2 * 100) / 100
  const grad = g.createLinearGradient(0, y + MASK_H, 0, y)
  grad.addColorStop(0, `rgba(0,0,0,${overlayAlpha})`)
  grad.addColorStop(0.22, `rgba(0,0,0,${a(0.22)})`)
  grad.addColorStop(0.45, `rgba(0,0,0,${a(0.45)})`)
  grad.addColorStop(0.65, `rgba(0,0,0,${a(0.65)})`)
  grad.addColorStop(0.85, `rgba(0,0,0,${a(0.85)})`)
  grad.addColorStop(1, 'rgba(0,0,0,0)')
  g.fillStyle = grad
  g.fillRect(x, y, w, MASK_H)

  let cursor = x + PILL.marginLeft
  const centerY = y + MASK_H - PILL.bottom - PILL.lineHeight / 2
  if (L.duration) {
    g.font = `${PILL.fontSize}px ${FONT_FAMILY}`
    const tw = g.measureText(L.duration).width
    const pw = tw + PILL.paddingX * 2
    g.fillStyle = `rgba(0,0,0,${pillAlpha})`
    rr(g, cursor, centerY - PILL.lineHeight / 2, pw, PILL.lineHeight, PILL.radius)
    g.fill()
    g.font = `${PILL.fontSize}px ${FONT_FAMILY}`
    g.fillStyle = '#ffffff'
    g.textAlign = 'left'
    g.textBaseline = 'middle'
    g.fillText(L.duration, cursor + PILL.paddingX, centerY)
    cursor += pw + INFO.marginLeft
  }
  if (L.info) {
    g.font = `${INFO.fontSize}px ${FONT_FAMILY}`
    g.fillStyle = '#ffffff'
    g.textAlign = 'left'
    g.textBaseline = 'middle'
    const text = fitText(g, L.info, INFO.maxWidth)
    // 与时长片同一根中线：两个片儿并排坐，字底不齐就显脏
    g.fillText(text, cursor, centerY)
  }
  g.restore()
}

/**
 * 把整张海报画到给定 2d 上下文（坐标系是 1000dp 基准，倍率由调用方 setTransform）。
 * assets = { cover, face, qr }，任一缺失都走兜底，不空手报错。
 * 返回这张海报的总高（dp），调用方据此决定画布高度。
 */
export function paintPoster(g, model, assets, theme, optsRaw) {
  const o = normalizeOpts(optsRaw) // null 进 null 出；已补完的直接放行
  const T = theme || posterThemeOf(assets?.cover ?? null, o)
  const L = layout(g, model, o)
  const cover = assets?.cover ?? null
  const face = assets?.face ?? null
  const qrCanvas = assets?.qr ?? null
  const qrOn = !o || o.show.qr

  // 整底：左上→右下三色渐变
  const bg = g.createLinearGradient(0, 0, POSTER_WIDTH, L.totalH)
  T.backgroundColors.forEach((c, i) => {
    const n = T.backgroundColors.length
    bg.addColorStop(n === 1 ? 0 : i / (n - 1), rgba(c))
  })
  g.fillStyle = bg
  g.fillRect(0, 0, POSTER_WIDTH, L.totalH)

  // ── 头卡（无边框，只有三层 E3 影）──
  const hp = () => rr(g, L.header.x, L.header.y, L.header.w, L.header.h, CARD_R)
  elevated(g, E3, hp, () => {
    hp()
    g.fillStyle = '#ffffff'
    g.fill()
  })
  paintHeaderBackdrop(g, L, T, cover)
  paintAvatar(g, L, T, face, model?.upName)

  const nameX = L.header.x + AV.left + AV.size + AV.textSpacing + NAME.left
  const timeX = L.header.x + AV.left + AV.size + AV.textSpacing
  const textRight = L.header.x + L.header.w - TEXT_RIGHT - (qrOn ? (QR.size + QR.inset + QR.bleedLeft) : 0)
  const nameMaxW = textRight - nameX
  const timeMaxW = textRight - timeX

  const name = String(model?.upName ?? '').trim() || '未知 UP'
  paintText(g, name, nameX, L.header.y + NAME.top + NAME.lineHeight / 2, {
    maxW: nameMaxW,
    font: `${NAME.size}px ${FONT_FAMILY}`,
    fill: 'rgba(255,255,255,0.9)',
    stroke: rgba(T.primary),
    strokeWidth: 8,
    shadows: [
      { x: 2, y: 3, blur: 3, color: 'rgba(0,0,0,0.3)' },
      { x: 0, y: 0, blur: 8, color: rgba(T.primary, 0.8) },
    ],
  })
  paintText(g, stampText(), timeX, L.header.y + NAME.top + NAME.lineHeight + TIME.topSpacing + TIME.lineHeight / 2, {
    maxW: timeMaxW,
    font: `${TIME.size}px ${FONT_FAMILY}`,
    fill: 'rgba(255,255,255,0.8)',
    stroke: rgba(T.primary, 0.6),
    strokeWidth: 5,
    shadows: [{ x: 2, y: 2, blur: 2, color: 'rgba(0,0,0,0.2)' }],
  })

  if (qrOn) paintQr(g, L, T, qrCanvas)

  // ── 一张卡（v8 拆平：没有内套媒体卡）──顶角用 COVER_R，与封面/角标的顶弧同心；
  // 底角留 CARD_R。封面通宽贴卡顶，靠卡的 clip 切顶弧，不再单独落影描边。
  const cp = () => rr(g, L.content.x, L.content.y, L.content.w, L.content.h, [COVER_R, COVER_R, CARD_R, CARD_R])
  elevated(g, E3, cp, () => {
    cp()
    g.fillStyle = T.cardColor
    g.fill()
  })

  // 封面（通宽贴卡顶）：原件 Media.kt 里封面本就是颗四角 COVER_R 圆、3dp 白边、E2 落影的子卡
  // （border(3,10,coverBorderColor).shadows(ELEVATION_2)）；v8 拆的是套娃灰底，这三件不该拆。
  // 热链/缺图 → 灰渐变 +「封面」占位字。
  const cv = L.cover
  const cvp = () => rr(g, cv.x, cv.y, cv.w, cv.h, COVER_R)
  g.save()
  cp() // 影只落在卡面上（卡顶之外不给漏）
  g.clip()
  elevated(g, E2, cvp, () => {
    g.save()
    cvp()
    g.clip()
    if (!fillImage(g, cover, cv.x, cv.y, cv.w, cv.h)) {
      const ph = g.createLinearGradient(cv.x, cv.y, cv.x + cv.w, cv.y + cv.h)
      ph.addColorStop(0, '#d9dde3')
      ph.addColorStop(1, '#b7bdc7')
      g.fillStyle = ph
      g.fillRect(cv.x, cv.y, cv.w, cv.h)
      g.font = `500 34px ${FONT_FAMILY}`
      g.textAlign = 'center'
      g.textBaseline = 'middle'
      g.fillStyle = 'rgba(0,0,0,0.32)'
      g.fillText('封面', cv.x + cv.w / 2, cv.y + cv.h / 2)
    }
    g.restore()
  })
  g.restore()

  // 卡描边最后补画：压在封面之上，一圈亮边完整不断弧
  g.save()
  g.lineWidth = CARD_BORDER
  g.strokeStyle = T.borderColor
  cp()
  g.stroke()
  g.restore()

  if (!o || o.show.badge) paintBadge(g, cv, T, o?.badgeText || '视频')
  if (L.duration || L.info) paintCoverMaskRow(g, cv, T, L)

  // 封面白边最后补画：压在黑幕之上，底两角的白弧不被遮罩咬掉（原件红框里白边完整绕四圈）
  g.save()
  g.lineWidth = CARD_BORDER
  g.strokeStyle = T.borderColor
  cvp()
  g.stroke()
  g.restore()

  // 落款（调参台）：卡外地带右对齐一行小字，不开则 layout 已把这块高度归零
  if (L.sign) {
    g.save()
    g.font = `500 ${SIGN.size}px ${FONT_FAMILY}`
    g.textAlign = 'right'
    g.textBaseline = 'middle'
    g.fillStyle = T.secondaryTextColor
    g.fillText(L.sign, L.content.x + L.content.w - CONTENT_PAD, L.content.y + L.content.h + SIGN.marginTop + SIGN.lineHeight / 2)
    g.restore()
  }

  // 标题（假粗 = 同色 strokeText 0.7 先铺再 fillText）与简介
  let ty = cv.y + cv.h + TITLE.marginTop
  g.save()
  g.textAlign = 'left'
  g.textBaseline = 'middle'
  g.font = L.titleFont
  g.lineWidth = 0.7
  g.lineJoin = 'round'
  g.strokeStyle = T.textColor
  g.fillStyle = T.textColor
  for (const line of L.titleLines) {
    const tx = L.content.x + CONTENT_PAD + TITLE.marginX
    g.strokeText(line, tx, ty + TITLE.lineHeight / 2)
    g.fillText(line, tx, ty + TITLE.lineHeight / 2)
    ty += TITLE.lineHeight
  }
  ty += L.titleLines.length ? TITLE.marginBottom : 0
  g.font = L.descFont
  g.fillStyle = T.secondaryTextColor
  for (const line of L.descLines) {
    g.fillText(line, L.content.x + CONTENT_PAD + DESC.marginX, ty + DESC.lineHeight / 2)
    ty += DESC.lineHeight
  }
  g.restore()

  return L.totalH
}

function stampText() {
  const d = new Date()
  const pad = (n) => String(n).padStart(2, '0')
  return `${d.getFullYear()}年${pad(d.getMonth() + 1)}月${pad(d.getDate())}日 ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`
}

/**
 * 海报字体（HarmonyOS Sans SC Medium，4MB 全量 CJK+假名子集）。
 * 许可如实写：华为《HarmonyOS Sans 字体软件许可协议》（免费商用，禁改本体、禁单独转售），
 * 不是 Apache-2.0——那个错标是从上游 dynamic-bot 的注释里拄来的，详见 THIRD-PARTY-NOTICES.md 的「字体」节。
 * 只走 import.meta.url 让 vite 打指纹，不手写 /src/ 路径；它只被 renderVideoPoster 碰，
 * 而 renderVideoPoster 只在海报弹窗里跑 —— 首屏关键路径上没这个东西（4MB 进首屏就是事故）。
 * 模块级单例：预览重画几遍、导出几回都只下载一次。
 */
const POSTER_FONT_FAMILY = 'HarmonyOS Sans SC'

const loadPosterFont = (() => {
  let p
  return () => (p ??= (async () => {
    try {
      const url = new URL('../assets/fonts/HarmonyOS_SansSC_Medium.woff2', import.meta.url).href
      const ff = new FontFace(POSTER_FONT_FAMILY, `url(${url}) format('woff2')`, { weight: '500', style: 'normal' })
      await ff.load()
      document.fonts.add(ff)
      // add 完不等于度量就位：再按海报真使到的几档字号各兜一次（全部失败也不算错，降级链照画）
      await Promise.all(
        [NAME.size, TIME.size, BADGE.fontSize, INFO.fontSize, PILL.fontSize, TITLE.size, DESC.size]
          .map((s) => document.fonts.load(`500 ${s}px "${POSTER_FONT_FAMILY}"`)),
      )
      return true
    } catch {
      return false // 网断 / 不认 FontFace / 文件缺失：FONT_FAMILY 降级链接手，不许拦着出图
    }
  })())
})()

/** 海报字体能不能拿到（true=已入 document.fonts；false=走降级链）。永不拒，调用方不用包 try。 */
export function ensurePosterFont() {
  return loadPosterFont()
}

/**
 * 一把梭：按倍率把海报画到 canvas 上（预览与导出共用这支笔，所见即所得）。
 * 开画前先 await 字体（单例，只下一回），探針量身高也在字体就位后跑 —— 海报身高由
 * 标题/简介的折行数定，拿错字度量出来的身高与真图对不上。字体拿不到就照降级链画。
 */
export async function renderVideoPoster(canvas, model, assets, scale = 1, opts) {
  await loadPosterFont()
  const o = normalizeOpts(opts)
  const theme = posterThemeOf(assets?.cover ?? null, o)
  const probe = document.createElement('canvas').getContext('2d')
  const h = paintPoster(probe, model, assets, theme, o)
  canvas.width = Math.round(POSTER_WIDTH * scale)
  canvas.height = Math.max(1, Math.round(h * scale))
  const g = canvas.getContext('2d')
  g.setTransform(scale, 0, 0, scale, 0, 0)
  paintPoster(g, model, assets, theme, o)
  return h
}
