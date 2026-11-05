/* ─────────────────────────────────────────────────────────────────────────
   小破分站 · 周报海报「编辑部」皮肤（11-03 从 weeklyPoster.js 整段平移，画法一字未改）
   ---------------------------------------------------------------------------
   字体、量宽裁字、千分位、封面裁切住在 weeklyPosterKit.js；本文件只管这套版面。
   两套皮肤互不 import，只各自 import kit——一张塌了不牵连另一张。

   设计底子（这一版推翻了大字报那套粗野主义）：
   · 一张海报只用一种强调色，其余全是中性灰阶；克制换来的就是高级感。
   · 网格对齐、发丝线、统一 14px 圆角，版面自己站得住，不靠歪斜和阴影撑场面。
   · 封面是主角：顶上做拼贴横幅，下面配一行行的标题，图片负责勾人，文字负责交代。
   · 预览与下载跑同一段代码（同一个 paintEditorial），所见即所得。

   画布宽度固定 750，高度随内容长；导出时整体乘 2 倍分辨率。
   ───────────────────────────────────────────────────────────────────────── */

import { FF, FFM, POSTER_ACCENTS, roundRect, fitText, kicker, fmtNum, drawCover, dither } from './weeklyPosterKit.js'

const PAD = 44 // 页面四边留白
const HERO_BLUR = 4 // 头图那层虚焦的轻重（CSS px，走全分辨率高斯）。填 0 就是完全清楚
const HERO_FADE = 170 // 头图下缘渐隐的那一段高度

/* ── 共用画法住在 kit，本文件只剩版面 ─────────────────────────────────────────────────────────── */

/* ── 配色 ─────────────────────────────────────────────────────────────── */

/**
 * 按参数解出一整套版面色。
 * 每档主题都自带 ramp（热力图的五级色阶）——从「没有」到「最热」是一条线，不是一堆随机色。
 */
export function editorialTheme(opts) {
  const acc = POSTER_ACCENTS[opts?.accent] ?? POSTER_ACCENTS.pink
  const accent = acc.base

  if (opts?.theme === 'paper') {
    return {
      id: 'paper',
      bg0: '#fcfcfb',
      bg1: '#eff1f4',
      glow: 'rgba(0,0,0,.05)',
      vignette: 'rgba(16,19,25,.05)',
      panel: 'rgba(255,255,255,.92)',
      panelEdge: 'rgba(16,19,25,.07)',
      hair: 'rgba(16,19,25,.10)',
      main: '#0f1219',
      soft: '#4d5563',
      faint: '#8b93a1',
      accent,
      accentSoft: `${accent}22`,
      onAccent: '#ffffff',
      ramp: ['rgba(16,19,25,.07)', `${accent}3d`, `${accent}66`, `${accent}99`, accent],
      chartTrack: 'rgba(16,19,25,.07)',
      phA: '#e8eaee',
      phB: '#d8dce3',
      phFg: '#9aa2ae',
      // 头图的字色：浅纸就配深字，否则图溶成纸色之后白字会跟着化掉
      light: true,
      heroTitle: '#0f1219',
      heroSub: 'rgba(15,18,25,.68)',
      heroKicker: 'rgba(15,18,25,.74)',
      heroRing: 'rgba(15,18,25,.26)',
      heroChip: 'rgba(255,255,255,.62)',
      heroVeil: '',
    }
  }

  if (opts?.theme === 'brand') {
    // 粉版：整张浅粉底。这档不走反白——白字压在浅粉上读不出来，墨色走深桃黑
    return {
      id: 'brand',
      bg0: '#fdf1f5',
      bg1: '#f8dbe6',
      glow: 'rgba(251,114,153,.12)',
      vignette: 'rgba(176,64,101,.06)',
      panel: 'rgba(255,255,255,.72)',
      panelEdge: 'rgba(176,64,101,.13)',
      hair: 'rgba(176,64,101,.16)',
      main: '#43202f',
      soft: '#7b4a5a',
      faint: '#ac7d8c',
      accent,
      accentSoft: `${accent}1f`,
      onAccent: '#ffffff',
      ramp: ['rgba(176,64,101,.10)', `${accent}3d`, `${accent}66`, `${accent}99`, accent],
      chartTrack: 'rgba(176,64,101,.12)',
      phA: '#f6d3df',
      phB: '#efbfce',
      phFg: '#b0808f',
      light: true,
      heroTitle: '#43202f',
      heroSub: 'rgba(67,32,47,.7)',
      heroKicker: 'rgba(67,32,47,.76)',
      heroRing: 'rgba(67,32,47,.3)',
      heroChip: 'rgba(255,255,255,.62)',
      heroVeil: '',
    }
  }

  // 夜航（默认）：深蓝墨底压着一层极淡的强调色光晕
  return {
    id: 'ink',
    bg0: '#080b11',
    bg1: '#151a26',
    glow: acc.glow,
    vignette: 'rgba(0,0,0,.35)',
    panel: 'rgba(255,255,255,.045)',
    panelEdge: 'rgba(255,255,255,.09)',
    hair: 'rgba(255,255,255,.11)',
    main: '#f2f5fa',
    soft: '#a3adbd',
    faint: '#68738a',
    accent,
    accentSoft: 'rgba(255,255,255,.08)',
    onAccent: '#0b0e14',
    ramp: ['rgba(255,255,255,.07)', `${accent}33`, `${accent}5c`, `${accent}8a`, accent],
    chartTrack: 'rgba(255,255,255,.09)',
    phA: '#1c2230',
    phB: '#131822',
    phFg: '#68738a',
    light: false,
    heroTitle: '#ffffff',
    heroSub: 'rgba(255,255,255,.78)',
    heroKicker: 'rgba(255,255,255,.88)',
    heroRing: 'rgba(255,255,255,.45)',
    heroChip: 'rgba(255,255,255,.18)',
    heroVeil: 'rgba(0,0,0,.14)',
  }
}

