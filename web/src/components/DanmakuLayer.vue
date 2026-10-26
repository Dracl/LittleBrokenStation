<script setup>
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'

const props = defineProps({
  src: { type: String, default: null },
  video: { type: Object, default: null }, // <video> DOM 元素
  enabled: { type: Boolean, default: true },
  fontSize: { type: Number, default: -1 }, // PlayRes 1080 基准下的字号，-1 用分站默认 50
  lineCount: { type: Number, default: -1 }, // 滚动轨道上限，-1 不限（按屏高自动）
  opacity: { type: Number, default: 1 }, // 0~1 弹幕整体透明度
  area: { type: Number, default: 1 }, // 滚动区占屏高度比例：0.25/0.5/0.75/1
  speed: { type: Number, default: 1 }, // 滚动速度倍率，>1 更快
  modes: { type: Object, default: () => ({ scroll: true, top: true, bottom: true, color: true }) },
  family: { type: String, default: 'hei' }, // hei 黑体 | song 宋体 | mono 等宽
  bold: { type: Boolean, default: false },
  stroke: { type: String, default: 'thick' }, // thick 重墨 | thin 描边 | shadow 45°投影
  keywords: { type: Array, default: () => [] }, // 屏蔽词，命中即隐藏
  localItems: { type: Array, default: () => [] }, // 站内弹幕 [{id, t, text}]：药丸+头像样式，存站可删
})

const emit = defineEmits(['delete-local'])

const canvas = ref(null)
let items = []
let rafId = 0
let observer = null

// ---- ass 解析（downkyicore 产出的 B 站弹幕轨）----
function parseTime(text) {
  const m = text.match(/(\d+):(\d{1,2}):(\d{1,2})(?:\.(\d{1,2}))?/)
  if (!m) return 0
  return Number(m[1]) * 3600 + Number(m[2]) * 60 + Number(m[3]) + Number(m[4] ?? 0) / 100
}

function assColorToRgba(hexBgr) {
  // ASS 是 &HBBGGRR（BGR 序），转成 canvas 要的 #RRGGBB
  if (!hexBgr || hexBgr.length !== 6) return '#ffffff'
  const b = hexBgr.slice(0, 2)
  const g = hexBgr.slice(2, 4)
  const r = hexBgr.slice(4, 6)
  return `#${r}${g}${b}`.toUpperCase()
}

function parseAss(text) {
  const result = []
  for (const line of text.split(/\r?\n/)) {
    if (!line.startsWith('Dialogue: ')) continue
    const parts = line.slice(10).split(',')
    if (parts.length < 10) continue
    const start = parseTime(parts[1])
    const end = parseTime(parts[2])
    let body = parts.slice(9).join(',')

    let mode = 'scroll'
    let x = null
    let y = null
    let color = '#ffffff'

    const cmds = body.match(/\{[^}]*\}/g) ?? []
    for (const cmd of cmds) {
      const pos = cmd.match(/\\pos\(\s*([\d.]+)\s*,\s*([\d.]+)\s*\)/)
      if (pos) {
        mode = 'fixed'
        x = Number(pos[1])
        y = Number(pos[2])
      }
      const move = cmd.match(/\\move\(\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)\s*\)/)
      if (move) {
        mode = 'move'
        x = Number(move[1])
        y = Number(move[2])
      }
      const colorMatch = cmd.match(/\\c&H([0-9A-Fa-f]{6})/)
      if (colorMatch) color = assColorToRgba(colorMatch[1])
    }

    body = body.replace(/\{[^}]*\}/g, '').replace(/\\N/g, ' ').trim()
    if (!body) continue
    const item = { start, end, mode, x, y, color, text: body, lane: null, gone: false, width: null, bmp: undefined }
    // B 站导出把顶/底弹幕也写成 \pos：按 y 落位归类，供显示模式开关过滤。
    if (mode === 'fixed' && typeof y === 'number') {
      item.zone = y < 324 ? 'top' : y > 756 ? 'bottom' : 'top'
    }
    result.push(item)
  }
  result.sort((a, b) => a.start - b.start)
  return result
}

