<script setup>
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { coverThumb } from '../utils/cover'
import LoginDialog from '../components/LoginDialog.vue'
import { member, loginDialogOpen, loginDialogMode } from '../memberStore'
import { friendlyTime } from '../utils/time'

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

// ─── 主人模式：key 只在生成那一刻亮一次，服务端只存 data/owner.key 文件 ───
const ownerState = ref(null) // { configured, isOwner }，/api/owner/state
const ownerKeyShown = ref('') // 生成/换钥时的一次性展示窗
const ownerInput = ref('')
const ownerOldInput = ref('')
const ownerBusy = ref(false)
const ownerNotice = ref(null) // { kind, text }

async function loadOwnerState() {
  try {
    const res = await fetch('/api/owner/state')
    ownerState.value = res.ok ? await res.json() : null
  } catch {
    ownerState.value = null
  }
}

async function requestOwnerKey(oldKey) {
  if (ownerBusy.value) return
  ownerBusy.value = true
  ownerNotice.value = null
  try {
    const res = await fetch('/api/owner/key', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ oldKey: oldKey ?? null }),
    })
    const data = await res.json().catch(() => null)
    if (res.ok && data?.key) {
      ownerKeyShown.value = data.key
      ownerNotice.value = { kind: 'ok', text: data.message }
      ownerOldInput.value = ''
      loadOwnerState()
    } else {
      ownerNotice.value = { kind: 'err', text: res.status === 401 ? '旧钥匙不对，换不了' : (data?.message ?? '操作失败') }
    }
  } catch {
    ownerNotice.value = { kind: 'err', text: '后端没应答' }
  } finally {
    ownerBusy.value = false
  }
}

async function enterOwnerMode() {
  const key = ownerInput.value.trim()
  if (!key || ownerBusy.value) return
  ownerBusy.value = true
  ownerNotice.value = null
  try {
    const res = await fetch('/api/owner/enter', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ key }),
    })
    const data = await res.json().catch(() => null)
    ownerNotice.value = res.ok ? { kind: 'ok', text: data?.message ?? '已进入主人模式' } : { kind: 'err', text: '钥匙不对' }
    if (res.ok) ownerInput.value = ''
    loadOwnerState()
  } catch {
    ownerNotice.value = { kind: 'err', text: '后端没应答' }
  } finally {
    ownerBusy.value = false
  }
}

