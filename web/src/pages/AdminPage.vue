<script setup>
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import LoginDialog from '../components/LoginDialog.vue'
import CrownBadge from '../components/CrownBadge.vue'
import RecycleBinCard from '../components/RecycleBinCard.vue'
import { member, loginDialogOpen, loginDialogMode } from '../memberStore'
import { requestConfirm } from '../confirmStore'
import { friendlyTime } from '../utils/time'
import { defaultAvatar, onAvatarError } from '../utils/avatar.js'
import { VIDEO_GONE_HINT } from '../utils/gone.js'
import { remoteCoverFallback, remoteCoverThumb } from '../utils/coverSrc'
import { forgetSiteBase } from '../utils/siteLink'

const health = ref(null)
const settings = ref(null)
const purgeArmed = ref(false)
const purgeBusy = ref(false)
const purgeNotice = ref(null) // { kind, text }
let purgeTimer = null

// ─── 下载设置（站长室面板）───────────────────────────────────────────
const eng = ref(null)
const engDraft = ref(null)
const engBusy = ref(false)
const engNotice = ref(null) // { kind, text }
const engWarnings = ref([])
const keywordDraft = ref('')
const uidDraft = ref('')

const QUALITY_OPTIONS = [
  [127, '自动上限（8K 探顶）'],
  [126, '杜比视界'],
  [125, 'HDR 真彩'],
  [120, '4K 超清'],
  [116, '1080P 60帧'],
  [112, '1080P 高码率'],
  [80, '1080P 高清'],
  [74, '720P 60帧'],
  [64, '720P 高清'],
  [32, '480P 清晰'],
  [16, '360P 流畅'],
]
const AUDIO_OPTIONS = [
  [31251, 'Hi-Res 无损'],
  [31250, 'Dolby Atmos'],
  [30280, '高质量'],
  [30232, '中质量'],
  [30216, '低质量'],
]
const CODEC_OPTIONS = [
  [7, 'AVC（H.264，兼容最好）'],
  [12, 'HEVC（H.265，更省空间）'],
  [13, 'AV1（新设备首选）'],
]
const HW_OPTIONS = [
  [0, '未设'],
  [1, '自动探测'],
  [2, '强制软解'],
  [3, 'NVIDIA NVENC'],
  [4, 'Intel QSV'],
  [5, 'AMD AMF'],
  [6, 'VA-API'],
  [7, 'VideoToolbox'],
]
const OUTPUT_OPTIONS = [
  [0, 'ASS（观看页认它）'],
  [1, 'XML'],
  [2, 'ASS + XML 都要'],
]
const LAYOUT_OPTIONS = [
  [0, '默认'],
  [1, '异步排版'],
  [2, '同步排版'],
]
const LANG_OPTIONS = [
  [0, '不限'],
  [1, '简体中文'],
  [2, '繁体中文'],
  [3, '英语'],
  [4, '日语'],
  [5, '韩语'],
]
const TYPE_OPTIONS = [
  [0, '人工字幕优先'],
  [1, 'AI 字幕优先'],
]
const ORDER_OPTIONS = [
  [1, '自然数 1,2,3'],
  [2, '前导零 01,02,03'],
]
const TOKEN_LABELS = {
  1: '分P序号', 2: '分部', 3: '标题', 4: '分P标题', 5: '分区', 6: '音质', 7: '画质', 8: '编码',
  9: '发布日', 10: 'avid', 11: 'bvid', 12: 'cid', 13: 'UP主mid', 14: 'UP主名',
  100: '/', 101: '_', 102: '-', 103: '+', 104: ',', 105: '.', 106: 'and', 107: '#',
  108: '(', 109: ')', 110: '[', 111: ']', 112: '{', 113: '}', 114: '空格',
}
const TOKEN_ADDABLE = Object.keys(TOKEN_LABELS).map(Number)

async function loadEngine() {
  try {
    const res = await fetch('/api/settings/engine')
    if (!res.ok) throw new Error(String(res.status))
    eng.value = await res.json()
    engDraft.value = JSON.parse(JSON.stringify(eng.value))
    keywordDraft.value = (eng.value.danmaku.blockedKeywords || []).join('\n')
    uidDraft.value = (eng.value.danmaku.blockedSenderUids || []).join('\n')
  } catch {
    eng.value = null
  }
}

function movePart(index, delta) {
  const parts = engDraft.value.video.fileNameParts
  const target = index + delta
  if (target < 0 || target >= parts.length) return
  const [item] = parts.splice(index, 1)
  parts.splice(target, 0, item)
}

function removePart(index) {
  engDraft.value.video.fileNameParts.splice(index, 1)
}

function addPart(event) {
  const value = Number(event.target.value)
  if (!value) return
  engDraft.value.video.fileNameParts.push(value)
  event.target.value = ''
}

async function saveEngine() {
  if (engBusy.value || !engDraft.value) return
  engBusy.value = true
  engWarnings.value = []
  const payload = JSON.parse(JSON.stringify(engDraft.value))
  payload.danmaku.blockedKeywords = keywordDraft.value.split(/[\n,，]/).map((s) => s.trim()).filter(Boolean)
  payload.danmaku.blockedSenderUids = uidDraft.value
    .split(/[\n,，]/)
    .map((s) => s.trim())
    .filter(Boolean)
    .map(Number)
    .filter((n) => Number.isFinite(n) && n > 0)
  try {
    const res = await fetch('/api/settings/engine', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload),
    })
    const data = await res.json().catch(() => null)
    if (res.ok) {
      eng.value = data.settings
      engDraft.value = JSON.parse(JSON.stringify(data.settings))
      keywordDraft.value = (data.settings.danmaku.blockedKeywords || []).join('\n')
      uidDraft.value = (data.settings.danmaku.blockedSenderUids || []).join('\n')
      engWarnings.value = data.warnings || []
      engNotice.value = { kind: 'ok', text: '设置已落库，引擎即刻消费' }
    } else {
      engNotice.value = { kind: 'err', text: data?.message ?? '保存失败' }
    }
  } catch {
    engNotice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    engBusy.value = false
  }
}

// 一键清空分站：两击确认，第一击后 5 秒内不点第二击就自行解除，防误触。
function armPurge() {
  if (purgeArmed.value || purgeBusy.value) return
  purgeArmed.value = true
  purgeNotice.value = null
  purgeTimer = setTimeout(() => {
    purgeArmed.value = false
  }, 5000)
}

async function purgeStation() {
  if (!purgeArmed.value) {
    armPurge()
    return
  }
  clearTimeout(purgeTimer)
  purgeArmed.value = false
  purgeBusy.value = true
  try {
    const res = await fetch('/api/admin/purge', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ confirm: true }),
    })
    const data = await res.json().catch(() => null)
    purgeNotice.value = { kind: res.ok ? 'ok' : 'err', text: data?.message ?? (res.ok ? '站已清空' : '清空失败') }
  } catch {
    purgeNotice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    purgeBusy.value = false
  }
}

const stats = ref(null) // 页头总览：/api/admin/stats
const collectState = ref(null) // 采集号情报：/api/stats/collect 原样（号从成员账号池来，站级号已取消）

// 站里的活（评论/弹幕归档、UP 名片、动态、编码加档）此刻在用谁的 B 站号：开机加载一次，打盹就空着不报。
async function loadCollectState() {
  try {
    collectState.value = await fetch('/api/stats/collect').then((r) => r.json())
  } catch {
    collectState.value = null
  }
}

const subscriptions = ref(null) // 订阅台账：/api/subscriptions
const subKind = ref('fav')
const subTarget = ref('')
const subBusy = ref(false)
const subNotice = ref(null) // { kind, text }

async function loadSubscriptions() {
  try {
    const res = await fetch('/api/subscriptions')
    subscriptions.value = res.ok ? await res.json() : []
  } catch {
    subscriptions.value = []
  }
}

async function addSubscription() {
  const target = subTarget.value.trim()
  if (!target || subBusy.value) return
  subBusy.value = true
  subNotice.value = null
  try {
    const res = await fetch('/api/subscriptions', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ kind: subKind.value, target }),
    })
    const data = await res.json().catch(() => null)
    if (res.ok) {
      subscriptions.value = data
      subTarget.value = ''
      subNotice.value = { kind: 'ok', text: '已订阅，每小时自动爬取等爬工上线' }
    } else {
      subNotice.value = { kind: 'err', text: data?.message ?? '订阅失败' }
    }
  } catch {
    subNotice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    subBusy.value = false
  }
}

async function removeSubscription(item) {
  if (subBusy.value) return
  subBusy.value = true
  try {
    const res = await fetch(`/api/subscriptions?kind=${encodeURIComponent(item.kind)}&target=${encodeURIComponent(item.target)}`, {
      method: 'DELETE',
    })
    const data = await res.json().catch(() => null)
    if (res.ok) {
      subscriptions.value = data?.list ?? []
      subNotice.value = null
    } else {
      subNotice.value = { kind: 'err', text: data?.message ?? '退订失败' }
    }
  } catch {
    subNotice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    subBusy.value = false
  }
}

function fmtCount(n) {
  if (n === null || n === undefined) return '—'
  if (n >= 100000000) return (n / 100000000).toFixed(1).replace(/\.0$/, '') + '亿'
  if (n >= 10000) return (n / 10000).toFixed(1).replace(/\.0$/, '') + '万'
  return String(n)
}

function fmtBytes(b) {
  if (b === null || b === undefined) return '—'
  if (b === 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let v = b
  let i = 0
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024
    i++
  }
  return `${v >= 100 || i === 0 ? Math.round(v) : v.toFixed(1)} ${units[i]}`
}

// ─── 悬停预览流水线：约 1MB 的 360p 无声片段，走 /data/previews，独立于 /media 与下载并发闸 ───
// 预览体积只在本卡上露脸，不进「分站统计」的库存体积（两回事：一个是看片前的预告片，一个是正片家底）。
const pv = ref(null) // /api/stats/preview 原样：{ enabled, workerState, roundDone, roundBatch, covered, pending, inflight, failed, nextRoundSeconds, lastRoundSeconds, previewBytes, avgBytes, res, seconds, batch, concurrency, yieldOnDownload, recent, failures }
const pvText = ref(null) // { kind, text }：页脚那一行小字兼作提示位
const pvBusy = ref(false) // 四枚动作钮共用
const pvSaving = ref(false) // 参数 POST 单独一个闸，免得动作钮把保存堵掉
const pvClearArmed = ref(false)
let pvClearTimer = null
let pvTimer = null
let pvTick = 0
let pvSaveTimer = null
let pvDraftDirty = false

// 回退用的单条基线常量表：以 30 秒片段为单（360/480 是这台机器 ffmpeg 8.0 实测，720 按 480p 外推、未实测）。
// 后端给了 unit（最近成功件的实测均值）就不碰这张表——推算不能扮实测。
const PV_UNIT = {
  360: { sec: 2.2, mb: 1.13, src: '本机 ffmpeg 8.0 实测' },
  480: { sec: 3.4, mb: 1.76, src: '本机 ffmpeg 8.0 实测' },
  720: { sec: 6.1, mb: 3.1, src: '按 480p 外推，未实测' },
}
const PV_BATCH_OPTIONS = [20, 40, 60]
// 回退基线的片段长度口径：常量按 30 秒量的，当前片段时长改了就线性缩放。
const PV_UNIT_BASE_SECONDS = 30
// 后端 unit 的样本量是「最近 20 条成功件」，样本数就是 min(covered, 20)。
const PV_UNIT_SAMPLES = 20

// 参数草稿：文本框走 700ms debounce，开关与下拉是明确动作、当场整包 POST。
const pvDraft = ref({ enabled: true, yieldOnDownload: true, res: 360, seconds: 45, batch: 40, concurrency: 1 })
// 每轮封顶的下拉：常用三档 + 库里的现值（万一是用 API 或旧版本设的不在这个表里，不能让下拉空选把值“吃”回去）。
const pvBatchOptions = computed(() => [...new Set([...PV_BATCH_OPTIONS, Number(pvDraft.value.batch) || 40])].sort((a, b) => a - b))
// 并发路数封顶 32（后端 ConcurrencyMax），日常建议 1～4：每一路 ffmpeg 吃一个核，NAS 上开高了
// 会把下载、页面应答与转码三边一起拖慢；而且换闸在下一轮开头，当场不生效。给下拉不给输数框：免得当场改完对不上数。
const PV_CONCURRENCY_OPTIONS = [1, 2, 4, 6, 8, 16, 32]

function pvSay(kind, text) {
  pvText.value = { kind, text }
}

async function loadPreviewStats() {
  try {
    const res = await fetch('/api/stats/preview')
    if (!res.ok) throw new Error(String(res.status))
    const data = await res.json()
    pv.value = data
    if (!pvDraftDirty) pvFillDraft(data) // 站长正在改的草稿不拿轮询结果盖掉
  } catch {
    pv.value = null // 404/5xx 一律显破折号，不让这张卡把站长室带走
  }
}

function pvFillDraft(s) {
  if (!s) return
  pvDraft.value = {
    enabled: !!s.enabled,
    yieldOnDownload: !!s.yieldOnDownload,
    res: Number(s.res) || 360,
    seconds: s.seconds ?? 45,
    batch: s.batch ?? 40,
    concurrency: s.concurrency ?? 1,
  }
}

function queuePvSave(immediate) {
  pvDraftDirty = true
  if (pvSaveTimer) {
    clearTimeout(pvSaveTimer)
    pvSaveTimer = null
  }
  if (immediate) {
    savePvSettings()
    return
  }
  pvSaveTimer = setTimeout(savePvSettings, 700)
}

async function savePvSettings() {
  if (pvSaveTimer) {
    clearTimeout(pvSaveTimer)
    pvSaveTimer = null
  }
  if (pvSaving.value) {
    // 上一发 POST 还在天上：把这一发续到稍后，不抹掉站长刚改的值
    pvSaveTimer = setTimeout(savePvSettings, 400)
    return
  }
  const d = pvDraft.value
  pvSaving.value = true
  try {
    const res = await fetch('/api/preview/settings', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        enabled: !!d.enabled,
        res: Number(d.res) || 360,
        seconds: Number(d.seconds) || 45,
        batch: Number(d.batch) || 40,
        concurrency: Number(d.concurrency) || 1,
        // 字段名必须与后端 PreviewSettingsRequest 一字不差：这里曾写成了 yield，
        // 绑不上 YieldOnDownload → 永远当「没改」处理，开关就看上去拔不动。
        yieldOnDownload: !!d.yieldOnDownload,
      }),
    })
    const data = await res.json().catch(() => null)
    pvSay(res.ok ? 'ok' : 'err', data?.message ?? (res.ok ? '参数已落库' : '参数没存上'))
    // 无论成败都放草稿旗：扣着它 loadPreviewStats 就不回填，开关被乐观翻过去后再没人能扳回来
    // （轮询看着对、屏上错）。失败后拉一次真相纠偏，后端真没起时它自己会显破折号。
    pvDraftDirty = false
    await loadPreviewStats()
  } catch {
    pvSay('err', '分站后端没有应答')
    pvDraftDirty = false // 同上：不放旗就纠不回来
    await loadPreviewStats()
  } finally {
    pvSaving.value = false
  }
}

async function postPreview(url, body) {
  const res = await fetch(url, body ? { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) } : { method: 'POST' })
  const data = await res.json().catch(() => null)
  return { ok: res.ok, message: data?.message }
}

async function pvBackfill() {
  if (pvBusy.value) return
  pvBusy.value = true
  try {
    const r = await postPreview('/api/preview/backfill')
    pvSay(r.ok ? 'ok' : 'err', r.message ?? (r.ok ? '已加塞一轮补采' : '补采没排上'))
    await loadPreviewStats()
  } catch {
    pvSay('err', '分站后端没有应答')
  } finally {
    pvBusy.value = false
  }
}

// 重试：不带 bvid 是「把失败的都捣回来」，带 bvid 是逐条捣（流水里那枚重试钮走这条）。
async function pvRetry(bvids) {
  if (pvBusy.value) return
  pvBusy.value = true
  try {
    const r = await postPreview('/api/preview/retry', bvids?.length ? { bvids } : undefined)
    pvSay(r.ok ? 'ok' : 'err', r.message ?? (r.ok ? (bvids?.length ? `已把 ${bvids.length} 条捣回待生成` : '失败件已捣回待生成') : '重试没排上'))
    await loadPreviewStats()
  } catch {
    pvSay('err', '分站后端没有应答')
  } finally {
    pvBusy.value = false
  }
}

// 补今天新货：走 backfill 的 bvids 参数（强制把指定条目排进本轮）——retry 只重置 Failed 行，
// 今天刚来、从来没生成过的条目根本没行，借它捐不动。捐动的名单走台账现拉，空名单不发 POST。
async function pvRetryToday() {
  if (pvBusy.value) return
  pvBusy.value = true
  try {
    const list = await fetch('/api/videos')
      .then((r) => (r.ok ? r.json() : []))
      .catch(() => [])
    if (!Array.isArray(list)) {
      pvSay('err', '馆藏台账拿不到，补不了今天新货')
      return
    }
    const day0 = new Date()
    day0.setHours(0, 0, 0, 0)
    const bvids = list
      .filter((v) => v.status === 2 && !v.previewUrl && Number.isFinite(Date.parse(v.createdAtUtc)) && Date.parse(v.createdAtUtc) >= day0.getTime())
      .map((v) => v.bvid)
    if (!bvids.length) {
      pvSay('ok', '今天到站的都已生成预览，没有欠账')
      return
    }
    const r = await postPreview('/api/preview/backfill', { bvids })
    pvSay(r.ok ? 'ok' : 'err', r.message ?? (r.ok ? `已把今天 ${bvids.length} 条新货排进本轮` : '补今天新货没排上'))
    await loadPreviewStats()
  } catch {
    pvSay('err', '分站后端没有应答')
  } finally {
    pvBusy.value = false
  }
}

// 清空预览：两击确认，第一击后 5 秒内不点第二击就自行解除（照一键清空分站那套）。
function armPvClear() {
  if (pvClearArmed.value || pvBusy.value) return
  pvClearArmed.value = true
  pvText.value = null
  pvClearTimer = setTimeout(() => {
    pvClearArmed.value = false
  }, 5000)
}

async function pvClear() {
  if (!pvClearArmed.value) {
    armPvClear()
    return
  }
  clearTimeout(pvClearTimer)
  pvClearArmed.value = false
  if (pvBusy.value) return
  pvBusy.value = true
  try {
    const r = await postPreview('/api/preview/clear', { confirm: true })
    pvSay(r.ok ? 'ok' : 'err', r.message ?? (r.ok ? '预览已全部清空，欠账重新记账' : '清空没成'))
    await loadPreviewStats()
  } catch {
    pvSay('err', '分站后端没有应答')
  } finally {
    pvBusy.value = false
  }
}

// 心跳：有件在生成时 2 秒一发，全站空闲降到 15 秒一发（照首页 pollTick 降拍思路）。
function pvBeat() {
  pvTick += 1
  const busy = (pv.value?.inflight ?? 0) > 0
  if (busy ? pvTick % 2 === 0 : pvTick % 15 === 0) loadPreviewStats()
}

const pvTotal = computed(() => (pv.value?.covered ?? 0) + (pv.value?.pending ?? 0) + (pv.value?.failed ?? 0))

function pvWidth(n) {
  if (!pv.value || !pvTotal.value) return '0%'
  return `${Math.max(0, Math.min(100, ((n ?? 0) / pvTotal.value) * 100))}%`
}

const pvCoverText = computed(() => {
  if (!pv.value || !pvTotal.value) return '—'
  return `${Math.round(((pv.value.covered ?? 0) / pvTotal.value) * 1000) / 10}%`
})

// 在岗/生成中/让路中/停摆/已关闭：后端定为 running|idle|paused|disabled|fault 五个值，走显式映射。
// 表外值一律按「在岗」，但把 workerState 原样带在文案里——不把未知说成坏了，也不把未知藏起来。
const PV_STATE_META = {
  running: { text: '生成中', tone: 'text-emerald-300', live: true },
  idle: { text: '在岗', tone: 'text-emerald-300', live: false },
  paused: { text: '让路中', tone: 'text-amber-300/85', live: false },
  disabled: { text: '已关闭', tone: 'text-white/35', live: false },
  fault: { text: '停摆', tone: 'text-rose-300/85', live: false },
}

const pvState = computed(() => {
  const s = pv.value
  if (!s) return { text: '未知', tone: 'text-white/30', live: false, rawText: '' }
  if (!s.enabled) return { ...PV_STATE_META.disabled, rawText: '' }
  const meta = PV_STATE_META[String(s.workerState ?? '').toLowerCase()]
  if (meta) return { ...meta, rawText: '' }
  return { text: '在岗', tone: 'text-emerald-300', live: false, rawText: s.workerState ? String(s.workerState) : '' }
})

// 卡题旁那枚进度徽章：worker 报 running 或台账有在途件都算「生成中」，与右侧状态灯同口径。
const pvBadge = computed(() => {
  const s = pv.value
  if (!s) return '状态未知'
  if (!s.enabled || String(s.workerState ?? '').toLowerCase() === 'disabled') return '开关已关'
  const round = `本轮 ${s.roundDone ?? 0}/${s.roundBatch ?? '—'}`
  const busy = (s.inflight ?? 0) > 0 || String(s.workerState ?? '').toLowerCase() === 'running'
  return busy ? `生成中 · ${round}` : `待命 · ${round}`
})

function pvDur(sec) {
  if (sec === null || sec === undefined) return '—'
  const n = Number(sec)
  if (!Number.isFinite(n)) return '—'
  const s = Math.round(n)
  if (s < 60) return `${s}s`
  const m = Math.floor(s / 60)
  const r = s % 60
  return r ? `${m}m${r}s` : `${m}m`
}

function pvIn(sec) {
  if (sec === null || sec === undefined) return '—'
  const n = Number(sec)
  if (!Number.isFinite(n)) return '—'
  const s = Math.round(n)
  if (s <= 0) return '就在眼下'
  if (s < 60) return `${s}秒后`
  return `${Math.round(s / 60)}分钟后`
}

// 欠账预估：后端 unit（最近成功件的实测均值 { seconds, costSeconds, bytes }）优先，
// 没数据才回退 30 秒基线常量表。两条路的来路各写各的，绝不把推算当实测显示。
const pvCost = computed(() => {
  const s = pv.value
  if (!s) return null
  const d = pvDraft.value
  const pending = s.pending ?? 0
  const total = (perSec) => (pending ? Math.max(1, Math.round(perSec * pending)) : 0)
  const unit = s.unit
  const costSec = Number(unit?.costSeconds)
  const unitBytes = Number(unit?.bytes)
  if (unit && Number.isFinite(costSec) && costSec > 0 && Number.isFinite(unitBytes) && unitBytes > 0) {
    // 样本数后端直接给了（unit.count），没给才拿 covered 封顶 20 条估。
    const given = Number(unit?.count)
    const samples = Number.isFinite(given) && given > 0 ? given : s.covered == null ? null : Math.min(s.covered, PV_UNIT_SAMPLES)
    const clipSec = Number(unit.seconds)
    const want = Number(d?.seconds ?? s.seconds)
    const drift = Number.isFinite(clipSec) && Number.isFinite(want) && clipSec !== want ? `基线量自 ${clipSec}s 片段，当前设 ${want}s` : ''
    const note = `按最近${samples == null ? '' : ` ${samples} 条`}实测${drift ? `（${drift}）` : ''}`
    return {
      pending,
      total: total(costSec),
      disk: fmtBytes(unitBytes * pending),
      baseline: `${costSec < 10 ? costSec.toFixed(1) : Math.round(costSec)}s / ${fmtBytes(unitBytes)}`,
      note,
      measured: true,
    }
  }
  // 回退：30 秒片段的常量基线按当前片段时长线性缩放，磁盘与耗时同比例。
  const res = Number(d?.res ?? s.res) || 360
  const unit0 = PV_UNIT[res] ?? PV_UNIT[360]
  const want = Number(d?.seconds ?? s.seconds) || 45
  const scale = Math.max(0.1, want / PV_UNIT_BASE_SECONDS)
  const perSec = unit0.sec * scale
  const perMb = unit0.mb * scale
  return {
    pending,
    total: total(perSec),
    disk: fmtBytes(Math.round(perMb * pending) * 1048576),
    baseline: `${perSec.toFixed(1)}s / ${perMb.toFixed(2)} MB`,
    note: `按 ${PV_UNIT_BASE_SECONDS} 秒片段基线推算 · ${res}p ${unit0.sec}s/${unit0.mb}MB（${unit0.src}）`,
    measured: false,
  }
})

// 逐件流水：本轮 recent 打底，再把往轮欠着的 failures 补在队尾，保证每条失败都有单条重试可点。
const pvRows = computed(() => {
  const recent = (pv.value?.recent ?? []).map((r, i) => ({ ...r, key: `r-${r.bvid}-${r.at ?? i}` }))
  const seen = new Set(recent.map((r) => r.bvid))
  const extra = (pv.value?.failures ?? [])
    .filter((f) => !seen.has(f.bvid))
    .map((f) => ({ bvid: f.bvid, ok: false, error: f.error, at: null, title: '', key: `f-${f.bvid}` }))
  return recent.concat(extra)
})

