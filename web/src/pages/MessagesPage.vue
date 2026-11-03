<script setup>
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import TopBar from '../components/TopBar.vue'
import PeerPicker from '../components/PeerPicker.vue'
import ShareCard from '../components/ShareCard.vue'
import ArrivalNoticeCard from '../components/ArrivalNoticeCard.vue'
import { member, loaded, unread, dmUnread, loginDialogOpen, loginDialogMode, load as loadMember, refreshUnread } from '../memberStore'
import { requestConfirm } from '../confirmStore'
import { friendlyTime } from '../utils/time'
import { defaultAvatar, onAvatarError } from '../utils/avatar.js'
import { VIDEO_GONE_HINT } from '../utils/gone.js'

const route = useRoute()
const router = useRouter()

// 私信里的动态分享卡：点卡回橱窗定位那条动态。
function gotoSharedDynamic(dyn) {
  if (dyn?.dynamicId) router.push({ path: '/dynamics', query: { focus: dyn.dynamicId } })
}

// 私信里的放映厅分享卡：点卡直落站内放映厅（后端定格里带 watchUrl，拿 bvid 拼同一扇门）。
function gotoSharedVideo(dyn) {
  if (dyn?.bvid) router.push(`/watch/${dyn.bvid}`)
}

// 周报公告（后端 WeeklyReportWorker 播的「分站周报 · 2026-W44」）：认得出 ISO 周号才递一枚跳到那期的钮，
// 认不出就当普通公告摊平——宁可不摆，也不摆一枚跳到最新一期的假钮。
const WEEKLY_ANNOUNCE_RE = /^分站周报\s*·\s*(\d{4}-W\d{2})$/
function weeklyIssueOf(item) {
  if (item?.kind !== 'announce') return ''
  const hit = WEEKLY_ANNOUNCE_RE.exec(String(item.title ?? '').trim())
  return hit ? hit[1] : ''
}

function gotoWeeklyIssue(w) {
  if (!w) return
  router.push({ path: '/weekly', query: { w } })
}

// ─── 页签 ───
// 上次停在哪个页签就开哪个（浏览器本地小账，同点赞一个路子）；没记过默认信箱
const TAB_KEY = 'lbs-msg-tab'
const lastTab = (() => {
  try {
    const v = localStorage.getItem(TAB_KEY)
    return v === 'dm' || v === 'inbox' ? v : 'inbox'
  } catch {
    return 'inbox'
  }
})()
const activeTab = ref(lastTab) // inbox | dm

// ─── 信箱（M5.2 原样）───
const items = ref([])
const loading = ref(true)
const inboxUnread = ref(0)
// 展开的信件集合：打开就一直摊着，再点一下才折回；多封可同时敞开
const expandedIds = ref(new Set())
const notice = ref(null)

const kindMeta = {
  welcome: { label: '欢迎', cls: 'bg-stationPink/10 text-stationPink' },
  complete: { label: '入档', cls: 'bg-emerald-500/10 text-emerald-600' },
  fail: { label: '趔趄', cls: 'bg-red-500/10 text-red-500' },
  announce: { label: '公告', cls: 'bg-amber-400/15 text-amber-600' },
  arrival: { label: '到货', cls: 'bg-stationPink/10 text-stationPink' },
}

// 到站通报（msg_type='arrivals'）：认得出载荷才摊卡面，解析砸/空单就当普通信摊 body 兜底。
// 卡面是播送那刻定格的快照（与 ShareCard 同族），读路径不现查——顶栏 30 秒轮询不陪跑。
function arrivalCards(item) {
  if (item?.msgType !== 'arrivals' || !item.payloadJson) return null
  try {
    const arr = JSON.parse(item.payloadJson)
    return Array.isArray(arr) && arr.length ? arr.filter((c) => c && c.bvid) : null
  } catch {
    return null
  }
}

// 入档回执（msg_type='video_receipt'，11-03 站长「这条也做个排版」）：认得出载荷就排单件回执卡
//（与到货卡同一套行版式，报头换脸），认不出退回纯文本 body——老信没这两列，渲染照旧。
function receiptCards(item) {
  if (item?.msgType !== 'video_receipt' || !item.payloadJson) return null
  try {
    const arr = JSON.parse(item.payloadJson)
    return Array.isArray(arr) && arr.length ? arr.filter((c) => c && c.bvid) : null
  } catch {
    return null
  }
}

function meta(kind) {
  return kindMeta[kind] ?? { label: kind || '信', cls: 'bg-lineSoft text-inkSub' }
}

// ─── 通知设置（站长 11-03：「有些人嫌烦」）───
// 三路定时货席位自关：开关即时 PUT 保存，静音闸在寄出前——关了就不再落库，
// 已到的信保留在手边，重开不补寄历史。欢迎信/站长公告/私信没有可关频道，不进面板。
const prefsOpen = ref(false)
const prefsLoading = ref(false)
const prefsSaving = ref(false)
const mutes = ref([]) // 当前静音的频道名（空＝全开）
const prefsStyle = ref({}) // 浮窗定位：Teleport 到 body 后用视口坐标钉在按钮正下

const MUTE_CHANNELS = [
  { key: 'arrival', icon: '📡', label: '到站播报' },
  { key: 'weekly', icon: '📰', label: '分站周报' },
  { key: 'submit', icon: '📮', label: '投稿回执' },
]

function isMuted(key) {
  return mutes.value.includes(key)
}

