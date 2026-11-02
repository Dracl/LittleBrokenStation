<script setup>
// 馆藏壁窗口壳：标题条（件数 + 投稿/历史/都放三档喂料 + 洗牌/复位/±/全屏）裹着画布内核。
// 三档选择记在本地（lbs.member.wall.scope），下次进站还是那面墙；
// 全屏借 fixed 浮层实现（分站没走 Fullscreen API，兼容性够用），Esc 或再按 ⤢ 收回。
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import CanvasWall from './CanvasWall.vue'
import EmptyTv from './EmptyTv.vue'

const SCOPES = [
  { key: 'both', label: '全部' },
  { key: 'submissions', label: '投稿' },
  { key: 'history', label: '历史' },
]
const SCOPE_KEY = 'lbs.member.wall.scope'

// 外递料：给定就不去 /api/member/wall 自取，喂料三档、分页续拉、出错误牌一并歇着，
// 洗牌/复位/±/全屏照旧——周报的「新货」就是这么上墙的（站长 11-03 定）。
const props = defineProps({
  fedItems: { type: Array, default: null },
  title: { type: String, default: '我的馆藏壁' },
  emptyTitle: { type: String, default: '' },
  emptySubtitle: { type: String, default: '' },
})
const fed = computed(() => (Array.isArray(props.fedItems) ? props.fedItems : null))

function savedScope() {
  const raw = localStorage.getItem(SCOPE_KEY)
  return SCOPES.some((s) => s.key === raw) ? raw : 'both'
}

const scope = ref(savedScope())
const items = ref(null) // null = 在飞/没拿到，[] = 确实一面空墙
const total = ref(0) // 库里够挂的总数：分页续拉的地板线
const loadError = ref('')
const seed = ref((Date.now() % 99991) | 0)
const fullscreen = ref(false)
const busy = ref(false)
const loadingMore = ref(false)
const wall = ref(null) // 画布内核的手柄（±/复位都喊它）

// 分页尺寸：一口 60 张上桌（后端封顶 120），视口盖不住时 CanvasWall 会连充几轮自动补货，
// 开墙即是满墙——参考站的从容不是魔法，它首屏就吃 120 张
const PAGE_SIZE = 60

async function fetchPage(offset) {
  const res = await fetch(`/api/member/wall?scope=${encodeURIComponent(scope.value)}&offset=${offset}&limit=${PAGE_SIZE}`)
  if (!res.ok) {
    const data = await res.json().catch(() => null)
    throw new Error(data?.message || '分站没把墙料递上来')
  }
  return res.json().catch(() => null)
}

async function load() {
  if (busy.value) return
  busy.value = true
  loadError.value = ''
  try {
    const data = await fetchPage(0)
    items.value = Array.isArray(data?.items) ? data.items : []
    total.value = Number(data?.total) || items.value.length
  } catch (err) {
    items.value = null
    loadError.value = err?.message || '分站后端没有应答'
  } finally {
    busy.value = false
  }
}

// 续拉：画布拖到已铺世界的边缘时由 CanvasWall 喊一声；双闸防重入，失败静默（首屏错另有错牌）
async function loadMore() {
  if (busy.value || loadingMore.value) return
  const cur = items.value
  if (!cur || cur.length >= total.value) return
  loadingMore.value = true
  try {
    const data = await fetchPage(cur.length)
    const got = Array.isArray(data?.items) ? data.items : []
    items.value = [...cur, ...got]
    if (Number(data?.total)) total.value = Number(data.total)
    // 空页兑底：接口说还有却递了空手，把地板线收到现状，别让用户反复撞墙
    if (!got.length) total.value = cur.length
  } catch {
    /* 续拉失败不打扰，下次拖拽还会再喊 */
  } finally {
    loadingMore.value = false
  }
}

watch(scope, (v) => {
  localStorage.setItem(SCOPE_KEY, v)
  if (!fed.value) load()
})

onMounted(() => {
  if (!fed.value) load()
})

function reshuffle() {
  seed.value = (seed.value + 1 + Math.floor(Math.random() * 9999)) | 0
}

function toggleFullscreen() {
  fullscreen.value = !fullscreen.value
}

// 全屏时锁住身后页面的滚动：浮层边缘露出一条能滚的网页最破功
watch(fullscreen, (on) => {
  document.body.style.overflow = on ? 'hidden' : ''
  if (on) window.scrollTo(0, 0)
})