function pvClock(at) {
  if (at === null || at === undefined || at === '') return '—'
  const t = typeof at === 'number' ? (at < 1e12 ? at * 1000 : at) : Date.parse(at)
  if (!Number.isFinite(t)) return '—'
  const d = new Date(t)
  const pad = (n) => String(n).padStart(2, '0')
  return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`
}

// 实测元数据：360p 1.1MB 45s 用 2.3s —— 前四个各是各的：产物体积与产物时长来自 ffprobe，
// 「用 Xs」才是这一件 ffmpeg 实跑的耗时（后端 cost_seconds）。后端没下发 cost 的行就不显这一段，不拿产物时长扮耗时。
function pvMeta(row) {
  const bits = [`${Number(pv.value?.res ?? pvDraft.value?.res) || 360}p`]
  if (row.bytes != null) bits.push(fmtBytes(row.bytes))
  const s = Number(row.seconds)
  if (row.seconds != null && Number.isFinite(s) && s > 0) bits.push(`${s < 10 ? s.toFixed(1) : Math.round(s)}s`)
  const c = Number(row.cost)
  if (row.cost != null && Number.isFinite(c) && c > 0) bits.push(`用 ${c < 10 ? c.toFixed(1) : Math.round(c)}s`)
  return bits.join(' ')
}

// ─── 订阅爬工状态：每小时前后随机一轮 + 站长手动一轮 ───
const crawlStatus = ref(null) // { runAtUtc, results: [{kind,target,ok,added,duplicated,error}] }
const crawlBusy = ref(false)
const throttleStatus = ref(null) // { coolingDown, remainingSeconds }：风控冷却倒计时

const crawlText = computed(() => {
  if (!crawlStatus.value?.runAtUtc) return '爬工已上岗，约每小时随机抖一轮，还没跑过'
  const t = new Date(crawlStatus.value.runAtUtc).toLocaleString('zh-CN', { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' })
  return `爬工已上岗 · 最近一轮 ${t}`
})

// 战报只给现役订阅看：退订的条目不再露脸（KV 里的旧报告下一轮爬完会被刷新覆盖）。
const activeCrawlResults = computed(() => {
  const subs = new Set((subscriptions.value ?? []).map((s) => `${s.kind}:${s.target}`))
  return (crawlStatus.value?.results ?? []).filter((r) => subs.has(`${r.kind}:${r.target}`))
})

async function loadCrawlStatus() {
  try {
    const res = await fetch('/api/subscriptions/crawl-status')
    crawlStatus.value = res.ok ? await res.json() : null
  } catch {
    crawlStatus.value = null
  }
  try {
    const res = await fetch('/api/station/throttle')
    throttleStatus.value = res.ok ? await res.json() : null
  } catch {
    throttleStatus.value = null
  }
}

async function crawlNow() {
  if (crawlBusy.value) return
  crawlBusy.value = true
  subNotice.value = null
  try {
    const res = await fetch('/api/subscriptions/crawl', { method: 'POST' })
    if (res.ok) {
      crawlStatus.value = await res.json()
    } else {
      subNotice.value = { kind: 'err', text: `爬工没应答（${res.status}）` }
    }
  } catch {
    subNotice.value = { kind: 'err', text: '后端没应答' }
  } finally {
    crawlBusy.value = false
  }
}

// ─── 下载控制面板：暂停/继续/取消排队 + 队列快照（站长一键止血） ───
const dlQueue = ref(null) // { paused, intervalSec, downloading:[{id,bvid,title,coverUrl}], pending:[...], codecs:[{videoId,bvid,title,codec,pageIdx,waiting}] }
const dlBusy = ref(false)
const dlText = ref('')
const dlInterval = ref('0')
let dlTimer = null

async function loadDlQueue() {
  try {
    const res = await fetch('/api/downloads/queue')
    if (res.status === 401) {
      // 会话掉了（分站重启、站长登出等）：退回身份门，请站长重新扫码。
      adminAuth.value = 'needOwner'
      if (dlTimer) {
        clearInterval(dlTimer)
        dlTimer = null
      }
      return
    }
    dlQueue.value = res.ok ? await res.json() : null
    if (dlQueue.value) dlInterval.value = String(dlQueue.value.intervalSec ?? 0)
  } catch {
    // 后端打盹时留上一份快照，卡片不闪空
  }
}

async function dlSetInterval() {
  try {
    const res = await fetch('/api/downloads/interval', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ seconds: Number(dlInterval.value) }),
    })
    const body = await res.json().catch(() => null)
    dlText.value = body?.message ?? (res.ok ? '间隔已设置' : '间隔没设上')
    if (!res.ok) await loadDlQueue()
  } catch {
    dlText.value = '间隔没设上，稍后再试'
    await loadDlQueue()
  }
}

async function dlToggle() {
  if (dlBusy.value) return
  dlBusy.value = true
  try {
    const res = await fetch(dlQueue.value?.paused ? '/api/downloads/resume' : '/api/downloads/pause', { method: 'POST' })
    const body = await res.json().catch(() => null)
    dlText.value = body?.message ?? (res.ok ? '好了' : '操作没成，稍后再试')
    await loadDlQueue()
  } catch {
    dlText.value = '操作没成，稍后再试'
  } finally {
    dlBusy.value = false
  }
}

async function dlCancelAll() {
  const n = dlQueue.value?.pending?.length ?? 0
  if (!n || dlBusy.value) return
  if (
    !(
      await requestConfirm({
        title: '取消整个排队区',
        sub: `${n} 件在排`,
        message: `把排队区里 ${n} 件整批撤下。反悔容易：从首页卡阵重新排队就行。`,
        confirmText: '全部取消',
        cancelText: '再想想',
        icon: 'warn',
      })
    )
  ) {
    return
  }
  dlBusy.value = true
  try {
    const res = await fetch('/api/downloads/cancel', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' })
    const body = await res.json().catch(() => null)
    dlText.value = body?.message ?? '已取消'
    await loadDlQueue()
  } finally {
    dlBusy.value = false
  }
}

// 一键清理「已取消」：卡阵里的已取消遗留整批抖进回收站，可在回收站恢复。
async function dlPurgeCanceled() {
  const n = dlQueue.value?.canceledCount ?? 0
  if (!n || dlBusy.value) return
  if (
    !(
      await requestConfirm({
        title: '清理「已取消」',
        sub: `${n} 件遗留`,
        message: `卡阵里的 ${n} 件「已取消」遗留整批抖进回收站。抖进去的都躺在那儿，随时能恢复。`,
        confirmText: '抖进去',
        cancelText: '再想想',
        icon: 'trash',
      })
    )
  ) {
    return
  }
  dlBusy.value = true
  try {
    const res = await fetch('/api/admin/canceled/purge', { method: 'POST' })
    const body = await res.json().catch(() => null)
    dlText.value = body?.message ?? (res.ok ? '已清理' : '清理没成，稍后再试')
    await loadDlQueue()
  } catch {
    dlText.value = '清理没成，稍后再试'
  } finally {
    dlBusy.value = false
  }
}

// 加档行实况小注：让路中报「让路中 · Ns 后重投 · 第 X 次」，进站中报引擎进度「42% · 68.4 MB · 3.2 MB/s」；
// 数据全部来自队列接口的透传字段，5 秒一轮的轮询自然刷新。
function codecDetail(item) {
  if (!item.waiting) {
    const p = item.progress
    if (!p) return ''
    if (p.percentage > 0) {
      const bits = [`${Math.round(p.percentage)}%`]
      if (p.downloadedText) bits.push(p.downloadedText)
      if (p.speedText && p.speedText !== '—' && p.speedText !== '0 B/s') bits.push(p.speedText)
      return bits.join(' · ')
    }
    return p.phase === 'Queued' ? '待引擎起跑' : ''
  }
  const bits = ['让路中']
  if (item.retryInSec != null) bits.push(`${item.retryInSec}s 后重投`)
  if (item.retryCount > 0) bits.push(`第 ${item.retryCount} 次`)
  return bits.join(' · ')
}

// 取消一件编码加档：后端销引擎账+判 Failed，反悔在首页重点；卡死的僵尸行也可以这样手动超度。
async function codecCancel(item) {
  if (dlBusy.value) return
  if (
    !(
      await requestConfirm({
        title: '取消这份加档',
        sub: `${item.title || item.bvid} · ${codecLabel(item.codec)}${item.pageIdx != null ? ` P${item.pageIdx}` : ''}`,
        message: '撤下这份编码加档：引擎账销掉，这一路不再下。想再要，回到那支片子的播放器里点「加档其他版本」就行。',
        confirmText: '取消加档',
        cancelText: '再想想',
        icon: 'warn',
      })
    )
  ) {
    return
  }
  dlBusy.value = true
  try {
    const res = await fetch(`/api/videos/${item.videoId}/codecs/cancel`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ codec: item.codec, pageIdx: item.pageIdx ?? null }),
    })
    const body = await res.json().catch(() => null)
    dlText.value = body?.message ?? (res.ok ? '已取消' : '取消没成，稍后再试')
    await loadDlQueue()
  } catch {
    dlText.value = '取消没成，稍后再试'
  } finally {
    dlBusy.value = false
  }
}

async function dlCancelOne(id) {
  if (dlBusy.value) return
  try {
    const res = await fetch('/api/downloads/cancel', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ videoIds: [id] }),
    })
    const body = await res.json().catch(() => null)
    dlText.value = body?.message ?? '已取消'
    await loadDlQueue()
  } catch {
    dlText.value = '取消没成，稍后再试'
  }
}

// ─── 身份门：会员会话说了算（401=访客请扫码，403=普通成员送客去个人空间）───
const adminAuth = ref('loading') // loading | ok | needOwner | member | offline

function openLogin(mode) {
  loginDialogMode.value = mode
  loginDialogOpen.value = true
}

// ─── 分站成员：席位台账 + 邀请码发放（仅站长会话可用，后端 message 原样展示）───
const members = ref(null) // /api/admin/members 回包
const membersBusy = ref(false)
const memberText = ref('')
const memberNote = ref('')
const inviteCode = ref('')
const expandedMember = ref(null) // 展开看账号明细的成员 id

async function loadMembers() {
  try {
    const res = await fetch('/api/admin/members')
    members.value = res.ok ? await res.json() : []
  } catch {
    members.value = []
  }
}

async function sendInvite() {
  if (membersBusy.value) return
  membersBusy.value = true
  memberText.value = ''
  try {
    const res = await fetch('/api/admin/members/invite', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ note: memberNote.value.trim() }),
    })
    const data = await res.json().catch(() => null)
    if (res.ok) {
      inviteCode.value = data?.code ?? ''
      memberNote.value = ''
      await loadMembers()
      await loadPendingInvites() // 台账跟着刷新：刚发的那枚立刻出现在下面，不然像没发出去
    } else {
      memberText.value = data?.message ?? '发码没成'
    }
  } catch {
    memberText.value = '分站后端没有应答'
  } finally {
    membersBusy.value = false
  }
}

async function copyInvite() {
  if (!inviteCode.value) return
  try {
    await navigator.clipboard.writeText(inviteCode.value)
    memberText.value = '邀请码已复制，发给朋友即可'
  } catch {
    // 非安全上下文等拿不到剪贴板：降级手拷
    window.prompt('自动复制没成，手动拷走：', inviteCode.value)
  }
}

// ─── 发出去的邀请码：事后查 + 当场作废（11-08 站长拍板）───
// 以前发完码就成失忆的黑箱：谁也没记着发给过谁、发过几枚，朋友拿着三个月前的码照样能进门。
// 现在站长室列得出来（只列还活着的：一次性码兑现完 status 就变了，从这份清单自然消失），发错人当场作废。
// 作废只抽码不改席位形态：两条兑现路（扫码 MemberEndpoints / 账密注册 MemberAccountEndpoints）都经
// FindInvitedByCode 那一条判据，抽掉 invite_code 就同时断，不必在两条路上各补一个 if。
const pendingInvites = ref(null) // null=没拉到（与「真的一张没发出去」分两脸，照举报柜那套口径）
const invitesLoading = ref(false)
const inviteBusy = ref(null) // 正在作废的那一席 id

async function loadPendingInvites() {
  if (invitesLoading.value) return
  invitesLoading.value = true
  try {
    const res = await fetch('/api/admin/members/invites')
    if (!res.ok) {
      pendingInvites.value = null
      return
    }
    const data = await res.json().catch(() => null)
    pendingInvites.value = Array.isArray(data?.items) ? data.items : []
  } catch {
    pendingInvites.value = null
  } finally {
    invitesLoading.value = false
  }
}

async function revokeInvite(item) {
  if (inviteBusy.value !== null) return
  const verdict = await requestConfirm({
    title: '作废这枚邀请码',
    sub: `${item.code}${item.note ? ` · ${item.note}` : ''}`,
    message: '只抽码不改席位：这席还停在待邀，重新发一枚照旧能进。已经拿这枚码进过门的人不受影响（那一下早兑现完了）。',
    confirmText: '作废',
    cancelText: '再想想',
    danger: true,
    icon: 'trash',
  })
  if (!verdict) return
  inviteBusy.value = item.id
  memberText.value = ''
  try {
    const res = await fetch('/api/admin/members/invite/revoke', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ id: item.id }),
    })
    const data = await res.json().catch(() => null)
    memberText.value = data?.message ?? (res.ok ? '已作废' : '作废没成')
    if (res.ok) await loadPendingInvites()
  } catch {
    memberText.value = '分站后端没有应答'
  } finally {
    inviteBusy.value = null
  }
}

// 席位状态四档一句话（11-08 多了 exiled）：徽章的词与色都从这儿取，免得模板里再堆一层嵌套三元。
// 除名单独给一档而不与封禁同色：两者意图不同（封的是资格、除的是人），撞成一个灰球站长就看不出该按哪枚。
function seatStatusTag(status) {
  if (status === 'active') return { text: '启用', cls: 'border-emerald-400/40 text-emerald-300' }
  if (status === 'invited') return { text: '待邀', cls: 'border-amber-400/40 text-amber-300' }
  if (status === 'exiled') return { text: '已除名', cls: 'border-slate-500/40 text-slate-400' }
  return { text: '封禁', cls: 'border-rose-400/40 text-rose-300' }
}

async function memberAction(action, item) {
  if (membersBusy.value) return
  const seat = seatLabel(item)
  // 五颗钮五种后果，分开说：封禁只动进站资格（馆藏、弹幕、历史原地留着），除名是把人请出站但留席位行，
  // 撤席位连席位行一起收；召回与解禁各自是前两者的反向操作。
  const ask =
    action === 'delete'
      ? {
          title: '撤掉这个席位',
          sub: seat,
          message: '席位收回，人请出站；历史事件原样留着。想回来得重新领一次邀请码。',
          confirmText: '撤席位',
          cancelText: '再想想',
          danger: true,
          icon: 'out',
        }
      : action === 'ban'
        ? {
            title: '封禁这位旅人',
            sub: seat,
            message: '封掉的只是进站资格：即刻请出分站，再扫码也不收。馆藏、弹幕、历史都原地留着，解禁就复原。',
            confirmText: '封禁',
            cancelText: '再想想',
            danger: true,
            icon: 'warn',
          }
        : action === 'exile'
          ? {
              title: '除名这位旅人',
              sub: seat,
              message: '除名与封禁分家：封的是「今天不许进」，除的是「这个人请出站」——即刻出站，扫码与账密都不再收，得走「召回」才回来。席位行留着，历史名片、投稿归属、发过的弹幕都还点得开（撤席位才是连行一起收）。',
              confirmText: '除名',
              cancelText: '再想想',
              danger: true,
              icon: 'trash',
            }
          : action === 'restore'
            ? {
                title: '召回这位旅人',
                sub: seat,
                message: '拍回启用：他可以重新进站，也重新能投稿。',
                confirmText: '召回',
                cancelText: '再想想',
                icon: 'out',
              }
            : {
            title: '解禁这位旅人',
            sub: seat,
            message: '解除封禁，他可以重新进站逛了。',
            confirmText: '解禁',
            cancelText: '再想想',
            icon: 'warn',
          }
  if (!(await requestConfirm(ask))) return
  membersBusy.value = true
  memberText.value = ''
  try {
    const res = await fetch(`/api/admin/members/${action}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ id: item.id }),
    })
    const data = await res.json().catch(() => null)
    // 400（对站长自己动手）404 等：后端 message 原样递到脸上
    memberText.value = data?.message ?? (res.ok ? '好了' : '操作没成')
    if (res.ok) await loadMembers()
  } catch {
    memberText.value = '分站后端没有应答'
  } finally {
    membersBusy.value = false
  }
}

async function memberAccountDelete(acc, item) {
  if (membersBusy.value) return
  const who = acc.accountName || `B站账号 ${acc.accountMid}`
  if (
    !(
      await requestConfirm({
        title: '从席位上解绑这个号',
        sub: `${seatLabel(item)} · ${who}`,
        message: '这个 B 站号从账号池里销掉，存的 Cookie 也跟着没，不再替这位旅人出力；要再出力得重新粘一次。',
        confirmText: '解绑',
        cancelText: '再想想',
        danger: true,
        icon: 'out',
      })
    )
  ) {
    return
  }
  membersBusy.value = true
  memberText.value = ''
  try {
    const res = await fetch('/api/admin/members/account/delete', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ id: acc.id }),
    })
    const data = await res.json().catch(() => null)
    memberText.value = data?.message ?? (res.ok ? '已解绑' : '解绑没成')
    if (res.ok) await loadMembers()
  } catch {
    memberText.value = '分站后端没有应答'
  } finally {
    membersBusy.value = false
  }
}

// ─── 台账备份：station.db 快照档案柜（自动周期 + 手动补拍）───
const backupState = ref(null) // { intervalHours, keep, lastBackupUtc, files:[{name,bytes,modifiedUtc}] }
const backupBusy = ref(false)
const backupText = ref('')

async function loadBackups() {
  try {
    const res = await fetch('/api/backups')
    backupState.value = res.ok ? await res.json() : null
  } catch {
    // 后端打盹时留上一份
  }
}

async function backupNow() {
  if (backupBusy.value) return
  backupBusy.value = true
  try {
    const res = await fetch('/api/backups/run', { method: 'POST' })
    const body = await res.json().catch(() => null)
    backupText.value = body?.message ?? (res.ok ? '已落柜' : '快照没落成')
    await loadBackups()
  } catch {
    backupText.value = '快照没落成，稍后再试'
  } finally {
    backupBusy.value = false
  }
}

async function backupConfig() {
  if (backupBusy.value || !backupState.value) return
  backupBusy.value = true // 与 backupNow 同一把闸：安排没落定前不让再点，也不让两轮写互踩
  try {
    const res = await fetch('/api/backups/config', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ intervalHours: backupState.value.intervalHours, keep: backupState.value.keep }),
    })
    const body = await res.json().catch(() => null)
    backupText.value = body?.message ?? (res.ok ? '备份安排已更新' : '备份安排没存上')
    if (res.ok) await loadBackups()
  } catch {
    backupText.value = '分站后端没有应答，安排没更新'
  } finally {
    backupBusy.value = false
  }
}

async function backupDelete(name) {
  if (backupBusy.value) return
  if (
    !(
      await requestConfirm({
        title: '销掉这份快照',
        sub: name,
        message: '这份 station.db 快照从档案柜里抹掉，销了就翻不回来。柜里其它快照与当下这座分站都不受影响。',
        confirmText: '销掉',
        cancelText: '再想想',
        danger: true,
        icon: 'trash',
      })
    )
  ) {
    return
  }
  // 快照名来自后端列表，拼进 URL 前照样得编一下（与下面 backupDownload 的 /file 同一口径）
  const res = await fetch(`/api/backups/${encodeURIComponent(name)}`, { method: 'DELETE' })
  const body = await res.json().catch(() => null)
  backupText.value = body?.message ?? '已销'
  await loadBackups()
}

function fmtBackupTime(iso) {
  return iso ? new Date(iso).toLocaleString('zh-CN', { hour12: false }) : '—'
}

// 下载快照（站长 11-03「现在备份好都没地方下载」）：直接给 <a download> 指到站长门内那个口，
// 不走 fetch→blob——整站台账快照可能上百 MB，先囤进内存再存盘没道理，浏览器自己会起下载条。
function backupDownload(name) {
  const a = document.createElement('a')
  a.href = `/api/backups/${encodeURIComponent(name)}/file`
  a.download = name
  document.body.appendChild(a)
  a.click()
  a.remove()
}

// 下载面板那两列的封面（站长 11-03 点名的例外）：条目还没到站、盘上没件，本地档位件无从谈起；
// 而这里显示的本来就是 B 站图、本来也在连 B 站服务器，于是给它贴缩放参数（60×36 的格子拉 200 KB 原图最亏）。
// 缩放哪天被收了就把 @ 段摘掉退回原图，只重试一次（全站其余封面都不贴缩放参数，那批才叫「不怕 ban」）。
function queueCover(url) {
  return remoteCoverThumb(url, 160)
}
function onQueueCoverError(event, raw) {
  const el = event.target
  if (el.dataset.rawTried === '1') return
  el.dataset.rawTried = '1'
  const fallback = remoteCoverFallback(raw)
  if (fallback && fallback !== el.getAttribute('src')) el.src = fallback
}

// ─── 私信记录（管理员视野）：成员展开区拉与我的往来；「发私信」跳信箱预开会话 ───
const router = useRouter()
const dmLogMember = ref(null) // 展开私信记录的成员 id
const dmLog = ref(null)
const dmLogBusy = ref(false)

async function toggleDmLog(m) {
  if (dmLogMember.value === m.id) {
    dmLogMember.value = null
    dmLog.value = null
    return
  }
  dmLogMember.value = m.id
  dmLog.value = null
  dmLogBusy.value = true
  try {
    // M5.4：管理员视野锁席位 id 对（素净席位没 mid 也能查往来）
    const mine = member.value?.memberId
    const query = mine && m.id ? `?peerA=${mine}&peerB=${m.id}` : ''
    const res = await fetch(`/api/admin/dm${query}`)
    dmLog.value = res.ok ? (await res.json()).messages ?? [] : []
  } catch {
    dmLog.value = []
  } finally {
    dmLogBusy.value = false
  }
}

function openDmWith(m) {
  // 跳信箱预开会话：私信认席位，带 m.id 不带 mid
  router.push({ path: '/messages', query: { dm: String(m.id) } })
}

// 站长代重置密码（忘密自救/成员求助都走这）：展开区就地填新密，提交后收摊
const resetTarget = ref(null) // 正在代重置的成员 id
const resetPwd = ref('')
const resetBusy = ref(false)
const resetNotice = ref(null) // { kind, text, id }

function startReset(m) {
  if (resetTarget.value === m.id) {
    resetTarget.value = null
    resetPwd.value = ''
    return
  }
  resetTarget.value = m.id
  resetPwd.value = ''
  resetNotice.value = null
}

async function confirmReset(m) {
  if (resetBusy.value) return
  if (resetPwd.value.length < 8) {
    resetNotice.value = { kind: 'err', id: m.id, text: '至少 8 位' }
    return
  }
  resetBusy.value = true
  try {
    const res = await fetch(`/api/admin/members/${m.id}/password`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ password: resetPwd.value }),
    })
    const data = await res.json().catch(() => null)
    resetNotice.value = { kind: res.ok ? 'ok' : 'err', id: m.id, text: data?.message ?? '分站后端没有应答' }
    if (res.ok) {
      resetTarget.value = null
      resetPwd.value = ''
    }
  } catch {
    resetNotice.value = { kind: 'err', id: m.id, text: '分站后端没有应答' }
  } finally {
    resetBusy.value = false
  }
}

// 席位对人的展示名：昵称 → @站名 → 席位号（素净席位不再叫 UID null）
function seatLabel(m) {
  return m.nickname || m.username || `席位 ${m.id}`
}
function seatIdLine(m) {
  return `UID #${m.id}`
}

// ─── 全站公告：向 active 席位播送站内信（两段式防手滑，后端 message 原样展示）───
const announceTitle = ref('')
const announceBody = ref('')
const announceBusy = ref(false)
const announceText = ref('')
const announceArmed = ref(false)
let announceTimer = null

function announceArm() {
  if (announceArmed.value) return
  announceArmed.value = true
  announceText.value = '6 秒内不确认自动作废'
  if (announceTimer) clearTimeout(announceTimer)
  announceTimer = setTimeout(() => {
    announceArmed.value = false
    announceText.value = ''
  }, 6000)
}

async function sendAnnounce() {
  if (announceBusy.value) return
  const title = announceTitle.value.trim()
  const body = announceBody.value.trim()
  if (!title || !body) {
    announceText.value = '公告要有标题和正文'
    return
  }
  if (!announceArmed.value) {
    announceArm()
    return
  }
  announceArmed.value = false
  if (announceTimer) clearTimeout(announceTimer)
  announceBusy.value = true
  announceText.value = ''
  try {
    const res = await fetch('/api/admin/messages/announce', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ title, body }),
    })
    const data = await res.json().catch(() => null)
    if (res.ok) {
      announceText.value = `已送达 ${data?.sent ?? 0} 席`
      loadAnnounceBatches() // 台账跟着刷新：刚发的那一批得立刻出现在下面，不然像没发出去
      announceTitle.value = ''
      announceBody.value = ''
      setTimeout(() => { announceText.value = '' }, 4000)
    } else {
      announceText.value = data?.message ?? '播送没成'
    }
  } catch {
    announceText.value = '分站后端没有应答'
  } finally {
    announceBusy.value = false
  }
}

// ─── 发过的公告：按批撤回与恢复（11-08 站长拍板「公告发出去收不回」）───
// 只有 11-08 之后带批次号的播送列得出来：之前发的没记 batch_id，拿 title+body 去凑会误伤
// 同标题同正文的旧批（BroadcastToActive 一次 INSERT…SELECT 里所有行共用同一个 created_at_utc 串）。
// 撤回是打戳不是删行（库层 revoked_at_utc），所以恢复钮能把当初的送达原样复原，含谁已读过。
const announceBatches = ref(null) // null=没拉到（与「真的一条没发过」分两脸）
const announceBatchesLoading = ref(false)
const announceBatchBusy = ref(null) // 正在动的那一批 batchId
const announceBatchText = ref('')

async function loadAnnounceBatches() {
  if (announceBatchesLoading.value) return
  announceBatchesLoading.value = true
  try {
    const res = await fetch('/api/admin/messages/announces')
    if (!res.ok) {
      announceBatches.value = null
      return
    }
    const data = await res.json().catch(() => null)
    announceBatches.value = Array.isArray(data?.items) ? data.items : []
  } catch {
    announceBatches.value = null
  } finally {
    announceBatchesLoading.value = false
  }
}

// watch 摆在下面 quietOpen 声明之后：setup 里提前碰它就是 TDZ 当场报（先声明、后观察）。

async function revokeAnnounce(batch) {
  if (announceBatchBusy.value !== null) return
  const verdict = await requestConfirm({
    title: '收回这次播送',
    sub: `${batch.title} · 当初送达 ${batch.sent} 席`,
    message: '成员那边的铃铛与信箱一并干净（打时间戳不删行，反悔有恢复钮）。发错了就收，不必硬扛。',
    confirmText: '收回',
    danger: true,
    icon: 'trash',
  })
  if (!verdict) return
  await runAnnounceBatch('revoke', batch, '收回')
}

// 恢复不必确认：它本身就一键可逆，而且只把本来就该在那儿的信放回去。
async function restoreAnnounce(batch) {
  if (announceBatchBusy.value !== null) return
  await runAnnounceBatch('restore', batch, '恢复')
}

