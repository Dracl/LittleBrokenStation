/* ─────────────────────────────────────────────────────────────────────────
   小破分站 · 周报海报「构成主义」皮肤（11-03 新画）
   ---------------------------------------------------------------------------
   参照的是 1920 年代那批宣传版面的做法，不是把它裱成文物：
   · 三色到底：纸、墨、一色大声（默认宣传红）。圆角一律 0，边角全是刀切的。
   · 版面靠对角线撑：红楔形从左下切到右上，粗黑横带反白当版块头，斜杠定视线。
   · 字号走两极：120px 的巨数与 9px 的西文小注同时存在，中间不留和事佬。
   · 封面当照片拼贴用：平行四边形裁切 + 半调网点叠层，让它像贴上去的，不像嵌进去的。
   · 编号、竖排、宽字距西文，都是那个年代排字房的常规动作，这儿当骨架用。

   与编辑部那套共用 model 与 show.* 开关，两版随时对切；本文件不 import 编辑部。
   ───────────────────────────────────────────────────────────────────────── */

import { FF, FFM, POSTER_CONSTRUCT_ACCENTS, fitText, kicker, fmtNum, drawCover, posterJitter } from './weeklyPosterKit.js'

const PAD = 42 // 页面四边留白（比编辑部紧一档，构成主义不留呼吸位）
const SLANT = -11 * (Math.PI / 180) // 主力斜角，全版共用于楔形与斜杠

/** 三档纸。名字沿用编辑部的 ink / paper / brand，调参台那排钮不用改。 */
export function constructTheme(opts) {
  const acc = POSTER_CONSTRUCT_ACCENTS[opts?.accent] ?? POSTER_CONSTRUCT_ACCENTS.red

  if (opts?.theme === 'ink') {
    return {
      id: 'ink',
      bg: '#141311',
      ink: '#f1ece0',
      inkSoft: 'rgba(241,236,224,.74)',
      inkFaint: 'rgba(241,236,224,.46)',
      block: '#f1ece0',
      onBlock: '#141311',
      accent: '#e03131',
      accentDeep: '#a5161c',
      paper: '#f1ece0',
      rule: '#f1ece0',
      hair: 'rgba(241,236,224,.16)',
      gridEmpty: 'rgba(241,236,224,.10)',
      screen: 'rgba(0,0,0,.42)',
      phA: '#2a2724',
      phB: '#1b1917',
      phFg: 'rgba(241,236,224,.5)',
      ramp: ['rgba(241,236,224,.10)', 'rgba(224,49,49,.34)', 'rgba(224,49,49,.58)', 'rgba(224,49,49,.8)', '#e03131'],
    }
  }

  if (opts?.theme === 'brand') {
    // 粉版：浅粉当纸，墨还是墨黑，红块照旧。反白那套在浅粉上读不出来，这档不走
    return {
      id: 'brand',
      bg: '#f9dbe6',
      ink: '#33121f',
      inkSoft: 'rgba(51,18,31,.74)',
      inkFaint: 'rgba(51,18,31,.5)',
      block: '#33121f',
      onBlock: '#fdeef3',
      accent: acc.base,
      accentDeep: acc.deep,
      paper: '#fdeef3',
      rule: '#33121f',
      hair: 'rgba(51,18,31,.18)',
      gridEmpty: 'rgba(51,18,31,.1)',
      screen: 'rgba(51,18,31,.26)',
      phA: '#f3c6d6',
      phB: '#eab2c6',
      phFg: 'rgba(51,18,31,.55)',
      ramp: ['rgba(51,18,31,.1)', `${acc.base}55`, `${acc.base}93`, `${acc.base}c8`, acc.base],
    }
  }

  // 纸（默认）：米纸底、墨黑字、红楔形——最正的那一档
  return {
    id: 'paper',
    bg: '#efe9dd',
    ink: '#15130f',
    inkSoft: 'rgba(21,19,15,.74)',
    inkFaint: 'rgba(21,19,15,.5)',
    block: '#15130f',
    onBlock: '#f4efe4',
    accent: acc.base,
    accentDeep: acc.deep,
    paper: '#f7f3ea',
    rule: '#15130f',
    hair: 'rgba(21,19,15,.16)',
    gridEmpty: 'rgba(21,19,15,.1)',
    screen: 'rgba(21,19,15,.34)',
    phA: '#dcd5c6',
    phB: '#c8c0ae',
    phFg: 'rgba(21,19,15,.55)',
    ramp: ['rgba(21,19,15,.1)', `${acc.base}55`, `${acc.base}93`, `${acc.base}c8`, acc.base],
  }
}