/* ── 零件 ─────────────────────────────────────────────────────────────── */

/** 一块面板：淡底 + 一圈发丝边，圆角统一 14。 */
function panel(g, T, x, y, w, h, r = 14) {
  g.fillStyle = T.panel
  roundRect(g, x, y, w, h, r)
  g.fill()
  g.strokeStyle = T.panelEdge
  g.lineWidth = 1
  g.stroke()
}

/** 版块头：左边一道强调色短杠 + 中文题名，右边一串压扁的英文。 */
function sectionHead(g, T, x, y, cn, en, W) {
  g.fillStyle = T.accent
  roundRect(g, x, y - 13, 3, 15, 1.5)
  g.fill()
  g.fillStyle = T.main
  g.font = `700 18px ${FF}`
  g.fillText(cn, x + 13, y)
  if (en) kicker(g, en, W - PAD, y - 1, 9, T.faint, '3px', 'right')
}

/** 图上叠幕，保证压在上面的字读得出来：深纸压黑，浅纸洗白（幕跟着纸走）。 */
function scrim(g, T, x, y, w, h) {
  const grad = g.createLinearGradient(x, y, x, y + h)
  if (T.light) {
    grad.addColorStop(0, 'rgba(255,255,255,.30)')
    grad.addColorStop(0.42, 'rgba(255,255,255,.46)')
    grad.addColorStop(0.72, 'rgba(255,255,255,.68)')
    grad.addColorStop(1, 'rgba(255,255,255,.82)')
  } else {
    grad.addColorStop(0, 'rgba(0,0,0,.62)')
    grad.addColorStop(0.3, 'rgba(0,0,0,.26)')
    grad.addColorStop(0.55, 'rgba(0,0,0,.66)')
    grad.addColorStop(0.78, 'rgba(0,0,0,.88)')
    grad.addColorStop(1, 'rgba(0,0,0,.96)')
  }
  g.fillStyle = grad
  g.fillRect(x, y, w, h)
}

/** 圆角小徽标：名次、序号都用它。 */
function badge(g, x, y, size, text, bg, fg, radius = 6) {
  g.fillStyle = bg
  roundRect(g, x, y, size, size, radius)
  g.fill()
  g.fillStyle = fg
  g.font = `700 ${Math.round(size * 0.46)}px ${FFM}`
  g.textAlign = 'center'
  g.fillText(text, x + size / 2, y + size * 0.68)
  g.textAlign = 'left'
}

/* ── 各版块 ───────────────────────────────────────────────────────────── */

/**
 * 顶部横幅：只放本周最热那一张封面，铺满整块，标题压在图上。
 * 11-03 走了两轮：先删掉右侧叠着的二三号封面（三张挤一堆，标题没地方站）；
 * 再把糊法与过渡换掉——先缩到 0.2 倍再放大那套出来是马赛克糊不是虚焦糊，
 * 拿同色渐变去盖下缘又会在接缝处留一块方补丁和横纹。现在改成：离屏画好、
 * 全分辨率高斯、再用竖向 alpha 渐隐把下缘抹掉露出底下的纸底——接缝天生就没有。
 */
