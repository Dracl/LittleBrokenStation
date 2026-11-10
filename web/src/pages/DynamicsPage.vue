<script setup>
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import TopBar from '../components/TopBar.vue'
import DynamicFeedCard from '../components/DynamicFeedCard.vue'
import { member } from '../memberStore'
import { defaultAvatar, onAvatarError } from '../utils/avatar.js'
import { faceFor } from '../utils/faceChoice.js'
import { keepFeed, tailCursor } from '../utils/dynFeed.js'

const route = useRoute()

const items = ref([])
const hasMore = ref(false)
const loading = ref(true)
const loadingMore = ref(false)
const targets = ref([])
const filterMid = ref(null) // null = 全部 UP
const activeTab = ref('all') // all | video
const toast = ref(null)
const roundBusy = ref(false)

const PAGE_SIZE = 20

// 翻页游标拿原始返回的尾巴（含被滤的开播条），不用过滤后的 items 尾巴：防整页开播时原地打转。
let feedCursor = null

// ─── 动态流代次护城（SearchPage 的 searchSeq 同族）：首屏、筛选、翻页共抽一个票池。───
// 每个请求出发时捕获序号 + 路由 epoch，await fetch 与 await json 之后都要过闸；
// 旧请求不得更新任何数据，也不得碰 loading / loadingMore：旧筛选的迟到包曾盖掉刚切完的列表，
// 游标也会拿着旧筛选的那份让「看更多」翻出混批。
let feedReq = 0
let feedEpoch = 0

function feedFresh(tok) {
  return tok.epoch === feedEpoch && tok.seq === feedReq
}

// 卡面全部搬进 DynamicFeedCard（11-06 抽共享，UP 空间「动态」tab 同一张卡）：
// 评论抽屉、安利、灯箱、本机赞都住在卡里，页面只管喂列表与翻页。

let toastTimer = null
function showToast(msg) {
  toast.value = msg
  clearTimeout(toastTimer)
  toastTimer = setTimeout(() => { toast.value = null }, 2600)
}

function isVideo(d) {
  return !!d.bvid
}

const shown = computed(() => (activeTab.value === 'video' ? items.value.filter(isVideo) : items.value))

// target 名片兜底：动态行没存脸时按 mid 找（转发条的原作者可能不在名单里，那就首字色块）
const faceByMid = computed(() => {
  const map = {}
  for (const t of targets.value) {
    if (t.faceUrl) map[t.mid] = t.faceUrl
  }
  return map
})

async function loadTargets() {
  try {
    const res = await fetch('/api/dynamics/targets')
    targets.value = res.ok ? await res.json() : []
  } catch {
    targets.value = []
  }
}

async function loadFeed() {
  const tok = { kind: 'front', epoch: feedEpoch, seq: ++feedReq }
  loading.value = true
  try {
    const q = new URLSearchParams({ limit: String(PAGE_SIZE) })
    if (filterMid.value) q.set('mid', String(filterMid.value))
    const res = await fetch(`/api/dynamics?${q}`)
    if (!feedFresh(tok)) return
    if (res.ok) {
      const data = await res.json()
      if (!feedFresh(tok)) return // fetch 先到、json 迟到也算迟到包：不许盖新筛选的内容
      items.value = keepFeed(data.items)
      hasMore.value = !!data.hasMore
      feedCursor = tailCursor(data.items)
    } else {
      // 非 2xx：列表、hasMore、游标一并清空——留着上一个筛选的旧货与旧游标是两处隐患：
      // 屏上看着是这位 UP 的内容（其实是上一位的），「看更多」还会拿旧游标 + 新 mid 翻出混批。
      items.value = []
      hasMore.value = false
      feedCursor = null
      showToast('动态没拉开，刷新再试')
    }
  } catch {
    if (!feedFresh(tok)) return
    items.value = []
    hasMore.value = false
    feedCursor = null
    showToast('动态没拉开，刷新再试')
  } finally {
    if (feedFresh(tok)) loading.value = false // 关 loading 只认最新一发：切筛选后不许摸掉新一轮的
  }
}