// 浮窗仿顶栏铃铛预览卡（站长 11-03 点名同款）：w-80、rounded-xl、lineSoft 描边、同一道深阴影。
// 不用 absolute 而 Teleport 到 body：信匣 section 自带 overflow-hidden，信少时浮窗会被剪尾；
// 视口坐标钉在按钮右下 6px（与铃铛的 mt-1.5 同一指宽）。
function openPrefs(event) {
  if (prefsOpen.value) {
    prefsOpen.value = false
    return
  }
  const rect = event.currentTarget.getBoundingClientRect()
  prefsStyle.value = { top: `${rect.bottom + 6}px`, right: `${window.innerWidth - rect.right}px` }
  prefsOpen.value = true
  loadPrefs()
}

async function loadPrefs() {
  prefsLoading.value = true
  try {
    const res = await fetch('/api/member/notify-prefs')
    if (res.ok) {
      const data = await res.json()
      mutes.value = Array.isArray(data.mutes) ? data.mutes : []
    } else {
      flashNotice('通知账没读出来，稍后再试')
    }
  } catch {
    flashNotice('通知账没读出来，稍后再试')
  } finally {
    prefsLoading.value = false
  }
}

// 页面滚起来了浮窗不陪跑（它是视口坐标，赖着就飘离按钮了）：一滚即收，点头像菜单也是这规矩。
watch(prefsOpen, (open) => {
  if (open) window.addEventListener('scroll', closePrefs, true)
  else window.removeEventListener('scroll', closePrefs, true)
})
function closePrefs() {
  prefsOpen.value = false
}
onUnmounted(() => window.removeEventListener('scroll', closePrefs, true))

// 清空信箱（站长 11-03 拍板真删）：单向投递的系统信，收信人是唯一干系人，真删不留墓碑；
// 清的是存量不是订阅——新信照来，嫌吵去通知设置关闸。二次确认防手滑（撤回/清会话同款）。
async function clearInbox() {
  if (!items.value.length) return
  if (
    !(
      await requestConfirm({
        title: '清空信箱',
        sub: '真删，不是藏起来',
        message: '这一本账整个抹掉，翻不回来。通知照旧会来——嫌吵去通知设置里关。',
        confirmText: '清空',
        cancelText: '再留留',
        danger: true,
      })
    )
  ) {
    return
  }
  try {
    const res = await fetch('/api/member/messages/clear', { method: 'POST' })
    const data = await res.json().catch(() => null)
    if (!res.ok) {
      flashNotice(data?.message || '清空没成，再试一次')
      return
    }
    flashNotice(data?.message || '信箱已清空')
    expandedIds.value = new Set() // 摊开的信随账一起收，不留孤页
    await loadInbox()
    refreshUnread()
  } catch {
    flashNotice('清空没成，再试一次')
  }
}

async function toggleMute(key) {
  if (prefsSaving.value) return
  const turningOn = isMuted(key) // 现在是静音态 → 这一扳是重新打开
  const next = turningOn ? mutes.value.filter((c) => c !== key) : [...mutes.value, key]
  prefsSaving.value = true
  try {
    const res = await fetch('/api/member/notify-prefs', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ mutes: next }),
    })
    const data = await res.json().catch(() => null)
    if (!res.ok) {
      flashNotice(data?.message || '通知偏好没存住，再试一次')
      return
    }
    mutes.value = Array.isArray(data.mutes) ? data.mutes : next
    const label = MUTE_CHANNELS.find((c) => c.key === key)?.label || key
    flashNotice(turningOn ? `「${label}」重新打开了` : `「${label}」已静音`)
  } catch {
    flashNotice('通知偏好没存住，再试一次')
  } finally {
    prefsSaving.value = false
  }
}

async function loadInbox() {
  if (!member.value) {
    items.value = []
    inboxUnread.value = 0
    loading.value = false
    return
  }
  loading.value = true
  try {
    const res = await fetch('/api/member/messages')
    if (res.ok) {
      const data = await res.json()
      items.value = data.items ?? []
      inboxUnread.value = Number(data.unread) || 0
      refreshUnread() // 合流数交给 store 统一算，这里只留页签自己的小账
    } else if (res.status === 401) {
      member.value = null
    }
  } catch {
    notice.value = '信箱没拉开，刷新再试'
  } finally {
    loading.value = false
  }
}

function flashNotice(text) {
  notice.value = text
  setTimeout(() => { notice.value = null }, 2500)
}

async function toggle(item) {
  const open = !expandedIds.value.has(item.id)
  const next = new Set(expandedIds.value)
  if (open) next.add(item.id)
  else next.delete(item.id)
  expandedIds.value = next
  if (!open || item.readAtUtc) return
  try {
    const res = await fetch(`/api/member/messages/${item.id}/read`, { method: 'POST' })
    if (res.ok) {
      item.readAtUtc = new Date().toISOString()
      if (inboxUnread.value > 0) inboxUnread.value -= 1
      if (unread.value > 0) unread.value -= 1
    }
  } catch {
    /* 标读没回音，下次进页再补 */
  }
}

async function markAll() {
  if (!member.value) return
  try {
    const res = await fetch('/api/member/messages/read-all', { method: 'POST' })
    if (res.ok) {
      const now = new Date().toISOString()
      items.value.forEach((m) => { if (!m.readAtUtc) m.readAtUtc = now })
      inboxUnread.value = 0
      const data = await res.json().catch(() => null)
      flashNotice(data?.message ?? '都标已读了')
      refreshUnread()
    }
  } catch {
    flashNotice('全部已读没递上去')
  }
}

// ─── 私信（M5.3）───
const threads = ref(null) // null=还没拉
const activePeer = ref(null) // {memberId, nickname, username, displayName, faceUrl, mid?}（M5.4 私信认席位不认 mid）
const conversation = ref([])
const convoLoading = ref(false)
const directoryOpen = ref(false)
const draft = ref('')
const sending = ref(false)
const dmToast = ref(null)
const scrollAnchor = ref(null)
let dmPollTimer = null