/* ── 画法零件 ─────────────────────────────────────────────────────────── */

/** 刀切矩形：构成主义不留圆角，一律硬边。 */
function slab(g, color, x, y, w, h) {
  g.fillStyle = color
  g.fillRect(x, y, w, h)
}

/** 红楔形：从左下扫向右上的一条斜切三角，是这张海报的主骨架。 */
function wedge(g, color, x, y, w, h, flip) {
  g.fillStyle = color
  g.beginPath()
  if (flip) {
    g.moveTo(x, y)
    g.lineTo(x + w, y + h * 0.42)
    g.lineTo(x + w, y + h)
    g.lineTo(x, y + h * 0.2)
  } else {
    g.moveTo(x, y + h)
    g.lineTo(x + w * 0.52, y)
    g.lineTo(x + w, y)
    g.lineTo(x + w * 0.42, y + h)
  }
  g.closePath()
  g.fill()
}

/** 斜杠：一条粗斜线，用来钉视线与分栏，粗细 5 到 9。 */
function slash(g, color, x, y, len, thick, angle = SLANT) {
  g.save()
  g.translate(x, y)
  g.rotate(angle)
  g.fillStyle = color
  g.fillRect(0, 0, len, thick)
  g.restore()
}

/** 同心圆规：两圈空心圆加一个实心点，摆在对角线交汇处当规印。 */
function compass(g, color, cx, cy, r) {
  g.strokeStyle = color
  g.lineWidth = 2
  g.beginPath()
  g.arc(cx, cy, r, 0, Math.PI * 2)
  g.stroke()
  g.lineWidth = 1
  g.beginPath()
  g.arc(cx, cy, r * 0.62, 0, Math.PI * 2)
  g.stroke()
  g.fillStyle = color
  g.beginPath()
  g.arc(cx, cy, Math.max(2.5, r * 0.12), 0, Math.PI * 2)
  g.fill()
}

/** 半调网点：给拼贴的封面罩一层印刷感，越往下越密，像老海报的晒版。 */
function halftone(g, x, y, w, h, color, step = 7) {
  g.save()
  g.beginPath()
  g.rect(x, y, w, h)
  g.clip()
  g.fillStyle = color
  for (let ry = y + step / 2, row = 0; ry < y + h; ry += step, row += 1) {
    const t = (ry - y) / h
    const r = 0.5 + t * 1.35
    for (let rx = x + step / 2 + (row % 2 ? step / 2 : 0); rx < x + w; rx += step) {
      g.beginPath()
      g.arc(rx, ry, r, 0, Math.PI * 2)
      g.fill()
    }
  }
  g.restore()
}

/** 斜切平行四边形裁切路径：封面从这儿进去，出来就是贴上去的样子。 */
function slantedClip(g, x, y, w, h, skew = 12) {
  g.beginPath()
  g.moveTo(x + skew, y)
  g.lineTo(x + w, y)
  g.lineTo(x + w - skew, y + h)
  g.lineTo(x, y + h)
  g.closePath()
}

/** 版块头：一条粗黑带反白题名，右边挂一串宽字距西文。带子左端压一枚小方块。 */
function band(g, T, x, y, w, cn, en) {
  const h = 34
  slab(g, T.block, x, y, w, h)
  slab(g, T.accent, x, y, 10, h)
  g.fillStyle = T.onBlock
  g.font = `800 17px ${FF}`
  g.fillText(cn, x + 22, y + 23)
  if (en) kicker(g, en, x + w - 14, y + 22, 9, T.onBlock, '3px', 'right')
  return y + h
}

/** 巨数：字号拉到 108 到 122，底下压一条斜粗杠当衬线。 */
function giant(g, T, text, x, y, size, color) {
  g.fillStyle = color
  g.font = `900 ${size}px ${FFM}`
  const w = g.measureText(text).width
  g.fillText(text, x, y)
  return w
}

/** 编号方块：№ 01 那种排字房编号，红底白字硬边。 */
function ordinal(g, T, n, x, y, size = 30) {
  slab(g, T.accent, x, y, size, size)
  g.fillStyle = T.onBlock
  g.font = `800 ${Math.round(size * 0.5)}px ${FFM}`
  g.textAlign = 'center'
  g.fillText(String(n).padStart(2, '0'), x + size / 2, y + size * 0.68)
  g.textAlign = 'left'
}

