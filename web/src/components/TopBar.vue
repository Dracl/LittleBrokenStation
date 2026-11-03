<script setup>
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import LoginDialog from './LoginDialog.vue'
import CrownBadge from './CrownBadge.vue'
import { defaultAvatar, onAvatarError } from '../utils/avatar.js'
import { useHoverGate } from '../utils/useHoverGate.js'
import { submitDialogOpen } from '../submitStore'
import { member, unread, loginDialogOpen, loginDialogMode, load as loadMember, logout as logoutMember, refreshUnread } from '../memberStore'

const route = useRoute()
const router = useRouter()
const searchText = ref(typeof route.query.q === 'string' ? route.query.q : '')
const menuOpen = ref(false)

// 结果页改词/回首页清词时，搜索框跟着页面走
watch(() => route.query.q, (v) => { searchText.value = typeof v === 'string' ? v : '' })
// 换页时收起头像菜单与信箱预览，不给导航完还悬着一张卡
watch(() => route.fullPath, () => {
  menuOpen.value = false
  menuFromHover = false
  previewOpen.value = false
})

function doSearch() {
  const term = searchText.value.trim()
  if (!term) return
  router.push({ path: '/search', query: { q: term } })
}

function openLogin(mode) {
  // 访客之前悬停头像可能把 menuOpen 悄悄置了真（那张卡当时没人可展），不摘掉的话登录完会凭空弹开
  menuOpen.value = false
  menuFromHover = false
  loginDialogMode.value = mode
  loginDialogOpen.value = true
}

// 投稿是成员待遇：访客先过扫码门，member 照常开投稿窗
function onSubmitClick() {
  if (member.value) submitDialogOpen.value = true
  else openLogin('admit')
}

// 头像就是总门：访客点开扫码窗，成员点开 B 站式下拉。
// 点开的那张卡不归鼠标管（手滑飘走不该把人家的菜单赶跑），只有悬停开的才由离开收。
function onAvatarClick() {
  if (!member.value) {
    openLogin('admit')
    return
  }

  avatarHover.cancel()
  menuFromHover = false
  menuOpen.value = !menuOpen.value
}

function go(path) {
  menuOpen.value = false
  menuFromHover = false
  router.push(path)
}

async function doLogout() {
  menuOpen.value = false
  menuFromHover = false
  await logoutMember()
}

function onVisible() {
  if (document.visibilityState === 'visible') {
    refreshUnread()
    previewAt = 0 // 回到前台算换了一轮：下次 hover 重新拉，别拿半小时前的旧快照糊人
  }
}

// M5.4 素净席位：没扫过 B 站就没有昵称，落 @站名；mid 也不再人人都有
const displayName = computed(() => {
  const m = member.value
  if (!m) return ''
  return m.nickname || m.username || '旅人'
})

function identityLine(m) {
  const role = m.role === 'owner' ? '站长' : '分站成员'
  return `${role} · UID #${m.memberId}`
}

let unreadTimer = null

onMounted(() => {
  loadMember()
  // 未读角标三把锁：登录时 load 顺手拉一次，回到前台立刻补拉，30s 心跳兜底。
  unreadTimer = setInterval(() => { refreshUnread() }, 30000)
  document.addEventListener('visibilitychange', onVisible)
})

onUnmounted(() => {
  if (unreadTimer) clearInterval(unreadTimer)
  cancelMarkRead()
  document.removeEventListener('visibilitychange', onVisible)
})

// ─── 悬停门：铃铛预览与头像菜单共用一把尺子（门本身在 utils/useHoverGate.js，放映厅的封面浮窗同款）───
// 离 260ms 才收：斜着移进面板不掉。进档本处单顶到 320ms（站长 11-02 点名：120ms 跟即时弹出没差别）：
// 顶栏这两颗挂在页面最上沿，指针去哪儿都要从它们头上擦一下，过路鼠标不该惊动面板。
// 不用公共默认值是因为封面浮窗那颗不靠顶栏、不会误擦，它继续吃 useHoverGate 的 120ms。
const TOP_HOVER_OPEN_MS = 320