async function loadMore() {
  // 首屏（含刚切完筛选发出的新一轮）在飞时不许提交翻页：游标要么没回、要么还是上一个筛选的
  if (loading.value || !hasMore.value || !feedCursor || loadingMore.value) return
  const tok = { kind: 'more', epoch: feedEpoch, seq: ++feedReq }
  // 出发瞬间捕获过滤与游标：途中换了筛选，这一发已被 pickFilter 作废，迟到回包一个字段都不许写
  const midAtRequest = filterMid.value
  const cursorAtRequest = feedCursor
  loadingMore.value = true
  try {
    const q = new URLSearchParams({ limit: String(PAGE_SIZE), beforeId: String(cursorAtRequest.id), beforeTime: String(cursorAtRequest.time) })
    if (midAtRequest) q.set('mid', String(midAtRequest))
    const res = await fetch(`/api/dynamics?${q}`)
    if (!feedFresh(tok)) return
    if (res.ok) {
      const data = await res.json()
      if (!feedFresh(tok)) return
      items.value = items.value.concat(keepFeed(data.items))
      hasMore.value = !!data.hasMore
      feedCursor = tailCursor(data.items) ?? cursorAtRequest
    }
  } catch {
    /* 翻页断一下不吭声，按钮还亮着（loadingMore 的归还在 finally 里过闸） */
  } finally {
    if (tok.kind === 'more' && feedFresh(tok)) loadingMore.value = false
  }
}

function pickFilter(mid) {
  feedReq += 1 // 先在飞的首屏/翻页整发作废：迟到回包不写数据、不动 loading/loadingMore
  filterMid.value = mid
  // 旧筛选那套当场清干净：加载中宁可空列表，不让上一位 UP 冒充新筛选；游标更不能留
  items.value = []
  hasMore.value = false
  feedCursor = null
  loadingMore.value = false
  loadFeed()
}

// 卸载：在飞 feed 请求整发作废（迟到包不写数据也不碰 loading/loadingMore），toast 定时器也不留在死后还亮
onBeforeUnmount(() => {
  feedEpoch += 1
  clearTimeout(toastTimer)
})