/* ── 各版块 ───────────────────────────────────────────────────────────── */

/**
 * 顶部：黑带站名 + 红楔形 + 一号封面的斜切拼贴 + 巨标题。
 * 这一版把标题从「压在图上」改成「压在楔形上」，字要读得出来才算数。
 */
function topSlab(g, M, T, W, opts, imgs) {
  const H = 432
  slab(g, T.bg, 0, 0, W, H)

  // 主骨架：红楔形从底部扫到右上
  wedge(g, T.accent, -30, 150, W + 60, H - 150, false)

  // 规印压在对角线上，右上角再补一条斜杠
  compass(g, T.paper, W - 132, 214, 62)
  slash(g, T.block, 52, 116, 210, 6)

  // 顶带：站名与周次
  slab(g, T.block, 0, 0, W, 62)
  slab(g, T.accent, 0, 0, 14, 62)
  g.fillStyle = T.onBlock
  g.font = `800 15px ${FF}`
  g.fillText('小破分站', 32, 38)
  kicker(g, 'LITTLE BROKEN STATION', 112, 37, 9, T.onBlock, '3.6px')
  g.fillStyle = T.accent
  g.fillRect(W - 176, 17, 158, 28)
  g.fillStyle = T.onBlock
  g.font = `700 13px ${FF}`
  g.textAlign = 'center'
  g.fillText(fitText(g, M.weekLabel, 140), W - 176 + 79, 36)
  g.textAlign = 'left'

  // 一号封面：斜切平行四边形 + 网点，压在楔形上
  const hot = opts.show.hot ? M.hottest ?? [] : []
  const coverW = 300
  const coverH = 190
  const coverX = W - PAD - coverW + 16
  const coverY = 108
  drawCover(g, imgs.get(hot[0]?.bvid), coverX, coverY, coverW, coverH, T, hot[0]?.bvid, {
    clip: slantedClip,
    overlay: (gg, x, y, w, h) => halftone(gg, x, y, w, h, T.screen, 6),
  })
  slab(g, T.block, coverX - 4, coverY + coverH, coverW - 16, 8)

  // 二三位小条：同样斜切，叠在主封面下沿
  ;[1, 2].forEach((k) => {
    const it = hot[k]
    if (!it) return
    const tw = 132
    const th = 84
    const tx = W - PAD - tw + 12
    const ty = coverY + coverH + 26 + (k - 1) * (th + 12)
    drawCover(g, imgs.get(it.bvid), tx, ty, tw, th, T, it.bvid, {
      clip: (gg, x, y, w, h) => slantedClip(gg, x, y, w, h, 9),
      overlay: (gg, x, y, w, h) => halftone(gg, x, y, w, h, T.screen, 7),
    })
  })

  // 大标题：压在楔形上，带一点仰角
  const title = opts.title || '本周分站'
  g.save()
  g.translate(PAD - 4, 236)
  g.rotate(SLANT * 0.34)
  g.fillStyle = T.id === 'brand' ? T.paper : T.block
  g.font = `900 62px ${FF}`
  g.fillText(fitText(g, title, 380), 0, 0)
  g.restore()

  // 副标语：紧跟标题一条硬边小带
  slab(g, T.block, PAD, 262, 6, 46)
  g.fillStyle = T.id === 'brand' ? T.paper : T.ink
  g.font = `500 14px ${FF}`
  g.fillText(fitText(g, opts.subtitle || '自建档案站 · 数据都在自己手里', 360), PAD + 18, 288)

  // 竖排：把「本周」两字立起来钉在左缘，那个年代常干的事
  if (opts.showSign && M.signName) {
    g.save()
    g.translate(30, 322)
    g.rotate(Math.PI / 2)
    g.fillStyle = T.id === 'brand' ? T.paper : T.inkFaint
    g.font = `600 11px ${FF}`
    g.fillText(`本周值守 · ${M.signName}`, 0, 0)
    g.restore()
  }

  return H
}