function flashDm(text) {
  dmToast.value = text
  setTimeout(() => { dmToast.value = null }, 2600)
}

async function loadThreads() {
  if (!member.value) return
  try {
    const res = await fetch('/api/dm/threads')
    threads.value = res.ok ? (await res.json()).threads ?? [] : []
  } catch {
    threads.value = []
  }
}

async function openThread(peerMemberId, peerHint = null) {
  if (!member.value) return
  convoLoading.value = true
  activePeer.value = peerHint ? { ...peerHint } : activePeer.value ?? { memberId: peerMemberId }
  directoryOpen.value = false
  try {
    const res = await fetch(`/api/dm/thread/${peerMemberId}`)
    if (res.ok) {
      const data = await res.json()
      activePeer.value = data.peer ?? { memberId: peerMemberId }
      conversation.value = data.messages ?? []
      // 后端拉取即标读：本地两本账跟着清
      dmUnread.value = Math.max(0, dmUnread.value - conversation.value.filter((m) => m.toMemberId === member.value?.memberId && !m.readAtUtc).length)
      const t = threads.value?.find((x) => x.peerMemberId === peerMemberId)
      if (t) t.unread = 0
      await nextTick()
      scrollAnchor.value?.scrollIntoView({ block: 'end' })
    } else {
      const data = await res.json().catch(() => null)
      flashDm(data?.message ?? '这条会话没拉开')
      activePeer.value = null
    }
  } catch {
    flashDm('分站后端没有应答')
  } finally {
    convoLoading.value = false
    restartDmPoll()
  }
}

// 撤回：发件人销自己那句，真删——双方账本同时没（轮询追新时对方那头自然消失）。
// 二次确认防手滑；销成当场从本地会话里拔掉，不等下拍轮询。
async function recallMessage(m) {
  if (
    !(
      await requestConfirm({
        title: '撤回这句',
        sub: '真删，不是只遮自己这边',
        message: '这句私信从你俩的会话里一起消失，对方那边也翻不回来了。',
        confirmText: '撤回',
        cancelText: '留着吧',
        danger: true,
        icon: 'trash',
      })
    )
  ) {
    return
  }
  try {
    const res = await fetch(`/api/dm/message/${m.id}`, { method: 'DELETE' })
    const data = await res.json().catch(() => null)
    if (res.ok) {
      conversation.value = conversation.value.filter((x) => x.id !== m.id)
      flashDm('已撤回，烟消云散')
    } else {
      flashDm(data?.message ?? '这句没撤回成')
    }
  } catch {
    flashDm('分站后端没有应答')
  }
}

function closeThread() {
  activePeer.value = null
  conversation.value = []
  loadThreads()
  restartDmPoll() // 不彻底停：退出会话后列表继续慢拍，新私信不至于要重开页面才现身
}

// 清空聊天记录：二次确认后走单边销账端点（自己这侧抹掉，对方账本不动），
// 销完当场清屏收场；对方再发信，会话自会复活。
async function clearThread() {
  const peer = activePeer.value
  if (!peer || !conversation.value.length) return
  if (
    !(
      await requestConfirm({
        title: '清空这段对话',
        sub: '只销自己这侧，对方那边的记录不动',
        message: `与「${peerLabel(peer)}」的聊天记录整段抹掉，翻不回来。对方再发信，会话自会复活。`,
        confirmText: '清空',
        cancelText: '再留留',
        danger: true,
        icon: 'trash',
      })
    )
  ) {
    return
  }
  try {
    const res = await fetch(`/api/dm/thread/${peer.memberId}`, { method: 'DELETE' })
    const data = await res.json().catch(() => null)
    if (res.ok) {
      conversation.value = []
      closeThread()
      flashDm('记录销了，再聊再记')
    } else {
      flashDm(data?.message ?? '记录没销成')
    }
  } catch {
    flashDm('分站后端没有应答')
  }
}

async function refreshConversation(silent = true) {
  if (!activePeer.value || !member.value) return
  try {
    const res = await fetch(`/api/dm/thread/${activePeer.value.memberId}`)
    if (res.ok) {
      const data = await res.json()
      const next = data.messages ?? []
      // 只有「来了新消息且人本来就在底部附近」才跟滚；翻历史翻到一半的，每秒轮询不该把人硬拽回底部
      const box = scrollAnchor.value?.parentElement
      const nearBottom = !box || box.scrollHeight - box.scrollTop - box.clientHeight < 96
      const grew = next.length > conversation.value.length
      conversation.value = next
      const t = threads.value?.find((x) => x.peerMemberId === activePeer.value.memberId)
      if (t) t.unread = 0
      if (grew && nearBottom) {
        await nextTick()
        scrollAnchor.value?.scrollIntoView({ block: 'end' })
      }
    } else if (!silent) {
      const data = await res.json().catch(() => null)
      flashDm(data?.message ?? '刷新没成')
    }
  } catch {
    /* 轮询断一下不吭声 */
  }
}

function stopDmPoll() {
  if (dmPollTimer) {
    clearInterval(dmPollTimer)
    dmPollTimer = null
  }
}

function restartDmPoll() {
  stopDmPoll()
  // 单心跳 1s：开着会话每拍追新消息（近实时）；会话列表 5 拍跟一次；
  // 没开会话时 15 拍才冒一次——平时可以晚，窗口开着就要快
  if (activeTab.value !== 'dm') return
  let tick = 0
  dmPollTimer = setInterval(() => {
    if (typeof document !== 'undefined' && document.hidden) return
    tick += 1
    if (activePeer.value) {
      refreshConversation()
      if (tick % 5 === 0) syncThreads()
    } else if (tick % 15 === 0) {
      syncThreads()
    }
  }, 1000)
}