function onKey(e) {
  if (e.key === 'Escape' && fullscreen.value) fullscreen.value = false
}
onMounted(() => window.addEventListener('keydown', onKey))
onBeforeUnmount(() => {
  window.removeEventListener('keydown', onKey)
  document.body.style.overflow = '' // 带全屏未退也不能把人家页面锁死
})

// 内外两路料共一个出口：外递料在手就用它，否则用自取的（null = 在飞，[] = 确实空墙）
const shownItems = computed(() => (fed.value ? fed.value : items.value))
const shownTotal = computed(() => (fed.value ? fed.value.length : total.value))
// 重画一把的钥匙：自取那路看喂料档与种子，外递那路看料数与种子
const wallKey = computed(() =>
  fed.value ? `fed:${props.fedItems?.length ?? 0}:${seed.value}` : `${scope.value}:${seed.value}`,
)

const countLabel = computed(() => {
  const its = shownItems.value
  if (its === null) return '…'
  return its.length < shownTotal.value ? `${its.length} / ${shownTotal.value} 件` : `${its.length} 件`
})
const isEmpty = computed(() => !busy.value && !loadError.value && shownItems.value?.length === 0)
</script>

<template>
  <!-- 全屏态整块搬到 body 底下。position:fixed 的参照物是最近那个带 transform / filter / 层叠上下文的祖先，
       而周报那张卡包着一层入场动画（weekly-rise 的关键帧里有 transform），
       不搬走时全屏层被自己那张卡关在原地，看上去就是「点一下全屏直接没了」（站长 11-03 报）。
       搬进 body 之后参照物是视口；非全屏态 :disabled 一挡，仍旧原地渲染，个人空间那面照旧。 -->
  <Teleport to="body" :disabled="!fullscreen">
    <!-- 全屏遮罩：压暗身后页面，点空白处也是退场 -->
    <div v-if="fullscreen" class="fixed inset-0 z-[89] bg-black/55" @click="fullscreen = false"></div>
  <section
    class="rounded-2xl border border-lineSoft bg-cardBg shadow-[0_1px_2px_rgba(0,0,0,0.03)]"
    :class="fullscreen && 'fixed inset-3 z-[90] flex flex-col overflow-hidden shadow-2xl'"
  >
    <!-- 标题条：左名右钮，窗口感从这条来 -->
    <header class="wall-bar flex flex-wrap items-center gap-2 border-b border-lineSoft/70 px-5 py-3">
      <span class="grid h-6 w-6 shrink-0 place-items-center rounded-md bg-stationPink/10 text-stationPink">
        <svg viewBox="0 0 24 24" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
          <rect x="3" y="4" width="8" height="6" rx="1.2" />
          <rect x="13.5" y="6" width="7.5" height="5.5" rx="1.2" />
          <rect x="6" y="13.5" width="7.5" height="5.5" rx="1.2" />
        </svg>
      </span>
      <h3 class="text-sm font-semibold text-inkMain">{{ title }}</h3>
      <span class="rounded-full border border-lineSoft px-2 py-px font-mono text-[11px] text-inkFaint">{{ countLabel }}</span>

      <div class="wall-tools ml-auto flex flex-wrap items-center gap-2">
        <!-- 喂料三档：我投的 / 我看的 / 都要。外递料的墙没这回事，整组收起 -->
        <div v-if="!fed" class="flex items-center rounded-full border border-lineSoft p-0.5" role="group" aria-label="墙上放什么">
          <button
            v-for="s in SCOPES"
            :key="s.key"
            class="rounded-full px-2.5 py-0.5 text-[11px] transition"
            :class="scope === s.key ? 'bg-stationPink text-white' : 'text-inkSub hover:text-stationPink'"
            :aria-pressed="scope === s.key"
            @click="scope = s.key"
          >
            {{ s.label }}
          </button>
        </div>

        <span v-if="!fed" class="h-4 w-px bg-lineSoft" aria-hidden="true"></span>

        <button class="wall-tool" title="洗牌：整墙重新撒一遍" aria-label="洗牌" @click="reshuffle">
          <svg viewBox="0 0 24 24" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
            <path d="M16 3h5v5M4 20 21 3M21 16v5h-5M15 15l6 6M4 4l5 5" />
          </svg>
        </button>
        <button class="wall-tool" title="复位：整墙归位居中" aria-label="复位" @click="wall?.fit()">
          <svg viewBox="0 0 24 24" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
            <path d="M3 8V5a2 2 0 0 1 2-2h3M16 3h3a2 2 0 0 1 2 2v3M21 16v3a2 2 0 0 1-2 2h-3M8 21H5a2 2 0 0 1-2-2v-3" />
          </svg>
        </button>
        <button class="wall-tool" title="放大（或 Ctrl+滚轮）" aria-label="放大" @click="wall?.zoomIn()">＋</button>
        <button class="wall-tool" title="缩小（或 Ctrl+滚轮）" aria-label="缩小" @click="wall?.zoomOut()">－</button>
        <button
          class="wall-tool"
          :title="fullscreen ? '退出全屏（Esc）' : '全屏漫游：滚轮直接缩放'"
          :aria-label="fullscreen ? '退出全屏' : '全屏'"
          @click="toggleFullscreen"
        >
          <svg v-if="!fullscreen" viewBox="0 0 24 24" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
            <path d="M9 3H3v6M15 3h6v6M9 21H3v-6M15 21h6v-6" />
          </svg>
          <svg v-else viewBox="0 0 24 24" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
            <path d="M3 9h6V3M21 9h-6V3M3 15h6v6M21 15h-6v6" />
          </svg>
        </button>
      </div>
    </header>

    <!-- 墙身：嵌入态 400px 视窗，全屏态 flex 撑满剩余高度；加载转环 / 空墙小电视 / 出错误牌重试，三态都在框内解决 -->
    <div
      class="relative overflow-hidden rounded-b-2xl bg-[#f4f2ee] dark:bg-[#17181c]"
      :class="fullscreen ? 'flex-1' : 'h-[400px]'"
    >
      <CanvasWall
        v-if="shownItems?.length"
        :key="wallKey"
        ref="wall"
        :items="shownItems"
        :seed="seed"
        :fullscreen="fullscreen"
        :has-more="!fed && shownItems.length < shownTotal"
        @load-more="loadMore"
      />

      <div v-if="busy && !items?.length" class="absolute inset-0 flex items-center justify-center">
        <span class="h-6 w-6 animate-spin rounded-full border-2 border-stationPink/25 border-t-stationPink"></span>
      </div>

      <div v-else-if="loadError" class="absolute inset-0 flex flex-col items-center justify-center gap-3 px-6 text-center">
        <p class="text-sm text-inkSub">{{ loadError }}</p>
        <button class="rounded-full border border-stationPink px-4 py-1 text-xs text-stationPink transition hover:bg-stationPink hover:text-white" @click="load">
          再试一次
        </button>
      </div>

      <div v-else-if="isEmpty" class="absolute inset-0 flex items-center justify-center">
        <EmptyTv
          :title="emptyTitle || (scope === 'history' ? '墙上还空着' : '还没有值得挂墙的东西')"
          :subtitle="emptySubtitle || (scope === 'submissions' ? '投几部片子进来，墙就活了' : '投稿或看片都会自动上墙')"
        >
          <p v-if="!fed" class="text-[11px] text-inkFaint">刚进站的话，先去首页逛两部再回来看看</p>
        </EmptyTv>
      </div>

      <!-- 右下角常驻提示：教一次手势就够 -->
      <p v-if="shownItems?.length" class="pointer-events-none absolute bottom-2 right-3 z-10 font-mono text-[10px] text-inkFaint/80 dark:text-white/30">
        {{ fullscreen
          ? '拖拽平移 · 滚轮缩放 · 方向键巡墙 · Esc 退出'
          : fed
            ? '拖 = 平移 · Ctrl+滚轮 = 缩放 · 点卡进放映厅'
            : '拖 = 平移 · Ctrl+滚轮 = 缩放 · 点卡进放映厅 · 拖到边缘自动续墙' }}
      </p>
    </div>
  </section>
  </Teleport>
</template>

<style scoped>
.wall-tool {
  @apply flex h-7 min-w-7 items-center justify-center rounded-full border border-lineSoft px-1.5 text-[12px] leading-none text-inkSub transition;
}
.wall-tool:hover {
  @apply border-stationPink/60 text-stationPink;
}

/* 标题条右侧那排工具（喂料三档 + 洗牌/复位/±/全屏）：平时收着，鼠标扫过标题条才浮现，
   脾气照首页分区行那两枚轮辖钮（opacity-0 → group-hover 现形）。
   触屏没 hover 这回事，媒体查询一挡就照常显；藏的时候连“点”一并收掉，免得照不到人的钮被误点；
   键盘 Tab 进来走 :focus-within，照样亮。 */
.wall-tools {
  transition: opacity 0.2s ease;
}
@media (hover: hover) {
  .wall-bar:not(:hover):not(:focus-within) .wall-tools {
    opacity: 0;
    pointer-events: none;
  }
}
</style>