function heroBanner(g, M, T, W, opts, imgs) {
  // 400 → 452（站长 11-06 圈图：标题与署名压住封面人物）：长出来的 52px 全让给字，
  // 标题/副标语/署名整套下移压进下缘渐隐带，上半屏整块还给封面
  const H = 452
  const hot = opts.show.hot ? M.hottest ?? [] : []
  const cover = imgs.get(hot[0]?.bvid)

  // 头图先画在离屏上，好留一手：最后要拿 destination-in 把下缘抹薄
  const tf = typeof g.getTransform === 'function' ? g.getTransform() : { a: 1 }
  const dpr = Math.abs(tf.a) || 1
  const off = document.createElement('canvas')
  off.width = Math.max(1, Math.round(W * dpr))
  off.height = Math.max(1, Math.round(H * dpr))
  const o = off.getContext('2d')
  o.setTransform(dpr, 0, 0, dpr, 0, 0)

  // 底：主题渐变（没封面时它就是全部）
  const base = o.createLinearGradient(0, 0, W * 0.6, H)
  base.addColorStop(0, T.bg1)
  base.addColorStop(1, T.bg0)
  o.fillStyle = base
  o.fillRect(0, 0, W, H)

  if (cover) {
    // 糊：filter 走全分辨率高斯，不缩采样。四周多画一圈 blur 宽，免得边缘糊出透明缺口
    const b = Number.isFinite(opts.heroBlur) ? opts.heroBlur : HERO_BLUR
    o.save()
    if ('filter' in o && b > 0) o.filter = `blur(${b}px)`
    drawCover(o, cover, -b, -b, W + b * 2, H + b * 2, T, hot[0]?.bvid, {
      clip: (gg, x, y, w, h) => {
        gg.beginPath()
        gg.rect(x, y, w, h)
      },
    })
    o.restore()
    // 幕：深纸压黑、浅纸洗白，都是为了让压在图上的字读得出来
    scrim(o, T, 0, 0, W, H)
    if (T.heroVeil) {
      o.fillStyle = T.heroVeil
      o.fillRect(0, 0, W, H)
    }
  } else {
    const glow = o.createRadialGradient(W * 0.3, H * 0.2, 0, W * 0.3, H * 0.2, W * 0.8)
    glow.addColorStop(0, T.glow)
    glow.addColorStop(1, 'rgba(0,0,0,0)')
    o.fillStyle = glow
    o.fillRect(0, 0, W, H)
  }

  // 下缘渐隐：拿竖向 alpha 把离屏底下抹掉，露出 g 上早已画好的纸底。
  // 溶的是真纸底，不是拿一块同色去盖，所以既没有方补丁也没有那条硬边
  o.save()
  o.globalCompositeOperation = 'destination-in'
  const mask = o.createLinearGradient(0, 0, 0, H)
  mask.addColorStop(0, 'rgba(0,0,0,1)')
  mask.addColorStop(Math.max(0.05, (H - HERO_FADE) / H), 'rgba(0,0,0,1)')
  mask.addColorStop(1, 'rgba(0,0,0,0)')
  o.fillStyle = mask
  o.fillRect(0, 0, W, H)
  o.restore()

  // 贴进海报；顺手铺一层极淡噪点，把浅渐变容易起的横纹抖散
  g.save()
  g.beginPath()
  g.rect(0, 0, W, H)
  g.clip()
  g.drawImage(off, 0, 0, off.width, off.height, 0, 0, W, H)
  dither(g, 0, 0, W, H, T.light ? 0.055 : 0.04)
  g.restore()

  // 顶行：站点标识 + 周次
  g.fillStyle = T.accent
  g.beginPath()
  g.arc(PAD + 4, 40, 4, 0, Math.PI * 2)
  g.fill()
  kicker(g, 'LITTLE BROKEN STATION', PAD + 17, 43, 9, T.heroKicker, '3.4px')
  kicker(g, M.weekLabel, W - PAD, 43, 10, T.heroKicker, '2px', 'right')

  // 标题：沉到底部与右侧署名同高（站长 11-06 二轮圈图），左边一道强调色竖杠，跟下面各版块的小竖杠同一套语言
  const title = opts.title || '小破分站周报'
  g.fillStyle = T.accent
  roundRect(g, PAD, 378, 4, 44, 2)
  g.fill()
  g.fillStyle = T.heroTitle
  g.font = `700 54px ${FF}`
  g.fillText(fitText(g, title, W - PAD * 2 - 16), PAD + 16, 418)

  // 副标语：标题下沉后只能站到标题头上；留空就一行不留，不拿分站标语去填位（11-03 改）
  const sub = String(opts.subtitle ?? '').trim()
  if (sub) {
    g.fillStyle = T.heroSub
    g.font = `400 13px ${FF}`
    g.fillText(fitText(g, sub, W - PAD * 2 - 16), PAD + 16, 366)
  }

  // 署名：头像 + 名字，压在右下角与左下标题对角平衡（站长 11-06 圈图点单搬右）
  if (opts.showSign && M.signName) {
    const av = imgs.get('__avatar')
    const r = 21
    const cx = W - PAD - r
    const cy = 420 - r
    g.save()
    g.beginPath()
    g.arc(cx, cy, r, 0, Math.PI * 2)
    g.closePath()
    g.clip()
    if (av) {
      g.drawImage(av, cx - r, cy - r, r * 2, r * 2)
    } else {
      g.fillStyle = T.heroChip
      g.fillRect(cx - r, cy - r, r * 2, r * 2)
      g.fillStyle = T.heroTitle
      g.font = `600 15px ${FF}`
      g.textAlign = 'center'
      g.fillText(M.signName.slice(0, 1), cx, cy + 5)
      g.textAlign = 'left'
    }
    g.restore()
    g.strokeStyle = T.heroRing
    g.lineWidth = 1.2
    g.beginPath()
    g.arc(cx, cy, r, 0, Math.PI * 2)
    g.stroke()
    kicker(g, 'THIS WEEK BY', cx - r - 14, cy + 1, 8, T.heroSub, '2.4px', 'right')
    g.fillStyle = T.heroTitle
    g.font = `500 13px ${FF}`
    g.textAlign = 'right'
    g.fillText(fitText(g, M.signName, 160), cx - r - 14, cy + 19)
    g.textAlign = 'left'
  }

  return H
}