// 一键巡站只认站长：访客看到的是橱窗，不是控制台
async function refreshRound() {
  if (roundBusy.value) return
  roundBusy.value = true
  try {
    const res = await fetch('/api/admin/dynamics/refresh', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' })
    const data = await res.json().catch(() => null)
    if (res.ok && data?.started) {
      showToast('已催巡捕出门，稍后刷新看新货')
    } else {
      showToast(res.ok ? '巡捕正在外头跑，等等再来' : (data?.message ?? '催轮没递上去'))
    }
  } catch {
    showToast('分站后端没有应答')
  } finally {
    roundBusy.value = false
  }
}

onMounted(async () => {
  await Promise.all([loadTargets(), loadFeed()])
  // 从私信分享卡进来：定位到那条动态并粉框闪两秒（不在当前页就不硬造位置）。
  const focus = typeof route.query.focus === 'string' ? route.query.focus : null
  if (focus) {
    await nextTick()
    const el = document.querySelector(`[data-dyn="${CSS.escape(focus)}"]`)
    if (el) {
      focusHit.value = focus
      el.scrollIntoView({ behavior: 'smooth', block: 'center' })
      setTimeout(() => { focusHit.value = null }, 2400)
    }
  }
})

const focusHit = ref(null)
</script>

<template>
  <div class="min-h-screen bg-pageBg">
    <TopBar />
    <main class="mx-auto max-w-[560px] px-4 py-6">
      <div class="mb-3 flex flex-wrap items-center justify-between gap-3">
        <h1 class="text-lg font-medium text-inkMain">动态</h1>
        <button
          v-if="member?.role === 'owner'"
          class="rounded-full border border-stationPink px-3.5 py-1 text-xs text-stationPink transition hover:bg-stationPink hover:text-white disabled:cursor-not-allowed disabled:opacity-50"
          :disabled="roundBusy"
          @click="refreshRound"
        >
          {{ roundBusy ? '催轮中…' : '⟳ 刷新' }}
        </button>
      </div>

      <!-- 盯梢 UP 过滤器：全部 + 各头像胶囊 -->
      <div class="mb-3 flex flex-wrap items-center gap-2">
        <button
          class="rounded-full px-3 py-1 text-xs transition"
          :class="filterMid === null ? 'bg-stationPink text-white' : 'border border-lineSoft bg-cardBg text-inkSub hover:border-stationPink hover:text-stationPink'"
          @click="pickFilter(null)"
        >
          全部 UP
        </button>
        <button
          v-for="t in targets"
          :key="t.mid"
          class="flex items-center gap-1.5 rounded-full py-1 pl-1 pr-3 text-xs transition"
          :class="filterMid === t.mid ? 'bg-stationPink text-white' : 'border border-lineSoft bg-cardBg text-inkSub hover:border-stationPink hover:text-stationPink'"
          :title="`${t.nickname || t.mid} · ${t.count} 条`"
          @click="pickFilter(t.mid)"
        >
          <img :src="faceFor(t.mid, t.faceUrl) || defaultAvatar" referrerpolicy="no-referrer" loading="lazy" class="h-5 w-5 rounded-full object-cover" alt="" @error="onAvatarError" />
          <span class="max-w-[110px] truncate">{{ t.nickname || `B站号 ${t.mid}` }}</span>
        </button>
      </div>

      <!-- B 站式文字 tab：全部 / 视频 -->
      <div class="mb-3 flex items-center gap-7 rounded-lg border border-lineSoft bg-cardBg px-5">
        <button
          class="relative py-3 text-sm transition"
          :class="activeTab === 'all' ? 'font-semibold text-inkMain' : 'text-inkSub hover:text-inkMain'"
          @click="activeTab = 'all'"
        >
          全部
          <span v-if="activeTab === 'all'" class="absolute bottom-0 left-1/2 h-[3px] w-6 -translate-x-1/2 rounded-t-full bg-stationPink"></span>
        </button>
        <button
          class="relative py-3 text-sm transition"
          :class="activeTab === 'video' ? 'font-semibold text-inkMain' : 'text-inkSub hover:text-inkMain'"
          @click="activeTab = 'video'"
        >
          视频
          <span v-if="activeTab === 'video'" class="absolute bottom-0 left-1/2 h-[3px] w-6 -translate-x-1/2 rounded-t-full bg-stationPink"></span>
        </button>
      </div>

      <p v-if="loading" class="py-16 text-center text-sm text-inkFaint">正在掀 UP 主的动态墙…</p>

      <div v-else-if="!shown.length" class="rounded-2xl border border-lineSoft bg-cardBg px-6 py-16 text-center">
        <svg viewBox="0 0 64 48" class="mx-auto mb-4 h-14 w-16 text-lineSoft" fill="none" stroke="currentColor" stroke-width="2.4">
          <rect x="6" y="6" width="52" height="36" rx="8" />
          <path d="M14 30l10-9 8 7 8-10 10 12" stroke-linecap="round" stroke-linejoin="round" />
          <circle cx="22" cy="17" r="3" />
        </svg>
        <p class="text-sm text-inkFaint">{{ activeTab === 'video' ? '这批橱窗里还没有视频条。' : '橱窗还空着。站长在站长室「动态关注」里登记 UP 后，等巡捕一轮就有货了。' }}</p>
      </div>

      <template v-else>
        <DynamicFeedCard
          v-for="d in shown"
          :key="d.id ?? d.dynamicId"
          :d="d"
          :face-by-mid="faceByMid"
          :highlight="focusHit === d.dynamicId"
          @toast="showToast"
        />

        <div class="pb-4 pt-1 text-center">
          <button
            v-if="hasMore"
            class="rounded-full border border-lineSoft bg-cardBg px-6 py-1.5 text-xs text-inkSub transition hover:border-stationPink hover:text-stationPink disabled:opacity-50"
            :disabled="loadingMore"
            @click="loadMore"
          >
            {{ loadingMore ? '翻页中…' : '看更多' }}
          </button>
          <p v-else class="text-xs text-inkFaint">橱窗见底了，巡捕每小时来补货</p>
        </div>
      </template>

      <p v-if="toast" class="fixed bottom-6 left-1/2 z-50 -translate-x-1/2 rounded-full bg-inkMain/85 px-4 py-2 text-xs text-white shadow-lg">{{ toast }}</p>
    </main>
  </div>
</template>
