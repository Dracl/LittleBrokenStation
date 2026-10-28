<script setup>
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import DanmakuLayer from './DanmakuLayer.vue'
import BIcon from './BIcon.vue'
import { biPlay, biVolume, biSetting, biPip, biFull, biDmOn, biDmSet } from '../assets/bicons'
import { probeTrackBitrates } from '../utils/mp4Meta'

const props = defineProps({
  page: { type: Object, default: null }, // { videoUrl, danmakuUrl, subtitleUrls, index }
  status: { type: Number, default: 0 },
  title: { type: String, default: '' },
  bvid: { type: String, default: '' },
  aid: { type: Number, default: 0 },
  videoId: { type: Number, default: 0 }, // 分站台账里的馆藏 id（进度上报的钥匙；没它就无处可报）
  resumeSec: { type: Number, default: 0 }, // 观看页算好的续播点（只在进页那一份，手动切 P 不再打断）
  hasNextPage: { type: Boolean, default: false },
  autoRelated: { type: Boolean, default: false }, // 自动连播：本页最后一 P 播完跳接下来播放首条
  danmakuCount: { type: Number, default: null }, // 全站已归档弹幕总数（watch 顶层字段）
  localItems: { type: Array, default: () => [] }, // 站内弹幕 [{id, pageIdx, t, text}]（已按当前 P 筛好）
})
const emit = defineEmits(['next', 'request-codec', 'shell-width', 'next-video', 'codec-change', 'send-danmaku', 'delete-danmaku', 'report-danmaku'])

const root = ref(null)
const video = ref(null)
const trackEl = ref(null)

const playing = ref(false)
const waiting = ref(false)
// 卡顿自愈：waiting 持续不推进先自动重播一次；再不行看缓冲余量——缓冲区里有货却不走才算真死，
// 货还没到货就继续转圈（首载/大 seek 的慢缓冲不冤枉），几轮救不回才弹手动恢复钮。
// 关键：lastTickTime 是「卡住起点冻结的坐标」，只在 onMediaWait / manualUnstick 落笔，绝不在 timeupdate 刷；
// 否则正常播放时它被逐帧贴着当前进度、推进检测永远测到 ≈0，会把在放的视频也误判成停滞。
const stuckRecover = ref(false)
let stuckTimer = 0
let lastTickTime = 0
let seekTarget = null
let seekMergeTimer = 0

// 当前播放点后面还有没有 2 秒以上的缓冲存货（有货不推进才是真停滞）
function bufferHeadroom(v) {
  try {
    for (let i = 0; i < v.buffered.length; i++) {
      if (v.buffered.start(i) <= v.currentTime + 0.5 && v.buffered.end(i) >= v.currentTime + 2) return true
    }
  } catch {
    /* 老内核异常当没缓冲处理 */
  }
  return false
}

// 复审阶梯：第一轮 3 秒，后两轮各 8 秒；每轮先试一手 play()，缓冲不足就多给时间，三轮封顶才认死
function recheckStuck(roundsLeft) {
  clearTimeout(stuckTimer)
  stuckTimer = setTimeout(() => {
    const v = video.value
    if (!v || !playing.value) return
    if (v.currentTime - lastTickTime >= 0.1) {
      waiting.value = false
      return
    }
    if (bufferHeadroom(v) || roundsLeft <= 0) {
      stuckRecover.value = true
      logEvent(`停滞复审定罪 rounds=${roundsLeft} headroom=${bufferHeadroom(v)}`)
    } else {
      logEvent(`缓冲未到货，继续等 rounds=${roundsLeft}`)
      v.play().catch(() => {})
      recheckStuck(roundsLeft - 1)
    }
  }, roundsLeft === 3 ? 3000 : 8000)
}

function onMediaWait() {
  const v = video.value
  if (v) logEvent(`缓冲 rs=${v.readyState} ns=${v.networkState} seek=${v.seeking ? 1 : 0} t=${v.currentTime.toFixed(1)}`)
  // 冻结卡住基准点：往后所有「有没有推进」的判据都拿实时 currentTime 减这个点，真放了就会显著变大。
  if (v) lastTickTime = v.currentTime
  waiting.value = true
  stuckRecover.value = false
  // 素圈住在控制条里，条自隐会把圈一起藏走：真缓冲先来唤醒控制条，圈随条淡入。
  wakeControls()
  clearTimeout(stuckTimer)
  stuckTimer = setTimeout(tryUnstick, 4000)
}

function tryUnstick() {
  const v = video.value
  if (!v || !playing.value) return
  if (v.currentTime - lastTickTime > 0.3) {
    waiting.value = false
    return
  }
  logEvent('缓冲停滞，自动重试播放')
  v.play().catch(() => {})
  recheckStuck(3)
}

function onMediaPause() {
  playing.value = false
  waiting.value = false
  stuckRecover.value = false
  clearTimeout(stuckTimer)
  showControls.value = true
  reportProgress() // 一停就报：关页面前多半再没机会了，这一发最值当
}

function manualUnstick() {
  const v = video.value
  if (!v) return
  // 手动重试也重置基准点到「点击这一刻」，之后看有没有从这个点往前走。
  lastTickTime = v.currentTime
  stuckRecover.value = false
  waiting.value = true
  logEvent('手动恢复：尝试播放')
  v.play().catch(() => {
    waiting.value = false
  })
  clearTimeout(stuckTimer)
  stuckTimer = setTimeout(() => {
    const vv = video.value
    if (!vv || !playing.value) return
    if (vv.currentTime - lastTickTime >= 0.1) {
      waiting.value = false
      return
    }
    // play() 也救不活 → 媒体管线内部死锁，重载管线并回到原位。
    const t = vv.currentTime
    logEvent(`手动恢复无效，重载媒体管线 t=${t.toFixed(1)} rs=${vv.readyState} ns=${vv.networkState}`)
    vv.addEventListener('loadedmetadata', () => {
      const v3 = video.value
      if (!v3) return
      v3.currentTime = t
      v3.play().catch(() => {})
      stuckTimer = setTimeout(() => {
        const v4 = video.value
        if (v4 && playing.value && v4.currentTime - lastTickTime < 0.1) {
          stuckRecover.value = true
          logEvent('重载后仍未恢复，等待再次点击')
        }
      }, 4000)
    }, { once: true })
    vv.load()
  }, 3000)
}
// 音量记忆消毒：存过脏值（NaN/越界）就回默认 100%，记住 0 则视为静音态，两者永远一致。
const storedVolRaw = localStorage.getItem('lbs.player.volume')
const storedVol = Number(storedVolRaw)
const volume = ref(storedVolRaw !== null && Number.isFinite(storedVol) ? Math.min(1, Math.max(0, storedVol)) : 1)
const muted = ref(volume.value === 0)
const currentTime = ref(0)
const duration = ref(0)
const bufferedEnd = ref(0)
const showControls = ref(true)
const isFs = ref(false)
const rate = ref(1)
const rateMenu = ref(false)
const qualityMenu = ref(false)
const videoHeight = ref(0)

// 只展示不切换：本地馆藏单档，菜单里当前档高亮、其他置灰（B 站样式）。
const QUALITIES = ['8K 超高清', '4K 超高清', '1080P 60帧', '1080P 高清', '720P 准高清', '480P 标清', '360P 流畅']
const qualityLabel = computed(() => {
  const h = videoHeight.value
  if (h >= 4320) return '8K 超高清'
  if (h >= 2160) return '4K 超高清'
  if (h >= 1080) return '1080P 高清'
  if (h >= 720) return '720P 准高清'
  if (h >= 480) return '480P 标清'
  if (h >= 360) return '360P 流畅'
  return '清晰度'
})
const gearPanel = ref(false)
const playPanel = ref(false)
const holdingSpeed = ref(false)

// 播放设定：分站自己的精简版（循环/连播/比例/长按倍速/关灯）。v2 键：长按倍速改为默认开。
const play = reactive({ ...{ loop: false, autoNext: true, fit: 'contain', holdSpeed: 1.5, lightsOff: false }, ...(JSON.parse(localStorage.getItem('lbs.player.playPrefs.v2') || 'null') ?? {}) })
watch(play, (v) => localStorage.setItem('lbs.player.playPrefs.v2', JSON.stringify(v)), { deep: true })

// ─── 多编码版本（播放页点单加档）───
// 播放源 = 激活版本的文件或主版本；换源重建 video 元素，进度与播放态随 restorePlan 接力。
const activeCodec = ref(null)
let restorePlan = null

const versions = computed(() => props.page?.codecVersions ?? [])
const currentSrc = computed(() => {
  const hit = activeCodec.value && versions.value.find((c) => c.codec === activeCodec.value && c.status === 'Arrived')
  return hit ? hit.relPath : props.page?.videoUrl
})

// 版本展示规则（B 站风）：一个编码一行，主版本编码与加档同列；在库的直接摆出，
// 播放哪个亮哪个；从未到过的编码收进「点单加档其他版本」折叠行，不给单版本视频摆一排灰钮。
const CODEC_ROWS = [
  ['avc', 'AVC'],
  ['hevc', 'HEVC'],
  ['av1', 'AV1'],
]
const showMore = ref(false)

function versionInfo(codec) {
  const row = versions.value.find((c) => c.codec === codec) ?? null
  const isMain = props.page?.codec === codec
  if (isMain || row?.status === 'Arrived') {
    // 主版本的大小由后端扫盘带出（mainBytes）；加档行Arrived 后自带 bytes。拿不到就显空，不编数。
    const bytes = row?.status === 'Arrived' ? row.bytes : isMain ? props.page?.mainBytes ?? null : null
    return { state: 'stock', fromMain: isMain, bytes: bytes ?? null }
  }
  if (row?.status === 'Downloading') return { state: 'downloading' }
  if (row?.status === 'Failed') return { state: 'failed' }
  return { state: 'missing' }
}