/** 巨数锚点：本周播放巨数 + 环比方块 + 一句人话 + 四格硬边小账。 */
function anchor(g, M, T, W, y) {
  const cw = W - PAD * 2
  kicker(g, 'PLAY COUNT THIS WEEK', PAD, y + 20, 9, T.inkFaint, '4.4px')

  const plays = fmtNum(M.plays)
  g.font = `900 112px ${FFM}`
  const numW = g.measureText(plays).width
  // 数字底下先垫一条斜粗杠，视线被钉住
  slash(g, T.accent, PAD + 2, y + 104, Math.max(180, numW * 0.86), 12)
  g.fillStyle = T.ink
  g.fillText(plays, PAD, y + 96)
  g.fillStyle = T.inkSoft
  g.font = `700 16px ${FF}`
  g.fillText('次播放', PAD + numW + 14, y + 96)

  // 环比：黑方块反白，不做圆角胶囊
  const d = M.deltas?.plays
  if (d) {
    const txt = d.dir === 'up' ? `较上周 ↑${d.pct}%` : d.dir === 'down' ? `较上周 ↓${d.pct}%` : d.dir === 'new' ? '上周没有数' : '与上周持平'
    g.font = `700 12px ${FF}`
    const tw = g.measureText(txt).width + 28
    slab(g, T.block, W - PAD - tw, y + 62, tw, 30)
    g.fillStyle = T.onBlock
    g.textAlign = 'center'
    g.fillText(txt, W - PAD - tw / 2, y + 82)
    g.textAlign = 'left'
  }

  g.fillStyle = T.inkSoft
  g.font = `400 14px ${FF}`
  g.fillText(fitText(g, M.quip, cw - 30), PAD, y + 148)

  // 四格小账：靠粗黑竖杠分家，不靠底色块
  const cells = [
    { label: '进厅', value: M.opens, unit: '次' },
    { label: '上架新货', value: M.newsCount, unit: '件' },
    { label: '弹幕新增', value: M.danmakuAdded, unit: '条' },
    { label: '观众', value: M.viewers, unit: '位' },
  ]
  const gy = y + 176
  const gh = 96
  const colW = cw / cells.length
  slab(g, T.rule, PAD, gy, cw, 4)
  slab(g, T.rule, PAD, gy + gh, cw, 4)
  cells.forEach((c, i) => {
    const x = PAD + i * colW
    if (i > 0) slab(g, T.rule, x, gy + 12, 3, gh - 24)
    g.fillStyle = T.inkFaint
    g.font = `600 11px ${FF}`
    g.fillText(c.label, x + 16, gy + 30)
    g.fillStyle = T.ink
    g.font = `800 30px ${FFM}`
    const vw = g.measureText(fmtNum(c.value)).width
    g.fillText(fmtNum(c.value), x + 16, gy + 64)
    g.fillStyle = T.inkFaint
    g.font = `500 11px ${FF}`
    g.fillText(c.unit, x + 20 + vw, gy + 64)
  })

  return gy + gh
}

/** 本周最多人看：编号方块 + 斜切封面 + 粗黑观众块。 */
function hottest(g, M, T, W, y, imgs) {
  const hot = M.hottest ?? []
  if (!hot.length) return y
  const cw = W - PAD * 2
  y = band(g, T, PAD, y, cw, '本周最多人看', 'MOST WATCHED')
  y += 22
  const rowH = 78
  hot.slice(0, 5).forEach((h, i) => {
    const ry = y + i * rowH
    const thW = 116
    const thH = 64
    drawCover(g, imgs.get(h.bvid), PAD + 12, ry, thW, thH, T, h.bvid, {
      clip: (gg, x, yy, w, hh) => slantedClip(gg, x, yy, w, hh, 10),
      overlay: (gg, x, yy, w, hh) => halftone(gg, x, yy, w, hh, T.screen, 7),
    })
    ordinal(g, T, i + 1, PAD - 8, ry - 6, 28)

    const tx = PAD + thW + 30
    g.fillStyle = T.ink
    g.font = `700 15px ${FF}`
    g.fillText(fitText(g, h.title || h.bvid, cw - thW - 130), tx, ry + 26)
    g.fillStyle = T.inkFaint
    g.font = `500 10px ${FFM}`
    g.fillText(`№ ${String(i + 1).padStart(2, '0')} · VIEWERS`, tx, ry + 46)

    // 观众数：黑方块反白，硬边当秤砣
    const vt = fmtNum(h.viewers)
    g.font = `800 20px ${FFM}`
    const vw = g.measureText(vt).width
    const boxW = Math.max(96, vw + 34)
    slab(g, T.block, W - PAD - boxW, ry + 8, boxW, 44)
    g.fillStyle = T.onBlock
    g.textAlign = 'right'
    g.fillText(vt, W - PAD - 16, ry + 38)
    g.textAlign = 'left'
    if (i < Math.min(hot.length, 5) - 1) slab(g, T.hair, tx, ry + thH + 4, cw - thW - 40, 1)
  })
  return y + Math.min(hot.length, 5) * rowH
}