function itemVisible(item) {
  const modes = props.modes ?? {}
  if (modes.color === false && item.color && item.color !== '#FFFFFF') return false
  if (props.keywords?.length) {
    for (const kw of props.keywords) {
      if (kw && item.text.includes(kw)) return false
    }
  }
  if (item.mode === 'scroll' || item.mode === 'move') return modes.scroll !== false
  if (item.zone === 'bottom') return modes.bottom !== false
  return modes.top !== false
}

// ---- 渲染 ----
function resize() {
  const el = canvas.value
  if (!el) return
  const rect = el.getBoundingClientRect()
  const dpr = window.devicePixelRatio || 1
  el.width = Math.max(1, Math.floor(rect.width * dpr))
  el.height = Math.max(1, Math.floor(rect.height * dpr))
  invalidateBitmaps()
}

// ---- 位图缓存：每条弹幕的描边+填充只画一次，重复弹幕（"2222"）直接命中 ----
const bmpCache = new Map() // `${kind}|${text}|${color}|${fontPx}|${style}` → entry
const BMP_LIMIT = 1500

function invalidateBitmaps() {
  bmpCache.clear()
  for (const it of items) {
    it.bmp = undefined
    it.width = null
  }
}

watch(() => [props.fontSize, props.family, props.bold, props.stroke], invalidateBitmaps)

const FONT_STACKS = {
  hei: '"PingFang SC", "Microsoft YaHei", sans-serif',
  song: '"Songti SC", "SimSun", serif',
  mono: 'ui-monospace, Consolas, "Courier New", monospace',
}

function fontStr(fontPx) {
  return `${props.bold ? 'bold ' : ''}${fontPx}px ${FONT_STACKS[props.family] ?? FONT_STACKS.hei}`
}

// ─── 站长头像：主人发的弹幕挂站长登录头像（服务端 nav 缓存，层启动时只取一次；取不到就用默认小人）───
let ownerFaceImg = null
let faceVersion = 0

fetch('/api/station/profile')
  .then((res) => (res.ok ? res.json() : null))
  .then((profile) => {
    const url = profile?.face
    if (!url) return
    const img = new Image()
    img.onload = () => {
      ownerFaceImg = img
      faceVersion++ // styleTag 变号，药丸位图自动作废重建
    }
    img.src = url
  })
  .catch(() => { /* 没头像就没，默认小人顶上 */ })

// 站内弹幕造型（方案 A）：半透黑药丸 + 白圆 B 站风小人头像（主人弹幕换站长真容）+ 正文。
function drawPillBadge(oc, x, y, d) {
  oc.fillStyle = '#ffffff'
  oc.beginPath()
  oc.arc(x + d / 2, y + d / 2, d / 2, 0, Math.PI * 2)
  oc.fill()
  oc.save()
  oc.beginPath()
  oc.arc(x + d / 2, y + d / 2, d / 2, 0, Math.PI * 2)
  oc.clip()
  oc.fillStyle = '#fb7299'
  oc.beginPath()
  oc.arc(x + d / 2, y + d * 0.37, d * 0.17, 0, Math.PI * 2)
  oc.fill()
  oc.beginPath()
  oc.arc(x + d / 2, y + d * 1.02, d * 0.32, Math.PI, 0)
  oc.closePath()
  oc.fill()
  oc.restore()
}