/** 大数 + 引语 + 四格数据条：整张海报的数据锚点。 */
function heroNumbers(g, M, T, W, y) {
  const cw = W - PAD * 2

  kicker(g, 'PLAY COUNT THIS WEEK', PAD, y + 16, 9, T.faint, '4px')
  g.fillStyle = T.accent
  g.beginPath()
  g.arc(PAD - 12, y + 12, 3, 0, Math.PI * 2)
  g.fill()

  const plays = fmtNum(M.plays)
  g.fillStyle = T.main
  g.font = `900 92px ${FFM}`
  const numW = g.measureText(plays).width
  g.fillText(plays, PAD, y + 108)
  g.fillStyle = T.soft
  g.font = `500 16px ${FF}`
  g.fillText('次播放', PAD + numW + 14, y + 108)

  // 强调色下划线：把视线钉在这个数上
  g.fillStyle = T.accent
  g.fillRect(PAD, y + 122, Math.max(52, numW), 4)

  // 环比角标贴在右边。上周根本没有数的时候不装持平，写明「上周没有」
  const d = M.deltas?.plays
  if (d) {
    const txt = d.dir === 'up' ? `较上周 ↑${d.pct}%` : d.dir === 'down' ? `较上周 ↓${d.pct}%` : d.dir === 'new' ? '上周没有数' : '与上周持平'
    const col = d.dir === 'up' ? T.accent : T.faint
    g.font = `600 12px ${FF}`
    const tw = g.measureText(txt).width + 24
    g.fillStyle = T.accentSoft
    roundRect(g, W - PAD - tw, y + 74, tw, 26, 13)
    g.fill()
    g.fillStyle = col
    g.textAlign = 'center'
    g.fillText(txt, W - PAD - tw / 2, y + 91)
    g.textAlign = 'left'
  }

  // 一句人话
  g.fillStyle = T.soft
  g.font = `400 14px ${FF}`
  g.fillText(fitText(g, M.quip, cw - 40), PAD, y + 152)

  // 四格数据：靠发丝线分隔，不靠底色块
  const panelY = y + 176
  const panelH = 92
  panel(g, T, PAD, panelY, cw, panelH)
  const cells = [
    { label: '进厅', value: M.opens, unit: '次' },
    { label: '上架新货', value: M.newsCount, unit: '件' },
    { label: '弹幕新增', value: M.danmakuAdded, unit: '条' },
    { label: '观众', value: M.viewers, unit: '位' },
  ]
  const colW = cw / cells.length
  cells.forEach((c, i) => {
    const x = PAD + i * colW
    if (i > 0) {
      g.fillStyle = T.hair
      g.fillRect(x, panelY + 20, 1, panelH - 40)
    }
    g.fillStyle = T.faint
    g.font = `500 11px ${FF}`
    g.fillText(c.label, x + 22, panelY + 32)
    g.fillStyle = T.main
    g.font = `700 27px ${FFM}`
    const vw = g.measureText(fmtNum(c.value)).width
    g.fillText(fmtNum(c.value), x + 22, panelY + 64)
    g.fillStyle = T.faint
    g.font = `400 11px ${FF}`
    g.fillText(c.unit, x + 22 + vw + 6, panelY + 64)
  })

  return panelY + panelH
}

/** 本周最多人看：三行，缩略图 + 标题 + 观众数。图在横幅上，名字在这儿。 */
function hotList(g, M, T, W, y, imgs) {
  const hot = M.hottest ?? []
  if (!hot.length) return y
  const cw = W - PAD * 2
  sectionHead(g, T, PAD, y + 18, '本周最多人看', 'MOST WATCHED', W)
  y += 44
  const rowH = 68
  hot.slice(0, 5).forEach((h, i) => {
    const ry = y + i * rowH
    const thW = 108
    const thH = 60
    drawCover(g, imgs.get(h.bvid), PAD, ry, thW, thH, T, h.bvid)
    badge(g, PAD, ry, 22, String(i + 1), T.accent, T.onAccent, 6)
    const tx = PAD + thW + 16
    g.fillStyle = T.main
    g.font = `500 14px ${FF}`
    g.fillText(fitText(g, h.title || h.bvid, cw - thW - 16 - 96), tx, ry + 28)
    g.textAlign = 'right'
    g.fillStyle = T.main
    g.font = `700 19px ${FFM}`
    g.fillText(fmtNum(h.viewers), W - PAD, ry + 28)
    g.fillStyle = T.faint
    g.font = `400 10px ${FF}`
    g.fillText('位观众', W - PAD, ry + 44)
    g.textAlign = 'left'
    if (i < Math.min(hot.length, 5) - 1) {
      g.fillStyle = T.hair
      g.fillRect(tx, ry + thH + 3, cw - thW - 16, 1)
    }
  })
  return y + Math.min(hot.length, 5) * rowH
}