// 在库行右侧标签：文件大小（采得到才显）+ 正在播的那条标「播放中」；未下载的不占位。
function stockBadge(codec) {
  const info = versionInfo(codec)
  const bits = []
  if (info.bytes != null) bits.push(fmtVersionSize(info.bytes))
  if (playingCodec() === codec) bits.push('播放中')
  return bits.join(' · ')
}

const presentRows = computed(() => CODEC_ROWS.filter(([c]) => versionInfo(c).state !== 'missing'))
const missingRows = computed(() => CODEC_ROWS.filter(([c]) => versionInfo(c).state === 'missing'))

function playingCodec() {
  return activeCodec.value ?? props.page?.codec ?? null
}

// 把「正在放的编码」报给观看页：标题下的下载按钮跟着换目标版本
watch([activeCodec, () => props.page?.codec], () => emit('codec-change', playingCodec()), { immediate: true })

function clickVersion(codec) {
  const info = versionInfo(codec)
  if (info.state === 'stock') {
    // 点行 = 要用这个编码的文件播：主版本编码回主文件，纯加档切版本文件（setCodec 自带 no-op 防护）
    setCodec(info.fromMain ? null : codec)
    return
  }
  if (info.state === 'failed' || info.state === 'missing') emit('request-codec', codec)
}

// 版本账按分 P 记，切 P 回主版本
watch(() => props.page?.index, () => {
  activeCodec.value = null
  restorePlan = null
  resumePending = 0 // 手动切选集就是从头看，不把上一集的续播点硬塞到新 P 上
})

watch(currentSrc, () => {
  const plan = restorePlan
  restorePlan = null
  if (!plan) return
  nextTick(() => {
    const v = video.value
    if (!v) return
    v.addEventListener(
      'loadedmetadata',
      () => {
        v.currentTime = plan.t
        if (plan.play) v.play().catch(() => {})
      },
      { once: true },
    )
  })
})

function setCodec(codec) {
  if (codec !== null) {
    const hit = versions.value.find((c) => c.codec === codec && c.status === 'Arrived')
    if (!hit || codec === activeCodec.value) return
  } else if (!activeCodec.value) {
    return
  }
  const v = video.value
  restorePlan = v && v.readyState >= 1 ? { t: v.currentTime, play: !v.paused } : null
  activeCodec.value = codec
}

function fmtVersionSize(n) {
  if (n == null) return '在库'
  const mb = n / (1024 * 1024)
  return mb >= 1024 ? `${(mb / 1024).toFixed(1)} GB` : `${mb.toFixed(1)} MB`
}

// ─── 站内弹幕（只在本页飘，不上传 B 站）───
const danmakuLayer = ref(null)
const danmakuDraft = ref('')

function sendDanmaku() {
  const text = danmakuDraft.value.trim()
  if (!text) return
  // 交给观看页入库；后端回 id 后 localItems 刷新，弹幕从发送时点起飞
  emit('send-danmaku', { text, pageIdx: props.page?.index ?? 0, t: video.value?.currentTime ?? 0 })
  danmakuDraft.value = ''
}

function onEnded() {
  playing.value = false
  reportProgress() // 末尾那一发会把这条历史销掉（位置过 95% 服务端直接删行）
  if (play.autoNext && props.hasNextPage) emit('next')
  else if (props.autoRelated) emit('next-video')
}

// ─── 观看历史：位置随手报（进度全从 video 元素现取，不养影子状态）───
// 心跳 15 秒一发（暂停与拖拽中跳发）；pause / ended 当场补一发；关页面前交给 sendBeacon——
// 那时候 fetch 会被浏览器掉手，sendBeacon 才是它答应替你送完的那一程。
const PROGRESS_BEAT_MS = 15000
let progressBeat = 0

function progressPayload() {
  const v = video.value
  if (!v || !props.videoId) return null
  const durationSec = Number(v.duration)
  const positionSec = Number(v.currentTime)
  if (!Number.isFinite(durationSec) || durationSec <= 0) return null
  if (!Number.isFinite(positionSec) || positionSec < 0) return null
  return { pageIdx: props.page?.index ?? 0, positionSec, durationSec }
}

function reportProgress() {
  const body = progressPayload()
  if (!body) return
  fetch(`/api/videos/${props.videoId}/progress`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  }).catch(() => {
    /* 报不上就算了：下一轮心跳自然补上，不打扰看片 */
  })
}

function reportProgressByBeacon() {
  const body = progressPayload()
  if (!body || typeof navigator.sendBeacon !== 'function') return
  navigator.sendBeacon(
    `/api/videos/${props.videoId}/progress`,
    new Blob([JSON.stringify(body)], { type: 'application/json' }),
  )
}

function onProgressBeat() {
  const v = video.value
  if (!v || v.paused || dragging) return // 暂停归 pause 报、拖拽归松手报，心跳只管正常播放那一段
  reportProgress()
}

// 关灯：用 clipPath 在整屏黑幕上挖掉播放器区域，只关页面不关画面。
const lightsStyle = ref(null)
function refreshLights() {
  const r = root.value?.getBoundingClientRect()
  if (!r) return
  lightsStyle.value = {
    clipPath: `polygon(evenodd, 0 0, 100vw 0, 100vw 100vh, 0 100vh, 0 0, ${r.left}px ${r.top}px, ${r.right}px ${r.top}px, ${r.right}px ${r.bottom}px, ${r.left}px ${r.bottom}px, ${r.left}px ${r.top}px)`,
  }
}
// B 站宽屏式高度驱动：先给顶栏/留白/弹幕条/与正文的间隔让出竖向空间，播放器高度吃满剩余视口，宽度按 16:9 反推居中
const SHELL_V_RESERVE = 170
const shellEl = ref(null)
const shellWidth = ref(0)
function measureShell() {
  if (!shellEl.value || document.fullscreenElement) {
    shellWidth.value = 0
    emit('shell-width', 0)
    return
  }
  const availH = window.innerHeight - SHELL_V_RESERVE
  shellWidth.value = Math.max(360, Math.min(window.innerWidth - 32, Math.floor((availH * 16) / 9)))
  emit('shell-width', shellWidth.value)
}
const shellStyle = computed(() => (shellWidth.value ? { width: `${shellWidth.value}px` } : null))
const onViewportChange = () => {
  // 全屏时黑幕（body 级）不在全屏元素子树内，会凭空消失，自动开灯兜底
  if (document.fullscreenElement && play.lightsOff) play.lightsOff = false
  if (play.lightsOff) refreshLights()
  measureShell()
}
window.addEventListener('resize', onViewportChange)
window.addEventListener('fullscreenchange', onViewportChange)
window.addEventListener('mouseup', onHoldMouseUp) // 在播放器外松右键的兜底，处理器幂等
watch(
  () => play.lightsOff,
  async (on) => {
    // 锁滚动：否则黑幕挖洞坐标不随页面滚，底下露白。黑幕本体已 Teleport 到 body，
    // 不再被 root 的 isolate 堆叠上下文困住（此前顶栏 z-20 压在整棵播放器子树上，盖不黑）。
    document.body.style.overflow = on ? 'hidden' : ''
    if (on) {
      await nextTick()
      refreshLights()
    }
  },
)
onBeforeUnmount(() => {
  document.body.style.overflow = ''
})

// 右键长按倍速（B 站同款手势）。状态机：右键按下开始计时，contextmenu（几乎总在按下后 ≤30ms 内到）决方向：
// ① holdSpeed 关档位 → contextmenu 到即弹菜单（老行为）；
// ② 开档位 → contextmenu 到达但未满 300ms 就松手 = 弹菜单；按住满 300ms = 临时加速，全程不弹菜单；
// ③ 鼠标移出播放器外松手 → window 级兜底；mouseup 彻底丢失 → 20s watchdog 自动归位，不永久卡加速态。
let holdTimer = 0
let menuTimer = 0
let watchdogTimer = 0
let ctxPending = null
let menuShownThisGesture = false

function clearHoldTimers() {
  clearTimeout(holdTimer)
  clearTimeout(menuTimer)
  clearTimeout(watchdogTimer)
  holdTimer = 0
  menuTimer = 0
  watchdogTimer = 0
}

function stopHold() {
  if (holdingSpeed.value) {
    holdingSpeed.value = false
    if (video.value) video.value.playbackRate = rate.value
  }
}

function endHoldGesture() {
  clearHoldTimers()
  ctxPending = null
  stopHold()
}

function beginHoldGesture(clientX, clientY) {
  endHoldGesture()
  menuShownThisGesture = false
  if (play.holdSpeed <= 0) return // 关档位：contextmenu 时机直接弹菜单，老行为
  ctxPending = clientX == null ? null : { x: clientX, y: clientY }
  holdTimer = setTimeout(() => {
    holdTimer = 0
    stopHold() // 上一次残留先归位
    holdingSpeed.value = true
    ctx.visible = false
    if (video.value) video.value.playbackRate = play.holdSpeed // 暂停中也允许按住，watchdog 会归位
    watchdogTimer = setTimeout(endHoldGesture, 20000) // mouseup/keyup 丢失时的自愈底线
  }, 300)
}

function onRootMouseDown(e) {
  if (e.button !== 2) return
  beginHoldGesture(e.clientX, e.clientY)
}

