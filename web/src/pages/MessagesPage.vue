<script setup>
import { computed, nextTick, onMounted, onUnmounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import TopBar from '../components/TopBar.vue'
import { member, loaded, unread, dmUnread, loginDialogOpen, loginDialogMode, load as loadMember, refreshUnread } from '../memberStore'
import { friendlyTime } from '../utils/time'

const route = useRoute()

// ─── 页签 ───
const activeTab = ref('inbox') // inbox | dm

// ─── 信箱（M5.2 原样）───
const items = ref([])
const loading = ref(true)
const inboxUnread = ref(0)
const expandedId = ref(null)
const notice = ref(null)

const kindMeta = {
  welcome: { label: '欢迎', cls: 'bg-stationPink/10 text-stationPink' },
  complete: { label: '入档', cls: 'bg-emerald-500/10 text-emerald-600' },
  fail: { label: '趔趄', cls: 'bg-red-500/10 text-red-500' },
  announce: { label: '公告', cls: 'bg-amber-400/15 text-amber-600' },
}

function meta(kind) {
  return kindMeta[kind] ?? { label: kind || '信', cls: 'bg-lineSoft text-inkSub' }
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
  expandedId.value = expandedId.value === item.id ? null : item.id
  if (expandedId.value !== item.id || item.readAtUtc) return
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
const directory = ref(null)
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

function closeThread() {
  activePeer.value = null
  conversation.value = []
  loadThreads()
  restartDmPoll() // 不彻底停：退出会话后列表继续慢拍，新私信不至于要重开页面才现身
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

async function openDirectory() {
  directoryOpen.value = true
  try {
    const res = await fetch('/api/dm/directory')
    directory.value = res.ok ? (await res.json()).people ?? [] : []
  } catch {
    directory.value = []
  }
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
  // 站长室「发私信」跳转：/messages?dm={memberId} 预开会话（M5.4 起锁席位 id）
  const dmPeer = typeof route.query.dm === 'string' ? Number(route.query.dm) : null
  if (dmPeer && Number.isFinite(dmPeer)) {
    switchTab('dm')
    openThread(dmPeer)
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
      <h1 class="mb-3 text-lg font-medium text-inkMain">站内信箱</h1>

      <!-- 访客：信箱与私信都是成员待遇，指路扫码 -->
      <section v-if="loaded && !member" class="rounded-2xl border border-lineSoft bg-cardBg px-6 py-14 text-center">
        <svg viewBox="0 0 64 48" class="mx-auto mb-4 h-14 w-16 text-lineSoft" fill="none" stroke="currentColor" stroke-width="2.4">
          <rect x="3" y="5" width="58" height="38" rx="6" />
          <path d="M5 9 32 29 59 9" stroke-linejoin="round" />
        </svg>
        <p class="text-sm text-inkSub">信箱和私信是分站成员的待遇——进站，第一封欢迎信就在等你。</p>
        <button
          class="mt-5 rounded-full bg-stationPink px-6 py-2 text-sm text-white transition hover:bg-stationPink/90"
          @click="loginDialogMode = 'admit'; loginDialogOpen = true"
        >
          直接登录
        </button>
        <RouterLink to="/register" class="mt-3 block text-xs text-inkSub underline-offset-4 transition hover:text-stationPink hover:underline">还没有站籍？注册去 →</RouterLink>
      </section>

      <template v-else-if="member">
        <!-- 双页签：朴素下划线式，激活站粉 -->
        <div class="mb-4 flex items-center gap-6 border-b border-lineSoft">
          <button
            class="-mb-px border-b-2 pb-2 text-sm transition"
            :class="activeTab === 'inbox' ? 'border-stationPink font-medium text-stationPink' : 'border-transparent text-inkSub hover:text-inkMain'"
            @click="switchTab('inbox')"
          >
            信箱<span v-if="inboxUnread > 0" class="ml-1 text-[11px] text-stationPink">({{ inboxUnread }})</span>
          </button>
          <button
            class="relative -mb-px border-b-2 pb-2 text-sm transition"
            :class="activeTab === 'dm' ? 'border-stationPink font-medium text-stationPink' : 'border-transparent text-inkSub hover:text-inkMain'"
            @click="switchTab('dm')"
          >
            私信
            <span
              v-if="dmUnread > 0"
              class="absolute -right-3 -top-0.5 flex h-4 min-w-4 items-center justify-center rounded-full bg-stationPink px-1 text-[10px] font-medium leading-none text-white"
            >{{ dmUnread > 99 ? '99+' : dmUnread }}</span>
          </button>
        </div>

        <!-- ═══ 信箱页签 ═══ -->
        <template v-if="activeTab === 'inbox'">
          <section class="overflow-hidden rounded-2xl border border-lineSoft bg-cardBg">
            <div class="flex items-center justify-between border-b border-lineSoft px-5 py-3">
              <span class="text-[13px] text-inkSub">
                {{ items.length ? `${items.length} 封信 · ${inboxUnread} 封未读` : '信笺都在这' }}
              </span>
              <button
                v-if="inboxUnread > 0"
                class="rounded-full border border-lineSoft px-3 py-1 text-xs text-inkSub transition hover:border-stationPink hover:text-stationPink"
                @click="markAll"
              >
                全部已读
              </button>
            </div>

            <p v-if="loading" class="px-5 py-10 text-center text-sm text-inkFaint">正在翻信箱…</p>

            <div v-else-if="!items.length" class="px-6 py-14 text-center">
              <svg viewBox="0 0 64 48" class="mx-auto mb-4 h-14 w-16 text-lineSoft" fill="none" stroke="currentColor" stroke-width="2.4">
                <rect x="3" y="5" width="58" height="38" rx="6" />
                <path d="M5 9 32 29 59 9" stroke-linejoin="round" />
                <path d="M44 34c2.5-3 6-3.4 9-1" stroke-linecap="round" opacity="0.55" />
              </svg>
              <p class="text-sm text-inkFaint">邮箱空空，岁月静好。投的稿子入档时，这里会亮起来。</p>
            </div>

            <ul v-else>
              <li v-for="item in items" :key="item.id" class="border-b border-lineSoft/60 last:border-b-0">
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

                <div v-if="expandedId === item.id" class="px-5 pb-4">
                  <div class="rounded-xl bg-[#f6f7f8] px-4 py-3">
                    <p class="whitespace-pre-wrap text-[13px] leading-relaxed text-inkMain">{{ item.body }}</p>
                    <RouterLink
                      v-if="item.link"
                      :to="item.link"
                      class="mt-2.5 inline-block text-xs text-stationPink transition hover:underline"
                    >去看 →</RouterLink>
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
            <!-- 左：会话列表 -->
            <section class="w-[290px] shrink-0 overflow-hidden rounded-2xl border border-lineSoft bg-cardBg">
              <div class="flex items-center justify-between border-b border-lineSoft px-4 py-3">
                <span class="text-[13px] text-inkSub">会话</span>
                <button
                  class="rounded-full border border-lineSoft px-2.5 py-0.5 text-xs text-inkSub transition hover:border-stationPink hover:text-stationPink"
                  @click="openDirectory"
                >
                  ＋ 发起私聊
                </button>
              </div>

              <p v-if="threads === null" class="px-4 py-8 text-center text-sm text-inkFaint">正在翻记录…</p>
              <div v-else-if="!threads.length" class="px-4 py-10 text-center">
                <p class="text-sm text-inkFaint">还没有人来敲门。<br />点「发起私聊」找位分站伙伴聊聊。</p>
              </div>
              <ul v-else>
                <li v-for="t in threads" :key="t.peerMemberId">
                  <button
                    class="flex w-full items-center gap-2.5 border-b border-lineSoft/60 px-4 py-3 text-left transition hover:bg-stationPink/5"
                    :class="peerId === t.peerMemberId ? 'bg-stationPink/5' : ''"
                    @click="openThread(t.peerMemberId, { memberId: t.peerMemberId, nickname: t.peerName, faceUrl: t.peerFace })"
                  >
                    <img
                      v-if="t.peerFace"
                      :src="t.peerFace"
                      :alt="t.peerName || ''"
                      referrerpolicy="no-referrer"
                      class="h-10 w-10 shrink-0 rounded-full object-cover"
                    />
                    <span v-else class="flex h-10 w-10 shrink-0 items-center justify-center rounded-full bg-lineSoft text-inkFaint">
                      <svg viewBox="0 0 24 24" class="h-5 w-5" fill="none" stroke="currentColor" stroke-width="1.8">
                        <circle cx="12" cy="8.5" r="3.5" />
                        <path d="M5 19.5c1.4-3.2 4-4.5 7-4.5s5.6 1.3 7 4.5" stroke-linecap="round" />
                      </svg>
                    </span>
                    <span class="min-w-0 flex-1">
                      <span class="block truncate text-[13px] font-medium text-inkMain">{{ peerLabel(t) }}</span>
                      <span class="block truncate text-[12px] text-inkSub">{{ t.lastBody }}</span>
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

            <!-- 右：聊天面板 -->
            <section class="flex min-h-[420px] min-w-0 flex-1 flex-col overflow-hidden rounded-2xl border border-lineSoft bg-cardBg">
              <template v-if="activePeer">
                <div class="flex items-center justify-between border-b border-lineSoft px-4 py-3">
                  <span class="flex items-center gap-2 text-[13px] font-medium text-inkMain">
                    {{ peerLabel(activePeer) }}
                    <span class="text-[11px] font-normal text-inkFaint">
                      <template v-if="activePeer.username">{{ activePeer.username }} · </template>UID #{{ activePeer.memberId }}
                    </span>
                  </span>
                  <button class="text-xs text-inkFaint transition hover:text-stationPink" @click="closeThread">收起</button>
                </div>

                <div class="flex-1 space-y-2.5 overflow-y-auto bg-[#f8f9fa] px-4 py-4" style="max-height: 480px">
                  <p v-if="convoLoading && !conversation.length" class="py-8 text-center text-sm text-inkFaint">正在翻记录…</p>
                  <p v-else-if="!conversation.length" class="py-8 text-center text-sm text-inkFaint">还没说过话，第一句由你来。</p>
                  <div
                    v-for="m in conversation"
                    :key="m.id"
                    class="flex"
                    :class="m.fromMemberId === member.memberId ? 'justify-end' : 'justify-start'"
                  >
                    <span
                      class="max-w-[72%] whitespace-pre-wrap break-words rounded-2xl px-3.5 py-2 text-[13px] leading-relaxed"
                      :class="m.fromMemberId === member.memberId
                        ? 'rounded-br-md bg-stationPink text-white'
                        : 'rounded-bl-md bg-lineSoft text-inkMain'"
                    >{{ m.body }}</span>
                  </div>
                  <div ref="scrollAnchor"></div>
                </div>

                <div class="border-t border-lineSoft p-3">
                  <div class="flex items-end gap-2">
                    <textarea
                      v-model="draft"
                      rows="2"
                      maxlength="2000"
                      class="min-h-[52px] flex-1 resize-none rounded-xl border border-lineSoft bg-pageBg px-3 py-2 text-[13px] outline-none transition placeholder:text-inkFaint focus:border-stationPink/60"
                      placeholder="说点什么…（Enter 发送，Shift+Enter 换行）"
                      @keydown="onDraftKeydown"
                    ></textarea>
                    <button
                      class="h-[52px] shrink-0 rounded-xl bg-stationPink px-5 text-sm text-white transition hover:bg-stationPink/90 disabled:cursor-not-allowed disabled:opacity-50"
                      :disabled="sending || !draft.trim()"
                      @click="sendDm"
                    >
                      {{ sending ? '发送中…' : '发送' }}
                    </button>
                  </div>
                </div>
              </template>

              <div v-else class="flex flex-1 items-center justify-center px-6 text-center">
                <p class="text-sm text-inkFaint">左边挑个会话，或「＋ 发起私聊」认识位分站伙伴。</p>
              </div>
            </section>
          </div>

          <p v-if="dmToast" class="mt-3 rounded-lg bg-stationPink/10 px-3 py-1.5 text-center text-xs text-stationPink">{{ dmToast }}</p>

          <!-- 选人弹层 -->
          <template v-if="directoryOpen">
            <div class="fixed inset-0 z-30 bg-black/30" @click="directoryOpen = false"></div>
            <div class="fixed left-1/2 top-1/2 z-40 w-[340px] -translate-x-1/2 -translate-y-1/2 overflow-hidden rounded-2xl border border-lineSoft bg-cardBg shadow-[0_20px_60px_-12px_rgba(0,0,0,0.35)]">
              <div class="flex items-center justify-between border-b border-lineSoft px-4 py-3">
                <span class="text-sm font-medium text-inkMain">发起私聊</span>
                <button class="text-inkFaint transition hover:text-inkMain" @click="directoryOpen = false">✕</button>
              </div>
              <p v-if="directory === null" class="px-4 py-8 text-center text-sm text-inkFaint">在点名…</p>
              <div v-else-if="!directory.length" class="px-4 py-8 text-center text-sm text-inkFaint">站上没别人可聊。</div>
              <ul v-else class="max-h-[320px] overflow-y-auto p-1.5">
                <li v-for="p in directory" :key="p.memberId">
                  <button
                    class="flex w-full items-center gap-3 rounded-xl px-2.5 py-2 text-left transition hover:bg-stationPink/10"
                    @click="openThread(p.memberId, { memberId: p.memberId, nickname: p.nickname, username: p.username, displayName: p.displayName, faceUrl: p.faceUrl })"
                  >
                    <img
                      v-if="p.faceUrl"
                      :src="p.faceUrl"
                      :alt="p.nickname || p.username || ''"
                      referrerpolicy="no-referrer"
                      class="h-9 w-9 shrink-0 rounded-full object-cover"
                    />
                    <span v-else class="flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-lineSoft text-inkFaint">
                      <svg viewBox="0 0 24 24" class="h-4.5 w-4.5" fill="none" stroke="currentColor" stroke-width="1.8">
                        <circle cx="12" cy="8.5" r="3.5" />
                        <path d="M5 19.5c1.4-3.2 4-4.5 7-4.5s5.6 1.3 7 4.5" stroke-linecap="round" />
                      </svg>
                    </span>
                    <span class="min-w-0 flex-1">
                      <span class="block truncate text-[13px] text-inkMain">{{ peerLabel(p) }}</span>
                      <span class="block text-[11px] text-inkFaint">
                        <template v-if="p.username">{{ p.username }} · </template>UID #{{ p.peerMemberId ?? p.memberId }}
                      </span>
                    </span>
                    <span class="shrink-0 rounded-full bg-stationPink/10 px-2.5 py-1 text-[11px] text-stationPink">开聊</span>
                  </button>
                </li>
              </ul>
            </div>
          </template>
        </template>
      </template>
    </main>
  </div>
</template>