/** 七日动静：实心柱加红帽，基线是一条粗黑杠。 */
function pulse(g, M, T, W, y, opts) {
  const bars = opts.show.bars ? M.dayBars ?? [] : []
  if (!bars.length) return y
  const cw = W - PAD * 2
  y = band(g, T, PAD, y, cw, '七日动静', 'WEEKLY PULSE')
  y += 20
  const h = 218
  const innerX = PAD + 6
  const innerW = cw - 12
  const base = y + h - 40
  const maxH = 104
  const groupW = innerW / bars.length
  const barW = Math.min(20, (groupW - 18) / 2)

  // 图例：实心小方块，右上一排
  const legend = [
    { label: '播放', c: T.accent },
    { label: '进厅', c: T.ink },
  ]
  g.font = `600 10px ${FF}`
  let lx = W - PAD - 6
  for (let i = legend.length - 1; i >= 0; i -= 1) {
    const item = legend[i]
    const tw = g.measureText(item.label).width
    slab(g, item.c, lx - tw - 14, y + 4, 9, 9)
    g.fillStyle = T.inkSoft
    g.fillText(item.label, lx - tw, y + 12)
    lx -= tw + 26
  }

  slab(g, T.rule, innerX, base, innerW, 4)
  bars.forEach((b, i) => {
    const gx = innerX + i * groupW
    const cx = gx + groupW / 2
    const bhPlay = Math.max(3, ((b.playPct || 0) / 100) * maxH)
    const bhOpen = Math.max(3, ((b.openPct || 0) / 100) * maxH)
    const x1 = cx - barW - 2
    const x2 = cx + 2
    slab(g, T.ink, x1, base - bhOpen, barW, bhOpen)
    slab(g, T.accent, x2, base - bhPlay, barW, bhPlay)
    // 播放柱顶一枚红帽，把它从黑柱里拎出来
    slab(g, T.block, x2, base - bhPlay - 5, barW, 4)

    g.fillStyle = T.ink
    g.font = `800 12px ${FFM}`
    g.textAlign = 'center'
    g.fillText(fmtNum(b.plays), cx, base - Math.max(bhPlay, bhOpen) - 14)
    g.fillStyle = T.inkSoft
    g.font = `600 11px ${FF}`
    g.fillText(b.dayLabel, cx, base + 22)
    g.textAlign = 'left'
  })

  return y + h
}

/** 热力墙与时段钟：两块方格，格子之间留硬边黑缝。 */
function grids(g, M, T, W, y, opts) {
  const wall = opts.show.wall ? M.heatWall ?? [] : []
  const mtx = opts.show.matrix ? M.heatMatrix?.cells ?? [] : []
  if (!wall.length && !mtx.length) return y
  const cw = W - PAD * 2
  const gap = 18
  const half = Math.floor((cw - gap) / 2)
  const h = 196

  const frame = (x, titleCn, titleEn) => {
    slab(g, T.rule, x, y, half, 3)
    g.fillStyle = T.ink
    g.font = `800 14px ${FF}`
    g.fillText(titleCn, x, y + 26)
    kicker(g, titleEn, x + half, y + 25, 8, T.inkFaint, '2.6px', 'right')
    slab(g, T.rule, x, y + 36, half, 2)
  }

  if (wall.length) {
    const x = PAD
    frame(x, '出没热力墙', '8 WEEKS')
    const gy = y + 52
    const cellH = 15
    const cols = 8
    const cellW = (half - 20 - (cols - 1) * 3) / cols
    wall.forEach((c, idx) => {
      const col = Math.floor(idx / 7)
      const row = idx % 7
      slab(g, T.ramp[c.level] ?? T.gridEmpty, x + 20 + col * (cellW + 3), gy + row * (cellH + 3), cellW, cellH)
    })
    g.fillStyle = T.inkFaint
    g.font = `600 9px ${FF}`
    for (const [t, r] of [['一', 0], ['三', 2], ['五', 4], ['日', 6]]) {
      g.fillText(t, x, gy + r * (cellH + 3) + 11)
    }
    g.font = `500 8px ${FF}`
    g.fillText('8 周前', x + 20, gy - 6)
    g.textAlign = 'right'
    g.fillText('本周', x + half, gy - 6)
    g.textAlign = 'left'
  }

  if (mtx.length) {
    const x = PAD + half + gap
    frame(x, '时段热度', 'HOUR CLOCK')
    const gy = y + 52
    const cellH = 15
    const cellW = (half - 20 - 5 * 3) / 6
    const max = Math.max(1, ...mtx.map((c) => c.shots))
    g.fillStyle = T.inkFaint
    g.font = `500 8px ${FFM}`
    for (const [t, c] of [['0', 0], ['4', 1], ['8', 2], ['12', 3], ['16', 4], ['20', 5]]) {
      g.textAlign = 'center'
      g.fillText(`${t}时`, x + 20 + c * (cellW + 3) + cellW / 2, gy - 6)
    }
    g.textAlign = 'left'
    mtx.forEach((c, idx) => {
      const col = idx % 6
      const row = Math.floor(idx / 6)
      const lvl = c.shots === 0 ? 0 : 1 + Math.min(4, Math.ceil((c.shots / max) * 4))
      slab(g, T.ramp[lvl] ?? T.gridEmpty, x + 20 + col * (cellW + 3), gy + row * (cellH + 3), cellW, cellH)
    })
    g.fillStyle = T.inkFaint
    g.font = `600 9px ${FF}`
    for (const [t, r] of [['一', 0], ['二', 1], ['三', 2], ['四', 3], ['五', 4], ['六', 5], ['日', 6]]) {
      g.fillText(t, x, gy + r * (cellH + 3) + 11)
    }
  }

  return y + h + 40
}

