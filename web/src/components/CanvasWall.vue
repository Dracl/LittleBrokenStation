<script setup>
// 馆藏壁画布内核：把一组视频按种子随机散布成拼贴小墙。
// 交互规矩（与站长对齐过的版本）：
//   · 窗内拖拽 = 平移（Myriad「拖卡片或空白」同款：点 4px 内算点击、之外算拖，不抢 RouterLink）
//   · Ctrl/⌘ + 滚轮 = 缩放；普通滚轮一律放行给页面（窗口长在长页中段，不能劫持访客）
//   · fullscreen 时滚轮也归墙（页面滚动已退场，滚轮就是缩放）；方向键平移只在全屏启用
//   · 触屏嵌入态走 touch-pan-y：竖划归页面滚，横划归墙平移
// 位置算法：mulberry32 吃 (seed, videoId)，同种子同位置——刷新不跳墙，洗牌只挪整体重排。
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'
import { coverThumb } from '../utils/cover'
import { claimPreview, previewHoverEnter, previewHoverLeave, releasePreview } from '../utils/previewHover'

const props = defineProps({
  items: { type: Array, default: () => [] }, // {videoId, bvid, title, localCover, coverUrl, source}
  seed: { type: Number, default: 1 },
  fullscreen: { type: Boolean, default: false },
  hasMore: { type: Boolean, default: false }, // 库里还有没上墙的货：拖到边界时喊父家续拉
})

const emit = defineEmits(['loadMore'])

const CARD_W = 168
const CARD_H = 105
const ZOOM_MIN = 0.35
const ZOOM_MAX = 2.2
const FIT_SCALE = 0.85 // 开墙/复位时的安家倍率：卡面可读，环墙从中心往外漫开

const stage = ref(null)
const view = ref({ x: 0, y: 0, scale: 1 })
const stageSize = ref({ w: 0, h: 0 }) // 窗口尺幅：fit/resize 时刷新，供裁剪与预取量视口
const dragging = ref(false)
const brokenCovers = ref(new Set()) // 本地封面 404 时热链兑底一次，再坏就留深色底不摆破图