function bitmapFor(ctx, item, fontPx) {
  if (item.bmp && item.bmpFont === fontPx && item.bmpStyle === styleTag()) return item.bmp
  const key = `${item.badge ? 'P' : 'N'}|${item.text}|${item.color}|${fontPx}|${styleTag()}`
  let entry = bmpCache.get(key)
  if (!entry) {
    const font = fontStr(fontPx)
    ctx.font = font
    const pad = Math.max(5, fontPx / 3.2)
    const textW = Math.max(1, Math.ceil(ctx.measureText(item.text).width))

    let w
    let h
    let textX = pad
    let textY = pad
    const off = document.createElement('canvas')
    if (item.badge) {
      // 药丸包住头像与正文；药丸比字稍高，垂直居中。
      const pillH = Math.round(fontPx * 1.32)
      const avatarD = pillH * 0.88
      const innerX = fontPx * 0.2
      const gapA = fontPx * 0.28
      textX = pad + innerX + avatarD + gapA
      textY = pad + pillH / 2 // middle 基线直接坐药丸中线，CJK 字形居中不再靠估高
      w = Math.max(1, Math.ceil(textX + textW + innerX + pad))
      h = Math.max(1, Math.ceil(pillH + pad * 2))
      off.width = w
      off.height = h
      const oc = off.getContext('2d')
      oc.fillStyle = 'rgba(17,17,20,0.52)'
      oc.beginPath()
      if (oc.roundRect) {
        oc.roundRect(pad, pad, w - pad * 2, pillH, pillH / 2)
      } else {
        oc.rect(pad, pad, w - pad * 2, pillH)
      }
      oc.fill()
      const avatarX = pad + innerX
      const avatarY = pad + (pillH - avatarD) / 2
      if (item.fromOwner && ownerFaceImg) {
        // 主人弹幕：圆裁站长真容顶替默认小人。
        oc.save()
        oc.beginPath()
        oc.arc(avatarX + avatarD / 2, avatarY + avatarD / 2, avatarD / 2, 0, Math.PI * 2)
        oc.clip()
        oc.drawImage(ownerFaceImg, avatarX, avatarY, avatarD, avatarD)
        oc.restore()
      } else {
        drawPillBadge(oc, avatarX, avatarY, avatarD)
      }
      oc.font = font
      oc.textBaseline = 'middle'
      oc.lineJoin = 'round'
      oc.strokeStyle = 'rgba(0,0,0,0.55)'
      oc.lineWidth = Math.max(1, fontPx / 18)
      oc.strokeText(item.text, textX, textY)
      oc.fillStyle = item.color
      oc.fillText(item.text, textX, textY)
    } else {
      w = Math.max(1, textW + pad * 2)
      h = Math.max(1, Math.ceil(fontPx * 1.35 + pad * 2))
      off.width = w
      off.height = h
      const oc = off.getContext('2d')
      oc.font = font
      oc.textBaseline = 'top'
      oc.lineJoin = 'round'
      if (props.stroke === 'shadow') {
        // 45°投影：先往右下垫一层半透黑字，再画正文。
        oc.fillStyle = 'rgba(0,0,0,0.65)'
        oc.fillText(item.text, pad + fontPx / 12, pad + fontPx / 12)
        oc.strokeStyle = 'rgba(0,0,0,0.9)'
        oc.lineWidth = Math.max(1.5, fontPx / 14)
        oc.strokeText(item.text, pad, pad)
      } else {
        oc.strokeStyle = 'rgba(0,0,0,0.9)'
        oc.lineWidth = props.stroke === 'thick' ? Math.max(2.5, fontPx / 8) : Math.max(1.5, fontPx / 14)
        oc.strokeText(item.text, pad, pad)
      }
      oc.fillStyle = item.color
      oc.fillText(item.text, pad, pad)
    }
    entry = { canvas: off, width: w, pad, padT: pad }
    if (bmpCache.size >= BMP_LIMIT) {
      // 简单 FIFO 蒸发，弹幕文本重复度高，命中率不受影响。
      bmpCache.delete(bmpCache.keys().next().value)
    }
    bmpCache.set(key, entry)
  }
  item.bmp = entry
  item.bmpFont = fontPx
  item.bmpStyle = styleTag()
  return entry
}

function styleTag() {
  return `${props.family}|${props.bold ? 'b' : 'n'}|${props.stroke}|f${faceVersion}`
}

// ---- 活跃集合：双指针增量维护，杜绝每帧全表扫描 ----
let ptr = 0
let active = []
let lastT = -1

function lowerBound(t) {
  // 第一条 start > t 的下标
  let lo = 0
  let hi = items.length
  while (lo < hi) {
    const mid = (lo + hi) >> 1
    if (items[mid].start <= t) lo = mid + 1
    else hi = mid
  }
  return lo
}

