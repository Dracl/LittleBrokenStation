<script setup>
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { coverThumb } from '../utils/cover'
import LoginDialog from '../components/LoginDialog.vue'
import CrownBadge from '../components/CrownBadge.vue'
import RecycleBinCard from '../components/RecycleBinCard.vue'
import { member, loginDialogOpen, loginDialogMode } from '../memberStore'
import { friendlyTime } from '../utils/time'
import { defaultAvatar, onAvatarError } from '../utils/avatar.js'

const health = ref(null)
const settings = ref(null)
const loginState = ref(null)
const cookieDraft = ref('')
const loginBusy = ref(false)
const loginNotice = ref(null) // { kind, text }
const altState = ref(null) // { isAlt, name, mid, vipStatus }
const altDraft = ref('')
const altBusy = ref(false)
const altNotice = ref(null) // { kind, text }
const collectMode = ref('mixed')
const collectBusy = ref(false)
const purgeArmed = ref(false)
const purgeBusy = ref(false)
const purgeNotice = ref(null) // { kind, text }
let purgeTimer = null

function notify(kind, text) {
  loginNotice.value = { kind, text }
}

const notice = ref(null)

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

async function loadLoginState() {
  try {
    loginState.value = await fetch('/api/login/status').then((r) => r.json())
  } catch {
    loginState.value = null
  }
}

async function saveCookie() {
  const cookie = cookieDraft.value.trim()
  if (!cookie || loginBusy.value) return
  loginBusy.value = true
  try {
    const res = await fetch('/api/login/cookie', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ cookie }),
    })
    const data = await res.json().catch(() => null)
    if (res.ok) {
      cookieDraft.value = ''
      notify('ok', data?.message ?? '登录态已更新')
    } else {
      notify('err', data?.message ?? '登录失败')
    }
  } catch {
    notify('err', '分站后端没有应答')
  } finally {
    loginBusy.value = false
    loadLoginState()
  }
}

async function logout() {
  if (loginBusy.value) return
  loginBusy.value = true
  try {
    const res = await fetch('/api/login/logout', { method: 'POST' })
    const data = await res.json().catch(() => null)
    notify(res.ok ? 'ok' : 'err', data?.message ?? (res.ok ? '已离站' : '登出失败'))
  } catch {
    notify('err', '分站后端没有应答')
  } finally {
    loginBusy.value = false
    loadLoginState()
  }
}

async function loadAlt() {
  try {
    altState.value = await fetch('/api/login/alt').then((r) => r.json())
  } catch {
    altState.value = null
  }
}

async function loadCollectMode() {
  try {
    const data = await fetch('/api/settings/collect').then((r) => r.json())
    collectMode.value = data?.mode ?? 'mixed'
  } catch {
    collectMode.value = 'mixed'
  }
}

async function saveCollectMode() {
  collectBusy.value = true
  try {
    const res = await fetch('/api/settings/collect', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ mode: collectMode.value }),
    })
    if (!res.ok) {
      const data = await res.json().catch(() => null)
      altNotice.value = { kind: 'err', text: data?.message ?? '采集偏好保存失败' }
    }
  } catch {
    altNotice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    collectBusy.value = false
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

async function saveAlt() {
  const cookie = altDraft.value.trim()
  if (!cookie || altBusy.value) return
  altBusy.value = true
  try {
    const res = await fetch('/api/login/alt', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ cookie }),
    })
    const data = await res.json().catch(() => null)
    if (res.ok) {
      altDraft.value = ''
      altNotice.value = { kind: 'ok', text: data?.message ?? '副号已就位' }
    } else {
      altNotice.value = { kind: 'err', text: data?.message ?? '副号保存失败' }
    }
  } catch {
    altNotice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    altBusy.value = false
    loadAlt()
  }
}

async function removeAlt() {
  if (altBusy.value) return
  altBusy.value = true
  try {
    const res = await fetch('/api/login/alt', { method: 'DELETE' })
    const data = await res.json().catch(() => null)
    altNotice.value = { kind: res.ok ? 'ok' : 'err', text: data?.message ?? (res.ok ? '副号已移除' : '移除失败') }
  } catch {
    altNotice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    altBusy.value = false
    loadAlt()
  }
}