let menuFromHover = false
const avatarHover = useHoverGate(menuOpen, {
  onOpen: () => { menuFromHover = true },
  shouldClose: () => menuFromHover,
  openMs: TOP_HOVER_OPEN_MS,
})

// ─── 铃铛悬停预览：摊开停一会儿就算读过 ───
// 悬停开面板后由 dwell 计时落账（见 markPreviewRead）；没停够时间的不抹未读。
const previewOpen = ref(false)
const preview = ref(null) // { inbox: [], dm: [] }，null = 还没拉过
const previewLoading = ref(false)
const previewFailed = ref(false)
let previewAt = 0
const PREVIEW_TTL_MS = 30000

const bellHover = useHoverGate(previewOpen, { onOpen: () => { loadPreview() }, openMs: TOP_HOVER_OPEN_MS })

// 未读合流数一动（读掉、新到、登出）就作废缓存，下次 hover 拿新的
watch(unread, () => { previewAt = 0 })
watch(member, (m) => {
  if (!m) {
    preview.value = null
    previewAt = 0
    previewOpen.value = false
  }
})

async function loadPreview(force = false) {
  if (!member.value) return
  const now = Date.now()
  if (!force && preview.value && now - previewAt < PREVIEW_TTL_MS) return
  if (previewLoading.value) return
  previewLoading.value = true
  previewFailed.value = false
  try {
    const [inboxRes, dmRes] = await Promise.all([
      fetch('/api/member/messages?unreadOnly=true&limit=5'),
      fetch('/api/dm/threads'),
    ])
    if (!inboxRes.ok && !dmRes.ok) {
      previewFailed.value = true
      return
    }
    const inbox = inboxRes.ok ? ((await inboxRes.json()).items ?? []) : []
    const threads = dmRes.ok ? ((await dmRes.json()).threads ?? []) : []
    // 两本账都只摊未读：预览答的是「有没有新东西」，不是把信箱重播一遍
    preview.value = {
      inbox: inbox.slice(0, 5),
      dm: threads.filter((t) => (t.unread || 0) > 0).slice(0, 5),
    }
    previewAt = now
  } catch {
    previewFailed.value = true
  } finally {
    previewLoading.value = false
  }
}

// 预览过就算看过：但得真摊在眼底下停过 500ms 才认账。
// 横穿的鼠标连 120ms 那道开门闸都过不了，更不能让它替你承认「看过」；面板刚开就跑路的也撤这一发。
const MARK_READ_DWELL_MS = 500
let markTimer = null
let markingRead = false

function cancelMarkRead() {
  if (markTimer) { clearTimeout(markTimer); markTimer = null }
}

async function markPreviewRead() {
  const snap = preview.value
  if (!snap || markingRead) return
  const inboxIds = snap.inbox.filter((m) => !m.readAtUtc).map((m) => m.id)
  const peers = snap.dm.filter((t) => (t.unread || 0) > 0).map((t) => t.peerMemberId)
  if (!inboxIds.length && !peers.length) return
  markingRead = true
  try {
    await Promise.all([
      // 信箱逐封标：只读预览里真摊开的那几封，没递到眼前的不顺手抹平
      ...inboxIds.map((id) => fetch(`/api/member/messages/${id}/read`, { method: 'POST' }).catch(() => null)),
      // 私信没有独立标读口：GET 会话本身就是标读（与点开对话同一动作），照这条走
      ...peers.map((p) => fetch(`/api/dm/thread/${p}`).catch(() => null)),
    ])
    snap.inbox.forEach((m) => { if (!m.readAtUtc) m.readAtUtc = new Date().toISOString() })
    snap.dm.forEach((t) => { t.unread = 0 })
    previewAt = 0 // 这本账已翻篇：下次 hover 重拉，别把刚读掉的再端一遍
    refreshUnread()
  } finally {
    markingRead = false
  }
}