function onHoldMouseUp(e) {
  if (e && e.button !== 2) return
  if (holdTimer) {
    // 300ms 内松手 = 右键点击：弹菜单（contextmenu 时机被按住判定拦下了，这里补弹）
    clearTimeout(holdTimer)
    holdTimer = 0
    if (ctxPending && !menuShownThisGesture) {
      openCtxAt(ctxPending.x, ctxPending.y)
      menuShownThisGesture = true
    }
    ctxPending = null
    return
  }
  endHoldGesture() // 加速中松手 → 恢复倍速
}
const statsOpen = ref(false)
const ctx = reactive({ visible: false, x: 0, y: 0 })
const hover = reactive({ show: false, x: 0, t: 0 })
const stats = ref({})
const speedText = ref('—')
const netText = ref('0 KB')
const sparkVideo = ref(null)
const sparkNet = ref(null)
const logs = []
let statsTimer = 0
const sparkVData = []
const sparkNData = []
const sizeCache = new Map() // currentSrc → 字节数（HEAD 探一次）
let speedTick = 0

function logEvent(msg) {
  logs.push(`[${new Date().toLocaleTimeString('zh-CN', { hour12: false })}] ${msg}`)
  if (logs.length > 400) logs.shift()
}

let hideTimer = 0

// 弹幕偏好：站长室下发当出厂默认，本地 localStorage 覆盖（B 站也这么记）。
function danmakuDefaults() {
  return {
    on: true,
    opacity: 100,
    area: 100,
    fontScale: 100, // 弹幕字号百分比 50~150
    speed: 1,
    speedSync: false, // 弹幕速度同步播放倍速
    bold: false,
    stroke: 'thick', // thick 重墨 | thin 描边 | shadow 45°投影
    family: 'hei', // hei 黑体 | song 宋体 | mono 等宽
    keywords: '',
    modes: { scroll: true, top: true, bottom: true, color: true },
  }
}
const danmaku = reactive({ ...danmakuDefaults(), ...(JSON.parse(localStorage.getItem('lbs.danmaku.prefs') || 'null') ?? {}) })
danmaku.modes = { ...danmakuDefaults().modes, ...(danmaku.modes ?? {}) }
watch(danmaku, (v) => localStorage.setItem('lbs.danmaku.prefs', JSON.stringify(v)), { deep: true })

function resetDanmakuPrefs() {
  Object.assign(danmaku, danmakuDefaults())
}

const keywordList = computed(() =>
  String(danmaku.keywords ?? '')
    .split(/[,，、;；\s]+/)
    .map((s) => s.trim())
    .filter(Boolean),
)

function filterOn(k) {
  if (k === 'scroll') return danmaku.modes.scroll !== false
  if (k === 'color') return danmaku.modes.color !== false
  return danmaku.modes.top !== false || danmaku.modes.bottom !== false
}

function toggleDmFilter(k) {
  if (k === 'fixed') {
    const on = !(danmaku.modes.top || danmaku.modes.bottom)
    danmaku.modes.top = on
    danmaku.modes.bottom = on
  } else {
    danmaku.modes[k] = !filterOn(k)
  }
}

const SPEED_TICKS = [
  [0.5, '较慢'],
  [0.75, '慢'],
  [1, '适中'],
  [1.5, '快'],
  [2, '较快'],
]
const speedTickIndex = computed(() => {
  let best = 2
  let bestGap = Infinity
  SPEED_TICKS.forEach(([v], i) => {
    const gap = Math.abs(v - (danmaku.speed || 1))
    if (gap < bestGap) {
      bestGap = gap
      best = i
    }
  })
  return best
})

const engineDefaults = ref({ fontSize: -1, lineCount: -1 })
const layerFontSize = computed(() => {
  const base = engineDefaults.value.fontSize > 0 ? engineDefaults.value.fontSize : 50
  return Math.round((base * (danmaku.fontScale || 100)) / 100)
})

async function loadEngineDefaults() {
  try {
    const res = await fetch('/api/settings/engine')
    if (res.ok) {
      const d = (await res.json()).danmaku
      if (d) engineDefaults.value = { fontSize: d.fontSize ?? -1, lineCount: d.lineCount ?? -1 }
    }
  } catch {
    /* 用站内默认 */
  }
}

// ---- 播放控制 ----
function togglePlay() {
  const v = video.value
  if (!v || !props.page?.videoUrl) return
  if (v.paused) {
    logEvent('播放')
    v.play().catch(() => {})
  } else {
    logEvent('暂停')
    v.pause()
  }
}

function fmt(sec) {
  if (!Number.isFinite(sec) || sec < 0) sec = 0
  const h = Math.floor(sec / 3600)
  const m = Math.floor((sec % 3600) / 60)
  const s = Math.floor(sec % 60)
  const mm = String(m).padStart(2, '0')
  const ss = String(s).padStart(2, '0')
  return h > 0 ? `${h}:${mm}:${ss}` : `${mm}:${ss}`
}

function seekTo(sec) {
  const v = video.value
  if (!v || !Number.isFinite(v.duration)) return
  const target = Math.min(Math.max(0, sec), v.duration - 0.05)
  // 连按合并：120ms 内的多次请求只落一次 currentTime，消灭 seek 风暴（快速连跳的卡死温床）。
  seekTarget = target
  clearTimeout(seekMergeTimer)
  seekMergeTimer = setTimeout(flushSeek, 120)
}

function flushSeek() {
  clearTimeout(seekMergeTimer)
  seekMergeTimer = 0
  const v = video.value
  if (!v || seekTarget == null) return
  logEvent(`seek→${seekTarget.toFixed(1)}s`)
  v.currentTime = seekTarget
  seekTarget = null
}

// 相对跳转：连按时以「还没落地的目标点」为基数累加，八次短按就是 8 个 2 秒而不是一次 2 秒。
function seekBy(delta) {
  const v = video.value
  if (!v || !Number.isFinite(v.duration)) return
  const base = seekTarget != null ? seekTarget : v.currentTime
  seekTo(base + delta)
}

function onLoadedMetadata() {
  const v = video.value
  duration.value = v.duration || 0
  videoHeight.value = v.videoHeight || 0
  v.playbackRate = rate.value
  v.volume = volume.value
  v.muted = muted.value
  const t = new URLSearchParams(window.location.search).get('t')
  if (t && Number.isFinite(Number(t))) seekTo(Number(t))
  else flushResume() // 地址栏的 ?t= 才是硬指令，没它才轮到续播点
}

// ─── 续播：观看页算好了该从哪一秒起（位置地板、95% 上限都在它那里判）───
// 元数据没就位时 seek 会被默默吃掉，所以这里存一发，等 loadedmetadata 再落（与编码切换的 restorePlan 同一套路）。
let resumePending = 0

watch(() => props.resumeSec, (v) => {
  resumePending = Number.isFinite(v) && v > 0 ? v : 0
  const el = video.value
  if (resumePending && el && el.readyState >= 1) flushResume()
}, { immediate: true })

function flushResume() {
  if (!resumePending) return
  const v = video.value
  if (!v || !Number.isFinite(v.duration) || v.duration <= 0) return
  const target = Math.min(resumePending, v.duration - 0.05)
  resumePending = 0
  logEvent(`续播 seek→${target.toFixed(1)}s`)
  seekTo(target)
}

function applyVolume(x) {
  volume.value = Math.min(1, Math.max(0, x))
  muted.value = volume.value === 0
  if (video.value) {
    video.value.volume = volume.value
    video.value.muted = muted.value
  }
  localStorage.setItem('lbs.player.volume', String(volume.value))
}

// ─── 音量竖条（B 站 .bpx-player-ctrl-volume-box 同款）：拖拽/点选都只跟 applyVolume 说话，静音联动全走老口 ───
// 可见性不吃 CSS group-hover（弹层与音量钮间有 8px 死区，穿越瞬间 CSS :hover 就断），改由三个状态合成：
//   volHovering —— 真 hover（mouseenter/leave 维护；弹层与桥都是容器子树，进出子树不算离开）；
//   volPinned  —— 拖拽锁（pointerdown 起锁，拖到哪都看得见）或鼠标已离场后的收起宽限；
//   可见 = volHovering || volPinned；离场后 200ms 才卸 pinned（重新进入则取消），手抖穿越间隙不闪没。
//   pointer capture 期间 hover 事件会被抑制，松手时用 容器∩弹层 包围盒实测坐标补判一次。
const volTrackEl = ref(null)
const volZoneEl = ref(null)
const volPopEl = ref(null)
const volHovering = ref(false)
const volPinned = ref(false)
const volDragging = ref(false)
const volVisible = computed(() => volHovering.value || volPinned.value)
const volumePercent = computed(() => Math.round((muted.value ? 0 : volume.value) * 100))
let volHideTimer = null

function volClearTimer() {
  if (volHideTimer) {
    clearTimeout(volHideTimer)
    volHideTimer = null
  }
}

function volArmHide(delay) {
  volClearTimer()
  volHideTimer = setTimeout(() => {
    volHideTimer = null
    if (!volDragging.value) volPinned.value = false // 拖拽中途绝不卸锁
  }, delay)
}

function volEnter() {
  volClearTimer()
  volHovering.value = true
  volPinned.value = false
}

function volLeave() {
  volHovering.value = false
  volArmHide(200)
}

function volInsidePoint(e) {
  // 松手时指针落在音量钮区或弹层（含桥）都算「人还在」，继续显示不收。
  for (const el of [volZoneEl.value, volPopEl.value]) {
    if (!el) continue
    const r = el.getBoundingClientRect()
    if (e.clientX >= r.left && e.clientX <= r.right && e.clientY >= r.top && e.clientY <= r.bottom) return true
  }
  return false
}