const stats = ref(null) // 页头总览：/api/admin/stats

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
    if (res.ok) {
      pvDraftDirty = false
      await loadPreviewStats()
    }
  } catch {
    pvSay('err', '分站后端没有应答')
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
const dlQueue = ref(null) // { paused, intervalSec, downloading:[{id,bvid,title,coverUrl}], pending:[...] }
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
  if (!window.confirm(`确定取消排队区全部 ${n} 件？取消后还能从首页卡阵重新排队。`)) return
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
const adminAuth = ref('loading') // loading | ok | needOwner | member

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

async function memberAction(action, item) {
  if (membersBusy.value) return
  const verbs = { ban: '封禁', unban: '解封', delete: '撤席位' }
  const hint =
    action === 'delete'
      ? `确定撤掉「${seatLabel(item)}」的席位？历史事件保留，想回来得重新领邀请码。`
      : `确定${verbs[action]}「${seatLabel(item)}」？`
  if (!window.confirm(hint)) return
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
  if (!window.confirm(`确定从「${seatLabel(item)}」的席位上解绑「${who}」？`)) return
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
  const res = await fetch('/api/backups/config', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ intervalHours: backupState.value.intervalHours, keep: backupState.value.keep }),
  })
  const body = await res.json().catch(() => null)
  backupText.value = body?.message ?? '备份安排已更新'
}

async function backupDelete(name) {
  if (backupBusy.value) return
  if (!window.confirm(`确定销掉快照「${name}」？`)) return
  const res = await fetch(`/api/backups/${name}`, { method: 'DELETE' })
  const body = await res.json().catch(() => null)
  backupText.value = body?.message ?? '已销'
  await loadBackups()
}