async function leaveOwnerMode() {
  if (ownerBusy.value) return
  ownerBusy.value = true
  try {
    await fetch('/api/owner/leave', { method: 'POST' })
    loadOwnerState()
  } finally {
    ownerBusy.value = false
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

// ─── 身份门：会员会话说了算（401=访客请扫码，403=普通成员送客去用户控制室）───
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
      ? `确定撤掉「${item.nickname}」的席位？历史事件保留，想回来得重新领邀请码。`
      : `确定${verbs[action]}「${item.nickname}」？`
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
  const who = acc.accountName || `UID ${acc.accountMid}`
  if (!window.confirm(`确定从「${item.nickname}」的席位上解绑「${who}」？`)) return
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

async function bootAdmin() {
  // 先拿站长专属端点探身份：401=访客（请扫码），403=普通成员（送客去用户控制室）。
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
  loadOwnerState()
  loadLoginState()
  loadAlt()
  loadCollectMode()
  loadEngine()
  loadDlQueue()
  loadBackups()
  loadMembers()
  if (!dlTimer) dlTimer = setInterval(loadDlQueue, 5000)
}

onMounted(() => {
  bootAdmin()
})

// 站长在 LoginDialog 里扫码进来、或从顶栏登出后，重跑开机核身。
watch(member, () => {
  if (adminAuth.value !== 'loading') bootAdmin()
})

onUnmounted(() => {
  if (dlTimer) clearInterval(dlTimer)
})
</script>

<template>
  <div class="min-h-screen bg-adminBg text-white">
    <!-- 身份门：会员会话说了算，访客挡在门外，普通成员送客去用户控制室 -->
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
            你已是分站成员，这里是站长室——普通成员请进用户控制室。
          </p>
          <RouterLink to="/me" class="lbs-btn inline-block w-full">进用户控制室</RouterLink>
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

    <main v-if="adminAuth === 'ok'" class="lbs-rise mx-auto grid max-w-[1100px] grid-cols-1 gap-4 px-6 py-8 md:grid-cols-2">
      <section class="lbs-card md:col-span-2">
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

      <section class="lbs-card md:col-span-2">
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
              <img v-if="m.faceUrl" :src="m.faceUrl" referrerpolicy="no-referrer" loading="lazy" class="h-8 w-8 shrink-0 rounded-full object-cover" alt="" />
              <span v-else class="flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-white/10 text-xs text-white/40">{{ (m.nickname || '?').slice(0, 1) }}</span>
              <div class="min-w-0 flex-1">
                <p class="flex items-center gap-1.5 text-xs text-white/85">
                  <span class="truncate">{{ m.nickname }}</span>
                  <span v-if="m.role === 'owner'" class="shrink-0 rounded-full border border-amber-400/50 bg-amber-400/10 px-1.5 py-px text-[10px] text-amber-300">♛ 站长</span>
                  <span
                    class="shrink-0 rounded-full border px-1.5 py-px text-[10px]"
                    :class="m.status === 'active' ? 'border-emerald-400/40 text-emerald-300' : m.status === 'invited' ? 'border-amber-400/40 text-amber-300' : 'border-rose-400/40 text-rose-300'"
                  >
                    {{ m.status === 'active' ? '启用' : m.status === 'invited' ? '待邀' : '封禁' }}
                  </span>
                </p>
                <p class="mt-0.5 font-mono text-[10px] text-white/30">
                  UID {{ m.mid }} · 入站 {{ fmtBackupTime(m.createdAtUtc) }} · 上次登录 {{ friendlyTime(m.lastLoginAtUtc) }}
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
                <img v-if="acc.faceUrl" :src="acc.faceUrl" referrerpolicy="no-referrer" loading="lazy" class="h-6 w-6 shrink-0 rounded-full object-cover" alt="" />
                <span v-else class="h-6 w-6 shrink-0 rounded-full bg-white/10"></span>
                <span class="min-w-0 flex-1 truncate text-[11px] text-white/70">
                  {{ acc.accountName || `UID ${acc.accountMid}` }}
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
              <li v-if="!m.accounts?.length" class="py-1 text-[11px] text-white/25">这席还没绑任何 B 站账号。</li>
            </ul>
          </li>
        </ul>
      </section>

      <section class="lbs-card md:col-span-2">
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

      <section class="lbs-card md:col-span-2">
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

      <section class="lbs-card">
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

      <section class="lbs-card">
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

      <section class="lbs-card">
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

      <section class="lbs-card">
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

      <section class="lbs-card">
        <div class="mb-3 flex items-center justify-between">
          <h3 class="lbs-title !mb-0">主人模式</h3>
          <span v-if="ownerState" class="flex items-center gap-1.5 text-[11px] text-white/35">
            <span :class="['lbs-dot', ownerState.isOwner ? 'lbs-dot-on' : 'lbs-dot-off']"></span>
            {{ ownerState.isOwner ? '主人在线' : ownerState.configured ? '访客中' : '未设主人' }}
          </span>
        </div>
        <p class="mb-3 text-xs leading-5 text-white/30">
          站内弹幕只有发送者本人或主人能删，访客只看不改。主人钥匙只存在服务器上，生成时只亮一次，存好；钥匙丢了只能拿旧的换新的。
        </p>

        <button v-if="!ownerState?.configured" class="lbs-btn" :disabled="ownerBusy" @click="requestOwnerKey(null)">
          生成主人钥匙
        </button>

        <div v-if="ownerKeyShown" class="mt-3 rounded-lg border border-adminAccent/40 bg-adminAccent/10 px-3 py-2.5">
          <p class="text-[11px] text-adminAccent/80">立刻复制走，离开本页就不再显示：</p>
          <p class="mt-1 select-all break-all font-mono text-base tracking-[0.2em] text-white/90">{{ ownerKeyShown }}</p>
        </div>

        <template v-if="ownerState?.configured">
          <div v-if="!ownerState.isOwner" class="mt-1 flex gap-2">
            <input
              v-model="ownerInput"
              type="password"
              placeholder="输主人钥匙"
              class="lbs-input flex-1"
              @keyup.enter="enterOwnerMode"
            />
            <button class="lbs-btn" :disabled="ownerBusy || !ownerInput.trim()" @click="enterOwnerMode">进主人</button>
          </div>
          <button v-else class="lbs-btn-ghost mt-1" :disabled="ownerBusy" @click="leaveOwnerMode">退出主人模式</button>

          <div class="mt-3 flex gap-2">
            <input
              v-model="ownerOldInput"
              type="password"
              placeholder="现钥匙（仅换钥时用得着）"
              class="lbs-input flex-1"
            />
            <button class="lbs-btn-ghost" :disabled="ownerBusy || !ownerOldInput.trim()" @click="requestOwnerKey(ownerOldInput.trim())">换钥匙</button>
          </div>
        </template>

        <p v-if="ownerNotice" class="mt-3 font-mono text-xs" :class="ownerNotice.kind === 'ok' ? 'text-adminAccent/80' : 'text-rose-300/80'">
          {{ ownerNotice.text }}
        </p>
      </section>

      <section v-if="eng && engDraft" class="lbs-card md:col-span-2">
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
                <label class="mb-1 block text-xs text-white/40">UID 黑名单（一行一个）</label>
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

      <section v-if="backupState" class="lbs-card md:col-span-2">
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

      <section class="lbs-card-danger md:col-span-2">
        <h3 class="lbs-title-danger">危险区 · 一键清空分站</h3>
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