/** 人气榜：UP 主与分区两栏，进度是一条实心粗杠。 */
function ranks(g, M, T, W, y, opts) {
  const ups = opts.show.rank ? M.topUps ?? [] : []
  const zones = opts.show.rank ? M.topZones ?? [] : []
  if (!ups.length && !zones.length) return y
  const cw = W - PAD * 2
  y = band(g, T, PAD, y, cw, '本周人气榜', 'TOP LISTS')
  y += 24
  const colW = (cw - 32) / 2
  const rowH = 52
  const rowsN = Math.min(5, Math.max(ups.length, zones.length, 1)) // 后端每榜发 5 条（WeeklyHotTake），页面摆几条海报就摆几条（11-06 补）

  const column = (list, x0, head0) => {
    if (!list.length) return
    kicker(g, head0, x0, y + 12, 9, T.inkFaint, '3px')
    slab(g, T.rule, x0, y + 20, colW, 3)
    const max = Math.max(1, ...list.map((u) => u.plays))
    list.slice(0, rowsN).forEach((u, i) => {
      const ry = y + 48 + i * rowH
      g.fillStyle = T.ink
      g.font = `${i === 0 ? 800 : 600} 14px ${FF}`
      g.fillText(fitText(g, u.upName || u.zone, colW - 74), x0 + 26, ry)
      g.fillStyle = T.inkFaint
      g.font = `700 11px ${FFM}`
      g.textAlign = 'right'
      g.fillText(`${fmtNum(u.plays)} 播`, x0 + colW, ry)
      g.textAlign = 'left'
      // 序号方块：第一名反白，其余只描边
      slab(g, i === 0 ? T.accent : T.gridEmpty, x0, ry - 16, 18, 18)
      g.fillStyle = i === 0 ? T.onBlock : T.inkSoft
      g.font = `800 10px ${FFM}`
      g.textAlign = 'center'
      g.fillText(String(i + 1), x0 + 9, ry - 3)
      g.textAlign = 'left'
      slab(g, T.gridEmpty, x0, ry + 12, colW, 6)
      slab(g, i === 0 ? T.accent : T.ink, x0, ry + 12, Math.max(6, (colW * u.plays) / max), 6)
    })
  }

  column(ups, PAD, 'UP 主 · CREATORS')
  column(zones, PAD + colW + 32, '分区 · ZONES')
  slab(g, T.rule, PAD + colW + 15, y + 6, 3, rowH * rowsN + 34)

  return y + rowH * rowsN + 44
}

/** 本周新货：周报页新货墙的海报版（站长 11-06 点单）——斜切拼贴墙。
 *  4×3 共 12 枚 16/10 封面：平行四边形裁切 + 半调网点 + 红块编号，每枚拿 bvid 折出
 *  定值倾角与错位（posterJitter，与页面墙同脾气：同一条片子永远歪在同一角度）。
 *  蜂窝散置版试过（11-06 二轮），站长嫌费空间撤回，留网格版。 */