function volFromPointer(e) {
  const el = volTrackEl.value
  if (!el) return
  const rect = el.getBoundingClientRect()
  const ratio = (rect.bottom - e.clientY) / rect.height // Y 自底向上映射 0-1
  applyVolume(Math.round(Math.min(1, Math.max(0, ratio)) * 100) / 100)
}

function volTrackDown(e) {
  volDragging.value = true
  volClearTimer()
  volPinned.value = true // 拖拽锁：无论 hover 与否都保持可见 + 可命中
  volTrackEl.value?.setPointerCapture(e.pointerId) // 滑出元素也能续拖
  volFromPointer(e) // 单点轨道任意位置直接跳到该音量
  e.preventDefault()
}

function volTrackMove(e) {
  if (volDragging.value) volFromPointer(e)
}

function volTrackUp(e) {
  volDragging.value = false
  try {
    volTrackEl.value?.releasePointerCapture(e.pointerId)
  } catch {
    /* 已自动释放，不管 */
  }
  volHovering.value = volInsidePoint(e)
  if (volHovering.value) {
    volClearTimer()
    volPinned.value = false // 人还在，hover 状态接管，锁当场卸
  } else {
    volArmHide(150) // 松手即离场：宽限期内锁继续撑着，timer 回调里才卸锁再收
  }
}

function setRate(x) {
  rate.value = x
  logEvent(`倍速 ${x}×`)
  holdingSpeed.value = false
  if (video.value) video.value.playbackRate = x
  rateMenu.value = false
}

function takeScreenshot() {
  const v = video.value
  if (!v || !v.videoWidth) return
  const c = document.createElement('canvas')
  c.width = v.videoWidth
  c.height = v.videoHeight
  c.getContext('2d').drawImage(v, 0, 0)
  const a = document.createElement('a')
  a.href = c.toDataURL('image/png')
  a.download = `${props.bvid}-P${props.page?.index ?? 1}-${fmt(currentTime.value).replace(/:/g, '')}.png`
  a.click()
  ctx.visible = false
  logEvent(`截图 ${fmt(currentTime.value)}`)
}

async function togglePip() {
  const v = video.value
  if (!v) return
  ctx.visible = false
  logEvent(document.pictureInPictureElement ? '关闭画中画' : '开启画中画')
  try {
    if (document.pictureInPictureElement) await document.exitPictureInPicture()
    else await v.requestPictureInPicture()
  } catch {
    /* 浏览器不让弹就算了 */
  }
}

function copyLink() {
  const url = `${window.location.origin}/watch/${props.bvid}?t=${Math.floor(currentTime.value)}`
  navigator.clipboard?.writeText(url).catch(() => {})
  ctx.visible = false
}

async function toggleFullscreen() {
  if (document.fullscreenElement) await document.exitFullscreen()
  else await root.value?.requestFullscreen().catch(() => {})
}

function probeCodec(v) {
  const probes = [
    ['avc1.640033', 'H.264'],
    ['avc1.64E01F', 'H.264'],
    ['hev1.1.6.L93.b0', 'HEVC'],
    ['av01.0.08M.08', 'AV1'],
  ]
  for (const [tag, label] of probes) {
    if (v.canPlayType(`video/mp4;codecs="${tag}"`)) return { tag, label }
  }
  return { tag: 'avc1.640033', label: 'H.264' }
}

function measureFps(done) {
  const v = video.value
  if (!v?.requestVideoFrameCallback) {
    done(null)
    return
  }
  let n = 0
  const t0 = performance.now()
  let settled = false
  const finish = () => {
    if (settled || n < 3) return
    settled = true
    done(Math.round((n / (performance.now() - t0)) * 1000 * 1000) / 1000)
  }
  const cb = () => {
    n++
    if (n < 15) v.requestVideoFrameCallback(cb)
    else finish()
  }
  v.requestVideoFrameCallback(cb)
  setTimeout(finish, 1700)
}

function fileBytes() {
  const v = video.value
  if (!v?.currentSrc) return 0
  for (const e of performance.getEntriesByType('resource')) {
    if (e.name === v.currentSrc) return e.encodedBodySize || e.transferSize || 0
  }
  return 0
}

// 视频大文件经常不进 performance 资源表，体积直接 HEAD 问服务器要 Content-Length。
async function probeTotalBytes() {
  const v = video.value
  const src = v?.currentSrc
  if (!src) return 0
  if (sizeCache.has(src)) return sizeCache.get(src)
  try {
    const r = await fetch(src, { method: 'HEAD', cache: 'no-store' })
    const len = Number(r.headers.get('content-length')) || 0
    if (len) {
      sizeCache.set(src, len)
      return len
    }
  } catch {
    /* 服务端不应 HEAD 就回到资源表 */
  }
  return fileBytes()
}

// 拉 4MB Range 块测瞬时吞吐，本地回环和远程部署都拿到的是真实网速。
async function sampleThroughput() {
  const v = video.value
  const src = v?.currentSrc
  if (!src) {
    speedText.value = '—'
    return
  }
  const ctrl = new AbortController()
  const t0 = performance.now()
  let got = 0
  const timer = setTimeout(() => ctrl.abort(), 1500)
  try {
    const res = await fetch(src, { headers: { Range: 'bytes=0-4194303' }, signal: ctrl.signal, cache: 'no-store' })
    const reader = res.body?.getReader()
    while (reader) {
      const { done, value } = await reader.read()
      if (done) break
      got += value?.byteLength ?? 0
    }
  } catch {
    /* 超时中断属于正常收尾 */
  }
  clearTimeout(timer)
  const secs = Math.max(0.05, (performance.now() - t0) / 1000)
  const kbps = Math.max(0, Math.round((got * 8) / secs / 1000))
  speedText.value = `${kbps.toLocaleString()} Kbps`
  sparkVData.push(kbps)
  if (sparkVData.length > 40) sparkVData.shift()
  drawSpark(sparkVideo.value, sparkVData)
}

function drawSpark(el, data) {
  if (!el) return
  const dpr = window.devicePixelRatio || 1
  el.width = Math.max(1, Math.floor(el.clientWidth * dpr))
  el.height = Math.max(1, Math.floor(el.clientHeight * dpr))
  const ctx2 = el.getContext('2d')
  ctx2.clearRect(0, 0, el.width, el.height)
  if (data.length < 2) return
  const max = Math.max(1, ...data)
  ctx2.strokeStyle = 'rgba(255,255,255,0.9)'
  ctx2.lineWidth = Math.max(1, dpr)
  ctx2.beginPath()
  data.forEach((val, i) => {
    const x = (i / (data.length - 1)) * el.width
    const y = el.height - (val / max) * (el.height - 2 * dpr) - dpr
    if (i === 0) ctx2.moveTo(x, y)
    else ctx2.lineTo(x, y)
  })
  ctx2.stroke()
}

function tickStats() {
  const v = video.value
  if (!v) return
  netText.value = fmtBytes(sparkNData.length ? sparkNData[sparkNData.length - 1] : 0)
  if (sparkNData.length > 40) sparkNData.shift()
  drawSpark(sparkNet.value, sparkNData)
  const q = v.getVideoPlaybackQuality?.() ?? {}
  stats.value['Dropped Frames'] = `${q.droppedVideoFrames ?? 0} / ${q.totalVideoFrames ?? 0}`
  stats.value['Segments'] = `${v.buffered.length || 0} / 1`
  sparkNData.push(sizeCache.get(v.currentSrc) || 0)
  // 每 3 秒重新测一次吞吐，曲线有起伏又不浪费带宽。
  if (++speedTick % 3 === 0) sampleThroughput()
}

function fmtBytes(bytes) {
  if (!bytes) return '0 KB'
  return bytes < 1024 * 1024 ? `${(bytes / 1024).toFixed(0)} KB` : `${(bytes / 1024 / 1024).toFixed(1)} MB`
}

async function openStats() {
  const v = video.value
  ctx.visible = false
  statsOpen.value = true
  logEvent('打开统计信息')
  if (!v) return
  const codec = probeCodec(v)
  const host = (() => {
    try {
      return new URL(v.currentSrc).host
    } catch {
      return window.location.host
    }
  })()
  const size = await probeTotalBytes()
  const totalKbps = size && duration.value ? Math.round((size * 8) / duration.value / 1000) : null
  stats.value = {
    'Mime Type': `video/mp4;codecs="${codec.tag}", audio/mp4;codecs="mp4a.40.2"`,
    'Player Type': 'LocalPlayer',
    'File Size': size ? fmtBytes(size) : '—',
    Resolution: '—',
    'Video DataRate': totalKbps ? `${totalKbps.toLocaleString()} Kbps [${codec.label}]` : '—',
    'Audio DataRate': '—',
    Segments: `${v.buffered.length || 0} / 1`,
    'Dropped Frames': '—',
    'Video Host': host,
    'Audio Host': host,
  }
  // 真·分轨码率：探 mp4 容器的 moov/trak，算出视频、音频各自的平均码率。
  probeTrackBitrates(v.currentSrc, size).then((tr) => {
    if (!tr || !statsOpen.value) return
    if (tr.video) stats.value['Video DataRate'] = `${tr.video.toLocaleString()} Kbps [${codec.label}]`
    if (tr.audio) stats.value['Audio DataRate'] = `${tr.audio.toLocaleString()} Kbps`
    logEvent(`分轨码率 视频${tr.video ?? '?'} / 音频${tr.audio ?? '?'} Kbps`)
  })
  if (v.videoWidth) {
    const apply = (fps) => {
      stats.value.Resolution = fps ? `${v.videoWidth} x ${v.videoHeight}@${fps.toFixed(3)}` : `${v.videoWidth} x ${v.videoHeight}`
    }
    if (v.paused || !v.requestVideoFrameCallback) apply(null)
    else measureFps(apply)
  }
  sparkVData.length = 0
  sparkNData.length = 0
  netText.value = fmtBytes(size)
  clearInterval(statsTimer)
  statsTimer = setInterval(tickStats, 1000)
  nextTick(() => {
    drawSpark(sparkVideo.value, [0, 0])
    drawSpark(sparkNet.value, [0, 0])
    sampleThroughput()
  })
}