// 面板摊开且有东西可看才开始计时；收起来就撤表
watch([previewOpen, preview], ([open]) => {
  if (!open || !preview.value) {
    cancelMarkRead()
    return
  }
  const snap = preview.value
  const hasUnread = snap.inbox.some((m) => !m.readAtUtc) || snap.dm.some((t) => (t.unread || 0) > 0)
  if (!hasUnread) {
    cancelMarkRead()
    return
  }
  cancelMarkRead()
  markTimer = setTimeout(() => { markTimer = null; markPreviewRead() }, MARK_READ_DWELL_MS)
})

function onBellLeave() {
  cancelMarkRead() // 刚开就溜：没看过就是没看过
  bellHover.onLeave()
}

// 点条目/查看全部：人已经在处置这批未读了，不用等计时走完，当场落账
function commitPreviewRead() {
  cancelMarkRead()
  markPreviewRead()
}

function openPreviewItem(target) {
  previewOpen.value = false
  menuFromHover = false
  commitPreviewRead()
  if (target.type === 'dm') router.push({ path: '/messages', query: { dm: target.peerMemberId } })
  else router.push({ path: '/messages', query: { msg: target.id } })
}

function openAllMessages() {
  previewOpen.value = false
  menuFromHover = false
  commitPreviewRead()
  router.push('/messages')
}

const KIND_META = {
  welcome: { label: '欢迎', cls: 'bg-stationPink/10 text-stationPink' },
  complete: { label: '入档', cls: 'bg-emerald-500/10 text-emerald-600' },
  fail: { label: '趔趄', cls: 'bg-red-500/10 text-red-500' },
  announce: { label: '公告', cls: 'bg-amber-400/15 text-amber-600' },
  arrival: { label: '到货', cls: 'bg-stationPink/10 text-stationPink' },
}

function kindMeta(kind) {
  return KIND_META[kind] ?? { label: kind || '信', cls: 'bg-lineSoft text-inkSub' }
}

// 到货信的 body 是「一件一行」的兜底清单，塞进预览那一行就成了字堆（站长 11-03「不要一堆字混着」）：
// 预览改报「首件标题 等 N 件」，完整货单点开信箱看卡面。
function previewTail(m) {
  if (m.kind === 'arrival') {
    const lines = String(m.body || '').split('\n').filter(Boolean)
    if (!lines.length) return ''
    const first = lines[0].replace(/^\d+\.\s*/, '').replace(/\s*——.*$/, '')
    return lines.length > 1 ? ` · ${first} 等 ${lines.length} 件` : ` · ${first}`
  }
  return m.body ? ` · ${m.body}` : ''
}