async function runAnnounceBatch(verb, batch, label) {
  announceBatchBusy.value = batch.batchId
  announceBatchText.value = ''
  try {
    const res = await fetch(`/api/admin/messages/announce/${verb}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ batchId: batch.batchId }),
    })
    const data = await res.json().catch(() => null)
    // 后端那句人话原样上屏（站内铁律），失败也拿它说话，前端不自编一套。
    announceBatchText.value = data?.message ?? (res.ok ? `已${label}` : `${label}没成`)
    await loadAnnounceBatches() // 重拉：revoked 状态与钮得跟着换脸
  } catch {
    announceBatchText.value = '分站后端没有应答'
  } finally {
    announceBatchBusy.value = null
  }
}

// ─── 事件台账（11-08 补上查阅界面）───
// 后端 GET /api/admin/events 早就写好了（身份 / 事件名前缀 / 条数三筛加总数，注释还说是「未来 AI 分析的取数口」），
// 前端一直没人调，库里已攒一千九百多条只增不删。这块给站长查「谁撞过门、谁改过稿、谁在试密码」，
// 不必再拿 sqlite 手搓；筛子与后端同名（actor / prefix / limit），不自造一套。
const eventsRows = ref(null) // null=没拉到（与「真的一条没有」分两脸，照举报柜那套口径）
const eventsTotal = ref(0)
const eventsLoading = ref(false)
const eventsNotice = ref('')
const eventsActor = ref('') // '' = 全部；guest 访客 / member 席位 / admin 站长 / system 站内
const eventsPrefix = ref('') // 事件名前缀：playback. 是全部观映账（改名前的老行是 watch.，族筛里单列一枚），admin. 是被门挡下的那些，login. 是进门那一串
const eventsLimit = ref(200)
const eventsOpen = ref(null) // 展开看详情的那一行 id：只开一行，读起来不散
// 摆多少行由这一格管：默认十条，每按一次往上加 50 行（11-09 站长：不要一展开就是 200 行的墙）
const EVENTS_PEEK = 10
const EVENTS_STEP = 50
const eventsShownCount = ref(EVENTS_PEEK)

// 事件名中文化：库里存的是后端那 ASCII 名（它是账本的字面，不能改），这儿只给站长看的那一层换皮。
// 认不出来的原样贴出（并在 title 与详情里留原名），绝不替它编一个像样的说法。
const eventsShown = computed(() => eventsRows.value?.slice(0, eventsShownCount.value) ?? [])
const allEventsShown = computed(() => (eventsRows.value?.length ?? 0) <= eventsShownCount.value)
// 同一枚钮干两件事：没摆全就往上加 50 行，摆全了就一拍退回十条（所以下面那句文案在两端都说得准）
function stepEvents() {
  if (allEventsShown.value) {
    eventsShownCount.value = EVENTS_PEEK
    return
  }
  eventsShownCount.value = Math.min(eventsShownCount.value + EVENTS_STEP, eventsRows.value?.length ?? 0)
}
const EVENT_LABEL = {
  'site.visit': '访问分站', 'search.query': '搜索', 'card.click': '点卡片',
  'watch.open': '进放映厅', 'home.open': '进首页', 'watch.heartbeat': '观映心跳',
  'watch.seek': '拖动进度', 'watch.blocked': '观映被挡', 'danmaku.send': '发弹幕',
  // 11-15 观映族改名 watch.*→playback.*：新行走下面这几枚，上面 watch.* 那串留给改名前的老台账（账不迁，标签双挂）
  'playback.open': '进放映厅', 'playback.play': '按下播放', 'playback.pause': '按下暂停',
  'playback.leave': '离开放映厅', 'playback.heartbeat': '观映心跳', 'playback.seek': '拖动进度', 'playback.blocked': '观映被挡',
  'download.link': '取播放直链', 'video.submit': '投稿', 'video.arrived': '稿件到站',
  'video.failed': '稿件下载失败', 'video.removed': '稿件删除', 'video.settings': '改稿件设置',
  'member.invite': '发邀请码', 'member.invite.revoke': '作废邀请码', 'member.ban': '封禁席位',
  'member.unban': '解封席位', 'member.delete': '撤席位', 'member.exile': '除名席位',
  'member.restore': '召回席位', 'member.nickname': '改席位名号', 'member.set_primary': '设主采集号',
  'member.drop_account': '摘 B 站号', 'member.register': '注册席位', 'member.password.change': '改密码',
  'admin.announce': '发公告', 'admin.announce.revoke': '撤公告', 'admin.agent.persona': '看板娘性格卡',
  'danmaku.recycle': '弹幕进回收站', 'danmaku.recycle.restore': '恢复弹幕',
  'codec.recycle': '加档进回收站', 'codec.recycle.restore': '恢复加档版本', 'codec.recycle.purge': '销毁加档版本',
  // 成员回收站弹窗（11-16）的两枚：恢复是自己请回；「销毁」只是盖隐藏戳——台账对站长说实话，货没真没。
  'video.recycle.self.restore': '成员自恢复视频', 'video.recycle.self.hide': '成员销毁视频（实为隐藏，货还封着）',
  'login.qr.start': '发起扫码', 'login.qr.scanned': '扫码已扫', 'login.qr.success': '扫码进门',
  'login.qr.expired': '扫码过期', 'login.qr.denied': '扫码被拒', 'login.logout': '退席',
  'login.password': '账密进门', 'login.password.denied': '账密被拒',
  'admin.action': '站长事务', 'admin.blocked': '被门挡下',
}
// 事件族：拿来做筛子与未命中时的前缀，后缀仍是 ASCII 的原样贴出（它是查询用的钥匙，不是给人读的）
const EVENT_FAMILY = [
  { prefix: '', label: '全部事件' },
  { prefix: 'playback.', label: '观映（进厅 / 心跳 / 拖进度 / 被挡）' },
  { prefix: 'watch.', label: '观映 · 旧（11-15 改名前的老账）' },
  { prefix: 'login.', label: '进门（扫码 / 账密 / 退席）' },
  { prefix: 'video.', label: '稿件（投稿 / 到站 / 失败 / 删除 / 改设置）' },
  { prefix: 'member.', label: '席位（邀请 / 封禁 / 除名 / 改名 / 采号）' },
  { prefix: 'danmaku.', label: '弹幕（发 / 回收 / 恢复）' },
  { prefix: 'codec.', label: '加档（回收 / 恢复 / 销毁）' },
  { prefix: 'admin.', label: '站长事务与公告' },
  { prefix: 'search.', label: '搜索' },
  { prefix: 'site.', label: '访问分站' },
  { prefix: 'home.', label: '进首页' },
  { prefix: 'card.', label: '点卡片' },
  { prefix: 'download.', label: '取播放直链' },
]
const SUBJECT_LABEL = {
  member: '席位', qr: '扫码票', video: '稿件', account: 'B 站号', batch: '公告批',
  path: '路径', query: '搜索词', page: '分 P', card: '卡片', comment: '评论',
  danmaku: '弹幕', up: 'UP 主', collection: '分区', codec: '加档', session: '会话',
}
function eventLabel(name) {
  if (!name) return '—'
  if (EVENT_LABEL[name]) return EVENT_LABEL[name]
  return name // 未命中就贴原名：宁可见着一串 ASCII 也不拿猜测冒充台账
}
function subjectLabel(type) {
  if (!type) return ''
  return SUBJECT_LABEL[type] ?? type
}

async function loadEventsLedger() {
  if (eventsLoading.value) return
  eventsLoading.value = true
  eventsNotice.value = ''
  try {
    const q = new URLSearchParams({ limit: String(eventsLimit.value) })
    if (eventsActor.value) q.set('actor', eventsActor.value)
    if (eventsPrefix.value.trim()) q.set('prefix', eventsPrefix.value.trim())
    const res = await fetch(`/api/admin/events?${q}`)
    if (!res.ok) {
      eventsRows.value = null // 拒客与空仓是两回事
      eventsNotice.value = '台账没拉到（多半是身份过期，重新扫个码再来）'
      return
    }
    const data = await res.json().catch(() => null)
    eventsRows.value = Array.isArray(data?.events) ? data.events : []
    eventsTotal.value = data?.total ?? eventsRows.value.length
    eventsShownCount.value = EVENTS_PEEK // 重拉或换筛子都回到十条：刚筛出来那一批该从头看，不该沿用上回加到的位置
  } catch {
    eventsRows.value = null
    eventsNotice.value = '分站后端没有应答'
  } finally {
    eventsLoading.value = false
  }
}

const EVENTS_ACTOR_LABEL = { guest: '访客', member: '席位', admin: '站长', system: '站内' }
function eventsActorLabel(actor) {
  return EVENTS_ACTOR_LABEL[actor] ?? actor ?? '—'
}

// 事件名后头那点人话：前缀相同的一眼扫过去容易糊，给几个常见族补一句说明（摆不出来的原样显示，不硬编）。
function eventsSubjectText(row) {
  if (!row.subjectType && !row.subjectId) return ''
  return `${subjectLabel(row.subjectType) || '对象'} ${row.subjectId || '—'}`
}

// payload 是后端塞的 JSON 串，展开时才格式化：读不懂的原文照贴，不替它编个好看的形状。
function eventsPayloadText(row) {
  if (!row.payload) return '（这条没带详情）'
  try {
    return JSON.stringify(JSON.parse(row.payload), null, 2)
  } catch {
    return row.payload
  }
}

// ─── 弹幕回收站（11-08）：单删弹幕不再不可逆 ───
// 后端 StationDb.DanmakuRecycle + AdminDanmakuRecycleEndpoints 已经齐了，这卡负责把它接上：
// 删掉的弹幕（本人自删 self / 站长按举报单删 report / 站长直接删 admin）都在这里躺着，能恢复也能销毁。
// 与整片回收站（RecycleBinCard）分家两张卡：那卡管稿件连文件一起的销账，这卡只管一行弹幕，
// 措辞口径向它看齐——只有「销毁 / 清空」才是真没了，得说透；恢复得说明白片子不在架时捐不动。
const dmRecycle = ref(null) // null=没拉到（与真空仓分两脸，照举报柜那套口径）
const dmRecycleTotal = ref(0)
const dmRecycleLoading = ref(false)
const dmRecycleBusy = ref(null) // 正在动的行 id（'all' 表示清空）
const dmRecycleNotice = ref(null) // { kind: 'ok' | 'err', text }：后端那句人话原样上屏

async function loadDmRecycle() {
  if (dmRecycleLoading.value) return
  dmRecycleLoading.value = true
  try {
    const res = await fetch('/api/admin/danmaku-recycle?limit=500')
    if (!res.ok) {
      dmRecycle.value = null
      return
    }
    const data = await res.json().catch(() => null)
    dmRecycle.value = Array.isArray(data?.items) ? data.items : []
    dmRecycleTotal.value = data?.total ?? dmRecycle.value.length
  } catch {
    dmRecycle.value = null
  } finally {
    dmRecycleLoading.value = false
  }
}

// 谁动的手三种来源，一眼得分清：self 是发送者自己反悔，report 是站长替举报单动的刀，admin 是站长直接删。
const DM_RECYCLE_SOURCE = { self: '本人收回', report: '按举报删的', admin: '站长删的' }
function dmRecycleSourceLabel(source) {
  return DM_RECYCLE_SOURCE[source] ?? source ?? '来历不明'
}

async function restoreDmRecycle(item) {
  if (dmRecycleBusy.value !== null) return
  dmRecycleBusy.value = item.id
  dmRecycleNotice.value = null
  try {
    const res = await fetch(`/api/admin/danmaku-recycle/${item.id}/restore`, { method: 'POST' })
    const data = await res.json().catch(() => null)
    dmRecycleNotice.value = {
      kind: res.ok ? 'ok' : 'err',
      text: data?.message ?? (res.ok ? '已恢复，这条重新飘得起来' : '恢复没成，这条还躺在回收站里'),
    }
  } catch {
    dmRecycleNotice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    dmRecycleBusy.value = null
    await loadDmRecycle()
  }
}

async function purgeDmRecycle(item) {
  if (dmRecycleBusy.value !== null) return
  const verdict = await requestConfirm({
    title: '销毁这一条弹幕',
    sub: `${item.bvid || '已下架'} · P${(item.pageIdx ?? 0) + 1} · ${dmReportClock(item.tSec)}`,
    message: `「${(item.text || '').slice(0, 40)}」这一行快照就地抹掉，之后恢复不回来。只是不想让它再飘的话用「恢复」再删一次就行，别在这儿断后路。`,
    confirmText: '销毁',
    cancelText: '再想想',
    danger: true,
    icon: 'trash',
  })
  if (!verdict) return
  dmRecycleBusy.value = item.id
  dmRecycleNotice.value = null
  try {
    const res = await fetch(`/api/admin/danmaku-recycle/${item.id}`, { method: 'DELETE' })
    const data = await res.json().catch(() => null)
    dmRecycleNotice.value = { kind: res.ok ? 'ok' : 'err', text: data?.message ?? (res.ok ? '已销毁' : '销毁没成') }
  } catch {
    dmRecycleNotice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    dmRecycleBusy.value = null
    await loadDmRecycle()
  }
}

async function clearDmRecycle() {
  if (dmRecycleBusy.value !== null) return
  const verdict = await requestConfirm({
    title: '清空弹幕回收站',
    message: '回收站里这些弹幕快照一并抹掉，之后一条都恢复不回来。馆藏与举报单不受影响——这一步只断弹幕的后路。',
    confirmText: '清空',
    cancelText: '再想想',
    danger: true,
    icon: 'trash',
  })
  if (!verdict) return
  dmRecycleBusy.value = 'all'
  dmRecycleNotice.value = null
  try {
    const res = await fetch('/api/admin/danmaku-recycle/clear', { method: 'POST' })
    const data = await res.json().catch(() => null)
    dmRecycleNotice.value = { kind: res.ok ? 'ok' : 'err', text: data?.message ?? (res.ok ? '已清空' : '清空没成') }
  } catch {
    dmRecycleNotice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    dmRecycleBusy.value = null
    await loadDmRecycle()
  }
}

// ─── 加档版本回收站（11-08）：已删除的加档连行带件封在这里，整组恢复 / 整组销毁 ───
// 后端 AdminCodecRecycleEndpoints + StationDb.CodecRecycle 已齐，这卡负责把它接上。
// <b>一次删除 = 一个组</b>：点单是整片点的，删一版 hevc 会落 N 行（每分 P 一行），所以这里按 groupId 归堆，
// 一张卡 = 当初那一次删除，恢复与销毁都整组走，不让站长按 N 下钮。
// 两枚钮只站长按得动（口挂在 /api/admin/ 下）：投稿人删得动自己那版的件，反悔的话语权不跟着放开。
const codecRecycle = ref(null) // null=没拉到（与真空仓分两脸，照弹幕回收站那套口径）
const codecRecycleTotal = ref(0)
const codecRecycleLoading = ref(false)
const codecRecycleBusy = ref(null) // 正在动的组 id
const codecRecycleNotice = ref(null) // { kind: 'ok' | 'err', text }：后端那句人话原样上屏

async function loadCodecRecycle() {
  if (codecRecycleLoading.value) return
  codecRecycleLoading.value = true
  try {
    const res = await fetch('/api/admin/codec-recycle?limit=500')
    if (!res.ok) {
      codecRecycle.value = null
      return
    }

    const data = await res.json().catch(() => null)
    codecRecycle.value = Array.isArray(data?.items) ? data.items : []
    codecRecycleTotal.value = data?.total ?? codecRecycle.value.length
  } catch {
    codecRecycle.value = null
  } finally {
    codecRecycleLoading.value = false
  }
}

// 把行归成组：页码排成一行、字节累加、件数分「搬成了」与「当初盘上就没这一版」两叠。
// 组里只要有一行片子已不在架，整组就捐不动（后端也是整组拒），所以 inStation 取与。
const codecRecycleGroups = computed(() => {
  const groups = []
  const index = new Map()
  for (const item of codecRecycle.value ?? []) {
    if (!index.has(item.groupId)) {
      const group = {
        groupId: item.groupId,
        videoId: item.videoId,
        bvid: item.bvid,
        codec: item.codec,
        pages: [],
        bytes: 0,
        files: 0,
        fileless: 0,
        deletedAtUtc: item.deletedAtUtc,
        deletedBy: item.deletedBy,
        source: item.source,
        inStation: item.inStation !== false,
      }
      index.set(item.groupId, group)
      groups.push(group)
    }

    const group = index.get(item.groupId)
    group.pages.push((item.pageIdx ?? 0) + 1)
    group.bytes += item.bytes || 0
    if (item.fileStored) group.files += 1
    else group.fileless += 1
    if (item.inStation === false) group.inStation = false
  }

  return groups
})

// 谁动的手：member = 投稿人自管删的，admin = 站长删的（台账里分得清，这里才有脸说「这位删的」）。
const CODEC_RECYCLE_SOURCE = { member: '投稿人删的', admin: '站长删的' }
function codecRecycleSourceLabel(source) {
  return CODEC_RECYCLE_SOURCE[source] ?? source ?? '来历不明'
}

async function restoreCodecGroup(group) {
  if (codecRecycleBusy.value !== null) return
  codecRecycleBusy.value = group.groupId
  codecRecycleNotice.value = null
  try {
    const res = await fetch(`/api/admin/codec-recycle/${group.groupId}/restore`, { method: 'POST' })
    const data = await res.json().catch(() => null)
    codecRecycleNotice.value = {
      kind: res.ok ? 'ok' : 'err',
      text: data?.message ?? (res.ok ? '已恢复，这一版重新回到货架' : '恢复没成，这一组还完整躺在回收站里'),
    }
  } catch {
    codecRecycleNotice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    codecRecycleBusy.value = null
    await loadCodecRecycle()
  }
}

async function purgeCodecGroup(group) {
  if (codecRecycleBusy.value !== null) return
  const verdict = await requestConfirm({
    title: `销毁这一组加档（${codecLabel(group.codec)}）`,
    sub: `${group.bvid || '已下架'} · 共 ${group.pages.length} 个分 P`,
    message: '回收目录里那些文件就地抹掉，台账也一并销掉，之后恢复不回来。只是不想让它再待在菜单上就用「恢复」再删一次，别在这儿断后路。',
    confirmText: '销毁',
    cancelText: '再想想',
    danger: true,
    icon: 'trash',
  })
  if (!verdict) return
  codecRecycleBusy.value = group.groupId
  codecRecycleNotice.value = null
  try {
    const res = await fetch(`/api/admin/codec-recycle/${group.groupId}`, { method: 'DELETE' })
    const data = await res.json().catch(() => null)
    codecRecycleNotice.value = { kind: res.ok ? 'ok' : 'err', text: data?.message ?? (res.ok ? '已销毁' : '销毁没成') }
  } catch {
    codecRecycleNotice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    codecRecycleBusy.value = null
    await loadCodecRecycle()
  }
}

// ─── 动态关注（M6）：盯梢名单管理 + 催巡一轮（两段式删，后端 message 原样展示）───
const dynTargets = ref(null)
const dynBusy = ref(false)
const dynText = ref('')
const dynDraft = ref('')
const dynArmedDelete = ref(null) // 同一行再点一次才真删
let dynArmedTimer = null
const dynRoundBusy = ref(false)

async function loadDynTargets() {
  try {
    const res = await fetch('/api/admin/dynamics/targets')
    dynTargets.value = res.ok ? await res.json() : []
  } catch {
    dynTargets.value = []
  }
}

async function addDynTarget() {
  if (dynBusy.value) return
  const value = dynDraft.value.trim()
  if (!value) {
    dynText.value = '先填个 MID 或空间链接'
    return
  }
  dynBusy.value = true
  dynText.value = ''
  try {
    const res = await fetch('/api/admin/dynamics/targets', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ mid: value }),
    })
    const data = await res.json().catch(() => null)
    dynText.value = data?.message ?? (res.ok ? '记下' : '添加没成')
    if (res.ok) {
      dynDraft.value = ''
      await loadDynTargets()
    }
  } catch {
    dynText.value = '分站后端没有应答'
  } finally {
    dynBusy.value = false
  }
}

async function removeDynTarget(t) {
  if (dynArmedDelete.value !== t.mid) {
    dynArmedDelete.value = t.mid
    if (dynArmedTimer) clearTimeout(dynArmedTimer)
    dynArmedTimer = setTimeout(() => { dynArmedDelete.value = null }, 5000)
    return
  }
  dynArmedDelete.value = null
  try {
    const res = await fetch(`/api/admin/dynamics/targets/${t.mid}`, { method: 'DELETE' })
    const data = await res.json().catch(() => null)
    dynText.value = data?.message ?? '撤了'
    await loadDynTargets()
  } catch {
    dynText.value = '撤除没成'
  }
}

async function dynRefreshRound() {
  if (dynRoundBusy.value) return
  dynRoundBusy.value = true
  try {
    const res = await fetch('/api/admin/dynamics/refresh', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' })
    const data = await res.json().catch(() => null)
    dynText.value = res.ok && data?.started ? '已催巡捕出门，稍后刷新橱窗' : '巡捕正在外头跑，等等再来'
  } catch {
    dynText.value = '催轮没递上去'
  } finally {
    dynRoundBusy.value = false
  }
}

// ─── 数据交换：勾选式导出打包 + 导入换库（档案柜 data/exports/，暂存 data/imports/）───
const expStats = ref(null) // {videos, cmtImg, avatars, emotes, cache, dbBytes}
const expPick = ref({ withMedia: false, withCmtImg: false, withCaches: false })
const expBusy = ref(false)
const expText = ref('')
const exportList = ref([])
const armedExport = ref(null) // 两段式销包：同一行再点一次才真删
let expTimer = null
let exportArmTimer = null

const cachesBytes = computed(() => (expStats.value?.avatars ?? 0) + (expStats.value?.emotes ?? 0) + (expStats.value?.cache ?? 0))

async function loadExportStats() {
  try {
    const res = await fetch('/api/admin/export/stats')
    expStats.value = res.ok ? await res.json() : null
  } catch {
    /* 后端打盹就不报体积 */
  }
}

async function loadExports() {
  try {
    const res = await fetch('/api/admin/exports')
    exportList.value = res.ok ? await res.json() : []
  } catch {
    exportList.value = []
  }
}

function startExport() {
  if (expBusy.value) return
  expBusy.value = true
  expText.value = ''
  fetch('/api/admin/export', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(expPick.value),
  })
    .then(async (res) => {
      const d = await res.json().catch(() => null)
      if (!res.ok || !d?.jobId) {
        expBusy.value = false
        expText.value = d?.message ?? '打包没发起成'
        return
      }
      pollExportJob(d.jobId)
    })
    .catch(() => {
      expBusy.value = false
      expText.value = '分站后端没有应答'
    })
}

// 大包在后台流式写盘，这里 2 秒一探，完了刷新档案柜
let disposed = false // 组件收了帘：自递归的探测不再续下一拍，也不再去拨已消失实例的 ref
function pollExportJob(jobId) {
  expTimer = setTimeout(async () => {
    if (disposed) return
    try {
      const res = await fetch(`/api/admin/export/jobs/${jobId}`)
      const d = await res.json().catch(() => null)
      if (disposed) return
      if (!res.ok) {
        expBusy.value = false
        expText.value = d?.error ?? '任务丢了'
        return
      }
      if (d?.state === 'running') {
        pollExportJob(jobId)
        return
      }
      expBusy.value = false
      if (d?.state === 'done') {
        expText.value = `包已出炉：${d.name}`
        await loadExports()
      } else {
        expText.value = d?.error ?? '打包失败'
      }
    } catch {
      expBusy.value = false
      expText.value = '分站后端没有应答'
    }
  }, 2000)
}

async function deleteExport(item) {
  if (armedExport.value !== item.name) {
    armedExport.value = item.name
    clearTimeout(exportArmTimer)
    exportArmTimer = setTimeout(() => {
      armedExport.value = null
    }, 5000)
    return
  }
  clearTimeout(exportArmTimer)
  armedExport.value = null
  try {
    await fetch(`/api/admin/exports/${encodeURIComponent(item.name)}`, { method: 'DELETE' })
  } catch {
    /* 删不动也照常重扫，列表见真章 */
  }
  await loadExports()
}

// 导入：上传暂存（解包+安检+验库）→ 预览 → 两段式应用 → 等站回来
const importBusy = ref(false)
const importText = ref('')
const importPreview = ref(null) // {stationName, exportedAtUtc, options, counts}
const importWarnings = ref([])
const importReceipt = ref('')
const applyArmed = ref(false)
const restarting = ref(false)
const importState = ref(null) // {pending, lastError}
let pendingTimer = null

async function loadImportState() {
  try {
    const res = await fetch('/api/admin/import/pending')
    importState.value = res.ok ? await res.json() : null
  } catch {
    /* 拿不到就不摆状态条 */
  }
}

async function onImportFile(event) {
  const file = event.target.files?.[0]
  event.target.value = ''
  if (!file || importBusy.value) return
  importBusy.value = true
  importText.value = ''
  importPreview.value = null
  importWarnings.value = []
  importReceipt.value = ''
  try {
    const form = new FormData()
    form.append('file', file)
    const res = await fetch('/api/admin/import/upload', { method: 'POST', body: form })
    const d = await res.json().catch(() => null)
    if (!res.ok) {
      importText.value = d?.error ?? '包没收成'
      return
    }
    importReceipt.value = d.receipt
    importPreview.value = d.preview
    importWarnings.value = d.warnings ?? []
  } catch {
    importText.value = '分站后端没有应答'
  } finally {
    importBusy.value = false
  }
}

async function applyImport() {
  if (!importReceipt.value || importBusy.value) return
  if (!applyArmed.value) {
    applyArmed.value = true
    setTimeout(() => {
      applyArmed.value = false
    }, 6000)
    return
  }
  applyArmed.value = false
  importBusy.value = true
  importText.value = ''
  try {
    const res = await fetch('/api/admin/import/apply', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ receipt: importReceipt.value }),
    })
    const d = await res.json().catch(() => null)
    if (!res.ok) {
      importText.value = d?.error ?? '导入没应用成'
      importBusy.value = false
      return
    }
    restarting.value = true
    watchRestart()
  } catch {
    importText.value = '分站后端没有应答'
    importBusy.value = false
  }
}

// 等站回魂：换库期间请求注定失败，pending 消失且 health 正常才算完
function watchRestart() {
  clearTimeout(pendingTimer)
  pendingTimer = setTimeout(async () => {
    try {
      const res = await fetch('/api/admin/import/pending')
      const d = await res.json().catch(() => null)
      importState.value = d
      const health = await fetch('/api/health')
        .then((r) => (r.ok ? r.json() : null))
        .catch(() => null)
      if (d && !d.pending && health?.ok) {
        restarting.value = false
        importBusy.value = false
        importReceipt.value = ''
        importPreview.value = null
        importWarnings.value = []
        loadBackups()
        loadMembers()
        loadExports()
        loadExportStats()
        return
      }
    } catch {
      /* 站正在换心，什么都拿不到才是常态 */
    }
    watchRestart()
  }, 2500)
}

async function bootAdmin() {
  // 先拿站长专属端点探身份：401=访客（请扫码），403=普通成员（送客去个人空间）。
  adminAuth.value = 'loading'
  try {
    const probe = await fetch('/api/admin/stats')
    if (probe.status === 401) {
      adminAuth.value = 'needOwner'
      return
    }
    if (probe.status === 403) {
      adminAuth.value = 'member'
      return
    }
    stats.value = probe.ok ? await probe.json() : null
    adminAuth.value = 'ok' // 牌对上了就要把门落下——早期只写了两张拒客分支，放行时反被卡在 loading
  } catch {
    // 探身份这一发都没回来，就是不知道对面是谁：拿 'ok' 放行只会换成一整页空仪表盘冒充正常。
    // 停在错误态，也不起下面的取数与轮询，交给重试钮重跑开机核身。
    adminAuth.value = 'offline'
    return
  }
  fetch('/api/health')
    .then((r) => r.json())
    .then((h) => (health.value = h))
    .catch(() => {})
  fetch('/api/settings')
    .then((r) => r.json())
    .then((s) => { settings.value = s; shareInput.value = s?.shareBaseUrl ?? '' })
    .catch(() => {})
  loadSubscriptions()
  loadCrawlStatus()
  loadCollectState()
  loadEngine()
  loadDlQueue()
  loadBackups()
  loadMembers()
  loadExportStats()
  loadExports()
  loadImportState()
  loadDynTargets()
  loadPreviewStats()
  loadDanmakuReports()
  if (!dlTimer) dlTimer = setInterval(loadDlQueue, 5000)
  if (!pvTimer) pvTimer = setInterval(pvBeat, 1000)
}

// ─── 弹幕举报（最小闭环）：分站朋友对站内弹幕递单，站长在这儿删弹幕或忽略 ───
// 只此一口：B 站档案弹幕（ass 通道）不归分站管，没有举报入口。
// 举报不预隐藏：pending 的弹幕照常飘，只有站长处置才消失（所以本卡不给「先藏起来」这种钮）。
const dmReportStatus = ref('pending') // 'pending' | 'all'
const dmReports = ref(null) // null = 没拉到（加载中或后端没应答），[] = 确实一张单子都没有
const dmReportLoading = ref(false)
const dmReportPending = ref(0) // h3 上的待处理徽章
const dmReportBusy = ref(null) // 正在处置的那张单 id：一颗在飞就把整卡的动作钮按死
const dmReportNotice = ref(null) // { kind: 'ok' | 'err', text }

async function loadDanmakuReports() {
  if (dmReportLoading.value) return
  dmReportLoading.value = true
  try {
    const res = await fetch(`/api/admin/danmaku-reports?status=${dmReportStatus.value}`)
    if (!res.ok) {
      dmReports.value = null // 拒客与空仓是两回事，别把 401 报成「天下太平」
      return
    }
    const data = await res.json().catch(() => null)
    dmReports.value = Array.isArray(data?.items) ? data.items : []
    dmReportPending.value = data?.pending ?? 0
  } catch {
    /* 后端打盹：不算空仓，交给卡里的重试条说话 */
    dmReports.value = null
  } finally {
    dmReportLoading.value = false
  }
}

function switchDmReportStatus(next) {
  if (dmReportStatus.value === next) return
  dmReportStatus.value = next
  loadDanmakuReports()
}

async function handleDanmakuReport(item, action) {
  if (dmReportBusy.value !== null) return
  dmReportBusy.value = item.id
  dmReportNotice.value = null
  try {
    const res = await fetch(`/api/admin/danmaku-reports/${item.id}/handle`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ action }),
    })
    const data = await res.json().catch(() => null)
    dmReportNotice.value = {
      kind: res.ok ? 'ok' : 'err',
      text: data?.message ?? (res.ok ? '已处置' : '处置没成，这张单子还在'),
    }
  } catch {
    dmReportNotice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    dmReportBusy.value = null
    await loadDanmakuReports() // 重拉一次：处置完的单子该从待处理里退场，徽章也跟着改数
  }
}

// 重开这张举报单（11-08 站长拍板「处置后能重开」）：处置只此一次是铁规，但铁规不等于不能反悔。
// 反悔走明路：单拍回 pending 可重新处置，当初删掉的弹幕从回收站恢复回来（后端一并把话说清）。
// 确认弹窗不删：重开会把一条已经消失的弹幕放回 public 视野，这一下该让站长看见自己要按什么。
async function reopenDanmakuReport(item) {
  if (dmReportBusy.value !== null) return
  const verdict = await requestConfirm({
    title: '重开这张举报单',
    sub: `${item.bvid || '已下架'} · P${(item.pageIdx ?? 0) + 1} · ${dmReportClock(item.tSec)}`,
    message: '单拍回待处理，能重新处置一次；当初删掉的弹幕会从回收站带回来（片子下架或被销毁就带不动，后端会跟你说清）。',
    confirmText: '重开',
    icon: 'out',
  })
  if (!verdict) return

  dmReportBusy.value = item.id
  dmReportNotice.value = null
  try {
    const res = await fetch(`/api/admin/danmaku-reports/${item.id}/reopen`, { method: 'POST' })
    const data = await res.json().catch(() => null)
    dmReportNotice.value = {
      kind: res.ok ? 'ok' : 'err',
      text: data?.message ?? (res.ok ? '已重开' : '重开没成，这张单还在原处'),
    }
  } catch {
    dmReportNotice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    dmReportBusy.value = null
    await loadDanmakuReports()
  }
}

// 片内时间点：举报单上是浮点秒，摆成 mm:ss（超一小时的片子就报 72:15 这种累加分钟，不装成时:分:秒）。
function dmReportClock(sec) {
  const n = Number(sec)
  if (!Number.isFinite(n) || n < 0) return '—'
  const pad = (x) => String(x).padStart(2, '0')
  const total = Math.floor(n)
  return `${pad(Math.floor(total / 60))}:${pad(total % 60)}`
}

// ─── 站长室排版：五组 + 顶部定位条（正文仍一屏到底，不切标签）───
// 十四张卡是同一个 grid 的孩子，分组靠 order 排座、靠 md:col-span-2 定整行还是半格：
// 这样分组只动 class，不必把卡块在文件里搬来搬去（搬一次就多一次丢代码的机会）。
const ADMIN_GROUPS = [
  // 组名走仓库比喻（柜台/进货口/观众席……）：胶囊是拿来跳位的，有点火气反而好记；
  // 但卡题里的「危险区」「一键清空分站」保持大白话，破坏性操作那一步不玩括令。
  // 原先还有「钥匙串」（放站级 B 站号的两张卡）：分站已不收站级号，采集号情报已搬到柜台的分站统计里。
  // 「观众席」与「库房」这两组则是设好就不碰的那一批，整组收进下面的「货架」抽屉，
  // 首屏只留天天盯的柜台与进货口（卡块不搬家，靠 order 排座 + quietOpen 一道闸）。
  // 悬停预览也下了柜（仪表盘不是天天睥的）；抽屉内下载设置压底。
  { id: 'overview', title: '柜台', hint: '家底与健康度，扫一眼', order: 10 },
  { id: 'harvest', title: '进货口', hint: '下载 · 订阅 · 动态关注', order: 20 },
]
const ADMIN_GROUP_KEY = 'lbs-admin-group' // 定位条高亮记住上次点过的组（进页恢复；滚动侦测会接着自己改口）

function savedAdminGroup() {
  try {
    const saved = localStorage.getItem(ADMIN_GROUP_KEY)
    return ['overview', 'harvest', 'quiet', 'recycle', 'events'].includes(saved) ? saved : 'overview'
  } catch {
    return 'overview' // 隐私模式里 localStorage 会抛：照常从柜台开始
  }
}

const activeGroup = ref(savedAdminGroup())
const dangerOpen = ref(false) // 危险区默认收起：常年在底下亮着红边，看久了就当背景板，误点机会少一层
// ─── 看板娘性格卡（11-16 站长令：默认 prompt 暴露可改）───
// 性格与说话风格站长随便写，全站成员共用一张；日期、成员名、家底数字与数据查账规矩由站里每轮自动注入，不进这张卡。
// 留空 = 用内置默认（placeholder 就是默认原文，站长所见即所得）。
const personaDraft = ref('')
const personaDefault = ref(null) // null = 还没拉；拉到后是默认原文（placeholder 的料）
const personaIsCustom = ref(false)
const personaBusy = ref(false)
const personaMaxChars = ref(4000)
const personaNotice = ref(null) // {kind:'ok'|'err', text}
async function loadPersona() {
  try {
    const res = await fetch('/api/admin/agent/persona')
    const data = await res.json().catch(() => null)
    if (!res.ok) throw new Error(data?.message || `HTTP ${res.status}`)
    personaDraft.value = data?.persona ?? ''
    personaDefault.value = data?.defaultPersona ?? ''
    personaIsCustom.value = data?.isCustom === true
    personaMaxChars.value = data?.maxChars ?? 4000
  } catch (e) {
    personaNotice.value = { kind: 'err', text: `性格卡没拉到：${e.message}` }
  }
}
async function savePersona() {
  if (personaBusy.value) return
  personaBusy.value = true
  try {
    const res = await fetch('/api/admin/agent/persona', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ persona: personaDraft.value }),
    })
    const data = await res.json().catch(() => null)
    if (!res.ok) throw new Error(data?.message || `HTTP ${res.status}`)
    personaIsCustom.value = data?.isCustom === true
    personaNotice.value = { kind: 'ok', text: personaIsCustom.value ? '性格卡已存，她下一轮开口就换这副嗓子' : '性格卡清空了，她回到站内原性' }
  } catch (e) {
    personaNotice.value = { kind: 'err', text: `存卡没成：${e.message}` }
  } finally {
    personaBusy.value = false
  }
}
function restorePersonaDefault() {
  personaDraft.value = ''
  savePersona()
}

// ─── 失败件明细（11-16 站长令：Failed 的 last_error 沉在首页卡阵最尾，后台只看得到页头一个数）───
// 数据同源首页卡阵 /api/videos（站长尺本就带 Failed 行，status 数值 3），动作全走首页那三扇现成门：
// requeue（重试；孤儿稿回 409 requireClaim → 本行换成「挂我名下重投」）、claim（挂到站长名下并顺手退队）、DELETE（进回收站）。
// 文案全是后端写好的中文，前端只负责贴进条——全站铁律。
const failedItems = ref(null) // null = 还没拉；数组 = Failed 行
const failedBusy = ref(null) // 在途那行的 id，防连点
const failedNotice = ref(null) // {kind:'ok'|'err'|'warn', text}
const failedArmed = ref(null) // 销毁二段式：哪条正等第二下
let failedArmTimer = null
let failedNoticeTimer = null

function fmtFailedAge(iso) {
  if (!iso) return ''
  const t = Date.parse(iso)
  if (Number.isNaN(t)) return ''
  const days = Math.floor((Date.now() - t) / 86400000)
  if (days <= 0) {
    const hours = Math.floor((Date.now() - t) / 3600000)
    return hours <= 0 ? '在途未满一小时' : `在途 ${hours} 小时`
  }
  return `在途 ${days} 天`
}

function sayFailed(kind, text) {
  failedNotice.value = { kind, text }
  if (failedNoticeTimer) clearTimeout(failedNoticeTimer)
  failedNoticeTimer = setTimeout(() => (failedNotice.value = null), 6000)
}

async function loadFailed() {
  try {
    const res = await fetch('/api/videos')
    const data = await res.json().catch(() => null)
    if (!res.ok) throw new Error(`HTTP ${res.status}`)
    failedItems.value = Array.isArray(data) ? data.filter((v) => v.status === 3) : []
    failedArmed.value = null
  } catch (e) {
    failedNotice.value = { kind: 'err', text: `失败件没拉到：${e.message}` }
  }
}

async function failedAction(video, mode) {
  if (failedBusy.value) return
  failedBusy.value = video.id
  try {
    if (mode === 'retry') {
      const res = await fetch(`/api/videos/${video.id}/requeue`, { method: 'POST' })
      const body = await res.json().catch(() => null)
      if (res.status === 409 && body?.requireClaim) {
        // 无归属孤儿稿：后端单列 requireClaim 旗，这一行就挂成待认领，文案照贴后端那句
        video.requireClaim = true
        sayFailed('warn', body.message ?? '这条稿子没有投稿人席位')
      } else {
        sayFailed(res.ok ? 'ok' : 'err', body?.message ?? (res.ok ? '已退回队列' : '重投没排上'))
        if (res.ok) loadFailed()
      }
    } else if (mode === 'claim') {
      const res = await fetch(`/api/videos/${video.id}/claim`, { method: 'POST' })
      const body = await res.json().catch(() => null)
      sayFailed(res.ok ? 'ok' : 'err', body?.message ?? (res.ok ? '已挂到你名下退回队列' : '认领没成'))
      if (res.ok) loadFailed()
    } else {
      const res = await fetch(`/api/videos/${video.id}`, { method: 'DELETE' })
      const body = await res.json().catch(() => null)
      sayFailed(res.ok ? 'ok' : 'err', body?.message ?? (res.ok ? '已进回收站' : '销毁没成'))
      if (res.ok) loadFailed()
    }
  } catch {
    sayFailed('err', '分站后端没有应答')
  } finally {
    failedBusy.value = null
  }
}

function failedDestroy(video) {
  // 二段式防手滑（与 purge 卡同精神）：第一下上弦，5 秒不补第二下作废
  if (failedArmed.value === video.id) {
    if (failedArmTimer) clearTimeout(failedArmTimer)
    failedArmed.value = null
    failedAction(video, 'destroy')
    return
  }
  failedArmed.value = video.id
  if (failedArmTimer) clearTimeout(failedArmTimer)
  failedArmTimer = setTimeout(() => (failedArmed.value = null), 5000)
}

function gotoFailedCard() {
  document.getElementById('grp-failed')?.scrollIntoView({ behavior: 'smooth', block: 'center' })
}

const quietOpen = ref(false) // 「货架」抽屉：悬停预览、下载设置、成员与公告、备份与搬家这些，盖子在碎纸机上面；下载设置压底
const dlSettingsOpen = ref(false) // 下载设置整块太长（十几个字段密排），进抽屉还要滚一屏：默认折上，要改再展开（11-09 站长点名）
// ── 分享链接设置（11-15 站长点单）：全站分享一张嘴（utils/siteLink）的门面地址，settings 表 share-base-url。
// 判据与后端 ShareLinkEndpoints.TryNormalize 等样镜像，后端才是真闸——这里只求当场反馈。
const shareInput = ref('')
const shareBusy = ref(false)
const shareNotice = ref(null) // { kind: 'ok' | 'err', text }
watch(shareInput, () => { shareNotice.value = null }) // 重新动手改地址，上一条回执就翻篇：错误条赖在输入框上面比没有还误导
const sharePreviewBase = computed(() => {
  const raw = shareInput.value.trim()
  return raw === '' ? window.location.origin : raw.replace(/\/+$/, '')
})
function shareDraftError(raw) {
  if (raw === '') return null
  let u = null
  try { u = new URL(raw) } catch { return '只收 http(s):// 地址，认不出来' }
  if (u.protocol !== 'http:' && u.protocol !== 'https:') return '只收 http(s):// 地址'
  if (u.username || u.password) return '地址里不许夹用户信息'
  if (!u.hostname) return '域名没填，光有协议头不行'
  if ((u.pathname !== '/' && u.pathname !== '') || u.search || u.hash) return '只填到端口为止，不许带路径、查询或锚点'
  return null
}
async function saveShareBase() {
  const raw = shareInput.value.trim()
  const err = shareDraftError(raw)
  if (err) { shareNotice.value = { kind: 'err', text: err }; return }
  shareBusy.value = true
  try {
    const res = await fetch('/api/admin/settings/share-base-url', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ url: raw === '' ? null : raw.replace(/\/+$/, '') }),
    })
    const data = await res.json().catch(() => ({}))
    if (!res.ok) { shareNotice.value = { kind: 'err', text: data?.message ?? '没存上，稍后再试' }; return }
    shareNotice.value = { kind: 'ok', text: data.shareBaseUrl ? `已存：${data.shareBaseUrl}` : '已恢复默认：跟随访问地址' }
    if (settings.value) settings.value = { ...settings.value, shareBaseUrl: data.shareBaseUrl ?? null }
    forgetSiteBase() // 站长本页的分享口立刻吃新值，不必刷页
  } catch {
    shareNotice.value = { kind: 'err', text: '没存上，网络不配合' }
  } finally {
    shareBusy.value = false
  }
}
async function resetShareBase() {
  shareInput.value = ''
  await saveShareBase()
}
const recycleOpen = ref(false) // 回收站那一堆（整片 / 弹幕 / 加档）默认折起：页尾不该常驻三张卡（11-09 站长点名）
const recycleBinCount = ref(0) // 整片回收站的件数：那卡自持数据，靠它 emit 上来，只为顶上那枚徽章
// 回收站徽章：三座加在一起。卡折起时仍挂在 DOM 里（v-show 不是 v-if），所以锁着盖子这数也是真的
const recycleBadge = computed(() => (dmRecycle.value?.length ?? 0) + (codecRecycle.value?.length ?? 0) + (recycleBinCount.value ?? 0))
watch(quietOpen, (open) => { if (open && announceBatches.value === null) loadAnnounceBatches() }) // 抽屉掀开才拉：这节就在抽屉里，盖子没开就拉纯属白跑一趟
// 事件台账 11-09 也从抽屉里拎出来了（站长点名：查账是常事，不该藏在「设好就不碰」那一堆里），改到下面进页就拉
watch(quietOpen, (open) => { if (open && pendingInvites.value === null) loadPendingInvites() }) // 抽屉掀开才拉：这节就在抽屉里
watch(quietOpen, (open) => { if (open && personaDefault.value === null) loadPersona() }) // 看板娘性格卡也住在抽屉里，掀开才拉
// 三座回收站 11-09 从「货架」搬到碎纸机那一块：常在页尾不进抽屉，所以进页就拉（列表小，而且站长随时想看有没有误删）
onMounted(() => {
  if (dmRecycle.value === null) loadDmRecycle()
  if (codecRecycle.value === null) loadCodecRecycle()
  if (eventsRows.value === null) loadEventsLedger() // 台账也常在页尾，进页就拉（默认只露十条，不阻渲染）
  if (failedItems.value === null) loadFailed() // 失败件明细常驻卡：进页拉一次，重拉钮与动作后补拉
})
// 加档牌面上的人话编码名：后端回的是 'avc'/'hevc'/'av1'，没见过的原样贴出，不替它编个名
const CODEC_LABEL = { avc: 'AVC', hevc: 'HEVC', av1: 'AV1' }
function codecLabel(codec) {
  return CODEC_LABEL[codec] ?? codec ?? '未知编码'
}

const dlWaiting = computed(() => (dlQueue.value?.pending?.length ?? 0) + (dlQueue.value?.downloading?.length ?? 0) + (dlQueue.value?.codecs?.length ?? 0))
const adminNav = computed(() => [
  // 预览欠账徽章随卡下柜：挂在货架胶囊上，柜台只留天天盯的
  { ...ADMIN_GROUPS[0], n: null, tone: '' },
  { ...ADMIN_GROUPS[1], n: dlWaiting.value > 0 ? dlWaiting.value : null, tone: 'hot' },
  // 抽屉也占一枚胶囊：点了先把盖子掀开再滚过去，否则定位锚不在
  { id: 'events', title: '事件台账', hint: '', order: 40, n: null, tone: '' },
  { id: 'quiet', title: '货架', hint: '', order: 55, n: (pv.value?.pending ?? 0) > 0 ? pv.value.pending : null, tone: 'warn' },
  // 回收站与事件台账 11-09 从抽屉/页尾常驻里拆出来各占一枚：它们一个是误删后的后路、一个是天天能查的账
  { id: 'recycle', title: '回收站', hint: '', order: 65, n: recycleBadge.value > 0 ? recycleBadge.value : null, tone: 'warn' },
].sort((a, b) => a.order - b.order)) // 排序按各组在页面上的实座次（order）来，不手写顺序：上一版胶囊排成「货架→回收站→事件台账」，而台账在页面上其实排在货架上面，对不上就是骗眼睛

async function gotoGroup(id) {
  if (id === 'danger') {
    dangerOpen.value = true // 先从收起来变回展开，否则下面找不到可滚的锚
  }
  if (id === 'quiet') {
    quietOpen.value = true // 抽屉收起时里面那六张卡不在 DOM 里，先得把盖子掀开才能滚过去
  }
  if (id === 'recycle') {
    recycleOpen.value = true // 回收站折起时只有一条胶囊，得先掀盖才能滚到卡阵
  }
  await nextTick()
  document.getElementById(`grp-${id}`)?.scrollIntoView({ behavior: 'smooth', block: 'start' })
  activeGroup.value = id
  try {
    localStorage.setItem(ADMIN_GROUP_KEY, id) // 下次进站长室还从上次那一组开始
  } catch {
    /* 存不下就算了，不影响跳位 */
  }
}

// 定位条的高亮：找最后一条已经滚到条子上沿之上的组头（rAF 节流，不每像素都算一遗）。
let groupSpyRaf = 0
function onGroupSpyScroll() {
  if (groupSpyRaf) return
  groupSpyRaf = requestAnimationFrame(() => {
    groupSpyRaf = 0
    let current = 'overview'
    for (const g of [...ADMIN_GROUPS.map((x) => x.id), 'quiet', 'recycle', 'events', 'danger']) {
      const el = document.getElementById(`grp-${g}`)
      if (el && el.getBoundingClientRect().top <= 120) current = g
    }
    activeGroup.value = current
  })
}

onMounted(() => {
  bootAdmin()
  window.addEventListener('scroll', onGroupSpyScroll, { passive: true })
})

// 站长在 LoginDialog 里扫码进来、或从顶栏登出后，重跑开机核身。
watch(member, () => {
  if (adminAuth.value !== 'loading') bootAdmin()
})

onUnmounted(() => {
  disposed = true
  window.removeEventListener('scroll', onGroupSpyScroll)
  if (groupSpyRaf) cancelAnimationFrame(groupSpyRaf)
  if (dlTimer) clearInterval(dlTimer)
  if (pvTimer) clearInterval(pvTimer)
  clearTimeout(pvClearTimer)
  if (pvSaveTimer) savePvSettings() // 帘子收了还有没交出去的参数：离场前补发一次，不吞站长的改动
  clearTimeout(expTimer)
  clearTimeout(exportArmTimer)
  clearTimeout(pendingTimer)
  clearTimeout(purgeTimer) // 回收站二次确认的 5 秒自解除，卸载后不该替已消失的实例摸钮
  if (announceTimer) clearTimeout(announceTimer) // 公告那一发的延时提示：一并掐掉
  if (dynArmedTimer) clearTimeout(dynArmedTimer) // 动态二次确认的 5 秒自解除
})
</script>

<template>
  <div class="admin-root min-h-screen bg-adminBg text-white">
    <!-- 身份门：会员会话说了算，访客挡在门外，普通成员送客去个人空间 -->
    <div v-if="adminAuth !== 'ok'" class="flex min-h-screen items-center justify-center px-6">
      <div class="lbs-card w-full max-w-[400px] p-8 text-center">
        <template v-if="adminAuth === 'needOwner'">
          <div class="mb-5 flex items-center justify-center gap-2">
            <span class="lbs-dot lbs-dot-off"></span>
            <span class="text-sm tracking-widest text-white/90">需要站长身份</span>
          </div>
          <p class="mb-5 text-xs leading-5 text-white/35">
            请用站长 B 站账号扫码进站，再回这扇门。分站本体照常开放，看片发弹幕不受影响。
          </p>
          <button class="lbs-btn w-full" @click="openLogin('admit')">扫码进站</button>
        </template>
        <template v-else-if="adminAuth === 'member'">
          <div class="mb-5 flex items-center justify-center gap-2">
            <span class="lbs-dot lbs-dot-off"></span>
            <span class="text-sm tracking-widest text-white/90">这里是站长室</span>
          </div>
          <p class="mb-5 text-xs leading-5 text-white/35">
            你已是分站成员，这里是站长室——普通成员请进个人空间。
          </p>
          <RouterLink to="/me" class="lbs-btn inline-block w-full">进个人空间</RouterLink>
        </template>
        <template v-else-if="adminAuth === 'offline'">
          <div class="mb-5 flex items-center justify-center gap-2">
            <span class="lbs-dot lbs-dot-off"></span>
            <span class="text-sm tracking-widest text-white/90">分站没应答</span>
          </div>
          <p class="mb-5 text-xs leading-5 text-white/35">
            身份还没核对上——分站后端此刻没回话，不拿空仪表盘冒充正常。等它缓过来再重试。
          </p>
          <button class="lbs-btn w-full" @click="bootAdmin()">重试核对</button>
        </template>
        <p v-else class="py-4 text-xs text-white/30">正在核对手牌……</p>
      </div>
    </div>

    <header v-if="adminAuth === 'ok'" class="border-b border-adminLine bg-gradient-to-b from-white/[0.02] to-transparent">
      <div class="mx-auto flex h-14 max-w-[1400px] items-center justify-between px-6">
        <div class="flex items-center gap-3">
          <span class="lbs-dot lbs-dot-on"></span>
          <span class="text-sm tracking-widest text-white/90">站长室</span>
          <span class="text-xs text-white/25">LittleBrokenStation · 控制室</span>
        </div>
        <RouterLink
          to="/"
          class="rounded-full border border-adminLine px-3.5 py-1.5 text-sm text-white/50 transition-colors hover:border-adminAccent/50 hover:text-adminAccent"
        >
          回分站
        </RouterLink>
      </div>
    </header>

    <!-- 分组定位条：sticky 在顶，只负责跳位与报数（下载排队、预览欠账）；正文仍一屏到底 -->
    <nav v-if="adminAuth === 'ok'" class="sticky top-0 z-30 mx-auto flex max-w-[1400px] flex-wrap items-center gap-2 border-b border-adminLine bg-adminBg/90 px-6 py-2.5 backdrop-blur">
      <button
        v-for="g in adminNav"
        :key="g.id"
        type="button"
        class="rounded-full border px-3 py-1 text-xs transition"
        :class="activeGroup === g.id ? 'border-adminAccent/50 bg-adminAccent/10 text-adminAccent' : 'border-white/10 text-white/45 hover:border-white/25 hover:text-white/70'"
        @click="gotoGroup(g.id)"
      >
        {{ g.title }}
        <span v-if="g.n" class="ml-1 font-mono text-[10px]" :class="g.tone === 'hot' ? 'text-rose-300/90' : 'text-amber-200/85'">{{ g.n }}</span>
      </button>
      <button
        type="button"
        class="ml-auto rounded-full border border-rose-400/25 px-3 py-1 text-xs text-rose-300/70 transition hover:border-rose-400/50 hover:text-rose-200"
        @click="gotoGroup('danger')"
      >
        碎纸机<span class="ml-1 text-[10px] text-rose-300/45">{{ dangerOpen ? '已开盖' : '已收起' }}</span>
      </button>
    </nav>

    <main v-if="adminAuth === 'ok'" class="lbs-rise mx-auto grid max-w-[1400px] grid-cols-1 gap-4 px-6 py-8 md:grid-cols-2">
      <!-- 组头：也是 grid 的孩子，跟着卡一起排座 -->
      <div
        v-for="g in ADMIN_GROUPS"
        :id="`grp-${g.id}`"
        :key="g.id"
        class="order-[9] md:col-span-2 flex items-baseline gap-2.5 pt-3"
        :style="{ order: g.order }"
      >
        <h2 class="text-[11px] font-medium uppercase tracking-[0.18em] text-white/40">{{ g.title }}</h2>
        <span class="font-mono text-[11px] text-white/20">{{ g.hint }}</span>
        <span class="ml-1 h-px flex-1 bg-white/5"></span>
      </div>

      <section class="lbs-card md:col-span-2 order-[11] border-l-2 border-l-adminAccent/50">
        <div class="mb-5 flex items-center justify-between">
          <h3 class="lbs-title !mb-0">分站统计</h3>
          <div class="flex items-center gap-3 font-mono text-xs">
            <span class="text-adminAccent/80">完成 {{ stats?.arrived ?? '—' }}</span>
            <span class="text-white/35">下载中 {{ stats?.downloading ?? '—' }}</span>
            <span class="text-white/35">排队 {{ stats?.pending ?? '—' }}</span>
            <span :class="stats?.failed ? 'cursor-pointer text-rose-300/80 underline decoration-rose-400/30 underline-offset-2 hover:text-rose-200' : 'text-white/35'" title="跳到下面的失败件明细卡" @click="gotoFailedCard">失败 {{ stats?.failed ?? '—' }}</span>
          </div>
        </div>
        <div class="grid grid-cols-2 gap-x-6 gap-y-6 sm:grid-cols-3 lg:grid-cols-5">
          <div class="lbs-stat-lg"><b>{{ stats ? stats.total : '—' }}</b><span>馆藏</span></div>
          <div class="lbs-stat-lg"><b>{{ fmtBytes(stats?.totalBytes) }}</b><span>库存体积</span></div>
          <div class="lbs-stat-lg"><b>{{ stats ? fmtCount(stats.viewTotal) : '—' }}</b><span>总播放</span></div>
          <div class="lbs-stat-lg"><b>{{ stats ? fmtCount(stats.likeTotal) : '—' }}</b><span>总赞</span></div>
          <div class="lbs-stat-lg"><b>{{ stats ? fmtCount(stats.coinTotal) : '—' }}</b><span>总币</span></div>
          <div class="lbs-stat-lg"><b>{{ stats ? fmtCount(stats.favTotal) : '—' }}</b><span>总收藏</span></div>
          <div class="lbs-stat-lg"><b>{{ stats ? fmtCount(stats.shareTotal) : '—' }}</b><span>总转发</span></div>
          <div class="lbs-stat-lg"><b>{{ stats ? fmtCount(stats.replyTotal) : '—' }}</b><span>总评论</span></div>
          <div class="lbs-stat-lg"><b>{{ stats ? stats.upCount : '—' }}</b><span>UP 主</span></div>
          <div class="lbs-stat-lg"><b>{{ stats ? stats.liveAccounts : '—' }}</b><span>成员活号</span></div>
        </div>

        <!-- 采集号：站里的活此刻在吃谁家的 B 站号。号从成员账号池来，站长席位的活号优先，借了别人的要说清 -->
        <div v-if="collectState" class="mt-4 flex flex-wrap items-baseline gap-x-2 gap-y-1 border-t border-adminLine pt-3 text-xs">
          <span class="text-white/40">采集号</span>
          <template v-if="collectState.hasAccount">
            <span class="font-mono text-white/80">{{ collectState.name || `B站账号 ${collectState.mid}` }}（mid {{ collectState.mid }}）</span>
            <span v-if="!collectState.isOwnerSeat" class="text-amber-200/75">（借自成员席位，风控消耗落在 Ta 头上）</span>
          </template>
          <span v-else class="text-white/30">
            采集已停摆：站里没有可用 B 站号，请到个人空间绑定或重新扫码
            <span v-if="collectState.stoppedSinceUtc">（{{ friendlyTime(collectState.stoppedSinceUtc) }}）</span>
          </span>
        </div>
      </section>

      <!-- 悬停预览：首页卡片鼠标停一下就起播的那批 360p 无声小片段，全套仪表盘随卡下柜进货架 -->
      <section v-if="quietOpen" class="lbs-card md:col-span-2 order-[56]">
        <div class="mb-4 flex flex-wrap items-end justify-between gap-x-4 gap-y-2">
          <h3 class="lbs-title !mb-0">
            悬停预览
            <span class="ml-1 rounded-full border border-adminAccent/30 px-2 py-0.5 text-[10px] normal-case tracking-normal text-adminAccent/70">{{ pvBadge }}</span>
          </h3>
          <div class="flex items-center gap-3.5 font-mono text-xs">
            <span class="flex items-center gap-1.5" :class="pvState.tone" :title="`workerState: ${pv?.workerState ?? '—'}`">
              <span class="pv-dot" :class="pvState.live ? 'pv-dot-live' : ''"></span>{{ pvState.text }}<template v-if="pvState.rawText"> · {{ pvState.rawText }}</template>
            </span>
            <span class="text-white/30">上轮耗时 <span class="text-adminAccent/85">{{ pvDur(pv?.lastRoundSeconds) }}</span></span>
            <span class="text-white/30">下轮 <span class="text-adminAccent/85">{{ pvIn(pv?.nextRoundSeconds) }}</span></span>
          </div>
        </div>

        <!-- 覆盖率三段条：已生成 / 失败 / 待生成（总宽固定，按 covered+pending+failed 分） -->
        <div>
          <div class="flex h-2 overflow-hidden rounded-full bg-white/[0.06]">
            <div class="bg-gradient-to-r from-adminAccent/85 to-adminAccent/55" :style="{ width: pvWidth(pv?.covered) }"></div>
            <div class="bg-rose-400/75" :style="{ width: pvWidth(pv?.failed) }"></div>
            <div class="bg-white/[0.13]" :style="{ width: pvWidth(pv?.pending) }"></div>
          </div>
          <div class="mt-2 flex flex-wrap items-center gap-4 text-[11px] text-white/35">
            <span><i class="mr-1.5 inline-block h-2 w-2 rounded-sm bg-adminAccent/70"></i>已生成 {{ pv?.covered ?? '—' }}</span>
            <span><i class="mr-1.5 inline-block h-2 w-2 rounded-sm bg-white/15"></i>待生成 {{ pv?.pending ?? '—' }}</span>
            <span><i class="mr-1.5 inline-block h-2 w-2 rounded-sm bg-rose-400/75"></i>失败 {{ pv?.failed ?? '—' }}</span>
            <span class="ml-auto" :title="'covered / 已到站总数'">覆盖率 {{ pvCoverText }}</span>
          </div>
        </div>

        <div class="mt-5 grid grid-cols-3 gap-x-5 gap-y-5 sm:grid-cols-6">
          <div class="lbs-stat-lg"><b>{{ pv?.covered ?? '—' }}</b><span>已生成</span></div>
          <div class="lbs-stat-lg"><b>{{ pv?.pending ?? '—' }}</b><span>待生成</span></div>
          <div class="lbs-stat-lg"><b>{{ pv?.inflight ?? '—' }}</b><span>生成中</span></div>
          <div class="lbs-stat-lg"><b :class="pv?.failed ? 'text-rose-300/90' : ''">{{ pv?.failed ?? '—' }}</b><span>失败待重试</span></div>
          <div class="lbs-stat-lg" title="悬停预览占用磁盘，不计入分站统计的库存体积"><b>{{ fmtBytes(pv?.previewBytes) }}</b><span>预览占用</span></div>
          <div class="lbs-stat-lg" :title="`${pv?.res ?? '—'}p 无声均值`"><b>{{ fmtBytes(pv?.avgBytes) }}</b><span>平均产物</span></div>
        </div>

        <div class="my-5 h-px bg-white/5"></div>

        <div class="grid grid-cols-1 gap-6 lg:grid-cols-[1.05fr_1fr]">
          <!-- 左：生成参数 + 欠账预估 + 四枚动作 -->
          <div>
            <h4 class="mb-2.5 text-[11px] font-medium uppercase tracking-[0.14em] text-white/30">生成参数</h4>
            <div class="grid grid-cols-1 gap-2.5 sm:grid-cols-2">
              <div class="flex items-center justify-between gap-2.5 rounded-lg border border-white/5 bg-black/20 px-2.5 py-2">
                <label class="text-xs text-white/40" for="pv-enabled">自动生成预览</label>
                <button
                  id="pv-enabled"
                  type="button"
                  role="switch"
                  class="pv-switch"
                  :aria-checked="pvDraft.enabled ? 'true' : 'false'"
                  :disabled="pvSaving || !pv"
                  @click="pvDraft.enabled = !pvDraft.enabled; queuePvSave(true)"
                ></button>
              </div>
              <div class="flex items-center justify-between gap-2.5 rounded-lg border border-white/5 bg-black/20 px-2.5 py-2">
                <label class="text-xs text-white/40" for="pv-yield" title="下载占带宽时预览生成自行让路">下载中让路</label>
                <button
                  id="pv-yield"
                  type="button"
                  role="switch"
                  class="pv-switch"
                  :aria-checked="pvDraft.yieldOnDownload ? 'true' : 'false'"
                  :disabled="pvSaving || !pv"
                  @click="pvDraft.yieldOnDownload = !pvDraft.yieldOnDownload; queuePvSave(true)"
                ></button>
              </div>
              <div class="flex items-center justify-between gap-2.5 rounded-lg border border-white/5 bg-black/20 px-2.5 py-2">
                <label class="text-xs text-white/40" for="pv-res">清晰度</label>
                <select id="pv-res" v-model="pvDraft.res" class="eng-input w-auto cursor-pointer" :disabled="pvSaving || !pv" @change="queuePvSave(true)">
                  <option :value="360">360p</option>
                  <option :value="480">480p</option>
                  <option :value="720">720p（推算）</option>
                </select>
              </div>
              <div class="flex items-center justify-between gap-2.5 rounded-lg border border-white/5 bg-black/20 px-2.5 py-2">
                <label class="text-xs text-white/40" for="pv-seconds">片段时长</label>
                <span class="flex items-center gap-1.5">
                  <input id="pv-seconds" v-model="pvDraft.seconds" type="number" min="5" max="120" class="eng-input !w-[62px]" :disabled="pvSaving || !pv" title="5..120 秒（后端就认这个区间）" @input="queuePvSave(false)" />
                  <span class="text-[11px] text-white/30">秒</span>
                </span>
              </div>
              <div class="flex items-center justify-between gap-2.5 rounded-lg border border-white/5 bg-black/20 px-2.5 py-2">
                <label class="text-xs text-white/40" for="pv-batch">每轮封顶</label>
                <select id="pv-batch" v-model.number="pvDraft.batch" class="eng-input w-auto cursor-pointer" :disabled="pvSaving || !pv" @change="queuePvSave(true)">
                  <option v-for="n in pvBatchOptions" :key="n" :value="n">{{ n }}</option>
                </select>
              </div>
              <div class="flex items-center justify-between gap-2.5 rounded-lg border border-white/5 bg-black/20 px-2.5 py-2">
                <label class="text-xs text-white/40" for="pv-conc" title="同时转几路：每路 ffmpeg 吃一个核，日常建议 1～4；封顶 32 是追齐积压用的，换档在下一轮开头生效">同时生成数</label>
                <select id="pv-conc" v-model.number="pvDraft.concurrency" class="eng-input w-auto cursor-pointer" :disabled="pvSaving || !pv" @change="queuePvSave(true)">
                  <option v-for="n in PV_CONCURRENCY_OPTIONS" :key="n" :value="n">{{ n }} 路</option>
                </select>
              </div>
            </div>

            <div class="mt-3 rounded-lg border border-dashed border-adminAccent/30 bg-adminAccent/[0.05] px-3 py-2.5">
              <p class="flex flex-wrap items-center gap-2 text-[11px] tracking-[0.06em] text-adminAccent/70">
                补齐欠账的成本预估（随清晰度与片段时长实时重算）
                <span
                  class="rounded-full border px-1.5 py-px text-[10px] normal-case tracking-normal"
                  :class="pvCost?.measured ? 'border-emerald-400/40 text-emerald-300' : 'border-amber-400/40 text-amber-300'"
                >
                  {{ pvCost ? (pvCost.measured ? '实测' : '推算') : '—' }}
                </span>
              </p>
              <p class="mt-1.5 font-mono text-xs leading-[1.65] text-white/65">
                待生成 <em class="not-italic text-adminAccent/90">{{ pvCost ? pvCost.pending : '—' }}</em> 条 · 约
                <em class="not-italic text-adminAccent/90">{{ pvCost ? pvCost.total : '—' }}</em> 秒 · 磁盘
                <em class="not-italic text-adminAccent/90">{{ pvCost ? `+${pvCost.disk}` : '—' }}</em><br />
                单条基线 {{ pvCost ? pvCost.baseline : '—' }}
                <span class="text-white/30">· {{ pvCost ? pvCost.note : '—' }}</span>
              </p>
            </div>

            <div class="mt-3 flex flex-wrap gap-2">
              <button class="lbs-btn" :disabled="pvBusy" @click="pvBackfill">{{ pvBusy ? '忙中…' : '立即补采' }}</button>
              <button class="lbs-btn-ghost" :disabled="pvBusy || !pv?.failed" @click="pvRetry()">重试失败{{ pv?.failed ? ` ${pv.failed} 条` : '' }}</button>
              <button class="lbs-btn-ghost" :disabled="pvBusy" title="只补今天到站、还没预览的馆藏" @click="pvRetryToday">补今天新货</button>
              <button :class="pvClearArmed ? 'lbs-btn-danger-armed' : 'lbs-btn-danger'" :disabled="pvBusy" @click="pvClear">
                {{ pvClearArmed ? '确定销掉全部预览？再点一次执行' : '清空预览' }}
              </button>
            </div>
          </div>

          <!-- 右：逐件流水（本轮 recent + 往轮欠着的 failures），失败行给原因与单条重试 -->
          <div class="flex min-w-0 flex-col">
            <h4 class="mb-2.5 flex items-center justify-between gap-2 text-[11px] font-medium uppercase tracking-[0.14em] text-white/30">
              最近一轮 · 逐件流水
              <span class="normal-case tracking-normal text-white/20">元数据：清晰度 · 体积 · 产物时长 · 生成耗时</span>
            </h4>
            <ul class="max-h-[330px] overflow-y-auto pr-1">
              <li v-for="row in pvRows" :key="row.key" class="flex items-center gap-2.5 border-b border-white/[0.04] py-1.5 text-xs">
                <time class="shrink-0 font-mono text-[11px] text-white/25">{{ pvClock(row.at) }}</time>
                <span class="shrink-0" :class="row.ok ? 'text-emerald-400' : 'text-rose-400'">{{ row.ok ? '✓' : '✗' }}</span>
                <span class="shrink-0 font-mono text-white/55">{{ row.bvid }}</span>
                <span v-if="row.ok" class="min-w-[40px] flex-1 truncate text-white/40" :title="row.title">{{ row.title || '—' }}</span>
                <span v-if="row.ok" class="shrink-0 font-mono text-[11px] text-white/30" title="实测产物：清晰度 · 体积 · 片段时长；末位「用 Xs」是这一件 ffmpeg 实跑的耗时">{{ pvMeta(row) }}</span>
                <template v-else>
                  <span class="min-w-[40px] flex-1 truncate text-[11px] text-rose-300/70" :title="row.error">{{ row.error || '生成失败' }}</span>
                  <button
                    class="shrink-0 rounded-md border border-white/10 px-1.5 py-px text-[11px] text-white/45 transition hover:border-adminAccent/50 hover:text-adminAccent"
                    :disabled="pvBusy"
                    @click="pvRetry([row.bvid])"
                  >
                    重试
                  </button>
                </template>
              </li>
              <li v-if="!pvRows.length" class="py-6 text-center text-xs text-white/25">{{ pv ? '这一轮还没跑过件：点「立即补采」催一轮。' : '预览状态没拿到（后端没应答），稍后自动重试。' }}</li>
            </ul>
          </div>
        </div>

        <div class="mt-4 flex flex-wrap items-center justify-between gap-3 border-t border-adminLine pt-3 font-mono text-[11px] text-white/25">
          <span>目录 /data/previews · 开关 settings:preview-enabled · 独立于 /media 与下载并发闸</span>
          <span
            v-if="pvText"
            :class="pvText.kind === 'ok' ? 'text-adminAccent/80' : pvText.kind === 'err' ? 'text-rose-300/80' : 'text-amber-200/80'"
          >
            {{ pvText.text }}
          </span>
        </div>
      </section>

      <section v-if="quietOpen" class="lbs-card md:col-span-2 order-[57]">
        <div class="mb-4 flex flex-wrap items-center justify-between gap-2">
          <h3 class="lbs-title !mb-0">
            分站成员
            <span class="ml-1 rounded-full border border-adminAccent/30 px-2 py-0.5 text-[10px] normal-case tracking-normal text-adminAccent/70">
              {{ members ? `${members.length} 席` : '—' }}
            </span>
          </h3>
          <div class="flex shrink-0 items-center gap-2">
            <input
              v-model="memberNote"
              type="text"
              placeholder="备注（写给谁，可选）"
              class="lbs-input w-[200px]"
              :disabled="membersBusy"
              @keyup.enter="sendInvite"
            />
            <button class="lbs-btn" :disabled="membersBusy" @click="sendInvite">发邀请码</button>
          </div>
        </div>

        <div v-if="inviteCode" class="mb-4 flex items-center justify-between gap-3 rounded-lg border border-adminAccent/40 bg-adminAccent/10 px-3 py-2.5">
          <div class="min-w-0">
            <p class="text-[11px] text-adminAccent/80">新邀请码 · 一次一用，发给朋友扫</p>
            <p class="mt-1 select-all break-all font-mono text-xl tracking-[0.25em] text-white/90">{{ inviteCode }}</p>
          </div>
          <button class="lbs-btn shrink-0" @click="copyInvite">复制</button>
        </div>

        <p v-if="memberText" class="mb-3 text-xs text-adminAccent/80">{{ memberText }}</p>

        <div v-if="!members?.length" class="py-6 text-center text-xs text-white/25">还没有成员：发个邀请码，把第一位朋友请进门。</div>
        <ul v-else class="space-y-1.5">
          <li v-for="m in members" :key="m.id" class="rounded-lg border border-white/5 bg-black/20">
            <div class="flex flex-wrap items-center gap-3 px-3 py-2">
              <img :src="m.faceUrl || defaultAvatar" referrerpolicy="no-referrer" loading="lazy" class="h-8 w-8 shrink-0 rounded-full object-cover" alt="" @error="onAvatarError" />
              <div class="min-w-0 flex-1">
                <p class="flex items-center gap-1.5 text-xs text-white/85">
                  <span class="truncate">{{ seatLabel(m) }}</span>
                  <span v-if="m.username && m.nickname" class="shrink-0 rounded-full border border-white/10 bg-white/5 px-1.5 py-px text-[10px] text-white/40">{{ m.username }}</span>
                  <span v-if="!m.mid && !m.username" class="shrink-0 rounded-full border border-white/10 bg-white/5 px-1.5 py-px text-[10px] text-white/40">素净</span>
                  <span v-if="m.role === 'owner'" class="shrink-0 rounded-full border border-amber-400/50 bg-amber-400/10 px-1.5 py-px text-[10px] text-amber-300">♛ 站长</span>
                  <span v-else class="flex shrink-0 items-center gap-1 rounded-full border border-slate-400/40 bg-slate-400/10 px-1.5 py-px text-[10px] text-slate-300">
                    <CrownBadge tier="member" class="h-2.5 w-2.5" />
                    成员
                  </span>
                  <span
                    class="shrink-0 rounded-full border px-1.5 py-px text-[10px]"
                    :class="seatStatusTag(m.status).cls"
                  >
                    {{ seatStatusTag(m.status).text }}
                  </span>
                </p>
                <p class="mt-0.5 font-mono text-[10px] text-white/30">
                  {{ seatIdLine(m) }} · 入站 {{ fmtBackupTime(m.createdAtUtc) }} · 上次登录 {{ friendlyTime(m.lastLoginAtUtc) }}
                </p>
              </div>
              <span class="shrink-0 font-mono text-[11px] text-white/45">投稿 {{ m.submitted ?? 0 }}</span>
              <span class="shrink-0 font-mono text-[11px] text-white/45">{{ fmtBytes(m.bytes) }}</span>
              <button
                class="shrink-0 rounded-md border border-white/10 px-2 py-0.5 text-[11px] text-white/45 transition hover:border-adminAccent/50 hover:text-adminAccent"
                @click="expandedMember = expandedMember === m.id ? null : m.id"
              >
                账号 {{ m.accounts?.length ?? 0 }} {{ expandedMember === m.id ? '▴' : '▾' }}
              </button>
              <button
                class="shrink-0 rounded-md border border-white/10 px-2 py-0.5 text-[11px] text-white/45 transition hover:border-amber-400/50 hover:text-amber-300 disabled:opacity-40"
                v-if="m.status !== 'exiled' && m.status !== 'invited'"
                :disabled="membersBusy"
                @click="memberAction(m.status === 'banned' ? 'unban' : 'ban', m)"
              >
                {{ m.status === 'banned' ? '解封' : '封禁' }}
              </button>

              <!-- 除名 / 召回（11-08）：与封禁分家——封的是「今天不许进」，除的是「这个人请出站」，
                   席位行留着，历史名片与投稿归属都还点得开（撤席位做不到这个）。站长自己不摆这枚钮。 -->
              <button
                v-if="m.role !== 'owner' && m.status !== 'invited'"
                class="shrink-0 rounded-md border border-white/10 px-2 py-0.5 text-[11px] text-white/45 transition hover:border-slate-300/50 hover:text-slate-200 disabled:opacity-40"
                :disabled="membersBusy"
                :title="m.status === 'exiled' ? '拍回启用：他可以重新进站，也重新能投稿' : '请出站：扫码与账密都不再收，得走召回才回来（席位行留着）'"
                @click="memberAction(m.status === 'exiled' ? 'restore' : 'exile', m)"
              >
                {{ m.status === 'exiled' ? '召回' : '除名' }}
              </button>
              <button
                class="shrink-0 rounded-md border border-rose-500/30 px-2 py-0.5 text-[11px] text-rose-300/80 transition hover:bg-rose-500/10 disabled:opacity-40"
                :disabled="membersBusy"
                @click="memberAction('delete', m)"
              >
                撤席位
              </button>
            </div>
            <ul v-if="expandedMember === m.id" class="space-y-1 border-t border-white/5 px-3 py-2">
              <li v-for="acc in m.accounts ?? []" :key="acc.id" class="flex items-center gap-2.5">
                <img :src="acc.faceUrl || defaultAvatar" referrerpolicy="no-referrer" loading="lazy" class="h-6 w-6 shrink-0 rounded-full object-cover" alt="" @error="onAvatarError" />
                <span class="min-w-0 flex-1 truncate text-[11px] text-white/70">
                  {{ acc.accountName || `B站账号 ${acc.accountMid}` }}
                  <span v-if="acc.isPrimary" class="ml-1 text-adminAccent/80">主号</span>
                  <span v-if="acc.expired" class="ml-1 text-rose-300/70">已失效</span>
                </span>
                <span class="shrink-0 font-mono text-[10px] text-white/25">{{ acc.lastSeenUtc ? friendlyTime(acc.lastSeenUtc) : '未露面' }}</span>
                <button
                  class="shrink-0 rounded-md p-1 text-white/35 transition hover:bg-rose-500/20 hover:text-rose-300"
                  title="解绑这个账号"
                  :disabled="membersBusy"
                  @click="memberAccountDelete(acc, m)"
                >
                  <svg viewBox="0 0 24 24" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="2">
                    <path d="M6 6l12 12M18 6L6 18" stroke-linecap="round" />
                  </svg>
                </button>
              </li>
              <li v-if="!m.accounts?.length" class="py-1 text-[11px] text-white/25">这席还没绑任何 B 站账号（素净席位：可逛可看可私信，不能投稿）。</li>
            </ul>
            <div v-if="expandedMember === m.id" class="border-t border-white/5 px-3 pb-2 pt-1.5">
              <div class="flex items-center gap-2">
                <button
                  class="rounded-md border border-white/10 px-2 py-0.5 text-[11px] text-white/45 transition hover:border-adminAccent/50 hover:text-adminAccent"
                  @click="toggleDmLog(m)"
                >
                  私信记录 {{ dmLogMember === m.id ? '▴' : '▾' }}
                </button>
                <button
                  v-if="m.status === 'active' && m.id !== member?.memberId"
                  class="rounded-md border border-stationPink/40 px-2 py-0.5 text-[11px] text-stationPink/80 transition hover:bg-stationPink/10"
                  @click="openDmWith(m)"
                >
                  发私信 →
                </button>
                <button
                  class="rounded-md border border-white/10 px-2 py-0.5 text-[11px] text-white/45 transition hover:border-amber-400/50 hover:text-amber-300"
                  @click="startReset(m)"
                >
                  代重置密码 {{ resetTarget === m.id ? '▴' : '▾' }}
                </button>
              </div>
              <div v-if="resetTarget === m.id" class="mt-1.5 flex items-center gap-2">
                <input
                  v-model="resetPwd"
                  type="text"
                  placeholder="新密码（至少 8 位）"
                  class="eng-input w-44 rounded-md border border-white/10 bg-black/30 px-2 py-1 text-[11px] text-white/80 placeholder:text-white/25"
                  :disabled="resetBusy"
                  @keyup.enter="confirmReset(m)"
                />
                <button
                  class="rounded-md border border-amber-400/50 px-2 py-0.5 text-[11px] text-amber-300 transition hover:bg-amber-400/10 disabled:opacity-40"
                  :disabled="resetBusy"
                  @click="confirmReset(m)"
                >
                  {{ resetBusy ? '提交中…' : '确认重置' }}
                </button>
                <span v-if="resetNotice?.id === m.id" class="text-[10px]" :class="resetNotice.kind === 'ok' ? 'text-emerald-300' : 'text-rose-300'">{{ resetNotice.text }}</span>
              </div>
              <div v-if="dmLogMember === m.id" class="mt-1.5">
                <p v-if="dmLogBusy" class="py-1 text-[11px] text-white/25">正在拉往来…</p>
                <p v-else-if="!dmLog?.length" class="py-1 text-[11px] text-white/25">你俩还没说过话。</p>
                <ul v-else class="max-h-44 space-y-1 overflow-y-auto pr-1">
                  <li v-for="row in dmLog" :key="row.id" class="flex items-start gap-2 rounded-lg bg-white/5 px-2 py-1.5">
                    <span class="shrink-0 font-mono text-[10px] text-white/30">{{ friendlyTime(row.createdAtUtc) }}</span>
                    <span class="min-w-0 flex-1 break-words text-[11px] text-white/70">
                      <b class="font-normal text-white/45">{{ row.fromMemberId === member?.memberId ? '我' : (row.fromName || `席位 ${row.fromMemberId}`) }} → {{ row.toMemberId === member?.memberId ? '我' : (row.toName || `席位 ${row.toMemberId}`) }}：</b>{{ row.body }}
                    </span>
                    <span class="shrink-0 text-[10px]" :class="row.readAtUtc ? 'text-white/20' : 'text-adminAccent/60'">{{ row.readAtUtc ? '已读' : '未读' }}</span>
                  </li>
                </ul>
              </div>
            </div>
          </li>
        </ul>
            <!-- 发出去的邀请码（11-08）：发过几枚、备注写的谁，这里列得出来；发错人当场作废。
                 只列还活着的（一次性码兑现完就消失），所以这份清单天然等于「还没被用的那些」。 -->
            <div class="mt-2 border-t border-white/5 px-3 py-2">
              <div class="mb-1.5 flex flex-wrap items-center gap-2">
                <span class="font-mono text-[11px] text-white/30">
                  {{ pendingInvites === null ? '邀请码台账没拉到' : pendingInvites.length ? `还没被用的邀请码 ${pendingInvites.length} 枚` : '没有还没被用的邀请码' }}
                </span>
                <button class="rounded-md border border-white/10 px-2 py-0.5 text-[11px] text-white/45 transition hover:border-adminAccent/50 hover:text-adminAccent disabled:opacity-40" :disabled="invitesLoading" @click="loadPendingInvites">
                  {{ invitesLoading ? '重拉中…' : '重拉' }}
                </button>
              </div>
              <ul v-if="pendingInvites && pendingInvites.length" class="flex flex-col gap-1">
                <li v-for="inv in pendingInvites" :key="inv.id" class="flex flex-wrap items-center gap-x-3 gap-y-0.5 text-[11px]">
                  <span class="font-mono text-white/60">{{ inv.code }}</span>
                  <span v-if="inv.note" class="text-white/35">{{ inv.note }}</span>
                  <span class="font-mono text-[10px] text-white/25">发于 {{ friendlyTime(inv.createdAtUtc) }}</span>
                  <button
                    class="ml-auto rounded-md border border-rose-500/30 px-2 py-0.5 text-[11px] text-rose-300/80 transition hover:bg-rose-500/10 disabled:opacity-40"
                    :disabled="inviteBusy !== null"
                    title="抽掉这枚码：扫码与账密两条兑现路一起断，席位本身不动"
                    @click="revokeInvite(inv)"
                  >
                    {{ inviteBusy === inv.id ? '作废中…' : '作废' }}
                  </button>
                </li>
              </ul>
            </div>

      </section>

      <section v-if="quietOpen" class="lbs-card md:col-span-2 order-[58]">
        <div class="mb-4 flex flex-wrap items-center justify-between gap-2">
          <h3 class="lbs-title !mb-0">
            全站公告
            <span class="ml-1 rounded-full border border-amber-400/30 px-2 py-0.5 text-[10px] normal-case tracking-normal text-amber-300/70">
              只向 active 席位播送
            </span>
          </h3>
        </div>
        <div class="flex flex-col gap-2 md:flex-row md:items-start">
          <input v-model="announceTitle" class="eng-input md:w-64" placeholder="公告标题" maxlength="60" />
          <textarea
            v-model="announceBody"
            class="eng-input min-h-[40px] flex-1 resize-y"
            placeholder="公告正文（成员在站内信箱里读到它）"
            maxlength="2000"
          ></textarea>
          <div class="flex shrink-0 items-center gap-2">
            <button
              class="lbs-btn"
              :class="announceArmed ? 'lbs-btn-danger-armed' : ''"
              :disabled="announceBusy"
              @click="sendAnnounce"
            >
              {{ announceBusy ? '播送中……' : announceArmed ? '确定向全站播送？再点一次执行' : '向全站播送' }}
            </button>
            <span v-if="announceText" class="text-[11px] text-white/40">{{ announceText }}</span>
          </div>
        </div>
        <!-- 发过的播送（11-08 站长拍板「公告发出去收不回」）：发错了能收回，收回了还能反悔。
             只列带批次号的那批：11-08 之前发的没记 batch_id，拿标题正文去凑会误伤同文旧批。 -->
        <div class="mt-3 border-t border-white/5 pt-3">
          <div class="mb-2 flex flex-wrap items-center gap-2">
            <span class="font-mono text-[11px] text-white/30">
              {{ announceBatches === null ? '播送台账没拉到' : announceBatches.length ? `发过的播送 ${announceBatches.length} 批` : '还没播送过（11-08 之前的旧公告没记批次号，列不出来）' }}
            </span>
            <button class="lbs-btn-ghost !px-2 !py-0.5 !text-[11px]" :disabled="announceBatchesLoading" @click="loadAnnounceBatches">
              {{ announceBatchesLoading ? '重拉中…' : '重拉' }}
            </button>
            <span v-if="announceBatchText" class="text-[11px] text-white/40">{{ announceBatchText }}</span>
          </div>
          <ul v-if="announceBatches && announceBatches.length" class="flex flex-col gap-1.5">
            <li v-for="batch in announceBatches" :key="batch.batchId" class="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs">
              <span class="font-medium" :class="batch.revoked ? 'text-white/30 line-through' : ''">{{ batch.title }}</span>
              <span class="font-mono text-[11px] text-white/30">{{ friendlyTime(batch.createdAtUtc) }} · 送达 {{ batch.sent }} 席</span>
              <span v-if="batch.revoked" class="rounded-full border border-white/10 px-1.5 text-[10px] text-white/30">已收回</span>
              <button
                v-if="!batch.revoked"
                class="lbs-btn-ghost !px-2 !py-0.5 !text-[11px]"
                :disabled="announceBatchBusy !== null"
                title="成员那边的铃铛与信箱一并干净（打时间戳不删行，反悔有恢复钮）"
                @click="revokeAnnounce(batch)"
              >
                {{ announceBatchBusy === batch.batchId ? '收回中…' : '收回' }}
              </button>
              <button
                v-else
                class="lbs-btn-ghost !px-2 !py-0.5 !text-[11px]"
                :disabled="announceBatchBusy !== null"
                title="把这次播送原样放回成员信箱，连谁已读过都带回来"
                @click="restoreAnnounce(batch)"
              >
                {{ announceBatchBusy === batch.batchId ? '恢复中…' : '恢复' }}
              </button>
            </li>
          </ul>
        </div>
      </section>

      <!-- 看板娘性格卡（11-16 站长令）：性格与说话风格全站共享一张卡，留空用内置默认；家底数字与查账规矩每轮硬注入，拆不掉底线 -->
      <section v-if="quietOpen" class="lbs-card md:col-span-2 order-[59]">
        <div class="mb-3 flex items-center justify-between gap-2">
          <h3 class="lbs-title !mb-0">
            看板娘性格卡
            <span class="ml-1 rounded-full border px-2 py-0.5 text-[10px] normal-case tracking-normal" :class="personaIsCustom ? 'border-adminAccent/40 text-adminAccent/80' : 'border-white/10 text-white/40'">
              {{ personaIsCustom ? '自定义性格生效中' : '内置默认性格' }}
            </span>
          </h3>
          <span class="font-mono text-[11px] text-white/25">{{ personaDraft.length }} / {{ personaMaxChars }} 字</span>
        </div>
        <p class="mb-3 text-xs leading-5 text-white/35">
          她是谁、什么口气、怎么说话，全由你写，全站成员共用这一张卡。日期、成员名、家底数字与数据查账规矩由站里每轮自动注入，不进这张卡——性格怎么写都拆不掉防编造的底线。留空保存 = 回到站内原性（框里淡字就是默认原文，照抄着改也行）。
        </p>
        <textarea
          v-model="personaDraft"
          rows="7"
          :maxlength="personaMaxChars"
          :placeholder="personaDefault ?? '（默认原文还没拉到——盖子合上再掀开试试）'"
          class="w-full rounded-xl border border-white/10 bg-white/[0.04] px-3.5 py-3 text-[13px] leading-6 text-white/85 placeholder:text-white/25 focus:border-adminAccent/50 focus:outline-none"
        ></textarea>
        <div class="mt-3 flex items-center gap-2">
          <button class="lbs-btn" :disabled="personaBusy" @click="savePersona">{{ personaBusy ? '存卡中…' : '保存性格卡' }}</button>
          <button class="lbs-btn-ghost" :disabled="personaBusy || (!personaIsCustom && !personaDraft)" @click="restorePersonaDefault">恢复默认</button>
          <p v-if="personaNotice" class="ml-2 font-mono text-[11px]" :class="personaNotice.kind === 'ok' ? 'text-adminAccent/80' : 'text-rose-300/80'">{{ personaNotice.text }}</p>
        </div>
      </section>

      <!-- 事件台账（11-08）：后端早有取数口，前端一直没接线；站长查「谁撞过门」如今不必手搓 sqlite。
           只读不写：这里没有任何改库的钮，看客与执法分开，误按的可能留零。 -->
      <!-- 事件台账 11-09 从「货架」抽屉拎出来常驻（站长点名：查账是常事，不该压在「设好就不碰」那一堆底下），自带组头与导航胶囊 -->
      <div id="grp-events" class="order-[40] md:col-span-2 flex items-baseline gap-2.5 pt-3">
        <h2 class="text-[11px] font-medium uppercase tracking-[0.18em] text-white/40">事件台账</h2>
        <span class="font-mono text-[11px] text-white/20">谁撞过门 · 谁改过稿 · 谁在试密码（只读）</span>
        <span class="ml-1 h-px flex-1 bg-white/5"></span>
      </div>
      <section class="lbs-card md:col-span-2 order-[41]">
        <div class="mb-3 flex flex-wrap items-center justify-between gap-2">
          <h3 class="lbs-title !mb-0">
            事件台账
            <span class="ml-1 rounded-full border border-white/10 px-2 py-0.5 text-[10px] normal-case tracking-normal text-white/40">
              {{ eventsRows === null ? '没拉到' : `共 ${eventsTotal} 条，摆 ${eventsShown.length} 条` }}
            </span>
          </h3>
          <div class="flex flex-wrap items-center gap-2">
            <select v-model="eventsActor" class="eng-input !w-auto !py-1 text-[11px]" title="谁干的事">
              <option value="">全部身份</option>
              <option value="guest">访客</option>
              <option value="member">席位</option>
              <option value="admin">站长</option>
              <option value="system">站内</option>
            </select>
            <select v-model="eventsPrefix" class="eng-input !w-auto !py-1 text-[11px]" title="只看哪一族事" @change="loadEventsLedger">
              <option v-for="f in EVENT_FAMILY" :key="f.prefix" :value="f.prefix">{{ f.label }}</option>
            </select>
            <input
              v-model="eventsPrefix"
              class="eng-input !w-32 !py-1 font-mono text-[11px]"
              placeholder="自定前缀"
              title="上面那一屏筛不到的拿这儿：如 playback. 或 danmaku.recycle"
              @keyup.enter="loadEventsLedger"
            />
            <select v-model.number="eventsLimit" class="eng-input !w-auto !py-1 text-[11px]" title="摆最近多少条">
              <option :value="100">最近 100 条</option>
              <option :value="200">最近 200 条</option>
              <option :value="500">最近 500 条</option>
              <option :value="2000">最近 2000 条</option>
            </select>
            <button class="lbs-btn-ghost !px-3 !py-1 !text-xs" :disabled="eventsLoading" @click="loadEventsLedger">
              {{ eventsLoading ? '翻账中…' : '重拉' }}
            </button>
            <button
              class="lbs-btn-ghost !px-3 !py-1 !text-xs"
              :disabled="!eventsRows || !eventsRows.length"
              :title="allEventsShown ? '一拍回到只看最近十条' : `再多摆 ${EVENTS_STEP} 行`"
              @click="stepEvents"
            >
              {{ allEventsShown ? '收起流水' : `再摆 ${EVENTS_STEP} 条` }}
            </button>
          </div>
        </div>
        <p v-if="eventsNotice" class="mb-2 text-[11px] text-rose-300/70">{{ eventsNotice }}</p>
        <!-- 这句只是旁注，不能与列表争位置（上一版写成 v-else-if，结果不展开时一句旁注把整张流水顶掉了） -->
        <p v-if="eventsRows && eventsRows.length && !allEventsShown" class="mb-1 text-[11px] text-white/30">
          只露了最近 {{ EVENTS_PEEK }} 条（共 {{ eventsTotal }} 条）。要往回翻就按「再摆 {{ EVENTS_STEP }} 条」，一次不拍一整面墙。
        </p>
        <ul v-if="eventsRows && eventsRows.length" class="flex flex-col gap-1">
          <li
            v-for="row in eventsShown"
            :key="row.id"
            class="cursor-pointer rounded-lg border border-transparent px-2 py-1 transition hover:border-white/10 hover:bg-white/[0.03]"
            @click="eventsOpen = eventsOpen === row.id ? null : row.id"
          >
            <div class="flex flex-wrap items-baseline gap-x-3 gap-y-0.5 text-xs">
              <span class="font-mono text-[11px] text-white/30">{{ friendlyTime(row.tsUtc) }}</span>
              <span
                class="rounded-full border px-1.5 text-[10px]"
                :class="row.actorType === 'admin' ? 'border-adminAccent/30 text-adminAccent/80'
                  : row.actorType === 'guest' ? 'border-white/10 text-white/35'
                  : row.actorType === 'system' ? 'border-white/10 text-white/30'
                  : 'border-stationBlue/30 text-stationBlue/80'"
              >
                {{ eventsActorLabel(row.actorType) }}<template v-if="row.actorMid"> · {{ row.actorMid }}</template>
              </span>
              <span class="text-xs" :class="EVENT_LABEL[row.event] ? 'text-white/75' : 'font-mono text-white/45'" :title="row.event">{{ eventLabel(row.event) }}</span>
              <span v-if="eventsSubjectText(row)" class="font-mono text-[11px] text-white/35">{{ eventsSubjectText(row) }}</span>
              <span v-if="row.ip" class="font-mono text-[11px] text-white/25">{{ row.ip }}</span>
              <span class="ml-auto font-mono text-[10px] text-white/20">{{ eventsOpen === row.id ? '收起' : '详情' }}</span>
            </div>
            <pre
              v-if="eventsOpen === row.id"
              class="mt-1 max-h-52 overflow-auto whitespace-pre-wrap break-all rounded-md bg-black/30 p-2 font-mono text-[11px] text-white/55"
            >事件名 {{ row.event }}
{{ eventsPayloadText(row) }}</pre>
          </li>
        </ul>
        <p v-else-if="eventsRows && !eventsRows.length" class="text-[11px] text-white/30">
          这一筛下头一条都没有。换个事件族试试（观映 / 进门 / 稿件 / 席位 / 弹幕 / 加档），或拿自定前缀填个准的。
        </p>
      </section>

      <!-- 弹幕回收站（11-08）：单删弹幕从此有后路。与整片回收站分家两张卡（那卡管稿件连文件一起销账）。 -->
      <section v-show="recycleOpen" class="lbs-card md:col-span-2 order-[67]">
        <div class="mb-3 flex flex-wrap items-center justify-between gap-2">
          <h3 class="lbs-title !mb-0">
            弹幕回收站
            <span class="ml-1 rounded-full border border-white/10 px-2 py-0.5 text-[10px] normal-case tracking-normal text-white/40">
              {{ dmRecycle === null ? '没拉到' : `躺着 ${dmRecycleTotal} 条` }}
            </span>
          </h3>
          <div class="flex items-center gap-2">
            <button class="lbs-btn-ghost !px-3 !py-1 !text-xs" :disabled="dmRecycleLoading" @click="loadDmRecycle">
              {{ dmRecycleLoading ? '翻箱中…' : '重拉' }}
            </button>
            <button
              class="lbs-btn-danger !px-3 !py-1 !text-xs"
              :disabled="dmRecycleBusy !== null || !dmRecycle?.length"
              title="把这些快照一并抹掉，之后一条都恢复不回来"
              @click="clearDmRecycle"
            >
              {{ dmRecycleBusy === 'all' ? '清空中…' : '清空回收站' }}
            </button>
          </div>
        </div>
        <p v-if="dmRecycleNotice" class="mb-2 text-[11px]" :class="dmRecycleNotice.kind === 'ok' ? 'text-emerald-300/80' : 'text-rose-300/70'">
          {{ dmRecycleNotice.text }}
        </p>
        <ul v-if="dmRecycle && dmRecycle.length" class="flex flex-col gap-1.5">
          <li v-for="item in dmRecycle" :key="item.id" class="flex flex-wrap items-center gap-x-3 gap-y-1 rounded-lg border border-white/5 px-2 py-1.5">
            <span class="min-w-0 flex-1 truncate text-xs text-white/75" :title="item.text">{{ item.text || '（空弹幕）' }}</span>
            <span class="shrink-0 font-mono text-[11px] text-white/35">
              {{ item.bvid || '已下架' }} · P{{ (item.pageIdx ?? 0) + 1 }} · {{ dmReportClock(item.tSec) }}
            </span>
            <span class="shrink-0 text-[11px] text-white/35">{{ item.senderName || '匿名朋友' }}</span>
            <span
              class="shrink-0 rounded-full border px-1.5 text-[10px]"
              :class="item.source === 'self' ? 'border-white/10 text-white/35' : 'border-rose-400/30 text-rose-300/70'"
            >
              {{ dmRecycleSourceLabel(item.source) }}
            </span>
            <span class="shrink-0 font-mono text-[10px] text-white/25">删于 {{ friendlyTime(item.deletedAtUtc) }}</span>
            <button
              class="shrink-0 rounded-md border border-white/10 px-2 py-0.5 text-[11px] text-white/50 transition hover:border-stationBlue/50 hover:text-stationBlue disabled:cursor-not-allowed disabled:opacity-35"
              :disabled="dmRecycleBusy !== null || !item.inStation"
              :title="item.inStation ? '连原 id 一起恢复，举报单那条「去看这条弹幕」的链也接着' : '片子已经不在架，恢复去也没人看得见，先把稿件恢复回来'"
              @click="restoreDmRecycle(item)"
            >
              {{ dmRecycleBusy === item.id ? '恢复中…' : '恢复' }}
            </button>
            <button
              class="shrink-0 rounded-md border border-rose-500/30 px-2 py-0.5 text-[11px] text-rose-300/80 transition hover:bg-rose-500/10 disabled:opacity-40"
              :disabled="dmRecycleBusy !== null"
              title="就地抹掉这行快照，之后恢复不回来"
              @click="purgeDmRecycle(item)"
            >
              销毁
            </button>
          </li>
        </ul>
        <p v-else-if="dmRecycle && !dmRecycle.length" class="text-[11px] text-white/30">
          回收站是空的。删掉的弹幕（含举报单处置掉的那些）会先落在这儿，恢复得回也销毁得了。
        </p>
      </section>

      <!-- 加档版本回收站（11-08）：投稿人删得动自己那版的件，反悔章只站长握得住。
           一行卡 = 一次删除（一组），恢复与销毁都是整组一次，与点单同粒度。 -->
      <section v-show="recycleOpen" class="lbs-card md:col-span-2 order-[68]">
        <div class="mb-3 flex flex-wrap items-center justify-between gap-2">
          <h3 class="lbs-title !mb-0">
            加档回收站
            <span class="ml-1 rounded-full border border-white/10 px-2 py-0.5 text-[10px] normal-case tracking-normal text-white/40">
              {{ codecRecycle === null ? '没拉到' : `躺着 ${codecRecycleTotal} 行 · ${codecRecycleGroups.length} 组` }}
            </span>
          </h3>
          <button class="lbs-btn-ghost !px-3 !py-1 !text-xs" :disabled="codecRecycleLoading" @click="loadCodecRecycle">
            {{ codecRecycleLoading ? '翻箱中…' : '重拉' }}
          </button>
        </div>
        <p v-if="codecRecycleNotice" class="mb-2 text-[11px]" :class="codecRecycleNotice.kind === 'ok' ? 'text-emerald-300/80' : 'text-rose-300/70'">
          {{ codecRecycleNotice.text }}
        </p>
        <ul v-if="codecRecycleGroups.length" class="flex flex-col gap-1.5">
          <li v-for="group in codecRecycleGroups" :key="group.groupId" class="flex flex-wrap items-center gap-x-3 gap-y-1 rounded-lg border border-white/5 px-2 py-1.5">
            <span class="shrink-0 rounded-md border border-stationBlue/30 px-1.5 text-[11px] text-stationBlue/80">{{ codecLabel(group.codec) }}</span>
            <span class="min-w-0 flex-1 truncate font-mono text-[11px] text-white/45" :title="`P${group.pages.join(' P')}`">
              {{ group.bvid || '已下架' }} · 共 {{ group.pages.length }} 个分 P
            </span>
            <span class="shrink-0 font-mono text-[11px] text-white/35">{{ fmtBytes(group.bytes) }} · 搬走 {{ group.files }} 个件</span>
            <span
              v-if="group.fileless"
              class="shrink-0 text-[11px] text-white/25"
              title="账上有行、盘上没文件（当初就没落盘），恢复回来也只是菜单上多一档"
            >
              {{ group.fileless }} 行没带件
            </span>
            <span class="shrink-0 text-[11px] text-white/35">{{ codecRecycleSourceLabel(group.source) }} · {{ group.deletedBy || '无名' }}</span>
            <span class="shrink-0 font-mono text-[10px] text-white/25">删于 {{ friendlyTime(group.deletedAtUtc) }}</span>
            <button
              class="shrink-0 rounded-md border border-white/10 px-2 py-0.5 text-[11px] text-white/50 transition hover:border-stationBlue/50 hover:text-stationBlue disabled:cursor-not-allowed disabled:opacity-35"
              :disabled="codecRecycleBusy !== null || !group.inStation"
              :title="group.inStation ? '文件与台账整组搬回原位，当初那一次点单原样回来' : '片子已经不在架，先把稿件恢复回来再动这一版'"
              @click="restoreCodecGroup(group)"
            >
              {{ codecRecycleBusy === group.groupId ? '恢复中…' : '整组恢复' }}
            </button>
            <button
              class="shrink-0 rounded-md border border-rose-500/30 px-2 py-0.5 text-[11px] text-rose-300/80 transition hover:bg-rose-500/10 disabled:opacity-40"
              :disabled="codecRecycleBusy !== null"
              title="回收目录里那些文件就地抹掉，之后恢复不回来"
              @click="purgeCodecGroup(group)"
            >
              销毁
            </button>
          </li>
        </ul>
        <p v-else-if="codecRecycle && !codecRecycle.length" class="text-[11px] text-white/30">
          回收站是空的。已删除的加档版本（含投稿人自己删的那些）会先落在这儿，可整组恢复，也可销毁。
        </p>
      </section>

      <section class="lbs-card order-[24]">
        <div class="mb-4 flex flex-wrap items-center justify-between gap-2">
          <h3 class="lbs-title !mb-0">
            动态关注
            <span class="ml-1 rounded-full border border-adminAccent/30 px-2 py-0.5 text-[10px] normal-case tracking-normal text-adminAccent/70">
              {{ dynTargets ? `${dynTargets.length} 位盯梢` : '—' }}
            </span>
          </h3>
          <button class="lbs-btn shrink-0" :disabled="dynRoundBusy" @click="dynRefreshRound">
            {{ dynRoundBusy ? '巡捕在跑…' : '立即巡一轮' }}
          </button>
        </div>
        <div class="mb-3 flex flex-wrap items-center gap-2">
          <input v-model="dynDraft" class="eng-input md:w-72" placeholder="UP 的 MID 或 space.bilibili.com/…" maxlength="80" @keyup.enter="addDynTarget" />
          <button class="lbs-btn" :disabled="dynBusy" @click="addDynTarget">{{ dynBusy ? '存盘中…' : '添加' }}</button>
          <span v-if="dynText" class="text-[11px] text-white/40">{{ dynText }}</span>
        </div>
        <ul class="space-y-1">
          <li v-for="t in dynTargets ?? []" :key="t.mid" class="flex items-center gap-2.5 rounded-lg bg-white/5 px-2.5 py-1.5">
            <img :src="t.faceUrl || defaultAvatar" referrerpolicy="no-referrer" loading="lazy" class="h-7 w-7 shrink-0 rounded-full object-cover" alt="" @error="onAvatarError" />
            <span class="min-w-0 flex-1 truncate text-[12px] text-white/75">
              {{ t.nickname || `B站号 ${t.mid}` }}
              <span class="font-mono text-[10px] text-white/30">B站号 {{ t.mid }}</span>
            </span>
            <span class="shrink-0 font-mono text-[11px] text-white/45">{{ t.count }} 条</span>
            <span class="shrink-0 font-mono text-[10px] text-white/25">{{ t.lastFetchAtUtc ? friendlyTime(t.lastFetchAtUtc) : '未抓' }}</span>
            <span class="shrink-0 rounded-full px-1.5 text-[10px]" :class="t.enabled ? 'border border-emerald-400/40 text-emerald-300' : 'border border-white/15 text-white/35'">{{ t.enabled ? '盯中' : '停盯' }}</span>
            <button
              class="shrink-0 rounded-md px-1.5 py-0.5 text-[11px] transition"
              :class="dynArmedDelete === t.mid ? 'lbs-btn-danger-armed' : 'text-rose-300/70 hover:bg-rose-500/10'"
              @click="removeDynTarget(t)"
            >
              {{ dynArmedDelete === t.mid ? '确认撤' : '撤' }}
            </button>
          </li>
          <li v-if="!dynTargets?.length" class="py-2 text-[11px] text-white/25">还没盯任何 UP。订阅台账里的 UP 主会自动灌进名单，这里也能手动加。</li>
        </ul>
      </section>

      <section class="lbs-card md:col-span-2 order-[21] border-l-2 border-l-adminAccent/50">
        <div class="mb-4 flex items-center justify-between gap-2">
          <h3 class="lbs-title !mb-0">
            下载队列
            <span
              class="ml-1 rounded-full border px-2 py-0.5 text-[10px] normal-case tracking-normal"
              :class="dlQueue?.paused ? 'border-amber-400/40 text-amber-300' : 'border-emerald-400/40 text-emerald-300'"
            >
              {{ dlQueue?.paused ? '已暂停 · 提交闸落下' : '运行中' }}
            </span>
          </h3>
          <div class="flex shrink-0 items-center gap-3">
            <label class="flex items-center gap-1.5 text-[11px] text-white/40">
              逐件间隔
              <select v-model="dlInterval" class="eng-input w-auto cursor-pointer !py-1 !text-[11px]" @change="dlSetInterval">
                <option value="0">极速（5~15s）</option>
                <option value="60">1 分钟</option>
                <option value="300">5 分钟</option>
                <option value="600">10 分钟</option>
                <option value="1800">30 分钟</option>
              </select>
            </label>
            <p class="text-[11px] text-white/30">
              进站中 {{ dlQueue?.downloading?.length ?? 0 }} · 排队 {{ dlQueue?.pending?.length ?? 0 }}
              <!-- 加档单独报数：三组活挤在「进站中/排队」两个词里，看的人分不清哪个数是编码加档；平时为零就不占位 -->
              <template v-if="dlQueue?.codecs?.length"> · 加档 {{ dlQueue.codecs.length }}</template>
            </p>
            <div class="flex gap-2">
              <button class="lbs-btn-ghost !px-3 !py-1 !text-[11px]" :disabled="dlBusy" @click="dlToggle">
                {{ dlBusy ? '操作中…' : dlQueue?.paused ? '继续全部' : '暂停全部' }}
              </button>
              <button
                class="lbs-btn-ghost !px-3 !py-1 !text-[11px] hover:!text-rose-300"
                :disabled="dlBusy || !dlQueue?.pending?.length"
                @click="dlCancelAll"
              >
                取消排队区
              </button>
              <button
                class="lbs-btn-ghost !px-3 !py-1 !text-[11px] hover:!text-rose-300"
                :disabled="dlBusy || !dlQueue?.canceledCount"
                @click="dlPurgeCanceled"
              >
                清理已取消{{ dlQueue?.canceledCount ? ` (${dlQueue.canceledCount})` : '' }}
              </button>
            </div>
          </div>
        </div>
        <p class="mb-3 text-xs leading-5 text-white/30">
          暂停是全站闸：提交停投、在途的引擎任务挂起，重启也记得住；取消只清排队区（还没碰引擎的行，零成本），取消后可在首页卡阵重新排队。
        </p>
        <p v-if="dlText" class="mb-3 text-xs text-adminAccent/80">{{ dlText }}</p>
        <div v-if="!dlQueue?.downloading?.length && !dlQueue?.pending?.length && !dlQueue?.codecs?.length" class="py-6 text-center text-xs text-white/25">
          队列空空，没有在途和排队的馆藏
        </div>
        <div v-else class="space-y-4">
          <div v-if="dlQueue?.downloading?.length">
            <p class="mb-2 text-[11px] uppercase tracking-widest text-stationPink/70">进站中</p>
            <ul class="space-y-1.5">
              <li v-for="item in dlQueue.downloading" :key="`dl-${item.id}`" class="flex items-center gap-2.5">
                <span class="h-1.5 w-1.5 shrink-0 animate-pulse rounded-full bg-stationPink"></span>
                <!-- 下载队列条目还没到站、没有本地件也没有档位缩略：只能直出热链原图，B 站缩放参数按站长要求全站停用（11-03） -->
                <img v-if="item.coverUrl" :src="queueCover(item.coverUrl)" referrerpolicy="no-referrer" loading="lazy" decoding="async" class="h-9 w-15 shrink-0 rounded-md object-cover" alt="" @error="onQueueCoverError($event, item.coverUrl)" />
                <span v-else class="h-9 w-15 shrink-0 rounded-md bg-white/5"></span>
                <span class="min-w-0 flex-1 truncate text-xs text-white/75" :title="item.title || item.bvid">{{ item.title || item.bvid }}</span>
                <span class="shrink-0 text-[10px] text-white/25">{{ item.bvid }}</span>
              </li>
            </ul>
          </div>
          <div v-if="dlQueue?.pending?.length">
            <p class="mb-2 text-[11px] uppercase tracking-widest text-white/35">排队中</p>
            <ul class="max-h-[320px] space-y-1.5 overflow-y-auto pr-1">
              <li v-for="item in dlQueue.pending" :key="`pd-${item.id}`" class="flex items-center gap-2.5">
                <!-- 同上：到站前没有本地件，只能直出热链原图，B 站缩放参数按站长要求全站停用（11-03） -->
                <img v-if="item.coverUrl" :src="queueCover(item.coverUrl)" referrerpolicy="no-referrer" loading="lazy" decoding="async" class="h-9 w-15 shrink-0 rounded-md object-cover opacity-70" alt="" @error="onQueueCoverError($event, item.coverUrl)" />
                <span v-else class="h-9 w-15 shrink-0 rounded-md bg-white/5"></span>
                <span class="min-w-0 flex-1 truncate text-xs text-white/60" :title="item.title || item.bvid">{{ item.title || item.bvid }}</span>
                <span class="shrink-0 text-[10px] text-white/25">{{ item.bvid }}</span>
                <button
                  class="shrink-0 rounded-md p-1 text-white/35 transition hover:bg-rose-500/20 hover:text-rose-300"
                  title="取消这件排队馆藏"
                  :disabled="dlBusy"
                  @click="dlCancelOne(item.id)"
                >
                  <svg viewBox="0 0 24 24" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="2">
                    <path d="M6 6l12 12M18 6L6 18" stroke-linecap="round" />
                  </svg>
                </button>
              </li>
            </ul>
          </div>
          <!-- 编码加档（第二发）：跟整片进站同队不同工——同一时刻只跑一个编码，waiting:true 的就是在等前一个跑完。
               每行可取消：后端销引擎账+判 Failed，反悔回到那支片子的播放器里重新点单；停滞超 30 分钟还有看门狗自动收摊。 -->
          <div v-if="dlQueue?.codecs?.length">
            <p class="mb-2 text-[11px] uppercase tracking-widest text-white/35">编码加档</p>
            <ul class="max-h-[320px] space-y-1.5 overflow-y-auto pr-1">
              <li v-for="item in dlQueue.codecs" :key="`cc-${item.videoId}-${item.codec}-${item.pageIdx}-${item.waiting}`" class="relative flex items-center gap-2.5">
                <span class="h-1.5 w-1.5 shrink-0 rounded-full" :class="item.waiting ? 'bg-white/25' : 'animate-pulse bg-stationPink'"></span>
                <span class="min-w-0 flex-1 truncate text-xs" :class="item.waiting ? 'text-white/60' : 'text-white/75'" :title="item.hint || `${item.title || item.bvid} · ${codecLabel(item.codec)} 加档`">
                  {{ item.title || item.bvid }} · {{ codecLabel(item.codec) }} 加档<span v-if="item.pageIdx != null" class="text-white/40"> · P{{ item.pageIdx }}</span>
                </span>
                <!-- 实况小注：让路中的缘由与回炉倒计时；进站中的引擎进度（百分比+已下+速度），hover 可看让路缘由原文 -->
                <span v-if="codecDetail(item)" class="shrink-0 text-[10px] tabular-nums text-white/40" :title="item.hint || codecDetail(item)">{{ codecDetail(item) }}</span>
                <span class="shrink-0 text-[10px] text-white/25">{{ item.bvid }}</span>
                <span
                  class="shrink-0 rounded-full border px-2 py-0.5 text-[10px]"
                  :class="item.waiting ? 'border-white/15 text-white/40' : 'border-stationPink/40 text-stationPink'"
                >
                  {{ item.waiting ? '排队中' : '进站中' }}
                </span>
                <button
                  class="shrink-0 rounded-md p-1 text-white/35 transition hover:bg-rose-500/20 hover:text-rose-300"
                  title="取消这次加档（销引擎账，想再要回那支片子的播放器里点「加档其他版本」）"
                  :disabled="dlBusy"
                  @click="codecCancel(item)"
                >
                  <svg viewBox="0 0 24 24" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="2">
                    <path d="M6 6l12 12M18 6L6 18" stroke-linecap="round" />
                  </svg>
                </button>
                <!-- 进站中的行底一条细进度条，不占字位也能看出在动 -->
                <span
                  v-if="!item.waiting && item.progress && item.progress.percentage > 0"
                  class="absolute bottom-[-3px] left-0 h-0.5 rounded-full bg-stationPink/70 transition-all"
                  :style="{ width: `${Math.min(100, Math.round(item.progress.percentage))}%` }"
                ></span>
              </li>
            </ul>
          </div>
        </div>
      </section>

      <section id="grp-failed" class="lbs-card md:col-span-2 order-[22] border-l-2 border-l-rose-400/40">
        <div class="mb-4 flex items-center justify-between gap-2">
          <h3 class="lbs-title !mb-0">
            失败件明细
            <span class="ml-1 rounded-full border px-2 py-0.5 text-[10px] normal-case tracking-normal" :class="failedItems?.length ? 'border-rose-400/40 text-rose-300' : 'border-emerald-400/30 text-emerald-300'">
              {{ failedItems === null ? '拉账中…' : failedItems.length ? `${failedItems.length} 件在途` : '队列干净' }}
            </span>
          </h3>
          <div class="flex items-center gap-2">
            <p v-if="failedNotice" class="font-mono text-[11px]" :class="{ 'text-adminAccent/80': failedNotice.kind === 'ok', 'text-rose-300/80': failedNotice.kind === 'err', 'text-amber-300/80': failedNotice.kind === 'warn' }">{{ failedNotice.text }}</p>
            <button type="button" class="text-[11px] text-white/30 transition hover:text-white/60" @click="loadFailed">重拉</button>
          </div>
        </div>
        <p v-if="failedItems === null" class="text-xs text-white/30">失败件账目拉取中……</p>
        <p v-else-if="!failedItems.length" class="text-xs text-white/30">眼下没有卡着的失败件。</p>
        <ul v-else class="space-y-3">
          <li v-for="item in failedItems" :key="`fail-${item.id}`" class="rounded-lg border border-rose-400/15 bg-rose-400/[0.04] px-3.5 py-3">
            <div class="flex flex-wrap items-baseline gap-2">
              <span class="min-w-0 flex-1 truncate text-sm text-white/85" :title="item.title || '标题都没归档（这稿没走到 B 站归档那步）'">{{ item.title || '（标题未归档）' }}</span>
              <span class="shrink-0 font-mono text-[10px] text-white/30">{{ item.bvid }}</span>
              <span class="shrink-0 text-[10px] text-white/30">{{ fmtFailedAge(item.createdAtUtc) }}</span>
            </div>
            <p class="mt-1.5 break-words text-[12px] leading-5 text-rose-300/75">{{ item.lastError || '没记报错缘由' }}</p>
            <div class="mt-2.5 flex items-center gap-2">
              <button v-if="!item.requireClaim" class="lbs-btn-ghost !px-2.5 !py-0.5 !text-[11px]" :disabled="failedBusy === item.id" @click="failedAction(item, 'retry')">重试</button>
              <button v-else class="lbs-btn !px-2.5 !py-0.5 !text-[11px]" :disabled="failedBusy === item.id" title="挂到站长名下并退回队列（后端补归属顺手把 Failed 退回队）" @click="failedAction(item, 'claim')">挂我名下重投</button>
              <button
                class="rounded-md px-2 py-0.5 text-[11px] text-white/35 transition hover:bg-rose-500/20 hover:text-rose-300"
                :disabled="failedBusy === item.id"
                :title="failedArmed === item.id ? '再点一下坐实：进回收站（可捞）' : '进回收站（可捞）'"
                @click="failedDestroy(item)"
              >
                {{ failedArmed === item.id ? '真销毁？再点一下' : '销毁' }}
              </button>
              <span v-if="failedBusy === item.id" class="text-[10px] text-white/30">处理中…</span>
            </div>
          </li>
        </ul>
      </section>

      <section class="lbs-card order-[23] border-l-2 border-l-adminAccent/50">
        <div class="mb-4 flex items-center justify-between">
          <h3 class="lbs-title !mb-0">
            订阅更新
            <span class="ml-1 rounded-full border border-adminAccent/30 px-2 py-0.5 text-[10px] normal-case tracking-normal text-adminAccent/70">每小时 · 已上岗</span>
          </h3>
        </div>
        <p class="mb-3 text-xs leading-5 text-white/30">
          登记收藏夹链接或博主（UP 主）后，爬工每小时自动巡一遍，新 BV 自动投稿进站（台账已有的一笑而过）。博主每轮收最新 20 条防灌水，收藏夹整架收。
        </p>
        <div class="flex flex-wrap items-center gap-2">
          <select v-model="subKind" class="eng-input w-auto cursor-pointer !text-sm" :disabled="subBusy">
            <option value="fav">收藏夹</option>
            <option value="up">博主 MID</option>
          </select>
          <input
            v-model="subTarget"
            type="text"
            :placeholder="subKind === 'fav' ? '收藏夹链接或 space.bilibili.com/…/favlist?fid=…' : '博主 MID（如 946974）或空间链接'"
            class="lbs-input min-w-[280px] flex-1"
            :disabled="subBusy"
            @keyup.enter="addSubscription"
          />
          <button class="lbs-btn" :disabled="subBusy || !subTarget.trim()" @click="addSubscription">订阅</button>
        </div>
        <ul v-if="subscriptions?.length" class="mt-4 flex flex-wrap gap-2">
          <li
            v-for="item in subscriptions"
            :key="`${item.kind}:${item.target}`"
            class="flex items-center gap-2 rounded-full border border-white/10 bg-black/30 py-1 pl-3 pr-1.5 font-mono text-xs text-white/70"
          >
            <span :class="item.kind === 'fav' ? 'text-adminAccent/80' : 'text-stationPink/80'">{{ item.kind === 'fav' ? '藏' : 'UP' }}</span>
            <span class="max-w-[360px] truncate">{{ item.target }}</span>
            <button
              class="flex h-5 w-5 items-center justify-center rounded-full text-white/30 transition hover:bg-white/10 hover:text-rose-300"
              :disabled="subBusy"
              aria-label="退订"
              @click="removeSubscription(item)"
            >
              ✕
            </button>
          </li>
        </ul>
        <p v-else class="mt-4 text-xs text-white/25">还没有订阅。收藏夹链接、博主 MID 或空间链接都能填。</p>

        <div v-if="subscriptions?.length" class="mt-4 border-t border-white/5 pt-3">
          <div class="flex items-center justify-between gap-2">
            <p class="text-[11px] text-white/30">{{ crawlText }}</p>
            <button class="lbs-btn-ghost shrink-0 !px-3 !py-1 !text-[11px]" :disabled="crawlBusy" @click="crawlNow">
              {{ crawlBusy ? '爬工干活中…' : '立刻爬一轮' }}
            </button>
          </div>
          <p v-if="throttleStatus?.coolingDown" class="mt-2 text-[11px] leading-5 text-amber-300/85">
            ⚠ 撞上 B 站风控，分站冷却中，约 {{ Math.ceil((throttleStatus.remainingSeconds || 0) / 60) }} 分钟后自动回暖；期间爬工跳轮、导入暂拦。
          </p>
          <ul v-if="activeCrawlResults.length" class="mt-2 space-y-1">
            <li
              v-for="(r, i) in activeCrawlResults"
              :key="`${r.kind}:${r.target}:${i}`"
              class="font-mono text-[11px]"
              :class="r.ok ? 'text-white/40' : 'text-rose-300/80'"
            >
              <span :class="r.kind === 'fav' ? 'text-adminAccent/70' : 'text-stationPink/70'">{{ r.kind === 'fav' ? '藏' : 'UP' }}</span>
              {{ r.target }}：{{ r.ok ? (r.added ? `+${r.added} 件新货${r.duplicated ? ` · ${r.duplicated} 件已有` : ''}` : (r.error || '一切如旧')) : `没爬成：${r.error}` }}
            </li>
          </ul>
        </div>

        <p v-if="subNotice" class="mt-3 font-mono text-xs" :class="subNotice.kind === 'ok' ? 'text-adminAccent/80' : 'text-rose-300/80'">
          {{ subNotice.text }}
        </p>
      </section>

      <section class="lbs-card order-[13]">
        <h3 class="lbs-title">分站状态</h3>
        <dl class="space-y-3 font-mono text-sm">
          <div class="flex justify-between">
            <dt class="text-white/50">应答</dt>
            <dd :class="health ? 'text-adminAccent' : 'text-white/30'">{{ health ? 'ok' : '—' }}</dd>
          </div>
          <div class="flex justify-between">
            <dt class="text-white/50">版本</dt>
            <dd>{{ health?.version ?? '—' }}</dd>
          </div>
          <div class="flex justify-between">
            <dt class="text-white/50">服务器时间</dt>
            <dd>{{ health ? new Date(health.serverTimeUtc).toLocaleString('zh-CN') : '—' }}</dd>
          </div>
        </dl>
      </section>

      <section class="lbs-card order-[14]">
        <h3 class="lbs-title">存储</h3>
        <dl class="space-y-3 font-mono text-sm">
          <div class="flex justify-between gap-4">
            <dt class="shrink-0 text-white/50">数据目录</dt>
            <dd class="truncate text-right">{{ settings?.dataDir ?? '—' }}</dd>
          </div>
          <p class="text-xs leading-5 text-white/30">
            视频库、SQLite 台账、各类缓存都在这个目录里（B 站号也存在台账的 member_accounts 表，不另存明文 cookie 件）。容器内请挂载为 /data volume，并保持落在 NAS 本地盘。
          </p>
        </dl>
      </section>

      <!-- 弹幕举报：分站朋友对站内弹幕递的单子（B 站档案弹幕不归分站管，没这个入口）。
           单子上写的都是举报那一刻定格的快照：弹幕被删、席位改名都不回头改这张单。 -->
      <section class="lbs-card md:col-span-2 order-[15] border-l-2 border-l-adminAccent/50">
        <div class="mb-4 flex flex-wrap items-center justify-between gap-2">
          <h3 class="lbs-title !mb-0">
            弹幕举报
            <span class="ml-1 rounded-full border border-adminAccent/30 px-2 py-0.5 text-[10px] normal-case tracking-normal text-adminAccent/70">{{ dmReportPending }} 条待处理</span>
          </h3>
          <div class="flex shrink-0 items-center gap-2">
            <div class="flex items-center gap-0.5 rounded-full border border-white/10 p-0.5">
              <button
                type="button"
                class="rounded-full px-2.5 py-0.5 text-[11px] transition"
                :class="dmReportStatus === 'pending' ? 'bg-adminAccent/15 text-adminAccent' : 'text-white/40 hover:text-white/70'"
                @click="switchDmReportStatus('pending')"
              >
                待处理
              </button>
              <button
                type="button"
                class="rounded-full px-2.5 py-0.5 text-[11px] transition"
                :class="dmReportStatus === 'all' ? 'bg-adminAccent/15 text-adminAccent' : 'text-white/40 hover:text-white/70'"
                @click="switchDmReportStatus('all')"
              >
                全部
              </button>
            </div>
            <button class="lbs-btn-ghost !px-3 !py-1 !text-[11px]" :disabled="dmReportBusy !== null" @click="loadDanmakuReports">
              重新翻一遍
            </button>
          </div>
        </div>

        <p class="mb-3 text-xs leading-5 text-white/30">
          分站朋友看着不对的站内弹幕会递到这儿来。举报只是递单、不预先藏弹幕——待处理的照旧在放映厅里飘；
          你点「删除弹幕」它才真没，点「忽略」就连原话一起留着。同一席位对同一条弹幕只能递一单。
        </p>
        <p v-if="dmReportNotice" class="mb-3 font-mono text-xs" :class="dmReportNotice.kind === 'ok' ? 'text-adminAccent/80' : 'text-rose-300/80'">
          {{ dmReportNotice.text }}
        </p>

        <div v-if="dmReports === null" class="flex items-center gap-3 py-4">
          <p class="text-xs text-white/30">{{ dmReportLoading ? '正在翻举报柜……' : '分站后端没有应答，举报柜暂时打不开' }}</p>
          <button v-if="!dmReportLoading" class="lbs-btn-ghost !px-3 !py-1 !text-[11px]" @click="loadDanmakuReports">再试一次</button>
        </div>
        <p v-else-if="!dmReports.length" class="py-4 text-xs text-white/30">
          {{ dmReportStatus === 'pending' ? '没有待处理的举报单，弹幕区天下太平。' : '一张举报单都没有——分站朋友还没递过单。' }}
        </p>
        <ul v-else class="max-h-[460px] space-y-2 overflow-y-auto pr-1">
          <li
            v-for="item in dmReports"
            :key="item.id"
            class="flex items-start gap-3 rounded-xl border border-white/[0.06] bg-black/25 p-3"
          >
            <div class="min-w-0 flex-1">
              <p class="break-words text-sm text-white/85" :title="item.text">{{ item.text }}</p>
              <p class="mt-1.5 flex flex-wrap items-center gap-x-3 gap-y-0.5 font-mono text-[11px] text-white/45">
                <RouterLink
                  v-if="item.inStation"
                  :to="`/video/${item.bvid}`"
                  class="text-adminAccent/80 transition hover:text-adminAccent hover:underline"
                  :title="`去放映厅看这条弹幕（${item.bvid}）`"
                >
                  {{ item.bvid }}
                </RouterLink>
                <!-- 按服务端算的 inStation 判，不拿 bvid 非空当“在架”：bvid 是落库定格列，片删了照旧非空，
                     拿它当判定会让下面这句「已下架」永远轮不到触发。 -->
                <span
                  v-else
                  class="text-white/25"
                  :title="item.bvid ? `馆藏里已经没有 ${item.bvid}（进了回收站或已销毁）` : '这张举报单没记下 BV 号'"
                >
                  {{ VIDEO_GONE_HINT }}<span v-if="item.bvid" class="ml-1 font-mono">{{ item.bvid }}</span>
                </span>
                <span>第 {{ item.pageIdx ?? 0 }} P</span>
                <span>片内 {{ dmReportClock(item.tSec) }}</span>
                <span>弹幕发于 {{ friendlyTime(item.dmCreatedAtUtc) }}</span>
              </p>
              <p class="mt-1 flex flex-wrap items-center gap-x-3 gap-y-0.5 font-mono text-[11px] text-white/35">
                <span>被举报：{{ item.senderName || (item.senderDmid ? `匿名朋友（凭证 ${String(item.senderDmid).slice(-6)}）` : '无凭证的老弹幕') }}</span>
                <span>举报人：{{ item.reporterName || '未留名' }}</span>
                <span :class="item.reason ? 'text-amber-200/75' : ''">理由：{{ item.reason || '没写' }}</span>
                <span>举报于 {{ friendlyTime(item.createdAtUtc) }}</span>
              </p>
            </div>
            <div class="flex shrink-0 flex-col items-stretch gap-2 self-center">
              <template v-if="item.status === 'pending'">
                <button
                  class="lbs-btn-danger !px-3 !py-1 !text-xs"
                  :disabled="dmReportBusy !== null"
                  title="删掉这条弹幕（行搬进弹幕回收站，站长恢复得回），这张单也结了"
                  @click="handleDanmakuReport(item, 'delete')"
                >
                  {{ dmReportBusy === item.id ? '删除中…' : '删除弹幕' }}
                </button>
                <button
                  class="lbs-btn-ghost !px-3 !py-1 !text-xs"
                  :disabled="dmReportBusy !== null"
                  title="不删，标成已忽略：这条弹幕留着照旧飘"
                  @click="handleDanmakuReport(item, 'dismiss')"
                >
                  {{ dmReportBusy === item.id ? '处理中…' : '忽略' }}
                </button>
              </template>
              <template v-else>
                <span
                  class="whitespace-nowrap text-center font-mono text-[11px]"
                  :class="item.status === 'deleted' ? 'text-rose-300/70' : 'text-white/35'"
                >
                  {{ item.status === 'deleted' ? '已删弹幕' : '已忽略' }}
                  <template v-if="item.handledAtUtc"> · {{ friendlyTime(item.handledAtUtc) }}</template>
                </span>
                <!-- 重开这张单（11-08）：处置只此一次是铁规，反悔得走这枚明路钮，不靠人去改库 -->
                <button
                  class="lbs-btn-ghost !px-3 !py-1 !text-xs"
                  :disabled="dmReportBusy !== null"
                  title="拍回待处理重新处置一次；当初删掉的弹幕从回收站恢复回来"
                  @click="reopenDanmakuReport(item)"
                >
                  {{ dmReportBusy === item.id ? '重开中…' : '重开这张单' }}
                </button>
              </template>
            </div>
          </li>
        </ul>
      </section>

      <!-- 分享链接设置：一口字段一张嘴（11-15 站长点单，11-15 晚站长嫌大改小卡），播放器复制链接 / 海报棚两处 / 周报码文与图链都吃这儿的门面地址。
           不跨列：坐在台账备份（order-59）右侧半列；预览只留放映厅一枚作样，海报与周报同头不再各占一行。 -->
      <section v-if="quietOpen" class="lbs-card order-[60] self-start">
        <div class="mb-3 flex items-center justify-between gap-2">
          <h3 class="lbs-title !mb-0">分享链接设置</h3>
          <div class="flex items-center gap-2">
            <button class="lbs-btn-ghost" :disabled="shareBusy" @click="resetShareBase">恢复默认</button>
            <button class="lbs-btn" :disabled="shareBusy" @click="saveShareBase">{{ shareBusy ? '保存中……' : '保存设置' }}</button>
          </div>
        </div>
        <p class="mb-2 text-xs leading-5 text-white/30">
          分享链接、海报与周报二维码的链接头。留空则跟随访问地址。
        </p>
        <p v-if="shareNotice" class="mb-2 font-mono text-xs" :class="shareNotice.kind === 'ok' ? 'text-adminAccent/80' : 'text-rose-300/80'">{{ shareNotice.text }}</p>
        <input v-model="shareInput" class="lbs-input" placeholder="http://localhost:8090（留空 = 跟随访问地址）" :disabled="shareBusy" />
        <div class="mt-2 flex items-baseline gap-2 text-[11px]" title="海报链接文字与周报二维码同一张头">
          <span class="shrink-0 text-white/35">例</span>
          <span class="break-all font-mono text-white/60">{{ sharePreviewBase }}/video/BV1gEps62EQY?t=83</span>
        </div>
      </section>
      <!-- 下载设置：字段密又一年改不了几回，整块收进「货架」抽屉压底 -->
      <section v-if="quietOpen && eng && engDraft" class="lbs-card md:col-span-2 order-[62]">
        <div class="mb-4 flex items-center justify-between">
          <h3 class="lbs-title !mb-0">
            下载设置
            <button
              type="button"
              class="ml-1 align-baseline text-[11px] font-normal text-white/30 transition hover:text-white/60"
              :title="dlSettingsOpen ? '把这十几个字段折起来，只留标题' : '展开改参数'"
              @click="dlSettingsOpen = !dlSettingsOpen"
            >
              {{ dlSettingsOpen ? '收起' : '展开' }}
            </button>
          </h3>
          <div class="flex items-center gap-2">
            <button
              class="lbs-btn-ghost"
              :disabled="engBusy"
              @click="loadEngine"
            >
              重载
            </button>
            <button
              class="lbs-btn"
              :disabled="engBusy || !dlSettingsOpen"
              :title="dlSettingsOpen ? '' : '先把上面展开，藏着字段时不递保存'"
              @click="saveEngine"
            >
              {{ engBusy ? '保存中……' : '保存设置' }}
            </button>
          </div>
        </div>
        <template v-if="dlSettingsOpen">
        <p class="mb-4 text-xs leading-5 text-white/30">
          这些参数直接写进下载引擎自己的设置：标注「即时」的改动对新下载立即生效，「新提交」的只影响之后投稿的视频，「重启」的要重启分站进程。保存目录、代理、cookie 不在这里：分站锁死 data/videos，不碰代理，B 站号只从成员账号池来（个人空间扫码绑）。
        </p>
        <p
          v-if="engNotice"
          class="mb-3 font-mono text-xs"
          :class="engNotice.kind === 'ok' ? 'text-adminAccent/80' : 'text-rose-300/80'"
        >
          {{ engNotice.text }}
        </p>
        <ul v-if="engWarnings.length" class="mb-3 space-y-1">
          <li v-for="warning in engWarnings" :key="warning" class="font-mono text-xs text-amber-200/80">⚠ {{ warning }}</li>
        </ul>

        <div class="grid grid-cols-1 gap-5 lg:grid-cols-2">
          <!-- 网络 -->
          <div class="rounded border border-white/5 p-4">
            <h4 class="mb-3 text-xs tracking-widest text-white/50">网络 <span class="text-white/25">UA 即时·并发重启</span></h4>
            <label class="mb-1 block text-xs text-white/40">UserAgent（B 站 API 与下载共用；留空回默认）</label>
            <textarea
              v-model="engDraft.network.userAgent"
              rows="2"
              class="mb-3 w-full resize-none rounded border border-adminLine bg-black/30 px-2 py-1.5 font-mono text-xs text-white/80 focus:border-adminAccent/60 focus:outline-none"
            ></textarea>
            <div class="grid grid-cols-2 gap-3">
              <div>
                <label class="mb-1 block text-xs text-white/40">同时下载数（重启后生效）</label>
                <input v-model.number="engDraft.network.maxCurrentDownloads" type="number" min="1" max="16" class="eng-input" />
              </div>
              <div>
                <label class="mb-1 block text-xs text-white/40">每文件线程数 1-32（即时）</label>
                <input v-model.number="engDraft.network.split" type="number" min="1" max="32" class="eng-input" />
              </div>
              <div>
                <label class="mb-1 block text-xs text-white/40">全局限速 KB/s（-1 不限）</label>
                <input v-model.number="engDraft.network.maxOverallDownloadLimit" type="number" min="-1" class="eng-input" />
              </div>
              <div>
                <label class="mb-1 block text-xs text-white/40">单任务限速 KB/s（-1 不限）</label>
                <input v-model.number="engDraft.network.maxDownloadLimit" type="number" min="-1" class="eng-input" />
              </div>
            </div>
          </div>

          <!-- 视频 -->
          <div class="rounded border border-white/5 p-4">
            <h4 class="mb-3 text-xs tracking-widest text-white/50">视频 <span class="text-white/25">新提交生效</span></h4>
            <div class="grid grid-cols-3 gap-3">
              <div>
                <label class="mb-1 block text-xs text-white/40">优先编码</label>
                <select v-model.number="engDraft.video.videoCodecs" class="eng-input">
                  <option v-for="[id, name] in CODEC_OPTIONS" :key="id" :value="id">{{ name }}</option>
                </select>
              </div>
              <div>
                <label class="mb-1 block text-xs text-white/40">画质</label>
                <select v-model.number="engDraft.video.quality" class="eng-input">
                  <option v-for="[id, name] in QUALITY_OPTIONS" :key="id" :value="id">{{ name }}</option>
                </select>
              </div>
              <div>
                <label class="mb-1 block text-xs text-white/40">音质</label>
                <select v-model.number="engDraft.video.audioQuality" class="eng-input">
                  <option v-for="[id, name] in AUDIO_OPTIONS" :key="id" :value="id">{{ name }}</option>
                </select>
              </div>
            </div>
            <div class="mt-3 grid grid-cols-2 gap-3">
              <div>
                <label class="mb-1 block text-xs text-white/40">ffmpeg 硬解</label>
                <select v-model.number="engDraft.video.ffmpegHardwareAcceleration" class="eng-input">
                  <option v-for="[id, name] in HW_OPTIONS" :key="id" :value="id">{{ name }}</option>
                </select>
              </div>
              <div>
                <label class="mb-1 block text-xs text-white/40">ffmpeg 并行 1-4（即时）</label>
                <input v-model.number="engDraft.video.ffmpegMaxParallelJobs" type="number" min="1" max="4" class="eng-input" />
              </div>
            </div>
            <div class="mt-3 flex flex-wrap gap-x-4 gap-y-2 text-xs text-white/60">
              <label class="flex items-center gap-1.5"><input v-model="engDraft.video.content.downloadVideo" type="checkbox" class="accent-adminAccent" />正片</label>
              <label class="flex items-center gap-1.5"><input v-model="engDraft.video.content.downloadAudio" type="checkbox" class="accent-adminAccent" />音频</label>
              <label class="flex items-center gap-1.5"><input v-model="engDraft.video.content.downloadDanmaku" type="checkbox" class="accent-adminAccent" />弹幕</label>
              <label class="flex items-center gap-1.5"><input v-model="engDraft.video.content.downloadSubtitle" type="checkbox" class="accent-adminAccent" />字幕</label>
              <label class="flex items-center gap-1.5"><input v-model="engDraft.video.content.downloadCover" type="checkbox" class="accent-adminAccent" />封面</label>
              <label class="flex items-center gap-1.5"><input v-model="engDraft.video.content.generateMovieMetadata" type="checkbox" class="accent-adminAccent" />NFO 元数据</label>
              <label class="flex items-center gap-1.5"><input v-model="engDraft.video.isTranscodingAacToMp3" type="checkbox" class="accent-adminAccent" />AAC 转 MP3</label>
            </div>
            <div class="mt-3 grid grid-cols-2 gap-3">
              <!-- 字幕恒抓全轨（11-11 站长拍板）：这两枚偏好不再下传引擎（它是把多语 AI 轨筛剩一条的闸），
                   停用不拆：值留在草稿里不丢，要恢复筛选改回 StationEngineSettings 那两行即可。 -->
              <div>
                <label class="mb-1 block text-xs text-white/40">字幕语言偏好（已停用：字幕恒抓全轨）</label>
                <select v-model.number="engDraft.video.content.preferredSubtitleLanguage" class="eng-input opacity-50" disabled>
                  <option v-for="[id, name] in LANG_OPTIONS" :key="id" :value="id">{{ name }}</option>
                </select>
              </div>
              <div>
                <label class="mb-1 block text-xs text-white/40">字幕类型偏好（已停用：同上）</label>
                <select v-model.number="engDraft.video.content.preferredSubtitleType" class="eng-input opacity-50" disabled>
                  <option v-for="[id, name] in TYPE_OPTIONS" :key="id" :value="id">{{ name }}</option>
                </select>
              </div>
            </div>
          </div>

          <!-- 弹幕 -->
          <div class="rounded border border-white/5 p-4">
            <h4 class="mb-3 text-xs tracking-widest text-white/50">弹幕 <span class="text-white/25">格式新提交·字号行数观看页即时</span></h4>
            <div class="grid grid-cols-2 gap-3">
              <div>
                <label class="mb-1 block text-xs text-white/40">输出格式</label>
                <select v-model.number="engDraft.danmaku.outputFormat" class="eng-input">
                  <option v-for="[id, name] in OUTPUT_OPTIONS" :key="id" :value="id">{{ name }}</option>
                </select>
              </div>
              <div>
                <label class="mb-1 block text-xs text-white/40">排版算法</label>
                <select v-model.number="engDraft.danmaku.layoutAlgorithm" class="eng-input">
                  <option v-for="[id, name] in LAYOUT_OPTIONS" :key="id" :value="id">{{ name }}</option>
                </select>
              </div>
              <div>
                <label class="mb-1 block text-xs text-white/40">字号（观看页同步）</label>
                <input v-model.number="engDraft.danmaku.fontSize" type="number" min="1" max="500" class="eng-input" />
              </div>
              <div>
                <label class="mb-1 block text-xs text-white/40">限行数 0=不限（观看页同步）</label>
                <input v-model.number="engDraft.danmaku.lineCount" type="number" min="0" class="eng-input" />
              </div>
              <div>
                <label class="mb-1 block text-xs text-white/40">画布宽</label>
                <input v-model.number="engDraft.danmaku.screenWidth" type="number" min="1" class="eng-input" />
              </div>
              <div>
                <label class="mb-1 block text-xs text-white/40">画布高</label>
                <input v-model.number="engDraft.danmaku.screenHeight" type="number" min="1" class="eng-input" />
              </div>
            </div>
            <label class="mb-1 mt-3 block text-xs text-white/40">字体名</label>
            <input v-model="engDraft.danmaku.fontName" type="text" class="eng-input" />
            <div class="mt-3 flex flex-wrap gap-x-4 gap-y-2 text-xs text-white/60">
              <label class="flex items-center gap-1.5"><input v-model="engDraft.danmaku.topFilter" type="checkbox" class="accent-adminAccent" />屏蔽顶部</label>
              <label class="flex items-center gap-1.5"><input v-model="engDraft.danmaku.bottomFilter" type="checkbox" class="accent-adminAccent" />屏蔽底部</label>
              <label class="flex items-center gap-1.5"><input v-model="engDraft.danmaku.scrollFilter" type="checkbox" class="accent-adminAccent" />屏蔽滚动</label>
              <label class="flex items-center gap-1.5"><input v-model="engDraft.danmaku.removeEmojiAndSpecialCharacters" type="checkbox" class="accent-adminAccent" />清 Emoji</label>
            </div>
            <div class="mt-3 grid grid-cols-2 gap-3">
              <div>
                <label class="mb-1 block text-xs text-white/40">关键词黑名单（一行一条）</label>
                <textarea v-model="keywordDraft" rows="3" class="eng-input resize-none"></textarea>
              </div>
              <div>
                <label class="mb-1 block text-xs text-white/40">B站 UID 黑名单（一行一个）</label>
                <textarea v-model="uidDraft" rows="3" class="eng-input resize-none"></textarea>
              </div>
            </div>
          </div>

          <!-- 命名 -->
          <div class="rounded border border-white/5 p-4">
            <h4 class="mb-3 text-xs tracking-widest text-white/50">命名 <span class="text-white/25">新提交生效</span></h4>
            <p class="mb-2 text-xs text-white/40">文件命名字段（从上到下拼接；没带序号/cid/bvid 时分站自动补 cid 防多 P 互撞）</p>
            <ul class="mb-2 space-y-1">
              <li v-for="(part, index) in engDraft.video.fileNameParts" :key="`${part}-${index}`" class="flex items-center gap-2 text-xs">
                <span class="w-5 text-right text-white/25">{{ index + 1 }}</span>
                <span class="flex-1 rounded bg-black/30 px-2 py-1 font-mono text-white/70">{{ TOKEN_LABELS[part] ?? part }}</span>
                <button class="text-white/30 transition hover:text-white" :disabled="index === 0" @click="movePart(index, -1)">↑</button>
                <button class="text-white/30 transition hover:text-white" :disabled="index === engDraft.video.fileNameParts.length - 1" @click="movePart(index, 1)">↓</button>
                <button class="text-rose-300/60 transition hover:text-rose-300" @click="removePart(index)">✕</button>
              </li>
            </ul>
            <select class="eng-input mb-3" value="" @change="addPart">
              <option value="">＋ 添加字段……</option>
              <option v-for="token in TOKEN_ADDABLE" :key="token" :value="token">{{ TOKEN_LABELS[token] }}</option>
            </select>
            <div class="grid grid-cols-2 gap-3">
              <div>
                <label class="mb-1 block text-xs text-white/40">日期格式</label>
                <input v-model="engDraft.video.fileNamePartTimeFormat" type="text" class="eng-input" />
              </div>
              <div>
                <label class="mb-1 block text-xs text-white/40">序号样式</label>
                <select v-model.number="engDraft.video.orderFormat" class="eng-input">
                  <option v-for="[id, name] in ORDER_OPTIONS" :key="id" :value="id">{{ name }}</option>
                </select>
              </div>
            </div>
            <label class="mt-3 flex items-center gap-1.5 text-xs text-white/60">
              <input v-model="engDraft.basic.repeatFileAutoAddNumberSuffix" type="checkbox" class="accent-adminAccent" />
              撞名时自动加 (1) 后缀
            </label>
          </div>
        </div>
        </template>
        <p v-else class="text-xs leading-5 text-white/30">
          这十几个参数设好就不碰（并发、重试、限速、分轨码率、日志那些），要改按标题旁的「展开」。
        </p>
      </section>

      <section v-if="quietOpen && backupState" class="lbs-card order-[59]">
        <div class="mb-4 flex items-center justify-between gap-2">
          <h3 class="lbs-title !mb-0">
            台账备份
            <span class="ml-1 rounded-full border border-adminAccent/30 px-2 py-0.5 text-[10px] normal-case tracking-normal text-adminAccent/70">station.db 在线快照</span>
          </h3>
          <div class="flex shrink-0 items-center gap-2">
            <select v-model.number="backupState.intervalHours" class="eng-input w-auto cursor-pointer !py-1 !text-[11px]" @change="backupConfig">
              <option :value="6">每 6 小时</option>
              <option :value="24">每 24 小时</option>
              <option :value="72">每 3 天</option>
              <option :value="168">每 7 天</option>
            </select>
            <select v-model.number="backupState.keep" class="eng-input w-auto cursor-pointer !py-1 !text-[11px]" @change="backupConfig">
              <option :value="7">保 7 份</option>
              <option :value="14">保 14 份</option>
              <option :value="30">保 30 份</option>
            </select>
            <button class="lbs-btn-ghost !px-3 !py-1 !text-[11px]" :disabled="backupBusy" @click="backupNow">立即拍一张</button>
          </div>
        </div>
        <p class="mb-3 text-xs leading-5 text-white/30">
          周期工自动把台账快照进 data/backups/（SQLite 在线备份，写盘中也能拍）。想备份整座分站就拷数据目录；恢复：停站 → 用快照覆盖 station.db 并删掉 -wal/-shm → 起站。
          上次落柜：{{ fmtBackupTime(backupState.lastBackupUtc) }}
        </p>
        <ul v-if="backupState?.files?.length" class="max-h-[220px] space-y-1.5 overflow-y-auto pr-1">
          <li v-for="f in backupState.files" :key="f.name" class="flex items-center gap-2 font-mono text-xs text-white/60">
            <span class="min-w-0 flex-1 truncate">{{ f.name }}</span>
            <span class="shrink-0 text-white/30">{{ (f.bytes / 1024).toFixed(0) }} KB</span>
            <span class="shrink-0 text-white/30">{{ fmtBackupTime(f.modifiedUtc) }}</span>
            <button
              class="shrink-0 rounded-md p-1 text-white/35 transition hover:bg-rose-500/20 hover:text-rose-300"
              title="销掉这份快照"
              :disabled="backupBusy"
              @click="backupDelete(f.name)"
            >
              <svg viewBox="0 0 24 24" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="2">
                <path d="M6 6l12 12M18 6L6 18" stroke-linecap="round" />
              </svg>
            </button>
            <!-- 下载钮排在销钮右边（站长 11-03「在删除符号的右边加一个下载」）：销一份要过确认框，下载是无副作用的读，不拦。 -->
            <button
              class="shrink-0 rounded-md p-1 text-white/35 transition hover:bg-adminAccent/20 hover:text-adminAccent"
              title="下载这份快照"
              @click="backupDownload(f.name)"
            >
              <svg viewBox="0 0 24 24" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="2">
                <path d="M12 4v10m0 0l-3.5-3.5M12 14l3.5-3.5M5 19h14" stroke-linecap="round" stroke-linejoin="round" />
              </svg>
            </button>
          </li>
        </ul>
        <p v-else class="text-xs text-white/25">档案柜还空着，点「立即拍一张」开张。</p>
        <p v-if="backupText" class="mt-2 text-xs text-adminAccent/80">{{ backupText }}</p>
      </section>

      <section v-if="quietOpen" class="lbs-card md:col-span-2 order-[61]">
        <div class="mb-4 flex items-center justify-between gap-2">
          <h3 class="lbs-title !mb-0">
            数据交换
            <span class="ml-1 rounded-full border border-adminAccent/30 px-2 py-0.5 text-[10px] normal-case tracking-normal text-adminAccent/70">导出打包 · 导入换库</span>
          </h3>
          <p class="text-[11px] text-white/30">档案柜 data/exports/</p>
        </div>

        <!-- 状态条：导入换库待重启 / 上次换库失败 -->
        <p v-if="importState?.pending" class="mb-3 rounded-lg border border-amber-400/40 bg-amber-400/10 px-3 py-2 text-xs leading-5 text-amber-200">
          换库单已落定，分站正等着重启换心——请重新启动服务端进程，起来后本卡自动对账。
        </p>
        <p v-else-if="importState?.lastError" class="mb-3 rounded-lg border border-rose-500/40 bg-rose-500/10 px-3 py-2 text-xs leading-5 text-rose-200">
          上次导入换库没成：{{ importState.lastError }}
        </p>

        <div class="grid grid-cols-1 gap-5 lg:grid-cols-2">
          <!-- 导出 -->
          <div class="rounded border border-white/5 p-4">
            <h4 class="mb-3 flex items-center gap-2 text-xs tracking-widest text-white/50">
              导出打包 <span class="text-white/25">必含 station.db + manifest</span>
              <span class="ml-auto font-mono text-white/25">台账 {{ fmtBytes(expStats?.dbBytes) }}</span>
            </h4>
            <label class="mb-1.5 flex items-center gap-2 text-xs text-white/65">
              <input v-model="expPick.withMedia" type="checkbox" class="accent-adminAccent" />
              视频媒体（data/videos 全量）
              <span class="ml-auto font-mono text-white/25">{{ fmtBytes(expStats?.videos) }}</span>
            </label>
            <label class="mb-1.5 flex items-center gap-2 text-xs text-white/65">
              <input v-model="expPick.withCmtImg" type="checkbox" class="accent-adminAccent" />
              评论图
              <span class="ml-auto font-mono text-white/25">{{ fmtBytes(expStats?.cmtImg) }}</span>
            </label>
            <label class="mb-3 flex items-center gap-2 text-xs text-white/65">
              <input v-model="expPick.withCaches" type="checkbox" class="accent-adminAccent" />
              头像表情缓存
              <span class="ml-auto font-mono text-white/25">{{ fmtBytes(cachesBytes) }}</span>
            </label>
            <div>
              <button class="lbs-btn" :disabled="expBusy" @click="startExport">
                {{ expBusy ? '打包中……' : '打包邮走' }}
              </button>
              <p v-if="expText" class="mt-2 text-xs text-adminAccent/80">{{ expText }}</p>
            </div>

            <div class="mt-4 border-t border-white/5 pt-3">
              <p class="mb-2 text-[11px] uppercase tracking-widest text-white/35">档案柜</p>
              <div v-if="!exportList.length" class="py-3 text-xs text-white/25">还没有包，勾几个框出第一单。</div>
              <ul v-else class="max-h-[220px] space-y-1.5 overflow-y-auto pr-1">
                <li v-for="e in exportList" :key="e.name" class="flex items-center gap-2 font-mono text-xs text-white/60">
                  <span class="min-w-0 flex-1 truncate" :title="e.name">{{ e.name }}</span>
                  <span v-if="e.options?.withCreds" class="shrink-0 rounded-full border border-amber-400/50 px-1.5 text-[10px] text-amber-300">凭证</span>
                  <span v-if="e.options?.withMedia" class="shrink-0 rounded-full border border-stationPink/60 px-1.5 text-[10px] text-stationPink">媒体</span>
                  <span v-if="e.options?.withCmtImg" class="shrink-0 rounded-full border border-adminAccent/50 px-1.5 text-[10px] text-adminAccent">评论图</span>
                  <span v-if="e.options?.withCaches" class="shrink-0 rounded-full border border-white/25 px-1.5 text-[10px] text-white/50">缓存</span>
                  <span class="shrink-0 text-white/30">{{ fmtBytes(e.bytes) }}</span>
                  <span class="shrink-0 text-white/30">{{ fmtBackupTime(e.createdAtUtc) }}</span>
                  <a
                    class="shrink-0 rounded-md border border-white/10 px-2 py-0.5 text-[11px] text-white/50 transition hover:border-adminAccent/50 hover:text-adminAccent"
                    :href="`/api/admin/exports/${encodeURIComponent(e.name)}/download`"
                  >下</a>
                  <button
                    :class="armedExport === e.name ? 'lbs-btn-danger-armed !px-2 !py-0.5 !text-[11px]' : 'lbs-btn-danger !px-2 !py-0.5 !text-[11px]'"
                    :disabled="expBusy"
                    @click="deleteExport(e)"
                  >
                    {{ armedExport === e.name ? '确认销' : '销' }}
                  </button>
                </li>
              </ul>
            </div>
          </div>

          <!-- 导入 -->
          <div class="rounded border border-white/5 p-4">
            <h4 class="mb-3 text-xs tracking-widest text-white/50">导入换库 <span class="text-white/25">上传后重启生效</span></h4>
            <label class="flex cursor-pointer flex-col items-center justify-center gap-1.5 rounded-lg border border-dashed border-white/15 bg-black/20 py-6 transition hover:border-adminAccent/50">
              <span class="text-xs text-white/55">{{ importBusy ? '收包处理中……' : '点我选 lbs-export-*.zip 导出包' }}</span>
              <span class="text-[10px] text-white/25">须是本卡出的包：station.db + manifest.json 缺一不可，坏包整包拒收</span>
              <input type="file" accept=".zip" class="hidden" :disabled="importBusy" @change="onImportFile" />
            </label>

            <div v-if="importPreview" class="mt-3 rounded-lg border border-white/10 bg-black/25 px-3 py-2.5">
              <p class="text-xs text-white/80">
                分站名 <b class="font-mono">{{ importPreview.stationName ?? '?' }}</b>
                · 出于 {{ fmtBackupTime(importPreview.exportedAtUtc) }}
              </p>
              <p class="mt-1 font-mono text-[11px] text-white/40">
                {{ importPreview.counts ? `${importPreview.counts.videos} 视频 · ${importPreview.counts.members} 成员 · ${importPreview.counts.events} 事件` : '台账数目不详' }}
                <template v-if="importPreview.options">
                  · {{ importPreview.options.withMedia ? '带媒体' : '无媒体' }}
                  · {{ importPreview.options.withCreds ? '带登录态' : '无登录态' }}
                </template>
              </p>
            </div>
            <ul v-if="importWarnings.length" class="mt-2 space-y-1">
              <li v-for="w in importWarnings" :key="w" class="rounded border border-amber-400/40 bg-amber-400/10 px-2 py-1 text-[11px] text-amber-200">⚠ {{ w }}</li>
            </ul>
            <p v-if="importText" class="mt-2 text-xs text-rose-300/85">{{ importText }}</p>

            <div class="mt-3 flex flex-wrap items-center gap-2">
              <button
                :class="applyArmed ? 'lbs-btn-danger-armed' : 'lbs-btn'"
                :disabled="importBusy || !importReceipt || restarting"
                @click="applyImport"
              >
                {{ restarting ? '换库中，等分站回魂……' : applyArmed ? '确定丢下现站跑？先自救快照一张 — 再点执行' : '应用并重启' }}
              </button>
              <span v-if="applyArmed && !restarting" class="text-[11px] text-rose-300/60">6 秒内不确认自动作废</span>
            </div>
            <p v-if="restarting" class="mt-2 text-xs leading-5 text-amber-200">
              分站正在自我停机换心——请重新启动服务端进程，起来后本卡自动对账换库结果。
            </p>
            <p class="mt-3 text-[10px] leading-4 text-white/25">
              换库前保底：自动给现站自救快照（data/backups/），现台账与登录态移入 data/pre-import/ 留底，缺件视频开机后自动回炉重下。
            </p>
          </div>
        </div>
      </section>

      <!-- 回收站：删档后的恢复口，挨着危险区放——破坏性操作集中在一处，翻柜子时顺手就能确认 -->
      <!-- 回收站这一片 11-09 从「货架」抽屉搬到碎纸机上面（站长点名：三座回收站该跟碎纸机放一处，方便找），
           同一夜又加了盖子：页尾不该常驻三张卡，折起只留一条胶囊，顶上导航也有「回收站」可跳。
           三张卡仍挂在 DOM 里（v-show 而非 v-if）：拉数不停，顶上那枚徽章才是真数，不是展开时才现编。 -->
      <button
        v-if="!recycleOpen"
        id="grp-recycle"
        type="button"
        class="order-[65] md:col-span-2 flex items-center gap-2.5 rounded-2xl border border-white/10 bg-white/[0.02] px-5 py-3 text-left text-xs text-white/45 transition hover:border-white/25 hover:text-white/70"
        @click="gotoGroup('recycle')"
      >
        <span class="h-3 w-[3px] rounded-full bg-white/25"></span>
        回收站 · 稿件 / 弹幕 / 加档
        <span class="ml-1 font-mono text-[11px] text-white/25">{{ recycleBadge > 0 ? `躺着 ${recycleBadge} 件` : '空仓' }}</span>
        <span class="ml-auto font-mono text-[11px] text-white/25">点开展开</span>
      </button>
      <div v-else id="grp-recycle" class="order-[65] md:col-span-2 flex items-baseline gap-2.5 pt-3">
        <h2 class="lbs-title !mb-0">回收站</h2>
        <span class="ml-2 font-mono text-[11px] text-white/22">稿件 · 弹幕 · 加档 —— 删掉的东西先落在这儿，能恢复也能销毁</span>
        <button type="button" class="ml-2 text-[11px] text-white/30 transition hover:text-white/60" @click="recycleOpen = false">盖回去</button>
      </div>
      <RecycleBinCard v-show="recycleOpen" class="order-[66]" @count="recycleBinCount = $event" />

      <!-- 货架：悬停预览、下载设置、成员席位、全站公告、备份与搬家这些设好就不碰的，整批收在这儿；下载设置压底 -->
      <button
        v-if="!quietOpen"
        id="grp-quiet"
        type="button"
        class="order-[55] md:col-span-2 flex items-center gap-2.5 rounded-2xl border border-white/8 bg-white/[0.015] px-5 py-3 text-left text-xs text-white/45 transition hover:border-adminAccent/35 hover:text-white/75"
        @click="gotoGroup('quiet')"
      >
        <span class="h-3 w-[3px] rounded-full bg-white/20"></span>
        货架
        <span class="ml-2 font-mono text-[11px] text-white/22">预览 · 下载设置 · 成员席位 · 全站公告 · 看板娘性格卡 · 备份与搬家 · 设好就不碰</span>
        <span class="ml-auto font-mono text-[11px] text-white/25">点开取出</span>
      </button>
      <div v-else id="grp-quiet" class="order-[55] md:col-span-2 flex items-baseline gap-2.5 pt-3">
        <h2 class="text-[11px] font-medium uppercase tracking-[0.18em] text-white/40">货架</h2>
        <span class="font-mono text-[11px] text-white/20">盖子已开 · 九张卡都在下面</span>
        <span class="ml-1 h-px flex-1 bg-white/5"></span>
        <button type="button" class="text-[11px] text-white/30 transition hover:text-white/60" @click="quietOpen = false">盖回去</button>
      </div>

      <button
        v-if="!dangerOpen"
        id="grp-danger"
        type="button"
        class="order-[70] md:col-span-2 flex items-center gap-2.5 rounded-2xl border border-rose-400/20 bg-rose-400/[0.04] px-5 py-3 text-left text-xs text-rose-300/60 transition hover:border-rose-400/40 hover:text-rose-200"
        @click="gotoGroup('danger')"
      >
        <span class="h-3 w-[3px] rounded-full bg-rose-400/60"></span>
        危险区 · 一键清空分站
        <span class="ml-auto font-mono text-[11px] text-white/25">点开展开</span>
      </button>
      <section v-else id="grp-danger" class="lbs-card-danger md:col-span-2 order-[70]">
        <div class="mb-1 flex items-center justify-between gap-2">
          <h3 class="lbs-title-danger">危险区 · 一键清空分站</h3>
          <button type="button" class="text-[11px] text-white/30 transition hover:text-white/60" @click="dangerOpen = false">收起</button>
        </div>
        <p class="mb-3 text-xs leading-5 text-white/30">
          把整座站拍回空仓：视频库（视频/弹幕/评论/封面）、头像表情评论图缓存、台账全部销账，引擎任务记录也一并清掉。成员席位与各自的 B 站账号池、下载设置全部保留。不可撤销。
        </p>
        <p v-if="purgeNotice" class="mb-3 font-mono text-xs" :class="purgeNotice.kind === 'ok' ? 'text-adminAccent/80' : 'text-rose-300/80'">
          {{ purgeNotice.text }}
        </p>
        <button
          :class="purgeArmed ? 'lbs-btn-danger-armed' : 'lbs-btn-danger'"
          :disabled="purgeBusy"
          @click="purgeStation"
        >
          {{ purgeBusy ? '清站中……' : purgeArmed ? '确定要清空！再点一次执行' : '一键清空分站' }}
        </button>
        <span v-if="purgeArmed && !purgeBusy" class="ml-2 text-xs text-rose-300/50">5 秒内不确认则自动作废</span>
      </section>
    </main>

    <!-- admit 实例：身份门后的「用站长账号扫码进站」窗 -->
    <LoginDialog />
  </div>
</template>

<style scoped>
/* 悬停预览卡专用小件：呼吸点与开关。只在这张卡上用，不挤全站装修件。 */
.pv-dot {
  display: inline-block;
  height: 7px;
  width: 7px;
  flex: none;
  border-radius: 9999px;
  background: currentColor;
}
.pv-dot-live {
  animation: pv-breathe 1.8s ease-out infinite;
}
@keyframes pv-breathe {
  0% {
    box-shadow: 0 0 0 0 currentColor;
  }
  70% {
    box-shadow: 0 0 0 6px rgba(0, 0, 0, 0);
  }
  100% {
    box-shadow: 0 0 0 0 rgba(0, 0, 0, 0);
  }
}
.pv-switch {
  position: relative;
  flex: none;
  height: 18px;
  width: 34px;
  border-radius: 9999px;
  background: rgba(255, 255, 255, 0.12);
  transition: background 0.18s ease;
}
.pv-switch::after {
  content: '';
  position: absolute;
  top: 2px;
  left: 2px;
  height: 14px;
  width: 14px;
  border-radius: 9999px;
  background: rgba(255, 255, 255, 0.55);
  transition: transform 0.18s ease, background 0.18s ease;
}
.pv-switch[aria-checked='true'] {
  background: rgba(34, 211, 238, 0.32);
}
.pv-switch[aria-checked='true']::after {
  transform: translateX(16px);
  background: #22d3ee;
}
.pv-switch:disabled {
  cursor: not-allowed;
  opacity: 0.4;
}
</style>