function mulberry32(seed) {
  let h = seed >>> 0
  return () => {
    h = (h + 0x6d2b79f5) >>> 0
    let t = Math.imul(h ^ (h >>> 15), 1 | h)
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}

// 世界坐标散布（Apple Watch 菜单的蜂窝骨架）：竖直列、邻列纵向错半格互相咬合的斜切方格阵，
// 六边螺旋环序从中心往外发——等距等隙天生不重叠，不需要松弛；第几张坐哪个巢只由序号决定，
// 续页只在更外环筑巢，已上墙的卡铟位不动。入场顺环序，天然从中心点亮到四周。
const GAP_X = 44 // 列间隙：站长在调节台上定的气口（2026-11-01 从 52 收到 44，墙更密）
const GAP_Y = 48 // 行间隙：邻列错半格咬合（同批从 55 收到 48）
const PITCH_X = CARD_W + GAP_X
const PITCH_Y = CARD_H + GAP_Y
const HEX_DIRS = [[1, 0], [1, -1], [0, -1], [-1, 0], [-1, 1], [0, 1]] // 轴向坐标六向
const HEX_START = HEX_DIRS[4] // 起环方位：左下，绕环顺时针铺开

function hexSpiral(n) {
  const pts = [[0, 0]]
  let ring = 0
  while (pts.length < n) {
    ring++
    let u = HEX_START[0] * ring
    let v = HEX_START[1] * ring
    for (let side = 0; side < 6; side++) {
      const [du, dv] = HEX_DIRS[side]
      for (let step = 0; step < ring; step++) {
        pts.push([u, v])
        u += du
        v += dv
      }
    }
  }
  // 环优先重排：先按六边环距分层，同环按屏幕角度顺时针扫——
  // 入场序由「螺旋扫街」变成「一圈圈向外涨的波纹」；坑位仍只由序号决定，分页铟定不破
  const PX = PITCH_X
  const PY = PITCH_Y
  const dist = ([u, v]) => (Math.abs(u) + Math.abs(v) + Math.abs(u + v)) / 2
  const angle = ([u, v]) => Math.atan2(PY * (v + u / 2), PX * u)
  pts.sort((a, b) => dist(a) - dist(b) || angle(a) - angle(b))
  return pts
}

const WALL_MAX = 400
const nestCache = new Map() // 巢位表与数据无关，算一次永久复用

function buildNests() {
  if (nestCache.size) return nestCache.get(0)
  const rings = hexSpiral(WALL_MAX)
  // 斜切映射：列距固定、每列往下错半格——就是蜂窝的咬合几何
  const nests = rings.map(([u, v]) => ({ x: u * PITCH_X, y: PITCH_Y * (v + u / 2) }))
  nestCache.set(0, nests)
  return nests
}

const placed = computed(() => {
  const n = props.items.length
  if (!n) return []
  const nests = buildNests()
  return props.items.map((it, i) => {
    const rand = mulberry32(((props.seed * 2654435761) ^ Math.imul(it.videoId || i + 1, 40503)) >>> 0)
    const jx = (rand() - 0.5) * 8 // 微量抖动：巢是正的，卡面有一口呼吸就好
    const jy = (rand() - 0.5) * 8
    const rot = (rand() - 0.5) * 3 // 蜂窝是整齐的，只留 ±1.5° 的手贴味
    const p = nests[i] || nests[nests.length - 1]
    return {
      ...it,
      // 巢位是卡心：左上角回退半张卡
      x: p.x + jx - CARD_W / 2,
      y: p.y + jy - CARD_H / 2,
      rot,
    }
  })
})

const worldStyle = computed(() => ({
  transform: `translate3d(${view.value.x}px, ${view.value.y}px, 0) scale(${view.value.scale})`,
}))

// 世界尺幅：裁剪、预取、居中三处共用
const worldBounds = computed(() => {
  const ps = placed.value
  if (!ps.length) return null
  let minX = Infinity
  let minY = Infinity
  let maxX = -Infinity
  let maxY = -Infinity
  for (const p of ps) {
    minX = Math.min(minX, p.x)
    minY = Math.min(minY, p.y)
    maxX = Math.max(maxX, p.x + CARD_W)
    maxY = Math.max(maxY, p.y + CARD_H)
  }
  return { minX, minY, maxX, maxY }
})

// 上墙闸门（懒挂载 · 只进不出）：新露脸的卡才挂进 DOM 并演一次入场，挂过的永不拆。
// Vue 键控节点不销毁，CSS 入场动画就天然只演一遍——上一版每次拖拽边缘卡集体重播
// 模糊淡入、拖来拖去像频闪的病根，就出在「随视野拆了重建」上（对标 Myriad 的 claimCanvasCardEnter 记账）。
const CULL_MARGIN = 300
const mounted = ref(new Set())
function syncMounted() {
  const b = worldBounds.value
  const { w, h } = stageSize.value
  if (!b || !w || !h) return
  const { x, y, scale } = view.value
  const m = CULL_MARGIN / scale
  const l = -x / scale - m
  const t = -y / scale - m
  const r = (w - x) / scale + m
  const bot = (h - y) / scale + m
  // 先收集再换 Set：拖拽的绝大多数帧没有新人上桌，不能每帧白克隆一次几百条的集合
  let toAdd = null
  for (const p of placed.value) {
    if (mounted.value.has(p.videoId)) continue
    if (p.x < r && p.x + CARD_W > l && p.y < bot && p.y + CARD_H > t) {
      if (!toAdd) toAdd = []
      toAdd.push(p.videoId)
    }
  }
  if (toAdd) mounted.value = new Set([...mounted.value, ...toAdd]) // 只在真有新人时换引用，免得每帧空转重渲染
}
const shown = computed(() => placed.value.filter((p) => mounted.value.has(p.videoId)))

// ─── 焦距连续缩放（Apple Watch 蜂窝菜单的机理，对标 Myriad 的 paintCanvasCardFocus）───
// 每张卡按「卡心离视口中心的世界距离」算自己的 scale：圈内 1.2、圈外 0.6，smoothstep 缓动；
// zIndex 跟着焦距走。测距用世界像素而非窗框归一化——同一巢位在全屏/嵌入/任意缩放下倍率恒定，
// 不会再出现「小窗口纵向半幅浅、满屏卡被误缩、间隙显大」的窗框依赖症。
// 2026-11-01 站长定档：1.2 / 0.6 / 1.3（旧值 1.3 / 0.8 / 1.65）——中心更突出、景深拉得更开。
const FOCUS_MIN = 0.6
const FOCUS_MAX = 1.2
const FOCUS_UNIT = 600 // 半径标尺：EXTENT 1.0 ≈ 600 世界像素（约三个巢位）
const FOCUS_EXTENT = 1.3
const FOCUS_WRITE_EPS = 0.004

const cardEls = new Map() // videoId -> 卡片元素
const focusMemo = new Map() // videoId -> { f, z } 上次写进样式里的值
const refCbs = new Map() // 每个 id 固定一个 ref 回调，免得重渲染时来回拆挂

const centers = computed(() => {
  const m = new Map()
  for (const p of placed.value) m.set(p.videoId, { cx: p.x + CARD_W / 2, cy: p.y + CARD_H / 2 })
  return m
})

function registerCard(id, comp) {
  // :ref 挂在 RouterLink 组件上，Vue 递来的是组件实例——不剥出 $el，
  // paintFocus 第一张卡就碰着 undefined.style 抛错，整条焦距渲染静默停摆（上几轮「不会放大缩小」的病根）
  const el = comp instanceof HTMLElement ? comp : comp && comp.$el instanceof HTMLElement ? comp.$el : null
  if (el) {
    cardEls.set(id, el)
    focusMemo.delete(id) // 新节点身上还空着，首帧无论如何补写一次焦距
    scheduleFocusPaint()
  } else {
    cardEls.delete(id)
    focusMemo.delete(id)
  }
}

function cardRef(id) {
  let cb = refCbs.get(id)
  if (!cb) {
    cb = (el) => registerCard(id, el)
    refCbs.set(id, cb)
  }
  return cb
}

function focusScaleAt(cx, cy) {
  const { w, h } = stageSize.value
  if (!w || !h) return FOCUS_MAX
  const { x, y, scale } = view.value
  // 视口中心落回世界坐标，量卡心到它的欧氏距离；半径锁世界像素，与窗框、缩放无关
  const wcx = (w / 2 - x) / scale
  const wcy = (h / 2 - y) / scale
  const d = Math.hypot(cx - wcx, cy - wcy) / (FOCUS_UNIT * FOCUS_EXTENT)
  const p = Math.min(1, d)
  const e = p * p * (3 - 2 * p) // smoothstep：两头缓、中段陡，焦距过渡不带硬边
  return FOCUS_MAX - (FOCUS_MAX - FOCUS_MIN) * e
}

let focusScheduled = false
function scheduleFocusPaint() {
  if (focusScheduled) return
  focusScheduled = true
  requestAnimationFrame(() => {
    focusScheduled = false
    paintFocus()
  })
}

function paintFocus() {
  const cs = centers.value
  for (const [id, el] of cardEls) {
    const c = cs.get(id)
    if (!c) continue
    const f = focusScaleAt(c.cx, c.cy)
    const z = Math.round(f * 100)
    const last = focusMemo.get(id)
    if (last && Math.abs(f - last.f) < FOCUS_WRITE_EPS && z === last.z) continue
    focusMemo.set(id, { f, z })
    el.style.setProperty('--focus', f.toFixed(3))
    el.style.zIndex = String(z)
  }
}

// 预取信号（对标 Myriad 的 preloadBoundary）：视口探过已铺世界的边缘就喊续拉；
// 带冷却防抖，父家另有 loading 闸，双保险不刷接口
let lastEdgeEmit = 0
let burstRounds = 0 // 本轮自动补货已充几轮（视野被盖住或换料时归零）
const BURST_MAX_ROUNDS = 6
function checkEdge() {
  syncMounted() // 每次视野动一下：先补挂新露脸的，再探要不要续拉
  scheduleFocusPaint() // 视野一动，焦距整墙重算（rAF 合帧，一帧最多刷一遍）
  if (!props.hasMore) return
  const b = worldBounds.value
  const { w, h } = stageSize.value
  if (!b || !w || !h) return
  const { x, y, scale } = view.value
  // 提前量：约一张半卡的缓冲起步，绝不等到看见荒野才补货（对标 Myriad 拿视口半径×1.25 探边界）
  const slack = Math.max(420, Math.hypot(w, h) * 0.45) / scale
  const beyond =
    -x / scale < b.minX - slack ||
    (w - x) / scale > b.maxX + slack ||
    -y / scale < b.minY - slack ||
    (h - y) / scale > b.maxY + slack
  if (!beyond) return
  // 开墙/拖拽后的自动补货：连充几轮直到视口被已铺世界盖住（对标 Myriad 的 preloadBoundary 自喂），
  // 轮数封顶防失控；充完一轮 items 变长会再探一次，循环自嗂。手动拖拽仍吃 500ms 冷却，不刷接口。
  const now = Date.now()
  const inBurst = burstRounds < BURST_MAX_ROUNDS
  if (!inBurst && now - lastEdgeEmit < 500) return
  lastEdgeEmit = now
  burstRounds += 1
  emit('loadMore')
}

function coverOf(it) {
  return it.localCover || (it.coverUrl ? coverThumb(it.coverUrl, 480) : '')
}

function onCoverError(it) {
  brokenCovers.value = new Set([...brokenCovers.value, it.videoId])
}

// ─── 悬停预览：与首页 VideoCard 同一套全站契约（previewHover 单路令牌 + 350ms 起播 + 200ms 原地续播）───
// 墙上有几百张卡，但 preload=none + 单路令牌保证同一时刻只有一路在拉流；封面 img 全程不卸，预览只是盖上去的一层。
const PREVIEW_ARM_MS = 350 // 鼠标停够才起播，路过卡阵的手指不算想看
const PREVIEW_RESET_MS = 200 // 离场窗口内回来就原地续播，不重新从 0 拉流
const reduceMotion = !!window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches

const previewShown = ref(new Set()) // 首帧到位才淡入，避免黑帧裸切
const previewGaveUp = ref(new Set()) // 出错就彻底放弃这张卡的预览层，绝不重试
const previewEls = new Map() // videoId → <video>，随卡片挂载/卸载同生共死
const previewTokens = new Map() // 停车票函数按 id 缓存，身份稳定
const previewTimers = new Map() // id → { arm, reset }
const hoverHeld = new Set() // 已向全站报备悬停的卡，配对计数不重不漏
let activePreview = null

function previewRef(id) {
  let cb = previewRefCbs.get(id)
  if (!cb) {
    cb = (el) => {
      if (el) previewEls.set(id, el)
      else previewEls.delete(id)
    }
    previewRefCbs.set(id, cb)
  }
  return cb
}
const previewRefCbs = new Map()

function previewToken(id) {
  let t = previewTokens.get(id)
  if (!t) {
    t = () => stopCard(id, true)
    previewTokens.set(id, t)
  }
  return t
}

function timersFor(id) {
  let t = previewTimers.get(id)
  if (!t) {
    t = {}
    previewTimers.set(id, t)
  }
  return t
}

function markShown(id, on) {
  const cur = previewShown.value
  if (cur.has(id) === on) return
  const next = new Set(cur)
  if (on) next.add(id)
  else next.delete(id)
  previewShown.value = next
}

function stopCard(id, resetTime) {
  if (activePreview === id) activePreview = null
  markShown(id, false)
  const el = previewEls.get(id)
  if (!el) return
  try {
    el.pause()
  } catch {
    /* 本来就没在播 */
  }
  if (resetTime) {
    try {
      el.currentTime = 0
    } catch {
      /* 流还没 ready，归零没意义 */
    }
  }
}

function giveUpPreview(id) {
  const t = timersFor(id)
  if (t.arm) {
    clearTimeout(t.arm)
    t.arm = null
  }
  if (t.reset) {
    clearTimeout(t.reset)
    t.reset = null
  }
  releasePreview(previewToken(id))
  stopCard(id, false)
  previewGaveUp.value = new Set([...previewGaveUp.value, id])
}

function onPreviewEnter(it) {
  const id = it.videoId
  // movedFar 不兼这份差：它只为吞掉拖拽收尾那一发 click 而活，会一直 true 到下次按下——
  // 拿它当预览的闸，就是「拖过一次画布，预览再也不亮」的病根（拖中的抑制交给 dragging，
  // 松手后那一小会儿由 dragCalmUntil 收尾：指针捕获一释放常会给卡补一发 enter，别让它抢播）。
  if (!it.preview || previewGaveUp.value.has(id) || reduceMotion || dragging.value || performance.now() < dragCalmUntil) return
  if (!hoverHeld.has(id)) {
    hoverHeld.add(id)
    previewHoverEnter()
  }
  const t = timersFor(id)
  if (t.reset) {
    clearTimeout(t.reset) // 窗口内回访：保住播放位，不回头重拉
    t.reset = null
  }
  if (t.arm) clearTimeout(t.arm)
  t.arm = setTimeout(() => {
    t.arm = null
    const el = previewEls.get(id)
    if (!el) return
    claimPreview(previewToken(id)) // 全站只许一路在播：先让上一路停车归零
    el.muted = true // 属性之外再钉一次，免得 autoplay policy 卡住无声起播
    activePreview = id
    const p = el.play()
    if (p && typeof p.catch === 'function') p.catch(() => giveUpPreview(id))
  }, PREVIEW_ARM_MS)
}

function onPreviewLeave(it) {
  const id = it.videoId
  if (hoverHeld.has(id)) {
    hoverHeld.delete(id)
    previewHoverLeave()
  }
  if (!it.preview || previewGaveUp.value.has(id)) return
  const t = timersFor(id)
  if (t.arm) {
    clearTimeout(t.arm)
    t.arm = null
  }
  releasePreview(previewToken(id))
  stopCard(id, false) // 立即按停（淡回封面）
  if (t.reset) clearTimeout(t.reset)
  t.reset = setTimeout(() => {
    t.reset = null
    const el = previewEls.get(id)
    if (el && el.currentTime) {
      try {
        el.currentTime = 0
      } catch {
        /* 流未 ready */
      }
    }
  }, PREVIEW_RESET_MS)
}

function releaseAllPreviews() {
  for (const t of previewTimers.values()) {
    if (t.arm) clearTimeout(t.arm)
    if (t.reset) clearTimeout(t.reset)
  }
  previewTimers.clear()
  for (const id of hoverHeld) previewHoverLeave()
  hoverHeld.clear()
  if (activePreview !== null) releasePreview(previewToken(activePreview))
}

// ─── 拖拽平移：按下先观望，越过 4px 阈值才接管为拖，并把指针捕获到舞台上 ───
let dragOrigin = { x: 0, y: 0 }
let pressAt = { x: 0, y: 0 }
let movedFar = false
let activePointer = null
// 松手后的“冷静期”：拖拽收尾那阵子补发的 enter 不算想看（比 PREVIEW_ARM_MS 短，不致于误伤真在看的人）
const DRAG_CALM_MS = 220
let dragCalmUntil = 0

function onPointerDown(e) {
  poke() // 手一伸就算「有人在用」：漫游立刻叫停，重计 10 秒
  if (e.button !== 0 || e.ctrlKey || e.metaKey) return
  dragging.value = true
  movedFar = false
  pressAt = { x: e.clientX, y: e.clientY }
  dragOrigin = { x: view.value.x, y: view.value.y }
  activePointer = e.pointerId
}

function onPointerMove(e) {
  if (!dragging.value || e.pointerId !== activePointer) {
    // 没在拖也算有人在用：指针真的挪了一段就算一次输入（原地不动不算，否则手搭在鼠标上永远开不了漂）
    if (!dragging.value && Math.hypot(e.clientX - lastPing.x, e.clientY - lastPing.y) > 8) poke()
    lastPing = { x: e.clientX, y: e.clientY }
    return
  }
  const dx = e.clientX - pressAt.x
  const dy = e.clientY - pressAt.y
  if (!movedFar && Math.hypot(dx, dy) < 4) return
  if (!movedFar) {
    movedFar = true
    // 拖成气候了：还在排队起播的卡一律撕队，免得拖途中某张自己唱起来
    for (const t of previewTimers.values()) {
      if (t.arm) {
        clearTimeout(t.arm)
        t.arm = null
      }
    }
    // 拖成气候了才捕获指针：普通点击不受捕获干扰，RouterLink 该跳就跳
    if (activePreview !== null) {
      // 一拖就走说明不是在看预览：按停当前路，也免得拖途中 pointerenter 误起新盘
      releasePreview(previewToken(activePreview))
      stopCard(activePreview, false)
    }
    try {
      stage.value?.setPointerCapture(activePointer)
    } catch {
      /* 捕获失败不拦平移 */
    }
  }
  view.value = { ...view.value, x: dragOrigin.x + dx, y: dragOrigin.y + dy }
  checkEdge()
}

function onPointerUp(e) {
  if (!dragging.value || e.pointerId !== activePointer) return
  dragging.value = false
  dragCalmUntil = performance.now() + DRAG_CALM_MS // 拖完就进冷静期；movedFar 继续留着吞那一发 click
  try {
    stage.value?.releasePointerCapture(activePointer)
  } catch {
    /* 已随 up 自动释放 */
  }
  activePointer = null
}

// 拖过阈值的松手落在卡上：当平移收尾吃点击，别误闯放映厅
function onCardClick(e) {
  if (movedFar) {
    e.preventDefault()
    e.stopPropagation()
  }
}

// ─── 缩放：锚住光标下的世界点 ───
function zoomAt(clientX, clientY, factor) {
  const el = stage.value
  if (!el) return
  poke() // 滚轮缩放与工具条±都算一次输入
  const rect = el.getBoundingClientRect()
  const px = clientX - rect.left
  const py = clientY - rect.top
  const old = view.value.scale
  const next = Math.min(ZOOM_MAX, Math.max(ZOOM_MIN, old * factor))
  if (next === old) return
  const k = next / old
  view.value = { scale: next, x: px - (px - view.value.x) * k, y: py - (py - view.value.y) * k }
  checkEdge()
}

function zoomIn() {
  const el = stage.value
  if (!el) return
  const r = el.getBoundingClientRect()
  zoomAt(r.left + r.width / 2, r.top + r.height / 2, 1.25)
}

function zoomOut() {
  const el = stage.value
  if (!el) return
  const r = el.getBoundingClientRect()
  zoomAt(r.left + r.width / 2, r.top + r.height / 2, 1 / 1.25)
}

function onWheel(e) {
  const zoomGesture = e.ctrlKey || e.metaKey || props.fullscreen
  if (!zoomGesture) return // 普通滚轮放行：页面滚动是大爷
  e.preventDefault()
  zoomAt(e.clientX, e.clientY, Math.exp(-e.deltaY * 0.0016))
}

// ─── 定位安家：以螺旋中心（第一张卡）为家，用可读倍率 FIT_SCALE 居中——
// 墙会随续拉无限长大，「把整墙塞进窗口」在几百张规模下会把卡碎成邮票，
// 所以家和参考站一样是「落在中心、看得清」，剩下的交给拖拽与自动补货 ───
function fit() {
  poke() // 复位 / 洗牌 / 进出全屏都是人在使唤起这面墙
  const el = stage.value
  const b = worldBounds.value
  // 量不到尺就不硬算：窗口没显示/尺寸没落定时量出 0，居中坐标会全歪到角上去
  if (!el || el.clientWidth < 10 || el.clientHeight < 10) {
    if (!b) view.value = { x: 0, y: 0, scale: 1 }
    return
  }
  stageSize.value = { w: el.clientWidth, h: el.clientHeight }
  const scale = Math.min(1, Math.max(ZOOM_MIN, FIT_SCALE))
  const anchor = placed.value[0]
  const cx = anchor ? anchor.x + CARD_W / 2 : 0
  const cy = anchor ? anchor.y + CARD_H / 2 : 0
  view.value = {
    scale,
    x: el.clientWidth / 2 - cx * scale,
    y: el.clientHeight / 2 - cy * scale,
  }
  checkEdge()
}

// 全屏态的方向键平移：只在墙独占屏幕时接管，免得跟页面滚键打架
function onKeyDown(e) {
  if (!props.fullscreen) return
  if (e.target instanceof Element && e.target.closest('input, textarea, select, [contenteditable]')) return
  const step = 90
  const pans = { ArrowLeft: [step, 0], ArrowRight: [-step, 0], ArrowUp: [0, step], ArrowDown: [0, -step] }
  const pan = pans[e.key]
  if (!pan) return
  e.preventDefault()
  poke()
  view.value = { ...view.value, x: view.value.x + pan[0], y: view.value.y + pan[1] }
  checkEdge()
}

// ─── 闲时漫游：静置 10 秒没人碰墙，自己慢慢漂 ───
// 脾气是「永远让位给真人」：任何一次真输入（按下、拖动、滚轮缩放、方向键、复位）当场打断本段并把计时归零；
// 指针停在某张卡上（悬停预览正播）也不漂，别把人正在看的东西抽走；页签在后台、系统要「减少动态效果」，一律不漂。
// 漂的时候只补挂新露脸的卡、不喊续拉：人不在看，闲时拉货太冒失。
const IDLE_MS = 10000 // 静置多久开漂
const DRIFT_SPEED = 42 // 名义速度（屏幕像素/秒）：慢到不抢戏，又看得出墙在走
const DRIFT_MIN_S = 1.6 // 单段最短几秒：近处也慢慢挪，不当弹珠
const DRIFT_MAX_S = 9 // 单段最长封顶：远端也不让人等半天
const DRIFT_HOLD_MS = 2600 // 到点歇口气，再挑下一段
const DRIFT_EDGE_PAD = 140 // 世界里缘至少留这么多像素在窗内：漂是巡墙，不是把墙推走

let idleTimer = null
let driftTimer = null
let driftRaf = null
let drifting = false
let lastPing = { x: -9999, y: -9999 }

// 真输入一律先掐掉漫游，再重新计 10 秒（未开漂时也由它续表，洗牌/复位后不会刚安家就被自己拖走）
function poke() {
  drifting = false
  if (driftRaf) {
    cancelAnimationFrame(driftRaf)
    driftRaf = null
  }
  clearTimeout(driftTimer)
  clearTimeout(idleTimer)
  idleTimer = setTimeout(startDrift, IDLE_MS)
}

function startDrift() {
  clearTimeout(idleTimer)
  if (reduceMotion) return // 这把尺子文件里本来就有（悬停预览也认它），不另开一份
  if (dragging.value || activePreview !== null || document.hidden) {
    idleTimer = setTimeout(startDrift, 2000) // 手正搭在卡上/页签没在眼前：绕一绕再来
    return
  }
  drifting = true
  scheduleLeg(0)
}

function scheduleLeg(delay) {
  clearTimeout(driftTimer)
  driftTimer = setTimeout(startLeg, delay)
}

function startLeg() {
  if (!drifting) return
  if (dragging.value || activePreview !== null) {
    scheduleLeg(1500)
    return
  }
  const el = stage.value
  const b = worldBounds.value
  if (!el || !b || el.clientWidth < 10 || document.hidden) {
    scheduleLeg(1200) // 窗口没量出尺 / 页签切走了：等下一班
    return
  }
  const ps = placed.value
  if (!ps.length) {
    scheduleLeg(2000)
    return
  }
  const w = el.clientWidth
  const h = el.clientHeight
  const { scale } = view.value
  // 目标 = 随机挑一张卡当「这一眼想看的东西」，再故意偏开中心一点，漂得像人在逛而不是对准
  const p = ps[Math.floor(Math.random() * ps.length)]
  const raw = {
    x: w / 2 - (p.x + CARD_W / 2) * scale + (Math.random() - 0.5) * w * 0.3,
    y: h / 2 - (p.y + CARD_H / 2) * scale + (Math.random() - 0.5) * h * 0.3,
  }
  const clamp = (v, lo, hi) => (hi < lo ? (lo + hi) / 2 : Math.min(hi, Math.max(lo, v)))
  const to = {
    x: clamp(raw.x, DRIFT_EDGE_PAD - b.maxX * scale, w - DRIFT_EDGE_PAD - b.minX * scale),
    y: clamp(raw.y, DRIFT_EDGE_PAD - b.maxY * scale, h - DRIFT_EDGE_PAD - b.minY * scale),
  }
  const from = { x: view.value.x, y: view.value.y }
  const dist = Math.hypot(to.x - from.x, to.y - from.y)
  if (dist < 30) {
    scheduleLeg(DRIFT_HOLD_MS) // 已经在这附近，歇会儿重挑一个
    return
  }
  const dur = Math.min(DRIFT_MAX_S, Math.max(DRIFT_MIN_S, dist / DRIFT_SPEED)) * 1000
  const t0 = performance.now()
  const tick = (now) => {
    if (!drifting) return
    if (dragging.value || activePreview !== null || document.hidden) {
      scheduleLeg(DRIFT_HOLD_MS) // 有人先动了手 / 正盯着某张卡 / 切走了：这段作废
      return
    }
    const t = Math.min(1, (now - t0) / dur)
    const e = t * t * (3 - 2 * t) // smoothstep：起步与停步都不硬甩
    view.value = { ...view.value, x: from.x + (to.x - from.x) * e, y: from.y + (to.y - from.y) * e }
    syncMounted() // 只补挂新露脸的，不 checkEdge：免得一漂就顺手拉六十张回来
    scheduleFocusPaint() // 视野一动，焦距得跟着重算
    if (t < 1) driftRaf = requestAnimationFrame(tick)
    else scheduleLeg(DRIFT_HOLD_MS)
  }
  driftRaf = requestAnimationFrame(tick)
}

function onVisibility() {
  if (document.hidden) poke() // 切走：停漂并重新计时
  else poke() // 切回来：给足 10 秒，别一返场就自己乱走
}

onMounted(() => {
  window.addEventListener('keydown', onKeyDown)
  window.addEventListener('resize', onResize)
  document.addEventListener('visibilitychange', onVisibility)
  poke() // 上桌就续表：静置满 10 秒才开始逛
})
onBeforeUnmount(() => {
  window.removeEventListener('keydown', onKeyDown)
  window.removeEventListener('resize', onResize)
  document.removeEventListener('visibilitychange', onVisibility)
  clearTimeout(idleTimer)
  clearTimeout(driftTimer)
  if (driftRaf) cancelAnimationFrame(driftRaf)
  releaseAllPreviews() // 拆铺前先收摊：停播、清计时器、退还悬停报备与单路令牌
})

let resizeTimer = null
function onResize() {
  clearTimeout(resizeTimer)
  resizeTimer = setTimeout(() => {
    const el = stage.value
    if (el) stageSize.value = { w: el.clientWidth, h: el.clientHeight }
    fit()
  }, 160)
}

// 构图时机分两派：初次上桌/洗牌/进出全屏才重新居中；续页只补尺量、探边缘——
// 续拉时用户的视野不能被子弹性地拽回中心（Myriad 的分页也绝不改动已铺位置）
const fitted = ref(false)
let prevLen = -1
watch(
  () => [props.seed, props.fullscreen],
  () => nextTick(fit),
  { flush: 'post' },
)
watch(
  () => props.items.length,
  (n) => {
    nextTick(() => {
      if (!n) return
      // 只有「在现有基础上变多」才算续页；倒着来/同长都当新家（换料重开另有 CanvasWall 重建兑）
      const isAppend = prevLen >= 0 && n > prevLen
      prevLen = n
      if (!fitted.value || !isAppend) {
        fit()
        fitted.value = true
        burstRounds = 0
        return
      }
      const el = stage.value
      if (el) stageSize.value = { w: el.clientWidth, h: el.clientHeight }
      checkEdge() // 新货上桌：顺带再探一轮，还空着就接着充
    })
  },
  { immediate: true, flush: 'post' },
)

defineExpose({ zoomIn, zoomOut, fit })
</script>

<template>
  <div
    ref="stage"
    class="wall-stage absolute inset-0 select-none overflow-hidden"
    :class="[dragging ? 'cursor-grabbing' : 'cursor-grab', fullscreen ? 'touch-none' : 'touch-pan-y']"
    @pointerdown="onPointerDown"
    @pointermove="onPointerMove"
    @pointerup="onPointerUp"
    @pointercancel="onPointerUp"
    @wheel="onWheel"
  >
    <div class="absolute left-0 top-0 origin-top-left will-change-transform" :style="worldStyle">
      <RouterLink
        v-for="it in shown"
        :key="it.videoId"
        :ref="cardRef(it.videoId)"
        :to="`/watch/${it.bvid}`"
        class="wall-card absolute block overflow-hidden rounded-lg border border-white/10 bg-[#1d1a16] shadow-[0_2px_10px_rgba(0,0,0,0.35)] outline-none transition-[box-shadow] focus-visible:ring-2 focus-visible:ring-stationPink hover:!z-[300]"
        :style="{ left: `${it.x}px`, top: `${it.y}px`, width: `${CARD_W}px`, height: `${CARD_H}px`, '--rot': `${it.rot}deg`, animationDelay: `${Math.min(Math.round(Math.hypot(it.x + CARD_W / 2, it.y + CARD_H / 2) / 2.2), 1100)}ms` }"
        :title="it.title || it.bvid"
        @pointerenter="onPreviewEnter(it)"
        @pointerleave="onPreviewLeave(it)"
        @click="onCardClick"
      >
        <img
          v-if="coverOf(it) && !brokenCovers.has(it.videoId)"
          :src="coverOf(it)"
          :alt="it.title || it.bvid"
          class="h-full w-full object-cover"
          loading="lazy"
          decoding="async"
          draggable="false"
          referrerpolicy="no-referrer"
          @error="onCoverError(it)"
        />
        <!-- 封面缺席/坏了：深色底 + 首字顶位，墙不留空洞 -->
        <span v-else class="flex h-full w-full items-center justify-center text-lg font-medium text-white/35">
          {{ (it.title || it.bvid || '?').slice(0, 1) }}
        </span>
        <!-- 悬停预览层：压在封面之上、字幕条之下；preload=none 不悬停一个字节不拉 -->
        <video
          v-if="it.preview && !previewGaveUp.has(it.videoId)"
          :key="it.preview"
          :ref="previewRef(it.videoId)"
          :src="it.preview"
          muted
          loop
          playsinline
          preload="none"
          disablepictureinpicture
          tabindex="-1"
          aria-hidden="true"
          class="pointer-events-none absolute inset-0 h-full w-full object-cover transition-opacity duration-200"
          :class="previewShown.has(it.videoId) ? 'opacity-100' : 'opacity-0'"
          @loadeddata="markShown(it.videoId, true)"
          @playing="markShown(it.videoId, true)"
          @error="giveUpPreview(it.videoId)"
        ></video>
        <span class="pointer-events-none absolute inset-x-0 bottom-0 bg-gradient-to-t from-black/85 to-transparent px-2 pb-1.5 pt-5 opacity-0 transition-opacity [.wall-card:hover_&]:opacity-100">
          <span class="block truncate text-[11px] leading-4 text-white/95">{{ it.title || it.bvid }}</span>
        </span>
      </RouterLink>
    </div>
  </div>
</template>

<style scoped>
.wall-stage {
  background-image: radial-gradient(rgba(127, 127, 127, 0.14) 1px, transparent 1px);
  background-size: 22px 22px;
}
/* 入场只演一次：节点只挂不拆，动画天然不重播；拖拽中新货也照常浮现，
   不再像上一版那样把手感好的浮现误杀成「啪」地出现 */
.wall-card {
  transform: rotate(var(--rot, 0deg)) scale(var(--focus, 1));
  transform-origin: center center;
  animation: card-in 0.32s cubic-bezier(0.22, 0.8, 0.36, 1) both;
}
@keyframes card-in {
  from {
    opacity: 0;
    scale: 0.92;
  }
  to {
    opacity: 1;
    scale: 1;
  }
}
@media (prefers-reduced-motion: reduce) {
  .wall-card {
    animation: none;
  }
}
</style>