// 与卡阵同款相对时间（无共享 util，各组件各留一份口径）
function relTime(v) {
  if (!v) return ''
  const d = new Date(v)
  if (Number.isNaN(d.getTime())) return ''
  const diff = Date.now() - d.getTime()
  if (diff < 60e3) return '刚刚'
  if (diff < 3600e3) return `${Math.floor(diff / 60e3)}分钟前`
  if (diff < 86400e3) return `${Math.floor(diff / 3600e3)}小时前`
  if (diff < 30 * 86400e3) return `${Math.floor(diff / 86400e3)}天前`
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

// 铃铛只对成员亮：访客连信箱的门都看不见，扫码按钮已经在旁边了
function goMessages() {
  previewOpen.value = false
  cancelMarkRead()
  router.push('/messages')
}
// 顶栏壳：版式按 B 站网页版对齐的大致轮廓，M2 对基准截图逐像素打磨
</script>

<template>
  <header class="h-10 bg-cardBg shadow-[0_1px_0_0_rgba(0,0,0,0.05)]">
    <div class="mx-auto flex h-full max-w-[1400px] items-center gap-5 px-6">
      <RouterLink to="/" class="flex shrink-0 items-center gap-2" aria-label="分站首页">
        <svg viewBox="0 0 64 64" class="h-7 w-7">
          <rect x="4" y="4" width="56" height="56" rx="14" fill="#fb7299" />
          <path d="M20 22 L28 14 M44 22 L36 14" stroke="#ffffff" stroke-width="4" stroke-linecap="round" />
          <rect x="14" y="22" width="36" height="26" rx="6" fill="#ffffff" />
          <rect x="19" y="27" width="26" height="16" rx="3" fill="#fb7299" />
        </svg>
        <span class="text-[15px] font-medium text-inkMain">小破分站</span>
      </RouterLink>

      <nav class="flex items-center gap-4 text-[13px] text-inkMain">
        <!-- 这三页是成员待遇（路由里有 guestBlocked 兼道守住）：入口藏起来只是别让访客去撞门 -->
        <RouterLink v-if="member" to="/dynamics" class="transition hover:text-stationPink" :class="route.path.startsWith('/dynamics') ? 'text-stationPink' : ''">动态</RouterLink>
        <RouterLink v-if="member" to="/zones" class="transition hover:text-stationPink" :class="route.path.startsWith('/zones') ? 'text-stationPink' : ''">分区</RouterLink>
        <!-- 周报入口：成员登录后才亮（周报数据本身就是成员待遇，访客点了也吃 401，干脆不递钥匙） -->
        <RouterLink v-if="member" to="/weekly" class="transition hover:text-stationPink" :class="route.path.startsWith('/weekly') ? 'text-stationPink' : ''">周报</RouterLink>
        <RouterLink v-if="member" to="/collections" class="transition hover:text-stationPink" :class="route.path.startsWith('/collection') ? 'text-stationPink' : ''">合集</RouterLink>
      </nav>

      <div class="mx-auto flex h-8 w-full max-w-[420px] items-center gap-2 rounded-full bg-[#f1f2f3] dark:bg-[#23262e] px-3.5 transition focus-within:ring-2 focus-within:ring-stationPink/40">
        <button class="shrink-0 text-inkFaint transition hover:text-stationPink" title="搜索" @click="doSearch">
          <svg viewBox="0 0 24 24" class="h-4 w-4" fill="none" stroke="currentColor" stroke-width="2">
            <circle cx="11" cy="11" r="7" />
            <path d="m20 20-3.5-3.5" stroke-linecap="round" />
          </svg>
        </button>
        <input
          v-model="searchText"
          class="w-full bg-transparent text-[13px] outline-none placeholder:text-inkFaint"
          placeholder="搜索小破分站视频"
          @keyup.enter="doSearch"
        />
      </div>

      <!-- 右簇间距 gap-5（20px，站长 11-02 定）：三颗都是 32px 的圆 hover 底，原先 gap-3 只剩 12px，
           两个 hover 圆看上去快粘在一起，指针斜着走很容易擦错颗；头像那枚右上角还挂个皇冠外沿，多留点不吃亏。 -->
      <div class="flex shrink-0 items-center gap-5">
        <!-- 投稿钮：最细一档——裸图标，与旁边那枚信箱铃铛同一个写法（站长 11-02 定）。
             走过的两版：分站自绘的粉描边胶囊（他嫌突兀），再到整枚照 B 站 laputa-home 那枚 .upload-entry__trigger 做的实心粉块（实机还是嫌抢）。
             图标也换过一次：laputa-home 包（index-84fb5632.js 里的 UploadIcon）那是描边款——托盘加开口箭头（viewBox 0 0 18 18、
             stroke currentColor、宽 1.7、三段 path：M12.0824 10H14.1412… / M8.99413 11.2353L8.99413 3.82353 / M12.0823 6.29413L8.9941 3.20589L5.90587 6.29413），
             登录与访客两个分支渲染的是同一个。而站长 11-02 截图里他页面上那枚是实心箭头加一横杠——与首页包不同版（laputa-home 是
             无 cookie 拉到的那版），所以下面这两笔是按他截图手绘的实心款，拿到他页面上那枚的 <svg> 原本就替成 1:1。 -->
        <button
          class="relative flex h-8 w-8 items-center justify-center rounded-full text-inkSub transition hover:bg-stationPink/10 hover:text-stationPink"
          title="投稿"
          aria-label="投稿"
          @click="onSubmitClick"
        >
          <svg viewBox="0 0 24 24" class="h-[18px] w-[18px]" fill="currentColor">
            <path d="M12 3.1 19.6 10.7h-4.4v5.7H8.8v-5.7H4.4Z" />
            <rect x="5.6" y="18.1" width="12.8" height="2.9" rx="1.45" />
          </svg>
        </button>

        <!-- 站内信箱铃铛：成员可见，未读>0 亮红点角标，爆 99 封显 99+。
             鼠标停上去摊开未读快照（两本账各前 5，一个 read 都不打），点条目才进 /messages 真读。
             常态不带描边（站长 11-02 试过加一圈 lineSoft，跟旁边不像一家、反而更怪，当日驳回）：
             它靠 hover 才出淡粉底，与头像那颗裸圆同族。 -->
        <div
          v-if="member"
          class="relative"
          @mouseenter="bellHover.onEnter"
          @mouseleave="onBellLeave"
        >
          <button
            class="relative flex h-8 w-8 items-center justify-center rounded-full text-inkSub transition hover:bg-stationPink/10 hover:text-stationPink"
            title="站内信箱"
            aria-label="站内信箱"
            @click="goMessages"
          >
            <svg viewBox="0 0 24 24" class="h-[18px] w-[18px]" fill="none" stroke="currentColor" stroke-width="1.8">
              <path d="M6 9.5a6 6 0 0 1 12 0c0 3.2.8 5.2 1.8 6.4.4.5.1 1.3-.6 1.3H4.8c-.7 0-1-.8-.6-1.3C5.2 14.7 6 12.7 6 9.5Z" stroke-linejoin="round" />
              <path d="M10 20a2.2 2.2 0 0 0 4 0" stroke-linecap="round" />
            </svg>
            <span
              v-if="unread > 0"
              class="absolute -right-0.5 -top-0.5 flex h-4 min-w-4 items-center justify-center rounded-full bg-stationPink px-1 text-[10px] font-medium leading-none text-white"
            >{{ unread > 99 ? '99+' : unread }}</span>
          </button>

          <!-- 悬停预览卡：热区含按钮本体与面板（中间那 6px 缝隙由 260ms 收尾补上） -->
          <div
            v-if="previewOpen"
            class="absolute right-0 top-full z-40 mt-1.5 w-80 overflow-hidden rounded-xl border border-lineSoft bg-cardBg shadow-[0_12px_32px_-8px_rgba(0,0,0,0.25)]"
          >
            <div class="flex items-baseline justify-between border-b border-lineSoft/70 px-3.5 py-2">
              <span class="text-[12px] font-medium text-inkMain">信箱</span>
            </div>

            <div v-if="previewLoading && !preview" class="px-3.5 py-6 text-center text-[11.5px] text-inkFaint">正在翻两本账…</div>
            <div v-else-if="previewFailed && !preview" class="px-3.5 py-6 text-center text-[11.5px] text-inkFaint">预览没拉出来，点开信箱再看</div>
            <div v-else-if="!preview" class="px-3.5 py-6 text-center text-[11.5px] text-inkFaint">暂时没有新消息</div>
            <template v-else>
              <div v-if="!preview.inbox.length && !preview.dm.length" class="px-3.5 py-6 text-center text-[11.5px] text-inkFaint">暂时没有新消息</div>
              <template v-else>
                <div v-if="preview.inbox.length" class="p-1.5">
                  <p class="px-2 pb-1 pt-0.5 text-[10px] text-inkFaint">站内信</p>
                  <button
                    v-for="m in preview.inbox"
                    :key="`inbox-${m.id}`"
                    class="flex w-full items-start gap-2 rounded-lg px-2 py-1.5 text-left transition hover:bg-stationPink/10"
                    @click="openPreviewItem({ type: 'inbox', id: m.id })"
                  >
                    <span class="mt-0.5 shrink-0 rounded px-1 py-0.5 text-[10px] leading-none" :class="kindMeta(m.kind).cls">{{ kindMeta(m.kind).label }}</span>
                    <span class="min-w-0 flex-1">
                      <span class="block truncate text-[12px] text-inkMain">{{ m.title || '一封没标题的信' }}</span>
                      <span class="block truncate text-[11px] text-inkFaint">{{ m.fromName || '分站' }}{{ previewTail(m) }}</span>
                    </span>
                    <span class="shrink-0 pt-0.5 text-[10px] text-inkFaint">{{ relTime(m.createdAtUtc) }}</span>
                  </button>
                </div>
                <div v-if="preview.dm.length" class="p-1.5" :class="preview.inbox.length ? 'border-t border-lineSoft/70' : ''">
                  <p class="px-2 pb-1 pt-0.5 text-[10px] text-inkFaint">私信</p>
                  <button
                    v-for="t in preview.dm"
                    :key="`dm-${t.peerMemberId}`"
                    class="flex w-full items-center gap-2 rounded-lg px-2 py-1.5 text-left transition hover:bg-stationPink/10"
                    @click="openPreviewItem({ type: 'dm', peerMemberId: t.peerMemberId })"
                  >
                    <img
                      :src="t.peerFace || defaultAvatar"
                      :alt="t.peerName || '成员'"
                      referrerpolicy="no-referrer"
                      class="h-6 w-6 shrink-0 rounded-full object-cover"
                      @error="onAvatarError"
                    />
                    <span class="min-w-0 flex-1">
                      <span class="block truncate text-[12px] text-inkMain">{{ t.peerName || `席位 ${t.peerMemberId}` }}</span>
                      <span class="block truncate text-[11px] text-inkFaint">{{ t.lastBody || '（这句没文字）' }}</span>
                    </span>
                    <span class="flex shrink-0 flex-col items-end gap-0.5">
                      <span class="text-[10px] text-inkFaint">{{ relTime(t.lastAtUtc) }}</span>
                      <span class="flex h-4 min-w-4 items-center justify-center rounded-full bg-stationPink px-1 text-[10px] leading-none text-white">{{ t.unread > 99 ? '99+' : t.unread }}</span>
                    </span>
                  </button>
                </div>
              </template>
            </template>

            <button
              class="block w-full border-t border-lineSoft/70 px-3.5 py-2 text-center text-[11.5px] text-inkSub transition hover:bg-stationPink/5 hover:text-stationPink"
              @click="openAllMessages"
            >
              查看全部 →
            </button>
          </div>
        </div>

        <!-- 总门小头像：不分访客/成员都是这个圆——访客点开登录窗（内含注册入口），成员点开下拉菜单 -->
        <!-- 悬停热区只罩头像本身与那张卡，不挂在包裹层上：
             点外遮罩是 fixed inset-0 的子孙，挂在包裹层上指针永远停在子孙里，mouseleave 永不响，悬停开的卡会赖着不走。 -->
        <div class="relative">
          <!-- 头像必须抬到遮罩之上（z-40 > 遮罩的 z-30）：否则菜单一开，fixed inset-0 的遮罩就盖在头像上，
               指针底下的元素从按钮变成遮罩 → mouseleave → 260ms 后关 → 遮罩拆掉 → 指针又落到按钮上 → mouseenter → 开，
               一进一出就是一直闪。卡片本来就是 z-40，按钮跟上同层。 -->
          <button
            class="relative z-40 block h-8 w-8 shrink-0 overflow-visible rounded-full transition"
            :title="member ? (member.role === 'owner' ? `站长「${displayName}」` : `「${displayName}」`) : '登录 / 注册'"
            @mouseenter="avatarHover.onEnter"
            @mouseleave="avatarHover.onLeave"
            @click="onAvatarClick"
          >
            <span class="relative block h-full w-full">
              <img
                :src="member?.faceUrl || defaultAvatar"
                :alt="member ? member.nickname : '未登录'"
                referrerpolicy="no-referrer"
                class="h-full w-full rounded-full object-cover"
                :class="menuOpen ? 'ring-2 ring-stationPink/60' : 'ring-1 ring-transparent hover:ring-stationPink/40'"
                @error="onAvatarError"
              />
              <!-- 冠冕角标：站长金冠、成员银冠，同形不同色；贴着一颗圆的弧走 -->
              <CrownBadge
                v-if="member"
                :tier="member.role"
                mode="fit"
                class="pointer-events-none absolute inset-0 h-full w-full"
              />
            </span>
          </button>

          <!-- B 站式个人卡：名片头 + 菜单行，退出登录垫底整条灰底 -->
          <template v-if="member && menuOpen">
            <div class="fixed inset-0 z-30" @click="menuOpen = false"></div>
            <div
              class="absolute right-0 top-full z-40 mt-1.5 w-52 overflow-hidden rounded-xl border border-lineSoft bg-cardBg shadow-[0_12px_32px_-8px_rgba(0,0,0,0.25)]"
              @mouseenter="avatarHover.onEnter"
              @mouseleave="avatarHover.onLeave"
            >
              <div class="flex items-center gap-2.5 px-3.5 pb-2 pt-3">
                <span class="relative block h-10 w-10 shrink-0">
                  <img
                    :src="member.faceUrl || defaultAvatar"
                    :alt="member.nickname"
                    referrerpolicy="no-referrer"
                    class="h-full w-full rounded-full object-cover"
                    @error="onAvatarError"
                  />
                  <CrownBadge
                    v-if="member"
                    :tier="member.role"
                    mode="fit"
                    class="pointer-events-none absolute inset-0 h-full w-full"
                  />
                </span>
                <span class="min-w-0">
                  <span class="block truncate text-[13px] font-medium text-inkMain">{{ displayName }}</span>
                  <span class="block text-[11px] text-inkFaint">{{ identityLine(member) }}</span>
                </span>
              </div>

              <div class="border-t border-lineSoft/70 p-1.5">
                <button
                  v-if="member.role === 'owner'"
                  class="flex w-full items-center gap-2.5 rounded-lg px-2.5 py-2 text-left text-xs text-inkSub transition hover:bg-stationPink/10 hover:text-stationPink"
                  @click="go('/admin')"
                >
                  <svg viewBox="0 0 24 24" class="h-4 w-4 shrink-0" fill="none" stroke="currentColor" stroke-width="1.8">
                    <path d="M3 17.5h18L22.5 8l-5.7 4L12 3.5 7.2 12 1.5 8 3 17.5Z" stroke-linejoin="round" />
                  </svg>
                  站长室
                </button>
                <button
                  class="flex w-full items-center gap-2.5 rounded-lg px-2.5 py-2 text-left text-xs text-inkSub transition hover:bg-stationPink/10 hover:text-stationPink"
                  @click="go('/me')"
                >
                  <svg viewBox="0 0 24 24" class="h-4 w-4 shrink-0" fill="none" stroke="currentColor" stroke-width="1.8">
                    <circle cx="12" cy="8.5" r="3.5" />
                    <path d="M5 19.5c1.4-3.2 4-4.5 7-4.5s5.6 1.3 7 4.5" stroke-linecap="round" />
                  </svg>
                  个人空间
                </button>
                <button
                  class="flex w-full items-center gap-2.5 rounded-lg px-2.5 py-2 text-left text-xs text-inkSub transition hover:bg-stationPink/10 hover:text-stationPink"
                  @click="go('/history')"
                >
                  <svg viewBox="0 0 24 24" class="h-4 w-4 shrink-0" fill="none" stroke="currentColor" stroke-width="1.8">
                    <circle cx="12" cy="12" r="8.5" />
                    <path d="M12 7.5V12l3 2" stroke-linecap="round" />
                  </svg>
                  历史记录
                </button>
              </div>

              <div class="border-t border-lineSoft/70 p-1.5">
                <button
                  class="w-full rounded-lg bg-[#f1f2f3] dark:bg-[#23262e] px-2.5 py-2 text-center text-xs text-inkSub transition hover:bg-lineSoft hover:text-inkMain"
                  @click="doLogout"
                >
                  退出登录
                </button>
              </div>
            </div>
          </template>
        </div>
      </div>
    </div>
  </header>
  <!-- admit 实例：全站顶栏页共用的扫码进站窗（bind 实例由个人空间自己挂） -->
  <LoginDialog />
</template>