/** 七日动静：四段细柱一组，一周七组，颜色是同一条强调色的深浅。 */
function pulseChart(g, M, T, W, y, opts) {
  const bars = opts.show.bars ? M.dayBars ?? [] : []
  if (!bars.length) return y
  const cw = W - PAD * 2
  sectionHead(g, T, PAD, y + 18, '七日动静', 'WEEKLY PULSE', W)
  y += 44
  const h = 214
  panel(g, T, PAD, y, cw, h)

  // 图例压在右上角
  const legend = [
    { label: '游客', c: T.ramp[1] },
    { label: '主页', c: T.ramp[2] },
    { label: '进厅', c: T.ramp[3] },
    { label: '播放', c: T.ramp[4] },
  ]
  g.font = `500 10px ${FF}`
  let lx = W - PAD - 20
  for (let i = legend.length - 1; i >= 0; i--) {
    const item = legend[i]
    const tw = g.measureText(item.label).width
    g.fillStyle = item.c
    roundRect(g, lx - tw - 14, y + 20, 8, 8, 2)
    g.fill()
    g.fillStyle = T.faint
    g.fillText(item.label, lx - tw, y + 28)
    lx -= tw + 28
  }

  const innerX = PAD + 20
  const innerW = cw - 40
  const base = y + h - 32
  const maxH = 108
  const groupW = innerW / bars.length
  const barW = Math.min(14, (groupW - 26) / 4)
  const gap = 3

  // 两条参考线，给柱子一个量纲
  g.fillStyle = T.hair
  g.fillRect(innerX, base - maxH, innerW, 1)
  g.fillRect(innerX, base - maxH / 2, innerW, 1)

  bars.forEach((b, i) => {
    const gx = innerX + i * groupW
    const total = barW * 4 + gap * 3
    const startX = gx + (groupW - total) / 2
    const series = [b.guestPct, b.homePct, b.openPct, b.playPct]
    let tallest = 0
    series.forEach((pct, k) => {
      const bh = Math.max(2, ((pct || 0) / 100) * maxH)
      if (bh > tallest) tallest = bh
      g.fillStyle = T.ramp[k + 1]
      roundRect(g, startX + k * (barW + gap), base - bh, barW, bh, 2)
      g.fill()
    })
    // 当天的播放数标在这一组最高那根柱的正上方，不吊在半空
    g.fillStyle = T.soft
    g.font = `600 11px ${FFM}`
    g.textAlign = 'center'
    g.fillText(fmtNum(b.plays), gx + groupW / 2, base - tallest - 8)
    g.fillStyle = T.faint
    g.font = `500 10px ${FF}`
    g.fillText(b.dayLabel, gx + groupW / 2, base + 18)
    g.textAlign = 'left'
  })

  return y + h
}

/** 热力墙 × 时段钟：两块并排的小面板，一块按天，一块按钟点。 */
function heatPanels(g, M, T, W, y, opts) {
  const wall = opts.show.wall ? M.heatWall ?? [] : []
  const mtx = opts.show.matrix ? M.heatMatrix?.cells ?? [] : []
  if (!wall.length && !mtx.length) return y
  const cw = W - PAD * 2
  const gap = 18
  const half = Math.floor((cw - gap) / 2)
  const h = 200

  if (wall.length) {
    const x = PAD
    panel(g, T, x, y, half, h)
    g.fillStyle = T.main
    g.font = `700 15px ${FF}`
    g.fillText('出没热力墙', x + 18, y + 30)
    kicker(g, '8 WEEKS', x + half - 18, y + 29, 8, T.faint, '2.4px', 'right')

    const labelW = 16
    const gridX = x + 18 + labelW
    const gridW = half - 36 - labelW
    const cellW = (gridW - 7 * 3) / 8
    const cellH = 14
    const gy = y + 46
    // 左右两端标一下时间方向
    g.fillStyle = T.faint
    g.font = `400 8px ${FF}`
    g.textAlign = 'left'
    g.fillText('8 周前', gridX, gy - 5)
    g.textAlign = 'right'
    g.fillText('本周', gridX + gridW, gy - 5)
    g.textAlign = 'left'
    wall.forEach((c, idx) => {
      const col = Math.floor(idx / 7)
      const row = idx % 7
      g.fillStyle = T.ramp[c.level] ?? T.ramp[0]
      roundRect(g, gridX + col * (cellW + 3), gy + row * (cellH + 3), cellW, cellH, 3)
      g.fill()
    })
    g.fillStyle = T.faint
    g.font = `400 9px ${FF}`
    for (const [t, r] of [['一', 0], ['三', 2], ['五', 4], ['日', 6]]) {
      g.fillText(t, x + 18, gy + r * (cellH + 3) + 11)
    }
  }

  if (mtx.length) {
    const x = PAD + half + gap
    panel(g, T, x, y, half, h)
    g.fillStyle = T.main
    g.font = `700 15px ${FF}`
    g.fillText('时段热度', x + 18, y + 30)
    kicker(g, 'HOUR CLOCK', x + half - 18, y + 29, 8, T.faint, '2.4px', 'right')

    const labelW = 16
    const gridX = x + 18 + labelW
    const gridW = half - 36 - labelW
    const cellW = (gridW - 5 * 3) / 6
    const cellH = 14
    const gy = y + 54
    const max = Math.max(1, ...mtx.map((c) => c.shots))
    g.fillStyle = T.faint
    g.font = `400 8px ${FFM}`
    for (const [t, c] of [['0', 0], ['4', 1], ['8', 2], ['12', 3], ['16', 4], ['20', 5]]) {
      g.textAlign = 'center'
      g.fillText(`${t}时`, gridX + c * (cellW + 3) + cellW / 2, gy - 6)
    }
    g.textAlign = 'left'
    mtx.forEach((c, idx) => {
      const col = idx % 6
      const row = Math.floor(idx / 6)
      const lvl = c.shots === 0 ? 0 : 1 + Math.min(3, Math.floor((c.shots / max) * 3.99))
      g.fillStyle = T.ramp[lvl]
      roundRect(g, gridX + col * (cellW + 3), gy + row * (cellH + 3), cellW, cellH, 3)
      g.fill()
    })
    g.fillStyle = T.faint
    g.font = `400 9px ${FF}`
    for (const [t, r] of [['一', 0], ['二', 1], ['三', 2], ['四', 3], ['五', 4], ['六', 5], ['日', 6]]) {
      g.fillText(t, x + 18, gy + r * (cellH + 3) + 11)
    }
  }

  return y + h
}