function closeStats() {
  statsOpen.value = false
  clearInterval(statsTimer)
}

function copyLog() {
  navigator.clipboard?.writeText(logs.join('\n')).catch(() => {})
}

function downloadLog() {
  const blob = new Blob([logs.join('\n')], { type: 'text/plain;charset=utf-8' })
  const a = document.createElement('a')
  a.href = URL.createObjectURL(blob)
  a.download = `${props.bvid}-player.log`
  a.click()
  setTimeout(() => URL.revokeObjectURL(a.href), 5000)
}

// ---- 进度条拖拽 ----
let dragging = false
function trackSeek(e) {
  const el = trackEl.value
  if (!el) return
  const rect = el.getBoundingClientRect()
  const ratio = Math.min(1, Math.max(0, (e.clientX - rect.left) / rect.width))
  seekTo(ratio * duration.value)
}
function trackDown(e) {
  dragging = true
  trackEl.value?.setPointerCapture(e.pointerId)
  trackSeek(e)
}
function trackMove(e) {
  const el = trackEl.value
  if (!el) return
  const rect = el.getBoundingClientRect()
  hover.x = Math.min(rect.width, Math.max(0, e.clientX - rect.left))
  hover.t = (hover.x / rect.width) * duration.value
  hover.show = true
  if (dragging) trackSeek(e)
}
function trackUp(e) {
  dragging = false
  trackEl.value?.releasePointerCapture?.(e.pointerId)
}

// ---- 控制条显隐 ----
function wakeControls() {
  showControls.value = true
  clearTimeout(hideTimer)
  hideTimer = setTimeout(() => {
    // 真缓冲（waiting）期间不落帘：素圈住在条里，条隐圈灭会让用户以为片子死了；缓冲一到货自恢复 5 秒落帘节奏。
    if (playing.value && !waiting.value && !rateMenu.value && !qualityMenu.value && !gearPanel.value && !playPanel.value && !statsOpen.value && !ctx.visible) showControls.value = false
  }, 5000) // 站长口径：与续播小泡同步驻留五秒，鼠标一碰重新计时
}

// ---- 右键菜单 ----
function openCtx(e) {
  if (menuShownThisGesture) return // 同一手势重复触发不重弹
  if (ctxPending !== null) return // 加速判定中：菜单交给松手路径；加速中则全程不弹
  openCtxAt(e.clientX, e.clientY)
  menuShownThisGesture = true
}

function openCtxAt(clientX, clientY) {
  const rect = root.value.getBoundingClientRect()
  ctx.visible = true
  ctx.x = Math.min(clientX - rect.left, rect.width - 170)
  ctx.y = Math.min(clientY - rect.top, rect.height - 190)
  clearTimeout(hideTimer)
  showControls.value = true
}
function closeCtx() {
  ctx.visible = false
  wakeControls()
}

// ---- 键盘快捷键 ----
function onKey(e) {
  const tag = (e.target?.tagName ?? '').toLowerCase()
  if (tag === 'input' || tag === 'textarea' || e.target?.isContentEditable) return
  if (e.altKey || e.ctrlKey || e.metaKey) return
  // 按住不放时系统会连发 keydown（e.repeat）：除音量键外一律忽略，
  // 避免长按方向键秒级连续跳片（B 站同款：一次按键只跳一次）。
  if (e.repeat && e.key !== 'ArrowUp' && e.key !== 'ArrowDown') return
  const v = video.value
  if (!v) return
  let used = true
  switch (e.key) {
    case ' ':
    case 'k':
      togglePlay()
      break
    case 'ArrowRight':
      // 按下只开快进计时：300ms 内松手 = 短按跳 2s（keyup 里跳）；满 300ms = 直接快进不跳。
      arrowSeekPending = true
      if (play.holdSpeed > 0) beginHoldGesture(null, null)
      break
    case 'ArrowLeft':
      seekBy(-2)
      break
    case 'l':
      seekBy(10)
      break
    case 'j':
      seekBy(-10)
      break
    case 'ArrowUp':
      applyVolume(volume.value + 0.1)
      break
    case 'ArrowDown':
      applyVolume(volume.value - 0.1)
      break
    case 'm':
      muted.value = !muted.value
      v.muted = muted.value
      break
    case 'f':
      toggleFullscreen()
      break
    case 'd':
      danmaku.on = !danmaku.on
      break
    default:
      if (/^[0-9]$/.test(e.key) && duration.value) seekTo((Number(e.key) / 10) * duration.value)
      else used = false
  }
  if (used) {
    e.preventDefault()
    wakeControls()
  }
}

let arrowSeekPending = false

function onKeyUp(e) {
  if (e.key !== 'ArrowRight') return
  const wasHolding = holdingSpeed.value
  const shortPress = arrowSeekPending && !wasHolding
  arrowSeekPending = false
  endHoldGesture()
  if (shortPress) seekBy(2)
}

function onFsChange() {
  isFs.value = Boolean(document.fullscreenElement)
}

onMounted(() => {
  measureShell()
  loadEngineDefaults()
  window.addEventListener('keydown', onKey)
  window.addEventListener('keyup', onKeyUp)
  window.addEventListener('blur', endHoldGesture) // 切走窗口时按键释放收不到，直接归位
  document.addEventListener('fullscreenchange', onFsChange)
  document.addEventListener('click', closeCtx)
  progressBeat = setInterval(onProgressBeat, PROGRESS_BEAT_MS) // 心跳只管正常播放那一段（函数里自己筛）
  window.addEventListener('pagehide', reportProgressByBeacon) // 关页面/刷新：最后一发走信标
})
onBeforeUnmount(() => {
  clearTimeout(hideTimer)
  clearTimeout(stuckTimer)
  clearTimeout(seekMergeTimer)
  endHoldGesture()
  window.removeEventListener('mouseup', onHoldMouseUp)
  window.removeEventListener('resize', onViewportChange)
  window.removeEventListener('fullscreenchange', onViewportChange)
  clearInterval(statsTimer)
  window.removeEventListener('keydown', onKey)
  window.removeEventListener('keyup', onKeyUp)
  window.removeEventListener('blur', endHoldGesture)
  document.removeEventListener('fullscreenchange', onFsChange)
  document.removeEventListener('click', closeCtx)
  clearInterval(progressBeat)
  progressBeat = 0
  window.removeEventListener('pagehide', reportProgressByBeacon)
})

const RATE_ITEMS = [0.5, 0.75, 1, 1.25, 1.5, 2]
const FAMILY_ITEMS = [
  ['hei', '黑体'],
  ['song', '宋体'],
  ['mono', '等宽'],
]
const STROKE_ITEMS = [
  ['thick', '重墨'],
  ['thin', '描边'],
  ['shadow', '45°投影'],
]
</script>