function resetActive(t) {
  ptr = lowerBound(t)
  // seek 后回填：起点在 t 之前、窗口仍覆盖 t 的弹幕重新入场，并洗掉飞出/占轨状态。
  active = []
  for (let i = ptr - 1; i >= 0 && items[i].start > t - 30; i--) {
    if (items[i].end >= t) {
      const it = items[i]
      it.gone = false
      if (it.mode === 'scroll') it.lane = null
      it.adjY = null
      active.push(it)
    }
  }
  lanes.clear()
}

const lanes = new Map() // lane -> 该轨道最右端的右边界 x

// 命中测试：每帧记下画出的站内弹幕矩形（设备像素），点中可删。
let hits = []

// 自适应帧率：默认跟随显示器刷新率（不再锁 30fps），绘制开销高时自动降到半速/1/3 速保主线程，负载回落自动恢复
let lastDraw = 0
let lastNow = 0
let refreshGap = 16.7 // 自适应屏刷新间隔（ms），用实际帧距估算
let costEma = 0 // 单帧绘制耗时 EMA

// 平滑时钟：video.currentTime 被视频帧率量化（30fps 源按 1/30 秒跳步），
// 直接拿来定位在高刷屏上弹幕就是一卡一卡。改用 performance.now() 外推，
// 再向 video.currentTime 软校正；seek/暂停/缓冲卡顿时硬同步，不会离音频越来越远。
const clk = { v: 0, p: 0, rate: 1, ready: false }
function smoothTime(now) {
  const video = props.video
  if (!video) return 0
  const tv = video.currentTime
  if (video.paused || video.ended) {
    clk.ready = false
    return tv
  }
  const rate = video.playbackRate || 1
  if (video.seeking || video.readyState < 3) {
    clk.p = now // 缓冲卡顿：冻结外推，恢复后接着走
    return clk.v
  }
  if (!clk.ready || rate !== clk.rate) {
    clk.v = tv
    clk.p = now
    clk.rate = rate
    return tv
  }
  const predicted = clk.v + ((now - clk.p) / 1000) * rate
  const diff = tv - predicted
  if (Math.abs(diff) > 0.3) {
    // seek/长时卡后硬同步
    clk.v = tv
    clk.p = now
    clk.rate = rate
    return tv
  }
  // 小漂移：把锚点向真实音频轻移，弹幕跟着走而不跳位
  const pull = diff * 0.06
  clk.v += pull
  clk.p = now - (pull * 1000) / rate
  return predicted
}