/** 人气榜：UP 主与分区各一栏，榜条靠一条细进度线说高低。 */
function rankLists(g, M, T, W, y, opts) {
  const ups = opts.show.rank ? M.topUps ?? [] : []
  const zones = opts.show.rank ? M.topZones ?? [] : []
  if (!ups.length && !zones.length) return y
  const cw = W - PAD * 2
  const rowsN = Math.min(5, Math.max(ups.length, zones.length, 1)) // 后端每榜发 5 条（WeeklyHotTake），页面摆几条海报就摆几条（11-06 补）
  sectionHead(g, T, PAD, y + 18, '本周人气榜', 'TOP LISTS', W)
  y += 44
  const h = rowsN * 54 + 52
  panel(g, T, PAD, y, cw, h)
  // 面板摆 PAD 处，两栏就得在面板里再留一条内边距。
  // 原来 colW 把面板边当列边，右栏的「N 播」正好压在面板边框上，看着像飞出去了（11-03 修）
  const inner = 24
  const gap = 24
  const colW = (cw - inner * 2 - gap) / 2

  const drawList = (list, x0, head0) => {
    if (!list.length) return
    kicker(g, head0, x0, y + 28, 9, T.faint, '2.6px')
    const max = Math.max(1, ...list.map((u) => u.plays))
    const rowH = 54
    list.slice(0, rowsN).forEach((u, i) => {
      const ry = y + 50 + i * rowH
      const isFirst = i === 0
      g.fillStyle = isFirst ? T.accent : T.chartTrack
      g.beginPath()
      g.arc(x0 + 10, ry - 4, 10, 0, Math.PI * 2)
      g.fill()
      g.fillStyle = isFirst ? T.onAccent : T.faint
      g.font = `700 11px ${FFM}`
      g.textAlign = 'center'
      g.fillText(String(i + 1), x0 + 10, ry)
      g.textAlign = 'left'
      g.fillStyle = T.main
      g.font = `${isFirst ? 600 : 400} 14px ${FF}`
      g.fillText(fitText(g, u.upName || u.zone, colW - 74), x0 + 28, ry)
      g.fillStyle = T.faint
      g.font = `500 10px ${FFM}`
      g.textAlign = 'right'
      g.fillText(`${fmtNum(u.plays)} 播`, x0 + colW, ry)
      g.textAlign = 'left'
      g.fillStyle = T.chartTrack
      g.fillRect(x0, ry + 18, colW, 2)
      g.fillStyle = T.accent
      g.fillRect(x0, ry + 18, Math.max(4, (colW * u.plays) / max), 2)
    })
  }

  drawList(ups, PAD + inner, 'UP 主 · CREATORS')
  drawList(zones, PAD + inner + colW + gap, '分区 · ZONES')
  g.fillStyle = T.hair
  g.fillRect(PAD + inner + colW + gap / 2, y + 44, 1, rowsN * 54 - 12)

  return y + h
}

/** 本周新货：周报页新货墙的海报版（站长 11-06 点单）。4 列 × 3 行共 12 枚 16/10 封面。
 *  页面那面墙的明信片歪斜与错位是页面的脾气（蜂窝版试过，站长嫌费空间，11-06 撤回）；
 *  本皮肤的立身之本是「版面自己站得住，不靠歪斜和阴影撑场面」，所以这面墙站得笔直：
 *  网格对齐、发丝边、统一圆角。超过 12 枚时在墙下右对一注「共 N 件」，说清墙上是抽样。 */