<template>
  <!-- 外壳：播放器本体 + 下方弹幕条成一个整体（直角，与 B 站新版一致）；限宽居中，避免通栏 16:9 在高分屏上吃满一整屏 -->
  <div class="flex justify-center">
  <div ref="shellEl" class="shrink-0 bg-cardBg" :style="shellStyle">
  <div
    ref="root"
    class="group relative isolate aspect-video w-full select-none overflow-hidden bg-black outline-none"
    tabindex="0"
    @mousemove="wakeControls"
    @mousedown="onRootMouseDown"
    @mouseleave="hover.show = false; playing && wakeControls()"
    @contextmenu.prevent="openCtx"
  >
    <video
      v-if="page?.videoUrl"
      ref="video"
      :key="currentSrc"
      :src="currentSrc"
      preload="metadata"
      playsinline
      :loop="play.loop"
      :style="{ objectFit: play.fit }"
      class="h-full w-full"
      @click="togglePlay"
      @dblclick="toggleFullscreen"
      @timeupdate="currentTime = video.currentTime; bufferedEnd = video.buffered.length ? video.buffered.end(video.buffered.length - 1) : 0"
      @loadedmetadata="onLoadedMetadata"
      @play="playing = true; wakeControls()"
      @playing="waiting = false; stuckRecover = false; playing = true"
      @pause="onMediaPause"
      @waiting="onMediaWait"
      @stalled="onMediaWait"
      @canplay="waiting = false; stuckRecover = false"
      @ended="onEnded"
      @volumechange="muted = video.muted; volume = video.muted ? volume : video.volume"
    >
      <track v-if="page.subtitleUrls?.length" :src="page.subtitleUrls[0]" kind="subtitles" label="字幕" default />
    </video>
    <div v-else class="flex h-full w-full flex-col items-center justify-center gap-2 text-sm text-white/50">
      <span v-if="status === 1">这集还在进站路上，稍后再来</span>
      <span v-else-if="status === 3">这集进站失败，去首页看失败原因</span>
      <span v-else>本地还没有这个分 P 的成片</span>
    </div>

    <DanmakuLayer
      v-if="page?.danmakuUrl || localItems.length > 0"
      ref="danmakuLayer"
      :src="page?.danmakuUrl ?? null"
      :local-items="localItems"
      :video="video"
      :enabled="danmaku.on"
      :font-size="layerFontSize"
      :line-count="engineDefaults.lineCount"
      :opacity="danmaku.opacity / 100"
      :area="danmaku.area / 100"
      :speed="danmaku.speed * (danmaku.speedSync ? rate : 1)"
      :modes="danmaku.modes"
      :family="danmaku.family"
      :bold="danmaku.bold"
      :stroke="danmaku.stroke"
      :keywords="keywordList"
      @delete-local="$emit('delete-danmaku', $event)"
      @report-local="$emit('report-danmaku', $event)"
    />

    <!-- 关灯模式：只黑掉画面以外的页面，视频和弹幕照常亮；点黑幕亮回（B 站同款）。必须传送到 body：root 带 isolate，黑幕留在子树里 z 再高也盖不住页面 -->
    <Teleport to="body">
      <div
        v-if="play.lightsOff && lightsStyle"
        class="fixed inset-0 z-[70] cursor-pointer bg-black/[0.92]"
        :style="lightsStyle"
        @click="play.lightsOff = false"
      ></div>
    </Teleport>

    <!-- 长按倍速提示 -->
    <div v-if="holdingSpeed" class="absolute right-4 top-4 z-30 rounded-md bg-black/70 px-2.5 py-1 text-xs text-stationBlue">
      ⏩ {{ play.holdSpeed }}× 快进中
    </div>

    <!-- 暂停时的中央大按钮 -->
    <button
      v-if="!playing && page?.videoUrl"
      class="absolute left-1/2 top-1/2 flex h-16 w-16 -translate-x-1/2 -translate-y-1/2 items-center justify-center rounded-full bg-black/45 text-white backdrop-blur transition hover:bg-stationPink"
      @click.stop="togglePlay"
    >
      <BIcon :meta="biPlay" class="ml-1 h-8 w-8" />
    </button>

    <!-- 缓冲转圈已搬进控制条（时间戳身后 14px 素圈，站长定稿）；正中不再挂大圈 -->

    <!-- 自动重试也没救回来时的手动恢复钮：站长口径摆播放器左上角，不再堵画面正中（加速提示在右上，不碰面） -->
    <button
      v-if="stuckRecover"
      class="absolute left-3 top-3 z-20 rounded-lg bg-black/70 px-4 py-2 text-sm text-white backdrop-blur transition hover:bg-stationBlue"
      @click.stop="manualUnstick"
    >
      缓冲停滞，点击恢复播放
    </button>

    <!-- 底部控制条：淡背与续播气泡同源，opacity 缓进缓出 500ms（v-show 只能 display 硬切不淡，改 pointer-events-none 兼管隐藏时不挡点）-->
    <div
      class="absolute inset-x-0 bottom-0 z-10 bg-gradient-to-t from-black/85 via-black/35 to-transparent px-3 pb-1.5 pt-10 transition-opacity duration-500"
      :class="showControls ? 'opacity-100' : 'opacity-0 pointer-events-none'"
      @click.stop
      @dblclick.stop
      @mousemove="wakeControls"
    >
      <!-- 进度条 -->
      <div
        ref="trackEl"
        class="group/track relative -mt-7 mb-1 cursor-pointer py-2"
        @pointerdown="trackDown"
        @pointermove="trackMove"
        @pointerup="trackUp"
        @pointerleave="hover.show = false"
      >
        <div class="relative h-1 w-full rounded-full bg-white/25 transition-[height] group-hover/track:h-1.5">
          <div class="absolute inset-y-0 left-0 rounded-full bg-white/45" :style="{ width: duration ? (bufferedEnd / duration) * 100 + '%' : '0%' }"></div>
          <div class="absolute inset-y-0 left-0 rounded-full bg-stationPink" :style="{ width: duration ? (currentTime / duration) * 100 + '%' : '0%' }"></div>
          <div
            class="absolute top-1/2 h-3.5 w-3.5 -translate-x-1/2 -translate-y-1/2 rounded-full bg-stationPink opacity-0 shadow transition-opacity group-hover/track:opacity-100"
            :style="{ left: duration ? (currentTime / duration) * 100 + '%' : '0%' }"
          ></div>
        </div>
        <div
          v-if="hover.show"
          class="pointer-events-none absolute -top-6 -translate-x-1/2 rounded bg-black/80 px-1.5 py-0.5 text-[11px] text-white"
          :style="{ left: hover.x + 'px' }"
        >
          {{ fmt(hover.t) }}
        </div>
      </div>

      <div class="flex items-center gap-0.5 text-white">
        <button class="flex h-8 w-8 items-center justify-center rounded hover:bg-white/10" :title="playing ? '暂停（空格）' : '播放（空格）'" @click="togglePlay">
          <BIcon v-if="!playing" :meta="biPlay" class="h-5 w-5" />
          <!-- 暂停态官方包里没有，自绘两条圆角竖条顶上（不碰 bicons.js） -->
          <svg v-else viewBox="0 0 24 24" class="h-5 w-5" fill="currentColor"><rect x="6" y="5" width="4" height="14" rx="1.2" /><rect x="14" y="5" width="4" height="14" rx="1.2" /></svg>
        </button>

        <span class="mx-1.5 text-xs tabular-nums text-white/90">{{ fmt(currentTime) }} / {{ fmt(duration) }}</span>

        <!-- 缓冲素圈：14px 贴时间戳身后 5px，无底无框透出控制条渐变（从正中 40px 大圈搬来，站长定稿） -->
        <span
          v-if="waiting && playing && !stuckRecover"
          class="ml-[5px] h-3.5 w-3.5 animate-spin rounded-full border-2 border-white/30 border-t-white"
          title="缓冲中"
        ></span>

        <span class="flex-1"></span>

        <!-- 音量：悬停弹 B 站同款竖条（深底圆角盒：数字 28 + 轨道 60 + 留白，骑在音量钮正上方）。
             可见性走 volVisible 状态机（hover+拖拽锁+收起宽限），不用 CSS group-hover：
             弹层与按钮间那 8px 是 :hover 死区，穿越瞬间会把弹层抽走、拖拽腰断。 -->
        <div ref="volZoneEl" class="relative flex h-8 items-center gap-1 rounded px-1 hover:bg-white/10" @mouseenter="volEnter" @mouseleave="volLeave">
          <button class="flex h-6 w-6 items-center justify-center" :title="muted ? '取消静音（M）' : '静音（M）'" @click="muted ? (applyVolume(volume || 1)) : applyVolume(0)">
            <span class="relative flex h-5 w-5 items-center justify-center">
              <BIcon :meta="biVolume" class="h-5 w-5" />
              <!-- 静音态官方包里没有，白身斜杠叠加在音量图标上（纯 CSS，不碰 bicons.js） -->
              <span v-if="muted || volume === 0" class="pointer-events-none absolute left-1/2 top-1/2 h-[1.6px] w-[15px] -translate-x-1/2 -translate-y-1/2 rotate-45 rounded bg-white"></span>
            </span>
          </button>
          <!-- 透明桥：填平弹层底边到容器顶边的 8px 间隙，鼠标垂直上移全程命中容器子树，不会触发 leave；观感零变化 -->
          <span class="pointer-events-auto absolute bottom-8 left-1/2 h-2 w-8 -translate-x-1/2" aria-hidden="true"></span>
          <div
            ref="volPopEl"
            class="absolute bottom-[40px] left-1/2 z-20 -translate-x-1/2 rounded-[4px] bg-[rgba(20,20,20,0.9)] backdrop-blur transition-opacity duration-150"
            :class="volVisible ? 'pointer-events-auto opacity-100' : 'pointer-events-none opacity-0'"
          >
            <div class="flex w-8 flex-col items-center pb-2">
              <span class="flex h-7 items-center justify-center text-xs tabular-nums text-[#e5e9ef]">{{ volumePercent }}</span>
              <div
                ref="volTrackEl"
                class="relative h-[60px] w-2 cursor-pointer touch-none"
                @pointerdown="volTrackDown"
                @pointermove="volTrackMove"
                @pointerup="volTrackUp"
                @pointercancel="volTrackUp"
              >
                <span class="absolute left-1/2 top-0 h-full w-0.5 -translate-x-1/2 rounded bg-white/90"></span>
                <span class="absolute bottom-0 left-1/2 w-0.5 -translate-x-1/2 rounded bg-[#00aedc]" :style="{ height: volumePercent + '%' }"></span>
                <span class="absolute left-1/2 h-3 w-3 -translate-x-1/2 rounded-full bg-[#00aedc]" :style="{ bottom: `calc(${volumePercent}% - 6px)` }"></span>
              </div>
            </div>
          </div>
        </div>

        <!-- 清晰度（只展示不切换） -->
        <div class="relative">
          <button
            class="flex h-8 items-center justify-center rounded px-1.5 text-xs hover:bg-white/10"
            title="清晰度（本地馆藏单档）"
            @click.stop="qualityMenu = !qualityMenu; rateMenu = false; gearPanel = false; wakeControls()"
          >
            {{ qualityLabel }}
          </button>
          <div v-if="qualityMenu" class="absolute bottom-9 right-0 z-20 w-36 rounded-lg bg-[#222]/95 py-1 text-xs backdrop-blur" @click.stop>
            <div
              v-for="q in QUALITIES"
              :key="q"
              class="flex items-center justify-between px-3 py-2"
              :class="q === qualityLabel ? 'text-stationPink' : 'text-white/30'"
            >
              <span>{{ q }}</span>
              <span v-if="q === qualityLabel" class="text-[10px] text-white/50">馆藏</span>
            </div>
            <div class="mt-1 border-t border-white/10 px-3 py-1.5 text-[10px] text-white/40">本地馆藏仅此档</div>
          </div>
        </div>

        <!-- 倍速 -->
        <div class="relative">
          <button
            class="flex h-8 min-w-9 items-center justify-center rounded px-1 text-xs tabular-nums hover:bg-white/10"
            title="播放速度"
            @click.stop="rateMenu = !rateMenu; qualityMenu = false; gearPanel = false; wakeControls()"
          >
            {{ rate === 1 ? '倍速' : rate + '×' }}
          </button>
          <div v-if="rateMenu" class="absolute bottom-9 right-0 z-20 w-20 rounded-lg bg-[#222]/95 py-1 text-center text-xs backdrop-blur" @click.stop>
            <button
              v-for="r in RATE_ITEMS"
              :key="r"
              class="block w-full py-1.5 tabular-nums transition hover:bg-white/10"
              :class="r === rate ? 'text-stationPink' : 'text-white/85'"
              @click="setRate(r)"
            >
              {{ r }}×
            </button>
          </div>
        </div>

        <!-- 截图 -->
        <button class="flex h-8 w-8 items-center justify-center rounded hover:bg-white/10" title="截图当前画面" @click="takeScreenshot">
          <svg viewBox="0 0 24 24" class="h-5 w-5" fill="none" stroke="currentColor" stroke-width="1.7">
            <path d="M4 8h3l1.5-2h7L17 8h3v11H4V8z" stroke-linejoin="round" />
            <circle cx="12" cy="13" r="3.4" />
          </svg>
        </button>

        <!-- 播放设定齿轮 -->
        <button
          class="flex h-8 w-8 items-center justify-center rounded hover:bg-white/10"
          :class="playPanel ? 'text-stationBlue' : ''"
          title="播放设定"
          @click.stop="playPanel = !playPanel; gearPanel = false; rateMenu = false; qualityMenu = false; wakeControls()"
        >
          <BIcon :meta="biSetting" class="h-5 w-5" />
        </button>

        <!-- 画中画 -->
        <button class="flex h-8 w-8 items-center justify-center rounded hover:bg-white/10" title="画中画" @click="togglePip">
          <BIcon :meta="biPip" class="h-5 w-5" />
        </button>

        <!-- 全屏 -->
        <button class="flex h-8 w-8 items-center justify-center rounded hover:bg-white/10" title="全屏（F）" @click="toggleFullscreen">
          <BIcon :meta="biFull" class="h-5 w-5" />
        </button>
      </div>
    </div>

    <!-- 播放设定面板：分站精简版 -->
    <div
      v-if="playPanel"
      class="absolute bottom-14 right-3 z-20 w-64 rounded-xl bg-[#222]/95 p-3.5 text-xs text-white/90 shadow-xl backdrop-blur"
      @click.stop
      @mousemove="wakeControls"
    >
      <p class="relative mb-3 text-center text-[13px] font-medium text-white">
        播放设定
        <button class="absolute right-0 top-0 text-white/50 transition hover:text-white" @click="playPanel = false">✕</button>
      </p>

      <div class="mb-2.5 flex items-center justify-between">
        <span class="text-white/85">单集循环</span>
        <button class="relative h-5 w-9 rounded-full transition" :class="play.loop ? 'bg-stationBlue' : 'bg-white/25'" @click="play.loop = !play.loop">
          <span class="absolute top-0.5 h-4 w-4 rounded-full bg-white shadow transition-all" :class="play.loop ? 'left-[18px]' : 'left-0.5'"></span>
        </button>
      </div>
      <div class="mb-2.5 flex items-center justify-between">
        <span class="text-white/85">播完自动播下一集</span>
        <button
          class="relative h-5 w-9 rounded-full transition"
          :class="[play.autoNext ? 'bg-stationBlue' : 'bg-white/25', hasNextPage ? '' : 'opacity-40']"
          :title="hasNextPage ? '' : '已经是最后一集'"
          @click="play.autoNext = !play.autoNext"
        >
          <span class="absolute top-0.5 h-4 w-4 rounded-full bg-white shadow transition-all" :class="play.autoNext ? 'left-[18px]' : 'left-0.5'"></span>
        </button>
      </div>

      <span class="mb-1 block text-white/60">视频比例</span>
      <div class="mb-2.5 flex gap-1.5">
        <button
          v-for="f in [['contain', '自动适配'], ['cover', '充满画面']]"
          :key="f[0]"
          class="flex-1 rounded py-1.5 transition"
          :class="play.fit === f[0] ? 'bg-stationBlue text-white' : 'bg-white/10 hover:bg-white/20'"
          @click="play.fit = f[0]"
        >
          {{ f[1] }}
        </button>
      </div>

      <span class="mb-1 block text-white/60">右键长按倍速</span>
      <div class="mb-2.5 flex gap-1.5">
        <button
          v-for="h in [[0, '关'], [1.5, '1.5×'], [2, '2×'], [3, '3×']]"
          :key="h[0]"
          class="flex-1 rounded py-1.5 tabular-nums transition"
          :class="play.holdSpeed === h[0] ? 'bg-stationBlue text-white' : 'bg-white/10 hover:bg-white/20'"
          @click="play.holdSpeed = h[0]"
        >
          {{ h[1] }}
        </button>
      </div>

      <span v-if="presentRows.length" class="mb-1 block text-white/60">播放版本</span>
      <div v-if="presentRows.length" class="mb-1.5 flex flex-col gap-1">
        <button
          v-for="c in presentRows"
          :key="c[0]"
          class="flex items-center justify-between rounded px-2 py-1.5 transition"
          :class="playingCodec() === c[0] ? 'bg-stationBlue text-white' : versionInfo(c[0]).state === 'stock' ? 'bg-white/10 hover:bg-white/20' : 'bg-white/5 text-white/50 hover:bg-white/10'"
          :title="c[0] === 'hevc' ? 'HEVC 在部分浏览器可能放不动' : ''"
          @click="clickVersion(c[0])"
        >
          <span>{{ c[1] }} 版</span>
          <span class="text-[10px] tabular-nums">
            <template v-if="versionInfo(c[0]).state === 'stock'">{{ stockBadge(c[0]) }}</template>
            <template v-else-if="versionInfo(c[0]).state === 'downloading'">进站中…</template>
            <template v-else-if="versionInfo(c[0]).state === 'failed'">失败 · 重点重试</template>
          </span>
        </button>
      </div>
      <button v-if="missingRows.length" class="mb-2 px-1 text-left text-[10px] text-white/40 transition hover:text-white/70" @click="showMore = !showMore">
        {{ showMore ? '▾ 收起其他版本' : '▸ 点单加档其他版本' }}
      </button>
      <div v-if="showMore && missingRows.length" class="mb-2 flex flex-col gap-1">
        <button
          v-for="c in missingRows"
          :key="c[0]"
          class="flex items-center justify-between rounded bg-white/5 px-2 py-1.5 text-white/50 transition hover:bg-white/10"
          :title="c[0] === 'hevc' ? 'HEVC 在部分浏览器可能放不动' : ''"
          @click="clickVersion(c[0])"
        >
          <span>{{ c[1] }} 版</span>
          <span class="text-[10px]">点单加档</span>
        </button>
      </div>

      <div class="flex items-center justify-between">
        <span class="text-white/85">关灯模式</span>
        <button class="relative h-5 w-9 rounded-full transition" :class="play.lightsOff ? 'bg-stationBlue' : 'bg-white/25'" @click="play.lightsOff = !play.lightsOff">
          <span class="absolute top-0.5 h-4 w-4 rounded-full bg-white shadow transition-all" :class="play.lightsOff ? 'left-[18px]' : 'left-0.5'"></span>
        </button>
      </div>
    </div>

    <!-- 弹幕设定面板：B 站原型，只留刚需 -->
    <div
      v-if="gearPanel"
      class="absolute bottom-14 left-3 z-20 w-[300px] rounded-xl bg-[#222]/95 p-3.5 text-xs text-white/90 shadow-xl backdrop-blur"
      @click.stop
      @mousemove="wakeControls"
    >
      <p class="relative mb-3 text-center text-[13px] font-medium text-white">
        弹幕设定
        <button class="absolute right-0 top-0 text-white/50 transition hover:text-white" @click="gearPanel = false">✕</button>
      </p>

      <span class="mb-1.5 block text-white/60">按类型过滤</span>
      <div class="mb-3 flex gap-2">
        <button v-for="f in [['scroll', '滚动'], ['fixed', '固定'], ['color', '彩色']]" :key="f[0]" class="flex flex-1 flex-col items-center gap-1" @click="toggleDmFilter(f[0])">
          <span class="flex h-8 w-full items-center justify-center rounded-md text-sm transition" :class="filterOn(f[0]) ? 'bg-white text-black' : 'bg-white/15 text-white/50'">
            <template v-if="f[0] === 'scroll'">≡</template>
            <template v-else-if="f[0] === 'fixed'">…</template>
            <template v-else>调色</template>
          </span>
          <span :class="filterOn(f[0]) ? 'text-white' : 'text-white/50'">{{ f[1] }}</span>
        </button>
      </div>

      <label class="mb-2 flex items-center gap-2">
        <span class="w-14 shrink-0 text-white/60">显示区域</span>
        <input v-model.number="danmaku.area" type="range" min="25" max="100" step="25" class="min-w-0 flex-1 accent-stationBlue" />
        <span class="w-10 shrink-0 text-right tabular-nums">{{ danmaku.area }}%</span>
      </label>
      <label class="mb-2 flex items-center gap-2">
        <span class="w-14 shrink-0 text-white/60">不透明度</span>
        <input v-model.number="danmaku.opacity" type="range" min="10" max="100" step="5" class="min-w-0 flex-1 accent-stationBlue" />
        <span class="w-10 shrink-0 text-right tabular-nums">{{ danmaku.opacity }}%</span>
      </label>
      <label class="mb-2 flex items-center gap-2">
        <span class="w-14 shrink-0 text-white/60">弹幕字号</span>
        <input v-model.number="danmaku.fontScale" type="range" min="50" max="150" step="5" class="min-w-0 flex-1 accent-stationBlue" />
        <span class="w-10 shrink-0 text-right tabular-nums">{{ danmaku.fontScale }}%</span>
      </label>
      <label class="mb-2.5 flex items-center gap-2">
        <span class="w-14 shrink-0 text-white/60">弹幕速度</span>
        <input :value="speedTickIndex" type="range" min="0" max="4" step="1" class="min-w-0 flex-1 accent-stationBlue" @input="danmaku.speed = SPEED_TICKS[Number($event.target.value)][0]" />
        <span class="w-10 shrink-0 text-right">{{ SPEED_TICKS[speedTickIndex][1] }}</span>
      </label>

      <div class="mb-2.5 flex items-center gap-4 pl-0.5">
        <label class="flex cursor-pointer items-center gap-1.5 text-white/85">
          <input v-model="danmaku.speedSync" type="checkbox" class="accent-stationBlue" />
          速度同步倍速
        </label>
        <label class="flex cursor-pointer items-center gap-1.5 text-white/85">
          <input v-model="danmaku.bold" type="checkbox" class="accent-stationBlue" />
          粗体
        </label>
      </div>

      <span class="mb-1 block text-white/60">弹幕字体</span>
      <div class="mb-2.5 flex gap-1">
        <button
          v-for="[k, label] in FAMILY_ITEMS"
          :key="k"
          class="flex-1 rounded py-1 transition"
          :class="danmaku.family === k ? 'bg-stationBlue text-white' : 'bg-white/10 hover:bg-white/20'"
          @click="danmaku.family = k"
        >
          {{ label }}
        </button>
      </div>

      <span class="mb-1 block text-white/60">描边类型</span>
      <div class="mb-2.5 flex gap-1">
        <button
          v-for="[k, label] in STROKE_ITEMS"
          :key="k"
          class="flex-1 rounded py-1 transition"
          :class="danmaku.stroke === k ? 'bg-stationBlue text-white' : 'bg-white/10 hover:bg-white/20'"
          @click="danmaku.stroke = k"
        >
          {{ label }}
        </button>
      </div>

      <span class="mb-1 block text-white/60">屏蔽词</span>
      <input
        v-model="danmaku.keywords"
        type="text"
        placeholder="逗号分隔，命中的弹幕直接隐藏"
        class="mb-3 w-full rounded-md bg-white/10 px-2 py-1.5 text-white placeholder-white/30 focus:bg-white/15 focus:outline-none"
      />

      <button class="w-full rounded-md border border-white/25 py-1.5 text-white/85 transition hover:bg-white/10" @click="resetDanmakuPrefs">恢复默认设置</button>
    </div>

    <!-- 右键菜单 -->
    <div
      v-if="ctx.visible"
      class="absolute z-30 w-40 overflow-hidden rounded-lg bg-[#222]/95 py-1 text-xs text-white/90 shadow-xl backdrop-blur"
      :style="{ left: ctx.x + 'px', top: ctx.y + 'px' }"
      @click.stop
      @contextmenu.prevent
    >
      <button class="block w-full px-3 py-2 text-left transition hover:bg-white/10" @click="takeScreenshot">截图当前画面</button>
      <button class="block w-full px-3 py-2 text-left transition hover:bg-white/10" @click="copyLink">复制带进度链接</button>
      <button class="block w-full px-3 py-2 text-left transition hover:bg-white/10" @click="togglePip">画中画播放</button>
      <div class="my-1 border-t border-white/10"></div>
      <button class="block w-full px-3 py-2 text-left transition hover:bg-white/10" @click="openStats">视频统计信息</button>
    </div>

    <!-- 统计信息：B 站同款左上角半透面板，锁在播放框内不挡页面 -->
    <div
      v-if="statsOpen"
      class="absolute left-2 top-2 z-40 max-h-[calc(100%-16px)] w-[520px] max-w-[calc(100%-16px)] overflow-y-auto rounded-2xl bg-black/80 px-4 py-3 text-[12px] text-white shadow-2xl backdrop-blur-sm"
      @click.stop
      @mousemove="wakeControls"
    >
      <p class="relative mb-2 text-center text-sm font-medium">
        统计信息
        <button class="absolute -top-0.5 right-0 px-1 text-base leading-none text-white/80 transition hover:text-white" @click="closeStats">✕</button>
      </p>
      <dl class="grid grid-cols-[118px_1fr] items-center gap-x-3 gap-y-1">
        <template v-for="(val, key) in stats" :key="key">
          <dt class="text-right text-white/55">{{ key }}:</dt>
          <dd class="break-all text-white/85">{{ val }}</dd>
        </template>
        <dt class="text-right text-white/55">Video Speed:</dt>
        <dd class="flex items-center gap-2">
          <canvas ref="sparkVideo" class="h-4 flex-1"></canvas>
          <span class="w-[90px] shrink-0 text-white/85">{{ speedText }}</span>
        </dd>
        <dt class="text-right text-white/55">Audio Speed:</dt>
        <dd class="flex items-center gap-2">
          <span class="h-px flex-1 bg-white/85"></span>
          <span class="w-[90px] shrink-0 text-white/85">—</span>
        </dd>
        <dt class="text-right text-white/55">Network Activity:</dt>
        <dd class="flex items-center gap-2">
          <canvas ref="sparkNet" class="h-4 flex-1"></canvas>
          <span class="w-[90px] shrink-0 text-white/85">{{ netText }}</span>
        </dd>
        <dt class="text-right text-white/55">分站-Log:</dt>
        <dd class="flex items-center gap-2">
          <button class="rounded bg-white/15 px-2.5 py-1 text-white/90 transition hover:bg-white/25" @click="copyLog">[Copy]</button>
          <button class="rounded bg-white/15 px-2.5 py-1 text-white/90 transition hover:bg-white/25" @click="downloadLog">[Download]</button>
        </dd>
      </dl>
    </div>
  </div>

    <!-- 弹幕条（B 站同款位，播放器下方）：左计数，中输入，右发送。分站不发 B 站，发出的弹幕只在本页飘落 -->
    <div class="flex items-center gap-3 border-t border-black/5 px-3 py-2 shadow-[0_2px_10px_rgba(0,0,0,0.12)]">
      <div class="flex shrink-0 items-center gap-3 text-xs text-inkFaint">
        <span class="flex items-center gap-1 tabular-nums" title="已归档的 B 站弹幕总数">
          <svg viewBox="0 0 16 16" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="1.4">
            <rect x="1.2" y="2.8" width="13.6" height="9" rx="2" />
            <path d="M4.5 14.2 7 11.8" stroke-linecap="round" />
          </svg>
          {{ danmakuCount != null ? `${danmakuCount} 条弹幕` : '弹幕未归档' }}
        </span>
        <span v-if="localItems.length > 0" class="hidden text-inkFaint/70 sm:inline">站内弹幕 +{{ localItems.length }}</span>
        <!-- 弹幕开关与设定：从控制栏搬来计数右边（B 站把弹幕设定也放弹幕栏一带） -->
        <button class="flex h-7 w-7 items-center justify-center rounded hover:bg-black/5" title="弹幕（D）" @click="danmaku.on = !danmaku.on">
          <!-- 官方弹幕枚：开启时对勾（第二条 path）染 B 站蓝，关闭态整枚灰掉 -->
          <BIcon :meta="biDmOn" class="h-[20px] w-[20px] transition" :class="danmaku.on ? 'dm-on text-inkSub' : 'text-inkFaint/50'" />
        </button>
        <button
          class="flex h-7 w-7 items-center justify-center rounded hover:bg-black/5"
          :class="gearPanel ? 'bg-stationBlue/10' : ''"
          title="弹幕设定"
          @click.stop="gearPanel = !gearPanel; playPanel = false; rateMenu = false; qualityMenu = false; wakeControls()"
        >
          <BIcon :meta="biDmSet" class="h-[20px] w-[20px] text-inkSub" />
        </button>
      </div>
      <div class="flex min-w-0 flex-1 items-center gap-2 rounded-full bg-[#f1f2f3] px-3.5 py-1.5">
        <svg viewBox="0 0 16 16" class="h-3.5 w-3.5 shrink-0 text-inkFaint" fill="none" stroke="currentColor" stroke-width="1.4">
          <path d="M3 12.5 8 3l5 9.5M4.8 9.2h6.4" stroke-linecap="round" stroke-linejoin="round" />
        </svg>
        <input
          v-model="danmakuDraft"
          type="text"
          maxlength="100"
          placeholder="发个友善的站内弹幕见证当下（仅本页可见，不上传 B 站）"
          class="min-w-0 flex-1 bg-transparent text-xs text-inkMain outline-none placeholder:text-inkFaint/70"
          @keydown.enter.prevent="sendDanmaku"
        />
      </div>
      <button
        class="shrink-0 rounded-md bg-stationBlue px-4 py-1.5 text-xs font-medium text-white transition enabled:hover:opacity-85 disabled:cursor-not-allowed disabled:opacity-40"
        :disabled="!danmakuDraft.trim()"
        @click="sendDanmaku"
      >
        发送
      </button>
    </div>
  </div>
  </div>
</template>

<style scoped>
/* 弹幕开启态：官方弹幕枚的第二条 path（对勾）染 B 站蓝。
   BIcon 用 v-html 注入图标内容，scoped 样式够不到，得 :deep。 */
.dm-on :deep(path:nth-of-type(2)) {
  fill: #00a1d6;
}
</style>