async function syncThreads() {
  await loadThreads()
  // 页签红点与顶栏铃铛跟着名录走；当前开着的那路已被拉取即标读，记 0 防旧值闪回
  const open = activePeer.value?.memberId
  dmUnread.value = (threads.value ?? []).reduce((s, t) => s + (t.peerMemberId === open ? 0 : t.unread || 0), 0)
  refreshUnread()
}

function openDirectory() {
  directoryOpen.value = true
}

async function sendDm() {
  const body = draft.value.trim()
  if (!body || !activePeer.value || sending.value) return
  sending.value = true
  try {
    const res = await fetch('/api/dm', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ toMemberId: activePeer.value.memberId, body }),
    })
    const data = await res.json().catch(() => null)
    if (res.ok) {
      conversation.value.push({
        id: data?.id ?? Date.now(),
        fromMemberId: member.value.memberId,
        toMemberId: activePeer.value.memberId,
        body,
        createdAtUtc: new Date().toISOString(),
        readAtUtc: null,
      })
      draft.value = ''
      const t = threads.value?.find((x) => x.peerMemberId === activePeer.value.memberId)
      if (t) {
        t.lastBody = body
        t.lastAtUtc = new Date().toISOString()
      }
      await nextTick()
      scrollAnchor.value?.scrollIntoView({ block: 'end' })
    } else {
      flashDm(data?.message ?? '私信没发出去')
    }
  } catch {
    flashDm('分站后端没有应答')
  } finally {
    sending.value = false
  }
}

function onDraftKeydown(e) {
  // Enter 发送，Shift+Enter 换行
  if (e.key === 'Enter' && !e.shiftKey) {
    e.preventDefault()
    sendDm()
  }
}

function switchTab(tab) {
  if (activeTab.value === tab) return
  activeTab.value = tab
  try {
    localStorage.setItem(TAB_KEY, tab)
  } catch {
    // 本地存不进也不拦切页，大不了下次从头开信箱
  }
  if (tab === 'dm') {
    loadThreads()
    refreshUnread()
    restartDmPoll()
  } else {
    stopDmPoll()
  }
}

onMounted(async () => {
  if (!loaded.value) await loadMember()
  await loadInbox()
  // 上次停在私信：开机直接落私信页签（列表、未读、追新心跳一并接上）
  if (activeTab.value === 'dm') {
    loadThreads()
    refreshUnread()
    restartDmPoll()
  }
  // 站长室「发私信」跳转：/messages?dm={memberId} 预开会话（M5.4 起锁席位 id）
  const dmPeer = typeof route.query.dm === 'string' ? Number(route.query.dm) : null
  if (dmPeer && Number.isFinite(dmPeer)) {
    switchTab('dm')
    openThread(dmPeer)
  }
  // 顶栏铃铛预览的深链：/messages?msg={id} 把那封信摊开。预览停留过已标读，这里只负责把它掀开来读全文
  const msgId = typeof route.query.msg === 'string' ? Number(route.query.msg) : null
  if (msgId && Number.isFinite(msgId)) {
    switchTab('inbox')
    const hit = items.value.find((m) => m.id === msgId)
    if (hit && !expandedIds.value.has(hit.id)) await toggle(hit)
  }
})

onUnmounted(() => {
  stopDmPoll()
})

const peerId = computed(() => activePeer.value?.memberId ?? null)
// 对人的展示名：昵称 → @站名 → 席位号，素净席位不再叫 UID 0
function peerLabel(p) {
  if (!p) return ''
  return p.displayName || p.nickname || p.peerName || p.peerDisplayName || (p.username ? p.username : `席位 ${p.memberId ?? p.peerMemberId ?? ''}`)
}
</script>

