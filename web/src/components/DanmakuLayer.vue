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
  localItems: { type: Array, default: () => [] }, // 站内弹幕 [{id, text}]：当场飘过，不上 B 站
})

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
const bmpCache = new Map() // `${text}|${color}|${fontPx}` → { canvas, width }
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

function bitmapFor(ctx, item, fontPx) {
  if (item.bmp && item.bmpFont === fontPx && item.bmpStyle === styleTag()) return item.bmp
  const key = `${item.text}|${item.color}|${fontPx}|${styleTag()}`
  let entry = bmpCache.get(key)
  if (!entry) {
    const font = fontStr(fontPx)
    ctx.font = font
    // pad 要够宽：字面顶部在 baseline top 之上还有描边半宽和字体 ascent 余量，不够就被削顶。
    const pad = Math.max(5, fontPx / 3.2)
    const w = Math.max(1, Math.ceil(ctx.measureText(item.text).width + pad * 2))
    const h = Math.max(1, Math.ceil(fontPx * 1.35 + pad * 2))
    const off = document.createElement('canvas')
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
    entry = { canvas: off, width: w, pad }
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
  return `${props.family}|${props.bold ? 'b' : 'n'}|${props.stroke}`
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

let lastDraw = 0
const DRAW_INTERVAL = 33 // 弹幕层 30fps 足够顺滑，省下一半开销

function frame() {
  rafId = requestAnimationFrame(frame)
  const el = canvas.value
  const video = props.video
  if (!el || !video) return

  const now = performance.now()
  if (now - lastDraw < DRAW_INTERVAL) return
  lastDraw = now

  const ctx = el.getContext('2d')
  ctx.clearRect(0, 0, el.width, el.height)
  if (!props.enabled || !items.length) return

  const t = video.currentTime
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
  let laneCount = Math.max(1, Math.floor((el.height * areaRatio) / (fontPx * 1.4)))
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
        // ass 导出的 y 会撞车：首次落位时若与同区已放项重叠，往屏内方向逐行找空，找不到就丢。
        const step = fontPx * 1.35
        const dir = zone === 'bottom' ? -1 : 1
        let y = item.y * scale
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
        drawY = (item.y ?? 0) * scale
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
        drawY = item.lane * fontPx * 1.4 + 4
        if (drawX + textWidth < 0) item.gone = true
      }
    }

    if (item.gone || drawX > el.width || drawX + textWidth < 0) continue
    ctx.drawImage(entry.canvas, drawX - entry.pad, drawY - entry.pad)
    drawn++
  }
}

// 站内弹幕注入：发送即从当前播放时刻起飞；src 重载（开关弹幕/换 P）后全量重飘一轮，不丢刚发的弹幕。
const injectedLocal = new Set()
function injectLocal(all) {
  const t = props.video?.currentTime ?? 0
  for (const li of props.localItems ?? []) {
    if (!all && injectedLocal.has(li.id)) continue
    if (!li.text) continue
    injectedLocal.add(li.id)
    items.push({ start: t, end: t + 12, mode: 'scroll', x: null, y: null, color: '#FFFFFF', text: li.text, lane: null, gone: false, width: null, bmp: undefined })
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
})

onBeforeUnmount(() => {
  cancelAnimationFrame(rafId)
  observer?.disconnect()
})
</script>

<template>
  <canvas ref="canvas" class="pointer-events-none absolute inset-0 h-full w-full"></canvas>
</template>