function frame() {
  rafId = requestAnimationFrame(frame)
  const el = canvas.value
  const video = props.video
  if (!el || !video) return

  const now = performance.now()
  const gap = now - lastNow
  lastNow = now
  if (gap > 1 && gap < 200) refreshGap = refreshGap * 0.9 + gap * 0.1
  const minInterval = costEma > 8 ? refreshGap * 2 : costEma > 4 ? refreshGap : 0
  if (now - lastDraw < minInterval) return
  lastDraw = now

  const ctx = el.getContext('2d')
  const t0 = now
  ctx.clearRect(0, 0, el.width, el.height)
  hits = []
  if (!props.enabled || !items.length) {
    costEma *= 0.9 // 空屏时衰减，弹幕密集过后能回到全速
    return
  }

  const t = smoothTime(now)
  // 时间大幅跳变（seek）就重建活跃集，正常播放只前推指针。
  if (!(t >= lastT && t - lastT < 1.5)) resetActive(t)
  lastT = t
  while (ptr < items.length && items[ptr].start <= t) {
    active.push(items[ptr])
    ptr++
  }
  if (active.length > 6000 || (active.length && active.some((i) => i.end < t))) {
    active = active.filter((i) => i.end >= t)
  }

  const scale = el.height / 1080
  const fontPx = Math.max(8, Math.round((props.fontSize > 0 ? props.fontSize : 50) * scale))
  const speed = props.speed > 0 ? props.speed : 1
  const areaRatio = Math.min(1, Math.max(0.25, props.area))
  // 弹幕保护区：上下各留一线不贴边（站长定的口味：1% 就够）。
  const safeTop = Math.round(el.height * 0.01)
  const safeBottom = Math.round(el.height * 0.01)
  const laneZone = Math.max(fontPx * 1.4, el.height * areaRatio - safeTop - safeBottom)
  let laneCount = Math.max(1, Math.floor(laneZone / (fontPx * 1.4)))
  if (props.lineCount > 0) laneCount = Math.min(laneCount, props.lineCount)

  ctx.globalAlpha = Math.min(1, Math.max(0.05, props.opacity))

  let drawn = 0
  const MAX_DRAWN = 240 // 同屏新入场上限，溢出丢最新的，保住帧率；已上轨的永不中途隐身
  const placedFixed = [] // 本帧 fixed 落位记录，后来者避让，杜绝同 y 叠罗汉

  for (const item of active) {
    if (!itemVisible(item)) continue

    const entry = bitmapFor(ctx, item, fontPx)
    const textWidth = entry.width
    let drawX
    let drawY

    if (item.mode === 'fixed') {
      drawX = item.x * scale - textWidth / 2
      const zone = item.zone === 'bottom' ? 'bottom' : 'top'
      if (item.adjY == null) {
        // ass 导出的 y 会撞车：首次落位时先钳进保护区，再与同区已放项重叠则往屏内方向逐行找空，找不到就丢。
        const step = fontPx * 1.35
        const dir = zone === 'bottom' ? -1 : 1
        let y = item.y * scale
        y = zone === 'bottom' ? Math.min(y, el.height - safeBottom - fontPx * 1.2) : Math.max(y, safeTop)
        let tries = 0
        const conflict = (yy) =>
          placedFixed.some((p) => p.zone === zone && Math.abs(p.y - yy) < fontPx * 1.15)
        while (conflict(y) && tries < 8) {
          y += dir * step
          tries++
        }
        if (conflict(y)) continue
        item.adjY = y
      }
      drawY = item.adjY
      placedFixed.push({ zone, y: drawY })
    } else {
      // 越长越快（B 站机制）：文本相对屏宽越长，飞行时长越短。
      const dur = Math.max(0.1, (item.end - item.start) / (1 + 0.35 * (textWidth / el.width)))
      const progress = Math.min(1, Math.max(0, ((t - item.start) * speed) / dur))
      if (item.mode === 'move') {
        drawX = (item.x + (0 - item.x) * progress) * scale
        // ass 自带 y 的滚动弹幕同样钳进保护区，不拿边缘当轨道。
        drawY = Math.min(Math.max((item.y ?? 0) * scale, safeTop), Math.max(safeTop, el.height - safeBottom - fontPx))
      } else {
        drawX = el.width - (el.width + textWidth) * progress
        if (item.lane === null) {
          if (drawn >= MAX_DRAWN) continue // 名额满：新项不上轨，下帧再试
          // 上一项的右边界必须完全驶出屏幕，才允许新项上轨；全忙则本轮不画，绝不叠字。
          let lane = -1
          for (let candidate = 0; candidate < laneCount; candidate++) {
            if ((lanes.get(candidate) ?? -Infinity) < el.width) {
              lane = candidate
              break
            }
          }
          if (lane < 0) continue
          item.lane = lane
        }
        lanes.set(item.lane, drawX + textWidth)
        drawY = safeTop + item.lane * fontPx * 1.4 + 2
        if (drawX + textWidth < 0) item.gone = true
      }
    }

    if (item.gone || drawX > el.width || drawX + textWidth < 0) continue
    const bl = drawX - entry.pad
    const bt = drawY - entry.padT
    ctx.drawImage(entry.canvas, bl, bt)
    if (item.badge && item.localId != null && item.canDelete !== false) {
      hits.push({ localId: item.localId, x: bl, y: bt, w: entry.width, h: entry.canvas.height })
    }
    drawn++
  }
  const cost = now - t0
  costEma = costEma ? costEma * 0.9 + cost * 0.1 : cost
}