<template>
  <div class="min-h-screen bg-pageBg">
    <TopBar />
    <main class="mx-auto max-w-[960px] px-6 py-6">
      <!-- 页眉：仿投稿窗的装裱语言——粉盾圆图标 + 渐变衬底 -->
      <div class="mb-4 flex flex-wrap items-center justify-between gap-3 rounded-2xl border border-lineSoft bg-cardBg bg-gradient-to-r from-stationPink/10 via-stationPink/5 to-transparent px-5 py-4">
        <div class="flex items-center gap-3">
          <span class="flex h-10 w-10 shrink-0 items-center justify-center rounded-full bg-stationPink/10">
            <svg viewBox="0 0 24 24" class="h-5 w-5 text-stationPink" fill="none" stroke="currentColor" stroke-width="1.8">
              <rect x="3" y="5" width="18" height="14" rx="2.5" />
              <path d="m3 7 9 6 9-6" stroke-linecap="round" stroke-linejoin="round" />
            </svg>
          </span>
          <div>
            <h1 class="text-[17px] font-semibold text-inkMain">站内信箱</h1>
            <p class="mt-0.5 text-xs text-inkFaint">站点的大小事与悄悄话，都从这里收发</p>
          </div>
        </div>
        <!-- 双页签：投稿窗同款胶囊，激活粉底白字（访客没得点，藏起来） -->
        <div v-if="member" class="flex gap-2">
          <button
            class="rounded-full border px-3.5 py-1.5 text-sm transition"
            :class="activeTab === 'inbox'
              ? 'border-stationPink bg-stationPink text-white shadow-sm shadow-stationPink/30'
              : 'border-lineSoft text-inkSub hover:border-stationPink/40 hover:text-inkMain'"
            @click="switchTab('inbox')"
          >
            信箱<span v-if="inboxUnread > 0" class="ml-1 text-[11px]" :class="activeTab === 'inbox' ? 'text-white/80' : 'text-stationPink'">({{ inboxUnread }})</span>
          </button>
          <button
            class="relative rounded-full border px-3.5 py-1.5 text-sm transition"
            :class="activeTab === 'dm'
              ? 'border-stationPink bg-stationPink text-white shadow-sm shadow-stationPink/30'
              : 'border-lineSoft text-inkSub hover:border-stationPink/40 hover:text-inkMain'"
            @click="switchTab('dm')"
          >
            私信
            <span
              v-if="dmUnread > 0"
              class="absolute -right-1.5 -top-1 flex h-4 min-w-4 items-center justify-center rounded-full bg-stationPink px-1 text-[10px] font-medium leading-none text-white"
            >{{ dmUnread > 99 ? '99+' : dmUnread }}</span>
          </button>
        </div>
      </div>

      <!-- 访客：信箱与私信都是分站成员待遇——投稿窗同款渐变钮指路 -->
      <section v-if="loaded && !member" class="rounded-2xl border border-lineSoft bg-cardBg px-6 py-14 text-center">
        <span class="mx-auto flex h-14 w-14 items-center justify-center rounded-full bg-stationPink/10">
          <svg viewBox="0 0 24 24" class="h-7 w-7 text-stationPink" fill="none" stroke="currentColor" stroke-width="1.8">
            <rect x="3" y="5" width="18" height="14" rx="2.5" />
            <path d="m3 7 9 6 9-6" stroke-linecap="round" stroke-linejoin="round" />
          </svg>
        </span>
        <p class="mt-4 text-sm text-inkSub">信箱和私信是分站成员的待遇——进站，第一封欢迎信就在等你。</p>
        <button
          class="mt-5 rounded-lg bg-gradient-to-r from-stationPink to-[#f994b6] px-6 py-2 text-sm font-medium text-white shadow-md shadow-stationPink/30 transition hover:-translate-y-px hover:shadow-lg hover:shadow-stationPink/40 active:translate-y-0"
          @click="loginDialogMode = 'admit'; loginDialogOpen = true"
        >
          直接登录
        </button>
        <RouterLink to="/register" class="mt-3 block text-xs text-inkSub underline-offset-4 transition hover:text-stationPink hover:underline">还没有站籍？注册去 →</RouterLink>
      </section>

      <template v-else-if="member">
        <!-- ═══ 信箱页签 ═══ -->
        <template v-if="activeTab === 'inbox'">
          <section class="overflow-hidden rounded-2xl border border-lineSoft bg-cardBg">
            <div class="flex items-center justify-between border-b border-lineSoft px-5 py-3">
              <span class="text-[13px] text-inkSub">
                {{ items.length ? `${items.length} 封信 · ${inboxUnread} 封未读` : '信笺都在这' }}
              </span>
              <div class="flex items-center gap-2">
                <button
                  class="rounded-full border px-3 py-1 text-xs transition"
                  :class="prefsOpen ? 'border-stationPink text-stationPink' : 'border-lineSoft text-inkSub hover:border-stationPink hover:text-stationPink'"
                  @click="openPrefs"
                >
                  🔔 通知设置
                </button>
                <button
                  v-if="inboxUnread > 0"
                  class="rounded-full border border-lineSoft px-3 py-1 text-xs text-inkSub transition hover:border-stationPink hover:text-stationPink"
                  @click="markAll"
                >
                  全部已读
                </button>
                <button
                  v-if="items.length"
                  class="rounded-full border border-lineSoft px-3 py-1 text-xs text-inkSub transition hover:border-red-400 hover:text-red-500"
                  @click="clearInbox"
                >
                  清空
                </button>
              </div>
            </div>

            <p v-if="loading" class="px-5 py-10 text-center text-sm text-inkFaint">正在翻信箱…</p>

            <div v-else-if="!items.length" class="px-6 py-14 text-center">
              <span class="mx-auto flex h-14 w-14 items-center justify-center rounded-full bg-stationPink/10">
                <svg viewBox="0 0 24 24" class="h-7 w-7 text-stationPink/70" fill="none" stroke="currentColor" stroke-width="1.8">
                  <rect x="3" y="5" width="18" height="14" rx="2.5" />
                  <path d="m3 7 9 6 9-6" stroke-linecap="round" stroke-linejoin="round" />
                  <path d="M16 17c1.2-1.6 3-1.8 4.5-.5" stroke-linecap="round" opacity="0.55" />
                </svg>
              </span>
              <p class="mt-4 text-sm text-inkFaint">邮箱空空，岁月静好。投的稿子入档时，这里会亮起来。</p>
            </div>

            <ul v-else>
              <li v-for="item in items" :key="item.id" class="border-b border-lineSoft/60 last:border-b-0" :class="item.readAtUtc ? '' : 'bg-stationPink/[0.03]'">
                <button class="flex w-full items-center gap-3 px-5 py-3.5 text-left transition hover:bg-stationPink/5" @click="toggle(item)">
                  <span class="shrink-0 rounded-md px-1.5 py-0.5 text-[11px]" :class="meta(item.kind).cls">{{ meta(item.kind).label }}</span>
                  <span class="min-w-0 flex-1">
                    <span class="block truncate text-[13px]" :class="item.readAtUtc ? 'text-inkSub' : 'font-medium text-inkMain'">
                      {{ item.title }}
                    </span>
                    <span v-if="item.fromName" class="block truncate text-[11px] text-inkFaint">来自 {{ item.fromName }}</span>
                  </span>
                  <span class="shrink-0 text-[11px] text-inkFaint">{{ friendlyTime(item.createdAtUtc) }}</span>
                  <span v-if="!item.readAtUtc" class="h-2 w-2 shrink-0 rounded-full bg-stationPink" title="未读"></span>
                </button>

                <div v-if="expandedIds.has(item.id)" class="px-5 pb-4">
                  <!-- 到站通报：卡面优先（一件一行，不是一堆字混着）；认不出载荷退回纯文本 body -->
                  <div v-if="arrivalCards(item)" class="mt-2.5">
                    <ArrivalNoticeCard :cards="arrivalCards(item)" :arrived-at="item.createdAtUtc" />
                  </div>
                  <!-- 入档回执：同款行版式换报头面孔；片下架了补一句提示（读路径现算的死活，卡面本身是定格快照） -->
                  <div v-else-if="receiptCards(item)" class="mt-2.5">
                    <ArrivalNoticeCard mode="receipt" :cards="receiptCards(item)" />
                    <p v-if="item.videoGone === false" class="mt-2 text-[11px] text-inkFaint">{{ VIDEO_GONE_HINT }}</p>
                  </div>
                  <div v-else class="mt-2.5 rounded-xl bg-[#f6f7f8] dark:bg-[#23262e] px-4 py-3">
                    <p class="whitespace-pre-wrap text-[13px] leading-relaxed text-inkMain">{{ item.body }}</p>
                    <RouterLink
                      v-if="item.link && !item.videoGone"
                      :to="item.link"
                      class="mt-2.5 inline-block text-xs text-stationPink transition hover:underline"
                    >去看 →</RouterLink>
                    <span v-else-if="item.link" class="mt-2.5 inline-block text-xs text-inkFaint">{{ VIDEO_GONE_HINT }}</span>
                    <!-- 周报公告专用：虚线分隔 + 右对齐描边幽灵钮，跳 /weekly?w=周号（长标题换行也挤不掉它，整行 flex-wrap） -->
                    <div
                      v-if="weeklyIssueOf(item)"
                      class="mt-3 flex flex-wrap items-center justify-end gap-2 border-t border-dashed border-lineSoft pt-2.5"
                    >
                      <button
                        type="button"
                        class="shrink-0 rounded-[8px] border border-stationPink/60 px-[11px] py-[5px] text-[11.5px] text-stationPink transition hover:bg-stationPink/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-stationPink/30 dark:border-stationPink/70"
                        @click="gotoWeeklyIssue(weeklyIssueOf(item))"
                      >
                        查看 {{ weeklyIssueOf(item) }} 周报 →
                      </button>
                    </div>
                  </div>
                </div>
              </li>
            </ul>
          </section>

          <p v-if="notice" class="mt-3 text-center text-xs text-inkFaint">{{ notice }}</p>
        </template>

        <!-- ═══ 私信页签 ═══ -->
        <template v-else>
          <div class="flex gap-4">
            <!-- 左：会话列表（与右栏同高，内部滚动；整卡跟着视口长，底下不垫空白） -->
            <section class="flex h-[max(500px,min(68vh,900px))] w-[290px] shrink-0 flex-col overflow-hidden rounded-2xl border border-lineSoft bg-cardBg">
              <div class="flex shrink-0 items-center justify-between border-b border-lineSoft px-4 py-3">
                <span class="text-[13px] text-inkSub">会话</span>
                <button
                  class="rounded-full border border-lineSoft px-2.5 py-0.5 text-xs text-inkSub transition hover:border-stationPink hover:text-stationPink"
                  @click="openDirectory"
                >
                  ＋ 发起私信
                </button>
              </div>

              <p v-if="threads === null" class="px-4 py-8 text-center text-sm text-inkFaint">正在翻记录…</p>
              <div v-else-if="!threads.length" class="px-4 py-10 text-center">
                <p class="text-sm text-inkFaint">还没有人来敲门。<br />点「发起私信」找位分站伙伴聊聊。</p>
              </div>
              <ul v-else class="min-h-0 flex-1 overflow-y-auto">
                <li v-for="t in threads" :key="t.peerMemberId">
                  <button
                    class="flex w-full items-center gap-2.5 border-b border-lineSoft/60 px-4 py-3 text-left transition hover:bg-stationPink/5"
                    :class="peerId === t.peerMemberId ? 'bg-stationPink/5' : ''"
                    @click="openThread(t.peerMemberId, { memberId: t.peerMemberId, nickname: t.peerName, faceUrl: t.peerFace })"
                  >
                    <img
                      :src="t.peerFace || defaultAvatar"
                      :alt="t.peerName || ''"
                      referrerpolicy="no-referrer"
                      class="h-10 w-10 shrink-0 rounded-full object-cover"
                      @error="onAvatarError"
                    />
                    <span class="min-w-0 flex-1">
                      <span class="block truncate text-[13px] font-medium text-inkMain">{{ peerLabel(t) }}</span>
                      <span class="block truncate text-[12px] text-inkSub">{{ t.lastBody || (t.lastMsgType === 'dynamic' ? '📣 分享了一条动态' : t.lastMsgType === 'video' ? '📣 分享了一个片子' : '') }}</span>
                    </span>
                    <span class="flex shrink-0 flex-col items-end gap-1">
                      <span class="text-[10px] text-inkFaint">{{ friendlyTime(t.lastAtUtc) }}</span>
                      <span
                        v-if="t.unread > 0"
                        class="flex h-4 min-w-4 items-center justify-center rounded-full bg-stationPink px-1 text-[10px] leading-none text-white"
                      >{{ t.unread > 99 ? '99+' : t.unread }}</span>
                    </span>
                  </button>
                </li>
              </ul>
            </section>

            <!-- 右：聊天面板（同左栏随视口长，消息区 flex-1 滚动，不再卡 480px 短腰） -->
            <section class="flex h-[max(500px,min(68vh,900px))] min-w-[400px] flex-1 flex-col overflow-hidden rounded-2xl border border-lineSoft bg-cardBg">
              <template v-if="activePeer">
                <div class="flex items-center justify-between border-b border-lineSoft bg-gradient-to-r from-stationPink/10 via-stationPink/5 to-transparent px-4 py-3">
                  <span class="flex items-center gap-2 text-[13px] font-medium text-inkMain">
                    <img :src="activePeer.faceUrl || defaultAvatar" :alt="activePeer.nickname || ''" referrerpolicy="no-referrer" class="h-7 w-7 rounded-full object-cover" @error="onAvatarError" />
                    {{ peerLabel(activePeer) }}
                    <span class="text-[11px] font-normal text-inkFaint">
                      <template v-if="activePeer.username">{{ activePeer.username }} · </template>UID #{{ activePeer.memberId }}
                    </span>
                  </span>
                  <span class="flex shrink-0 items-center gap-3">
                    <!-- 清空聊天记录：单边抹掉自己这侧的账（微信式），对方账本不动；二次确认才落刀 -->
                    <button
                      v-if="conversation.length"
                      class="flex items-center gap-1 text-xs text-inkFaint transition hover:text-stationPink"
                      title="你这边清空，对方不受影响"
                      @click="clearThread"
                    >
                      <svg viewBox="0 0 24 24" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="1.8">
                        <path d="M4 7h16M9 7V5a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2m-8 0 1 13h8l1-13" stroke-linecap="round" stroke-linejoin="round" />
                      </svg>
                      清空
                    </button>
                    <button class="text-xs text-inkFaint transition hover:text-stationPink" @click="closeThread">收起</button>
                  </span>
                </div>

                <div class="min-h-0 flex-1 space-y-2.5 overflow-y-auto bg-[#f8f9fa] dark:bg-[#23262e] px-4 py-4">
                  <p v-if="convoLoading && !conversation.length" class="py-8 text-center text-sm text-inkFaint">正在翻记录…</p>
                  <div v-else-if="!conversation.length" class="flex flex-col items-center justify-center gap-2.5 py-12 text-center">
                    <span class="flex h-11 w-11 items-center justify-center rounded-full bg-stationPink/10">
                      <svg viewBox="0 0 24 24" class="h-5 w-5 text-stationPink/70" fill="none" stroke="currentColor" stroke-width="1.8">
                        <path d="M21 12a8 8 0 0 1-8 8c-1.2 0-2.3-.2-3.3-.6L5 21l1.2-3.4A8 8 0 1 1 21 12Z" stroke-linecap="round" stroke-linejoin="round" />
                      </svg>
                    </span>
                    <p class="text-sm text-inkFaint">还没说过话，第一句由你来。</p>
                  </div>
                  <div
                    v-for="m in conversation"
                    :key="m.id"
                    class="group flex items-center"
                    :class="m.fromMemberId === member.memberId ? 'justify-end' : 'justify-start'"
                  >
                    <!-- 自己发的：悬停浮出撤回（真删，双方同灭）；平时隐形不扰阅读 -->
                    <button
                      v-if="m.fromMemberId === member.memberId"
                      class="mr-1.5 shrink-0 invisible flex h-6 w-6 items-center justify-center rounded-full text-inkFaint transition hover:bg-black/5 hover:text-stationPink group-hover:visible"
                      title="撤回"
                      @click="recallMessage(m)"
                    >
                      <svg viewBox="0 0 24 24" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="1.8">
                        <path d="M4 7h16M9 7V5a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2m-8 0 1 13h8l1-13" stroke-linecap="round" stroke-linejoin="round" />
                      </svg>
                    </button>
                    <!-- 动态分享：C2 融合气泡（安利语/来源/内容/动作一泡装下，点泡走橱窗） -->
                    <ShareCard
                      v-if="m.msgType === 'dynamic' && m.dyn"
                      kind="dynamic"
                      :dyn="m.dyn"
                      :note="m.body || ''"
                      :mine="m.fromMemberId === member.memberId"
                      class="max-w-[78%] min-w-0"
                      @open="gotoSharedDynamic(m.dyn)"
                    />
                    <!-- 放映厅分享：同族同款 C2 融合气泡，点泡直落 /watch/bvid -->
                    <ShareCard
                      v-else-if="m.msgType === 'video' && m.dyn"
                      kind="video"
                      :dyn="m.dyn"
                      :note="m.body || ''"
                      :video-gone="!!m.videoGone"
                      :mine="m.fromMemberId === member.memberId"
                      class="max-w-[78%] min-w-0"
                      @open="gotoSharedVideo(m.dyn)"
                    />
                    <span
                      v-else
                      class="max-w-[72%] whitespace-pre-wrap break-words rounded-2xl px-3.5 py-2 text-[13px] leading-relaxed"
                      :class="m.fromMemberId === member.memberId
                        ? 'rounded-br-md bg-stationPink text-white'
                        : 'rounded-bl-md bg-lineSoft text-inkMain'"
                    >{{ m.body }}</span>
                  </div>
                  <div ref="scrollAnchor"></div>
                </div>

                <div class="border-t border-lineSoft bg-[#fafbfc] dark:bg-white/5 p-3">
                  <div class="flex items-end gap-2">
                    <textarea
                      v-model="draft"
                      rows="2"
                      maxlength="2000"
                      class="min-h-[52px] flex-1 resize-none rounded-xl border border-lineSoft bg-[#f6f7f8] dark:bg-[#23262e] dark:focus:bg-[#23262e] px-3.5 py-2 text-[13px] text-inkMain outline-none transition placeholder:text-inkFaint focus:border-stationPink focus:bg-white focus:ring-2 focus:ring-stationPink/20"
                      placeholder="说点什么…（Enter 发送，Shift+Enter 换行）"
                      @keydown="onDraftKeydown"
                    ></textarea>
                    <button
                      class="h-[52px] shrink-0 rounded-xl bg-gradient-to-r from-stationPink to-[#f994b6] px-5 text-sm font-medium text-white shadow-md shadow-stationPink/30 transition hover:-translate-y-px hover:shadow-lg hover:shadow-stationPink/40 active:translate-y-0 disabled:translate-y-0 disabled:cursor-not-allowed disabled:opacity-50 disabled:shadow-none"
                      :disabled="sending || !draft.trim()"
                      @click="sendDm"
                    >
                      {{ sending ? '发送中…' : '发送' }}
                    </button>
                  </div>
                </div>
              </template>

              <div v-else class="flex flex-1 flex-col items-center justify-center gap-3 px-6 text-center">
                <span class="flex h-14 w-14 items-center justify-center rounded-full bg-stationPink/10">
                  <svg viewBox="0 0 24 24" class="h-7 w-7 text-stationPink/70" fill="none" stroke="currentColor" stroke-width="1.8">
                    <path d="M21 12a8 8 0 0 1-8 8c-1.2 0-2.3-.2-3.3-.6L5 21l1.2-3.4A8 8 0 1 1 21 12Z" stroke-linecap="round" stroke-linejoin="round" />
                  </svg>
                </span>
                <p class="text-sm text-inkFaint">左边挑个会话，或「＋ 发起私信」认识位分站伙伴。</p>
              </div>
            </section>
          </div>

          <p v-if="dmToast" class="mt-3 rounded-lg bg-stationPink/10 px-3 py-1.5 text-center text-xs text-stationPink">{{ dmToast }}</p>

          <!-- 选人弹层 -->
          <template v-if="directoryOpen">
            <div class="fixed inset-0 z-30 bg-black/30" @click="directoryOpen = false"></div>
            <div class="fixed left-1/2 top-1/2 z-40 w-[340px] -translate-x-1/2 -translate-y-1/2 overflow-hidden rounded-2xl border border-lineSoft bg-cardBg shadow-[0_20px_60px_-12px_rgba(0,0,0,0.35)]">
              <div class="flex items-center justify-between border-b border-lineSoft bg-gradient-to-r from-stationPink/10 via-stationPink/5 to-transparent px-4 py-3">
                <span class="flex items-center gap-2.5 text-sm font-medium text-inkMain">
                  <span class="flex h-8 w-8 items-center justify-center rounded-full bg-stationPink/10">
                    <svg viewBox="0 0 24 24" class="h-4 w-4 text-stationPink" fill="none" stroke="currentColor" stroke-width="1.8">
                      <circle cx="10" cy="8.5" r="3.5" />
                      <path d="M3.5 19.5c1.3-3.1 3.8-4.4 6.5-4.4s5.2 1.3 6.5 4.4" stroke-linecap="round" />
                      <path d="M17 9h5M19.5 6.5v5" stroke-linecap="round" />
                    </svg>
                  </span>
                  发起私信
                </span>
                <!-- after 只撑热区（28→44）：盒子和 hover 圆底不变，外拓落在 header px-4/py-3 留白里 -->
                <button class="relative flex h-7 w-7 items-center justify-center rounded-full text-inkFaint transition hover:bg-black/5 hover:text-inkMain after:absolute after:-inset-[9px] after:content-['']" @click="directoryOpen = false">✕</button>
              </div>
              <div class="px-3 pb-3 pt-3">
                <PeerPicker mode="open" @pick="p => openThread(p.memberId, p)" />
              </div>
            </div>
          </template>
        </template>
      </template>
    </main>

    <!-- 通知设置浮窗（站长 11-03 点名仿顶栏铃铛预览卡）：w-80、rounded-xl、lineSoft 描边、同一段深阴影，
         点外即收（fixed inset-0 z-30 遮罩，头像菜单同款）。Teleport 到 body 而非 absolute：
         信匣 section 自带 overflow-hidden，信少时浮窗会被剪尾，视口坐标才稳。 -->
    <Teleport to="body">
      <template v-if="prefsOpen">
        <div class="fixed inset-0 z-30" @click="prefsOpen = false"></div>
        <div
          class="fixed z-40 w-80 overflow-hidden rounded-xl border border-lineSoft bg-cardBg shadow-[0_12px_32px_-8px_rgba(0,0,0,0.25)]"
          :style="prefsStyle"
        >
          <div class="border-b border-lineSoft/70 px-3.5 py-2">
            <span class="text-[12px] font-medium text-inkMain">通知设置</span>
          </div>
          <div v-if="prefsLoading" class="px-3.5 py-6 text-center text-[11.5px] text-inkFaint">正在翻通知账…</div>
          <ul v-else class="space-y-0.5 px-2 py-2">
            <li v-for="ch in MUTE_CHANNELS" :key="ch.key" class="flex items-center gap-2.5 rounded-lg px-2 py-2 transition hover:bg-stationPink/5">
              <span class="text-base">{{ ch.icon }}</span>
              <span class="min-w-0 flex-1 text-[12.5px] text-inkMain">{{ ch.label }}</span>
              <button
                role="switch"
                :aria-checked="String(!isMuted(ch.key))"
                :aria-label="`${ch.label}通知`"
                :disabled="prefsSaving"
                class="relative h-5 w-9 shrink-0 rounded-full transition disabled:opacity-50"
                :class="isMuted(ch.key) ? 'bg-lineSoft' : 'bg-stationPink'"
                @click="toggleMute(ch.key)"
              >
                <span
                  class="absolute top-0.5 h-4 w-4 rounded-full bg-white shadow transition-all"
                  :class="isMuted(ch.key) ? 'left-0.5' : 'left-[18px]'"
                />
              </button>
            </li>
          </ul>
        </div>
      </template>
    </Teleport>
  </div>
</template>