function fmtBackupTime(iso) {
  return iso ? new Date(iso).toLocaleString('zh-CN', { hour12: false }) : '—'
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
    const res = await fetch('/api/dynamics/targets')
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
const expPick = ref({ withCreds: false, withMedia: false, withCmtImg: false, withCaches: false })
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
function pollExportJob(jobId) {
  expTimer = setTimeout(async () => {
    try {
      const res = await fetch(`/api/admin/export/jobs/${jobId}`)
      const d = await res.json().catch(() => null)
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
    /* 后端打盹：照常进页面，各卡自己空着 */
    adminAuth.value = 'ok'
  }
  fetch('/api/health')
    .then((r) => r.json())
    .then((h) => (health.value = h))
    .catch(() => {})
  fetch('/api/settings')
    .then((r) => r.json())
    .then((s) => (settings.value = s))
    .catch(() => {})
  loadSubscriptions()
  loadCrawlStatus()
  loadLoginState()
  loadAlt()
  loadCollectMode()
  loadEngine()
  loadDlQueue()
  loadBackups()
  loadMembers()
  loadExportStats()
  loadExports()
  loadImportState()
  loadDynTargets()
  loadPreviewStats()
  if (!dlTimer) dlTimer = setInterval(loadDlQueue, 5000)
  if (!pvTimer) pvTimer = setInterval(pvBeat, 1000)
}

// ─── 站长室排版：六组 + 顶部定位条（正文仍一屏到底，不切标签）───
// 十六张卡是同一个 grid 的孩子，分组靠 order 排座、靠 md:col-span-2 定整行还是半格：
// 这样分组只动 class，不必把卡块在文件里搬来搬去（搬一次就多一次丢代码的机会）。
const ADMIN_GROUPS = [
  // 组名走仓库比喻（柜台/进货口/观众席……）：胶囊是拿来跳位的，有点火气反而好记；
  // 但卡题里的「危险区」「一键清空分站」保持大白话，破坏性操作那一步不玩括令。
  { id: 'overview', title: '柜台', hint: '家底与健康度，扫一眼', order: 10 },
  { id: 'harvest', title: '进货口', hint: '下载 · 订阅 · 动态关注', order: 20 },
  { id: 'people', title: '观众席', hint: '成员席位与全站公告', order: 30 },
  { id: 'accounts', title: '钥匙串', hint: '站长登录与采集号', order: 40 },
  { id: 'data', title: '库房', hint: '备份 · 搬家 · 捐回', order: 50 },
]
const activeGroup = ref('overview')
const dangerOpen = ref(false) // 危险区默认收起：常年在底下亮着红边，看久了就当背景板，误点机会少一层
const dlWaiting = computed(() => (dlQueue.value?.pending?.length ?? 0) + (dlQueue.value?.downloading?.length ?? 0))
const adminNav = computed(() => [
  { ...ADMIN_GROUPS[0], n: (pv.value?.pending ?? 0) > 0 ? pv.value.pending : null, tone: 'warn' },
  { ...ADMIN_GROUPS[1], n: dlWaiting.value > 0 ? dlWaiting.value : null, tone: 'hot' },
  { ...ADMIN_GROUPS[2], n: null, tone: '' },
  { ...ADMIN_GROUPS[3], n: null, tone: '' },
  { ...ADMIN_GROUPS[4], n: null, tone: '' },
])

async function gotoGroup(id) {
  if (id === 'danger') {
    dangerOpen.value = true // 先从收起来变回展开，否则下面找不到可滚的锚
  }
  await nextTick()
  document.getElementById(`grp-${id}`)?.scrollIntoView({ behavior: 'smooth', block: 'start' })
  activeGroup.value = id
}

// 定位条的高亮：找最后一条已经滚到条子上沿之上的组头（rAF 节流，不每像素都算一遗）。
let groupSpyRaf = 0
function onGroupSpyScroll() {
  if (groupSpyRaf) return
  groupSpyRaf = requestAnimationFrame(() => {
    groupSpyRaf = 0
    let current = 'overview'
    for (const g of [...ADMIN_GROUPS.map((x) => x.id), 'danger']) {
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
  window.removeEventListener('scroll', onGroupSpyScroll)
  if (groupSpyRaf) cancelAnimationFrame(groupSpyRaf)
  if (dlTimer) clearInterval(dlTimer)
  if (pvTimer) clearInterval(pvTimer)
  clearTimeout(pvClearTimer)
  if (pvSaveTimer) savePvSettings() // 帘子收了还有没交出去的参数：离场前补发一次，不吞站长的改动
  clearTimeout(expTimer)
  clearTimeout(exportArmTimer)
  clearTimeout(pendingTimer)
})
</script>

<template>
  <div class="min-h-screen bg-adminBg text-white">
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
        <p v-else class="py-4 text-xs text-white/30">正在核对手牌……</p>
      </div>
    </div>

    <header v-if="adminAuth === 'ok'" class="border-b border-adminLine bg-gradient-to-b from-white/[0.02] to-transparent">
      <div class="mx-auto flex h-14 max-w-[1100px] items-center justify-between px-6">
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
            <span :class="stats?.failed ? 'text-rose-300/80' : 'text-white/35'">失败 {{ stats?.failed ?? '—' }}</span>
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
          <div class="lbs-stat-lg"><b>{{ stats ? stats.altOwned : '—' }}</b><span>副号定档</span></div>
        </div>
      </section>

      <!-- 悬停预览：首页卡片鼠标停一下就起播的那批 360p 无声小片段，本卡是它的全套仪表盘（状态·覆盖率·参数·流水） -->
      <section class="lbs-card md:col-span-2 order-[12] border-l-2 border-l-adminAccent/50">
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

      <section class="lbs-card md:col-span-2 order-[31]">
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
                    :class="m.status === 'active' ? 'border-emerald-400/40 text-emerald-300' : m.status === 'invited' ? 'border-amber-400/40 text-amber-300' : 'border-rose-400/40 text-rose-300'"
                  >
                    {{ m.status === 'active' ? '启用' : m.status === 'invited' ? '待邀' : '封禁' }}
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
                :disabled="membersBusy"
                @click="memberAction(m.status === 'banned' ? 'unban' : 'ban', m)"
              >
                {{ m.status === 'banned' ? '解封' : '封禁' }}
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
      </section>

      <section class="lbs-card md:col-span-2 order-[32]">
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
            </div>
          </div>
        </div>
        <p class="mb-3 text-xs leading-5 text-white/30">
          暂停是全站闸：提交停投、在途的引擎任务挂起，重启也记得住；取消只清排队区（还没碰引擎的行，零成本），取消后可在首页卡阵重新排队。
        </p>
        <p v-if="dlText" class="mb-3 text-xs text-adminAccent/80">{{ dlText }}</p>
        <div v-if="!dlQueue?.downloading?.length && !dlQueue?.pending?.length" class="py-6 text-center text-xs text-white/25">
          队列空空，没有在途和排队的馆藏
        </div>
        <div v-else class="space-y-4">
          <div v-if="dlQueue?.downloading?.length">
            <p class="mb-2 text-[11px] uppercase tracking-widest text-stationPink/70">进站中</p>
            <ul class="space-y-1.5">
              <li v-for="item in dlQueue.downloading" :key="`dl-${item.id}`" class="flex items-center gap-2.5">
                <span class="h-1.5 w-1.5 shrink-0 animate-pulse rounded-full bg-stationPink"></span>
                <img v-if="item.coverUrl" :src="coverThumb(item.coverUrl, 160)" referrerpolicy="no-referrer" loading="lazy" decoding="async" class="h-9 w-15 shrink-0 rounded-md object-cover" alt="" />
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
                <img v-if="item.coverUrl" :src="coverThumb(item.coverUrl, 160)" referrerpolicy="no-referrer" loading="lazy" decoding="async" class="h-9 w-15 shrink-0 rounded-md object-cover opacity-70" alt="" />
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
        </div>
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
            视频库、SQLite、cookies、缓存都在这个目录里。容器内请挂载为 /data volume，并保持落在 NAS 本地盘。
          </p>
        </dl>
      </section>

      <section class="lbs-card order-[41]">
        <h3 class="lbs-title">站长登录</h3>
        <div class="mb-4 flex items-center gap-2 text-sm">
          <span class="lbs-dot" :class="loginState?.isLogin ? 'lbs-dot-on' : 'lbs-dot-off'"></span>
          <span v-if="loginState?.isLogin" class="text-white/85">
            {{ loginState.name }}
            <span v-if="loginState.vipStatus > 0" class="ml-1 text-adminAccent/80">大会员</span>
          </span>
          <span v-else class="text-white/40">未登录（匿名态，画质上限 480P）</span>
        </div>
        <p class="mb-2 text-xs leading-5 text-white/30">
          在浏览器登录 www.bilibili.com 后，从开发者工具的请求头里整行复制 Cookie 粘到这里。cookie 只进不出，不会出现在任何页面回显和日志里。
        </p>
        <textarea
          v-model="cookieDraft"
          rows="3"
          placeholder="SESSDATA=…; bili_jct=…; DedeUserID=…"
          class="lbs-input resize-none"
          :disabled="loginBusy"
        ></textarea>
        <div class="mt-3 flex items-center gap-2">
          <button
            class="lbs-btn"
            :disabled="loginBusy || !cookieDraft.trim()"
            @click="saveCookie"
          >
            {{ loginBusy ? '验证中……' : '保存并进站' }}
          </button>
          <button
            v-if="loginState?.hasSavedCookies"
            class="lbs-btn-ghost"
            :disabled="loginBusy"
            @click="logout"
          >
            登出
          </button>
        </div>
        <p
          v-if="loginNotice"
          class="mt-3 font-mono text-xs"
          :class="loginNotice.kind === 'ok' ? 'text-adminAccent/80' : 'text-rose-300/80'"
        >
          {{ loginNotice.text }}
        </p>
      </section>

      <section class="lbs-card order-[42]">
        <h3 class="lbs-title">副登录 · 按视频分号</h3>
        <div class="mb-4 flex items-center gap-2 text-sm">
          <span class="lbs-dot" :class="altState?.isAlt ? 'lbs-dot-on' : 'lbs-dot-off'"></span>
          <span v-if="altState?.isAlt" class="text-white/85">
            {{ altState.name }}
            <span v-if="altState.vipStatus > 0" class="ml-1 text-adminAccent/80">大会员</span>
            <span class="ml-2 text-xs text-white/30">两号已按视频分流</span>
          </span>
          <span v-else class="text-white/40">未配置（下载与采集全走主号）</span>
        </div>
        <p class="mb-2 text-xs leading-5 text-white/30">
          配了副号后，新视频按提交顺序两号轮转定档（视频1主、视频2副……）：提交、playurl、采集全跟归属号走，两号各扛一路分散风控；没副号时一切回主号。注意画质上限看下载那个号的权益（副号非大会员时付费内容会自动回主号重下）。cookie 只进不出。
        </p>
        <textarea
          v-model="altDraft"
          rows="3"
          placeholder="SESSDATA=…; bili_jct=…; DedeUserID=…"
          class="lbs-input resize-none"
          :disabled="altBusy"
        ></textarea>
        <div class="mt-3 flex items-center gap-2">
          <button
            class="lbs-btn"
            :disabled="altBusy || !altDraft.trim()"
            @click="saveAlt"
          >
            {{ altBusy ? '验证中……' : '保存副号' }}
          </button>
          <button
            v-if="altState?.isAlt"
            class="lbs-btn-ghost"
            :disabled="altBusy"
            @click="removeAlt"
          >
            移除副号
          </button>
        </div>
        <p v-if="altNotice" class="mt-3 font-mono text-xs" :class="altNotice.kind === 'ok' ? 'text-adminAccent/80' : 'text-rose-300/80'">
          {{ altNotice.text }}
        </p>
        <div class="mt-4 border-t border-adminLine pt-3">
          <label class="mb-1 block text-xs text-white/40">采集账号 <span class="text-white/25">UP 名片 · 互动统计 · 评论 · 收藏夹导入</span></label>
          <select
            v-model="collectMode"
            class="eng-input w-auto cursor-pointer !text-sm"
            :disabled="collectBusy"
            @change="saveCollectMode"
          >
            <option value="mixed">混合（跟随视频归属号）</option>
            <option value="main">只用主号</option>
            <option value="alt">只用副号</option>
          </select>
        </div>
      </section>

      <section v-if="eng && engDraft" class="lbs-card md:col-span-2 order-[22]">
        <div class="mb-4 flex items-center justify-between">
          <h3 class="lbs-title !mb-0">下载设置</h3>
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
              :disabled="engBusy"
              @click="saveEngine"
            >
              {{ engBusy ? '保存中……' : '保存设置' }}
            </button>
          </div>
        </div>
        <p class="mb-4 text-xs leading-5 text-white/30">
          这些参数直接写进下载引擎自己的设置：标注「即时」的改动对新下载立即生效，「新提交」的只影响之后投稿的视频，「重启」的要重启分站进程。保存目录、代理、cookie 不在这里：分站锁死 data/videos，不碰代理，cookie 在站长登录卡。
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
              <div>
                <label class="mb-1 block text-xs text-white/40">字幕语言偏好</label>
                <select v-model.number="engDraft.video.content.preferredSubtitleLanguage" class="eng-input">
                  <option v-for="[id, name] in LANG_OPTIONS" :key="id" :value="id">{{ name }}</option>
                </select>
              </div>
              <div>
                <label class="mb-1 block text-xs text-white/40">字幕类型偏好</label>
                <select v-model.number="engDraft.video.content.preferredSubtitleType" class="eng-input">
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
      </section>

      <section v-if="backupState" class="lbs-card order-[51]">
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
          </li>
        </ul>
        <p v-else class="text-xs text-white/25">档案柜还空着，点「立即拍一张」开张。</p>
        <p v-if="backupText" class="mt-2 text-xs text-adminAccent/80">{{ backupText }}</p>
      </section>

      <section class="lbs-card md:col-span-2 order-[53]">
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
            <h4 class="mb-3 text-xs tracking-widest text-white/50">导出打包 <span class="text-white/25">必含 station.db + manifest</span></h4>
            <label class="mb-1.5 flex items-center gap-2 text-xs text-white/65">
              <input v-model="expPick.withCreds" type="checkbox" class="accent-adminAccent" />
              登录态（成员 cookie · 副号 · 站长登录）
              <span class="ml-auto font-mono text-white/25">{{ fmtBytes(expStats?.dbBytes) }} + 凭证件</span>
            </label>
            <p v-if="expPick.withCreds" class="mb-1.5 rounded border border-amber-400/40 bg-amber-400/10 px-2 py-1 text-[11px] text-amber-200">
              ⚠ 包含 B 站凭证，请妥善保管
            </p>
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

      <!-- 回收站：删档后的捞回口，挨着危险区放——破坏性操作集中在一处，翻柜子时顺手就能确认 -->
      <RecycleBinCard class="order-[52]" />

      <button
        v-if="!dangerOpen"
        id="grp-danger"
        type="button"
        class="order-[61] md:col-span-2 flex items-center gap-2.5 rounded-2xl border border-rose-400/20 bg-rose-400/[0.04] px-5 py-3 text-left text-xs text-rose-300/60 transition hover:border-rose-400/40 hover:text-rose-200"
        @click="gotoGroup('danger')"
      >
        <span class="h-3 w-[3px] rounded-full bg-rose-400/60"></span>
        危险区 · 一键清空分站
        <span class="ml-auto font-mono text-[11px] text-white/25">点开展开</span>
      </button>
      <section v-else id="grp-danger" class="lbs-card-danger md:col-span-2 order-[61]">
        <div class="mb-1 flex items-center justify-between gap-2">
          <h3 class="lbs-title-danger">危险区 · 一键清空分站</h3>
          <button type="button" class="text-[11px] text-white/30 transition hover:text-white/60" @click="dangerOpen = false">收起</button>
        </div>
        <p class="mb-3 text-xs leading-5 text-white/30">
          把整座站拍回空仓：视频库（视频/弹幕/评论/封面）、头像表情评论图缓存、台账全部销账，引擎任务记录也一并清掉。站长登录、副登录、采集偏好、下载设置全部保留。不可撤销。
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