function newsWall(g, M, T, W, y, imgs, opts) {
  const items = opts.show.news ? M.news ?? [] : []
  if (!items.length) return y
  const cw = W - PAD * 2
  sectionHead(g, T, PAD, y + 18, '本周新货', 'NEW ARRIVALS', W)
  y += 44
  const tiles = items.slice(0, 12)
  const cols = 4
  const gap = 12
  const tileW = (cw - gap * (cols - 1)) / cols
  const tileH = Math.round(tileW * 0.625)
  const rowH = tileH + 34 // 瓦下一行留给标题与序号
  tiles.forEach((n, i) => {
    const x = PAD + (i % cols) * (tileW + gap)
    const ty = y + Math.floor(i / cols) * rowH
    drawCover(g, imgs.get(n.bvid), x, ty, tileW, tileH, T, n.bvid, { radius: 10 })
    roundRect(g, x, ty, tileW, tileH, 10)
    g.strokeStyle = T.panelEdge
    g.lineWidth = 1
    g.stroke()
    g.fillStyle = T.main
    g.font = `500 11px ${FF}`
    g.fillText(fitText(g, n.title || n.bvid, tileW - 26), x, ty + tileH + 17)
    g.textAlign = 'right'
    g.fillStyle = T.faint
    g.font = `500 9px ${FFM}`
    g.fillText(String(i + 1).padStart(2, '0'), x + tileW, ty + tileH + 17)
    g.textAlign = 'left'
  })
  y += Math.ceil(tiles.length / cols) * rowH
  if ((M.newsCount ?? 0) > tiles.length) {
    // 注记另起一行：贴着 rowH 底画会压在末行标题与序号上（11-06 harness 实抓）
    kicker(g, `共 ${M.newsCount} 件 · 墙上摆这 ${tiles.length} 件`, W - PAD, y + 7, 9, T.faint, '1px', 'right')
    y += 15
  }
  return y
}

/** 角落账：三格小账，框在一块面板里。 */
function ledger(g, M, T, W, y, opts) {
  if (!opts.show.chips) return y
  const chips = []
  const gst = M.guest ?? {}
  if ((gst.heads ?? 0) > 0 || (gst.blockedWatch ?? 0) > 0) {
    chips.push({ label: '站外游客', value: `${gst.heads ?? 0} 位`, note: `逛主页 ${gst.home ?? 0} 枪 · 被挡 ${gst.blockedWatch ?? 0} 次` })
  }
  if (M.personal && M.personalDuration) {
    chips.push({ label: '我的战报', value: M.personalValue, note: `看完 ${M.personalDuration.finished ?? 0} 部 · 登录 ${M.personal.logins ?? 0} 回` })
  }
  if (M.personal && ((M.personal.videosWatched ?? 0) > 0 || (M.personal.opens ?? 0) > 0)) {
    // 观看链（站长 11-06 点单「海报也该补数据」）：链上四个数按序摆，字号降一档才装进栏宽；
    // 每部几放与看完率两个比率进注里。零动隐身，与页内链卡同一口径。
    const opens = M.personal.opens ?? 0
    const plays = M.personal.plays ?? 0
    const vids = M.personal.videosWatched ?? 0
    const fin = M.personalDuration?.finished ?? 0
    chips.push({
      label: '观看链',
      value: `${opens}→${plays}→${vids}→${fin}`,
      vsize: 13, // 箭头比数字宽得多：16px 装不进 121px 的栏宽，会把链尾省略掉（11-06 harness 实抓）
      note: `每部 ${vids ? (plays / vids).toFixed(1) : 0} 放 · 看完率 ${vids ? Math.round((fin / vids) * 100) : 0}%`,
    })
  }
  chips.push({
    label: '存货总账',
    value: `${M.totals?.videos ?? 0} 件`,
    note: `弹幕 ${fmtNum(M.totals?.danmaku ?? 0)} · 成员 ${M.totals?.members ?? 0} 席`,
  })
  if (!chips.length) return y

  const cw = W - PAD * 2
  sectionHead(g, T, PAD, y + 18, '角落账', 'THE LEDGER', W)
  y += 44
  const h = 100
  panel(g, T, PAD, y, cw, h)
  const colW = cw / chips.length
  chips.forEach((c, i) => {
    const x = PAD + i * colW
    if (i > 0) {
      g.fillStyle = T.hair
      g.fillRect(x, y + 20, 1, h - 40)
    }
    g.fillStyle = T.faint
    g.font = `500 11px ${FF}`
    g.fillText(c.label, x + 22, y + 32)
    g.fillStyle = T.main
    g.font = `700 ${c.vsize ?? 24}px ${FFM}`
    g.fillText(fitText(g, c.value, colW - 44), x + 22, y + 64)
    g.fillStyle = T.faint
    g.font = `400 10px ${FF}`
    g.fillText(fitText(g, c.note, colW - 44), x + 22, y + 84)
  })
  return y + h
}

/* ── 主入口 ───────────────────────────────────────────────────────────── */

/**
 * 画完海报正文，返回落笔后的底部 y。
 * 预览和下载都调它，所以它必须是纯函数式的：不许改状态，只许画。
 */