// ---- 站内弹幕：点击命中出删除泡 ----
const pick = ref(null) // { id, left, top } CSS 像素

function onStageClick(e) {
  const el = canvas.value
  if (!el || !props.enabled) return
  if (pick.value && e.target?.closest?.('[data-dm-del]')) return // 点的是删除泡自身，交给按钮
  const rect = el.getBoundingClientRect()
  if (!pick.value) {
    const inStage = e.clientX >= rect.left && e.clientX <= rect.right && e.clientY >= rect.top && e.clientY <= rect.bottom
    if (!inStage) return
  }
  const dpr = el.width / Math.max(1, rect.width)
  const px = (e.clientX - rect.left) * dpr
  const py = (e.clientY - rect.top) * dpr
  const hit = hits.find((v) => px >= v.x && px <= v.x + v.w && py >= v.y && py <= v.y + v.h)
  if (hit) {
    e.stopPropagation()
    pick.value = { id: hit.localId, left: e.clientX - rect.left + 6, top: e.clientY - rect.top + 10 }
  } else {
    pick.value = null
  }
}

function removePicked() {
  if (!pick.value) return
  const id = pick.value.id
  emit('delete-local', id)
  items = items.filter((it) => it.localId !== id)
  active = active.filter((it) => it.localId !== id)
  hits = hits.filter((it) => it.localId !== id)
  pick.value = null
}

// 站内弹幕注入：按发弹幕时记录的时点起飞（刷新重进照旧位置飘）；无时点的当场即发。
const injectedLocal = new Set()
function injectLocal(all) {
  const t = props.video?.currentTime ?? 0
  for (const li of props.localItems ?? []) {
    if (!all && injectedLocal.has(li.id)) continue
    if (!li.text) continue
    injectedLocal.add(li.id)
    const start = Number.isFinite(li.t) ? li.t : t
    items.push({ start, end: start + 12, mode: 'scroll', x: null, y: null, color: '#FFFFFF', text: li.text, badge: true, fromOwner: li.fromOwner === true, localId: li.id, canDelete: li.canDelete !== false, lane: null, gone: false, width: null, bmp: undefined })
  }
}
watch(() => props.localItems.length, () => injectLocal(false))

async function load() {
  items = []
  lanes.clear()
  invalidateBitmaps()
  ptr = 0
  active = []
  lastT = -1
  clk.ready = false
  injectedLocal.clear()
  pick.value = null
  if (!props.src) {
    injectLocal(true)
    return
  }
  try {
    const res = await fetch(props.src)
    if (!res.ok) return
    items = parseAss(await res.text())
  } catch {
    items = []
  }
  injectLocal(true)
}

watch(() => [props.src, props.enabled], load)

onMounted(() => {
  resize()
  observer = new ResizeObserver(resize)
  if (canvas.value) observer.observe(canvas.value)
  load()
  rafId = requestAnimationFrame(frame)
  const stage = canvas.value?.parentElement
  if (stage) stage.addEventListener('click', onStageClick, true)
})

onBeforeUnmount(() => {
  cancelAnimationFrame(rafId)
  observer?.disconnect()
  const stage = canvas.value?.parentElement
  if (stage) stage.removeEventListener('click', onStageClick, true)
})
</script>

<template>
  <canvas ref="canvas" class="pointer-events-none absolute inset-0 h-full w-full"></canvas>
  <button
    v-if="pick"
    data-dm-del
    class="absolute z-20 flex items-center gap-1 rounded-full border border-white/15 bg-[#1b1d23]/95 px-2.5 py-1 text-[11px] text-white/85 shadow-lg backdrop-blur transition hover:border-rose-400/60 hover:text-rose-300"
    :style="{ left: `${pick.left}px`, top: `${pick.top}px` }"
    @click.stop="removePicked"
  >
    <svg viewBox="0 0 24 24" class="h-3 w-3" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round">
      <path d="M4 7h16M9 7V5a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2m3 0-1 13a1 1 0 0 1-1 1H8a1 1 0 0 1-1-1L6 7" />
    </svg>
    删除这条
  </button>
</template>