function arrivals(g, M, T, W, y, imgs, opts) {
  const items = opts.show.news ? M.news ?? [] : []
  if (!items.length) return y
  const cw = W - PAD * 2
  y = band(g, T, PAD, y, cw, '本周新货', 'ON THE WALL')
  y += 24
  const tiles = items.slice(0, 12)
  const cols = 4
  const gap = 10
  const tileW = (cw - gap * (cols - 1)) / cols
  const tileH = Math.round(tileW * 0.625)
  const rowH = tileH + 44 // 瓦下一行留给标题，也够倾角与错位晃出来
  tiles.forEach((n, i) => {
    const j = posterJitter(n.bvid)
    const x = PAD + (i % cols) * (tileW + gap)
    const row = Math.floor(i / cols)
    const ty = y + row * rowH + 5 + j.dy
    g.save()
    g.translate(x + tileW / 2, ty + tileH / 2)
    g.rotate(j.rot)
    g.translate(-tileW / 2, -tileH / 2)
    drawCover(g, imgs.get(n.bvid), 0, 0, tileW, tileH, T, n.bvid, {
      clip: (gg, xx, yy, w, hh) => slantedClip(gg, xx, yy, w, hh, 8),
      overlay: (gg, xx, yy, w, hh) => halftone(gg, xx, yy, w, hh, T.screen, 8),
    })
    ordinal(g, T, i + 1, 0, 0, 22)
    g.restore()
    const baseY = y + row * rowH
    g.fillStyle = T.ink
    g.font = `700 11px ${FF}`
    g.fillText(fitText(g, n.title || n.bvid, tileW - 4), x, baseY + tileH + 34 + j.dy)
  })
  const bottom = y + Math.ceil(tiles.length / cols) * rowH
  if ((M.newsCount ?? 0) > tiles.length) {
    // 注记另起一行：贴着 bottom 画会压在末行标题上（11-06 harness 实抓）
    kicker(g, `共 ${M.newsCount} 件 · 墙上摆这 ${tiles.length} 件`, W - PAD, bottom + 11, 9, T.inkFaint, '2px', 'right')
  }
  return bottom + 18
}