/** 分站流水／我的动向：双栏账目（站长 11-06 点单「海报数据还少」——把页面的流水账与个人账整搬）。
 *  左标签右数字一行一条；次/回同族的行（pct 由页面算好）垫根细底条当排序的余光，
 *  其余族不上轴——条/位/封同轴就是假比较，同页面「只给次/回放条」一条规矩。 */
function rowsGrid(g, T, W, y, cn, en, rows) {
  if (!rows.length) return y
  sectionHead(g, T, PAD, y + 18, cn, en, W)
  y += 42
  const cw = W - PAD * 2
  const gap = 28
  const colW = (cw - gap) / 2
  const rowH = 30
  const half = Math.ceil(rows.length / 2)
  rows.forEach((r, i) => {
    const col = i < half ? 0 : 1
    const x = PAD + col * (colW + gap)
    const ry = y + (i % half) * rowH
    g.fillStyle = T.soft
    g.font = `400 11px ${FF}`
    g.fillText(r.label, x, ry + 17)
    const disp = r.text || `${fmtNum(r.value)}${r.unit ? ` ${r.unit}` : ''}`
    g.font = `600 13px ${FFM}`
    g.fillStyle = T.main
    g.textAlign = 'right'
    g.fillText(fitText(g, disp, colW - 56), x + colW, ry + 17)
    g.textAlign = 'left'
    if (r.pct != null) {
      g.fillStyle = T.chartTrack
      g.fillRect(x, ry + 24, colW, 2)
      g.fillStyle = T.accent
      g.fillRect(x, ry + 24, Math.max(3, (colW * r.pct) / 100), 2)
    } else {
      g.fillStyle = T.hair
      g.fillRect(x, ry + 24, colW, 1)
    }
  })
  return y + half * rowH
}

export function paintEditorial(g, M, W, imgs, opts) {
  const T = editorialTheme(opts)
  let y = 0

  y = heroBanner(g, M, T, W, opts, imgs)
  y += 44
  y = heroNumbers(g, M, T, W, y)
  y += 44

  if (opts.show.hot) {
    const hot = M.hottest ?? []
    if (hot.length) {
      y = hotList(g, M, T, W, y, imgs)
      y += 40
    }
  }
  if (opts.show.bars && (M.dayBars ?? []).length) {
    y = pulseChart(g, M, T, W, y, opts)
    y += 40
  }
  if (opts.show.flow && (M.flowRows ?? []).length) {
    y = rowsGrid(g, T, W, y, '分站流水', 'ACTIVITY LOG', M.flowRows)
    y += 40
  }
  if ((opts.show.wall && (M.heatWall ?? []).length) || (opts.show.matrix && M.heatMatrix?.cells?.length)) {
    y = heatPanels(g, M, T, W, y, opts)
    y += 40
  }
  if (opts.show.rank && ((M.topUps ?? []).length || (M.topZones ?? []).length)) {
    y = rankLists(g, M, T, W, y, opts)
    y += 40
  }
  if (opts.show.news && (M.news ?? []).length) {
    y = newsWall(g, M, T, W, y, imgs, opts)
    y += 40
  }
  if (opts.show.mine && (M.mineRows ?? []).length) {
    y = rowsGrid(g, T, W, y, '我的动向', 'MY MOVES', M.mineRows)
    y += 40
  }
  y = ledger(g, M, T, W, y, opts)

  return y
}

/** 纸底：渐变 + 强调色光晕 + 底部压沉。画在所有内容底下。 */
export function editorialBackdrop(g, W, H, opts) {
  const T = editorialTheme(opts)
  const grad = g.createLinearGradient(0, 0, W * 0.4, H)
  grad.addColorStop(0, T.bg1)
  grad.addColorStop(1, T.bg0)
  g.fillStyle = grad
  g.fillRect(0, 0, W, H)

  const glow = g.createRadialGradient(W * 0.22, 60, 0, W * 0.22, 60, W * 0.95)
  glow.addColorStop(0, T.glow)
  glow.addColorStop(1, 'rgba(0,0,0,0)')
  g.fillStyle = glow
  g.fillRect(0, 0, W, H)

  const vig = g.createLinearGradient(0, H - 360, 0, H)
  vig.addColorStop(0, 'rgba(0,0,0,0)')
  vig.addColorStop(1, T.vignette)
  g.fillStyle = vig
  g.fillRect(0, H - 360, W, 360)
}

/** 落款：一条发丝线 + 站点标识 + 生成日期。 */
export function editorialFooter(g, M, T, W, H) {
  const y = H - 62
  g.fillStyle = T.hair
  g.fillRect(PAD, y, W - PAD * 2, 1)
  // 落款只留周次与生成日期。左边那句「小破分站 · 自建档案站 · …」跟它的
  // 那枚强调色小方块一起删了（11-03）——站名上面头图里已经有了，不必再说一遍
  g.textAlign = 'right'
  g.fillStyle = T.faint
  g.font = `400 11px ${FFM}`
  g.fillText(`${M.weekLabel} · ${M.generatedAt}`, W - PAD, y + 30)
  g.textAlign = 'left'
}