/** 角落账：三格小账，粗黑上下杠夹住。 */
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
    // 观看链（站长 11-06 点单「海报也该补数据」）：链上四个数按序摆，字号降一档才装进栏宽；零动隐身同页内口径。
    const opens = M.personal.opens ?? 0
    const plays = M.personal.plays ?? 0
    const vids = M.personal.videosWatched ?? 0
    const fin = M.personalDuration?.finished ?? 0
    chips.push({
      label: '观看链',
      value: `${opens}→${plays}→${vids}→${fin}`,
      vsize: 15,
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
  y = band(g, T, PAD, y, cw, '角落账', 'THE LEDGER')
  y += 18
  const h = 96
  const colW = cw / chips.length
  slab(g, T.rule, PAD, y, cw, 4)
  slab(g, T.rule, PAD, y + h, cw, 4)
  chips.forEach((c, i) => {
    const x = PAD + i * colW
    if (i > 0) slab(g, T.rule, x, y + 12, 3, h - 24)
    g.fillStyle = T.inkFaint
    g.font = `600 11px ${FF}`
    g.fillText(c.label, x + 16, y + 30)
    g.fillStyle = T.ink
    g.font = `800 ${c.vsize ?? 26}px ${FFM}`
    g.fillText(fitText(g, c.value, colW - 32), x + 16, y + 62)
    g.fillStyle = T.inkFaint
    g.font = `400 10px ${FF}`
    g.fillText(fitText(g, c.note, colW - 32), x + 16, y + 82)
  })
  return y + h
}

/* ── 入口 ─────────────────────────────────────────────────────────────── */

/** 画完构成主义版的正文，返回落笔后的底部 y。预览与下载共用，不许改状态。 */
/** 分站流水／我的动向：双栏账目（站长 11-06 点单「海报数据还少」——页面的流水账与个人账整搬）。
 *  左标签右数字一行一条，数字粗黑当秤砣；次/回同族（pct 页面算好）垫红底条，其余族不上轴。 */
function rowsGrid(g, T, W, y, cn, en, rows) {
  if (!rows.length) return y
  const cw = W - PAD * 2
  y = band(g, T, PAD, y, cw, cn, en)
  y += 22
  const gap = 28
  const colW = (cw - gap) / 2
  const rowH = 32
  const half = Math.ceil(rows.length / 2)
  rows.forEach((r, i) => {
    const col = i < half ? 0 : 1
    const x = PAD + col * (colW + gap)
    const ry = y + (i % half) * rowH
    g.fillStyle = T.inkSoft
    g.font = `600 11px ${FF}`
    g.fillText(r.label, x + 10, ry + 17)
    const disp = r.text || `${fmtNum(r.value)}${r.unit ? ` ${r.unit}` : ''}`
    g.font = `800 14px ${FFM}`
    g.fillStyle = T.ink
    g.textAlign = 'right'
    g.fillText(fitText(g, disp, colW - 64), x + colW, ry + 18)
    g.textAlign = 'left'
    if (r.pct != null) {
      slab(g, T.gridEmpty, x, ry + 25, colW, 4)
      slab(g, T.accent, x, ry + 25, Math.max(4, (colW * r.pct) / 100), 4)
    } else {
      slab(g, T.hair, x, ry + 26, colW, 1)
    }
  })
  return y + half * rowH + 8
}

export function paintConstruct(g, M, W, imgs, opts) {
  const T = constructTheme(opts)
  let y = 0

  y = topSlab(g, M, T, W, opts, imgs)
  y += 40
  y = anchor(g, M, T, W, y)
  y += 42
  if (opts.show.hot && (M.hottest ?? []).length) {
    y = hottest(g, M, T, W, y, imgs)
    y += 40
  }
  if (opts.show.bars && (M.dayBars ?? []).length) {
    y = pulse(g, M, T, W, y, opts)
    y += 40
  }
  if (opts.show.flow && (M.flowRows ?? []).length) {
    y = rowsGrid(g, T, W, y, '分站流水', 'ACTIVITY LOG', M.flowRows)
    y += 36
  }
  if ((opts.show.wall && (M.heatWall ?? []).length) || (opts.show.matrix && M.heatMatrix?.cells?.length)) {
    y = grids(g, M, T, W, y, opts)
    y += 8
  }
  if (opts.show.rank && ((M.topUps ?? []).length || (M.topZones ?? []).length)) {
    y = ranks(g, M, T, W, y, opts)
    y += 4
  }
  if (opts.show.news && (M.news ?? []).length) {
    y = arrivals(g, M, T, W, y, imgs, opts)
    y += 40
  }
  if (opts.show.mine && (M.mineRows ?? []).length) {
    y = rowsGrid(g, T, W, y, '我的动向', 'MY MOVES', M.mineRows)
    y += 36
  }
  y = ledger(g, M, T, W, y, opts)

  return y
}

/** 底：纸色平铺 + 一条贯穿全版的大斜切 + 右下角网点。画在所有内容底下。 */
export function constructBackdrop(g, W, H, opts) {
  const T = constructTheme(opts)
  slab(g, T.bg, 0, 0, W, H)
  g.save()
  g.beginPath()
  g.moveTo(W, 0)
  g.lineTo(W, H)
  g.lineTo(W * 0.62, H)
  g.closePath()
  g.fillStyle = T.id === 'paper' ? 'rgba(21,19,15,.035)' : 'rgba(255,255,255,.03)'
  g.fill()
  g.restore()
  halftone(g, W - 210, H - 300, 210, 300, T.id === 'paper' ? 'rgba(21,19,15,.10)' : 'rgba(255,255,255,.07)', 10)
}

/** 落款：一条粗黑带反白，比编辑部的发丝线更像那个年代的门楣。 */
export function constructFooter(g, M, T, W, H) {
  const y = H - 66
  slab(g, T.block, 0, y, W, 66)
  slab(g, T.accent, 0, y, 14, 66)
  g.fillStyle = T.onBlock
  g.font = `600 11px ${FF}`
  g.fillText('小破分站 · 自建档案站 · 数据都在自己手里', 32, y + 30)
  kicker(g, 'CONSTRUCTIVIST WEEKLY EDITION', 32, y + 48, 8, T.onBlock, '3px')
  g.textAlign = 'right'
  g.fillStyle = T.onBlock
  g.font = `700 12px ${FFM}`
  g.fillText(`${M.weekLabel} · ${M.generatedAt}`, W - PAD, y + 38)
  g.textAlign = 'left'
}
