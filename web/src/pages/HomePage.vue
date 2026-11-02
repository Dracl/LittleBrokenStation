<script setup>
import { computed, onMounted, onUnmounted, ref, shallowRef, watch } from 'vue'
import TopBar from '../components/TopBar.vue'
import AdminVideoCell from '../components/AdminVideoCell.vue'
import { heroCoverSrc } from '../utils/coverSrc'
import { previewHovering } from '../utils/previewHover'
import { submitDialogOpen } from '../submitStore'
import { askDeleteVideo } from '../confirmStore'
import { isDark, toggleTheme } from '../theme'
import BangumiHome from '../components/BangumiHome.vue'
import { guardGuestNav } from '../utils/guestGate'

// ─── 视图开关：大屏幕模式（大幕轮播 + 分区横滚行，照 B 站番剧区）为默认，网格卡阵是另一档。
// 选择存 localStorage，下次进站还是上次的样子；没选过的新客默认落大屏幕模式。右下三颗圆钮最下面那颗切换。
const VIEW_KEY = 'lbs_home_view'
const viewMode = ref(localStorage.getItem(VIEW_KEY) === 'grid' ? 'grid' : 'anime')
function toggleView() {
  viewMode.value = viewMode.value === 'grid' ? 'anime' : 'grid'
  localStorage.setItem(VIEW_KEY, viewMode.value)
}

// ─── 日夜开关：坐在首页右下角换一批钮上方，同款黑玻离圆钮。
// 只管把 html.dark 掰上掰下（真活在 style.css 的变量里），图标跟着当前昼夜换脸。
const nightMode = ref(isDark())
function flipDayNight() {
  toggleTheme()
  nightMode.value = isDark()
}

// 两百多件馆藏每 5 秒整数组替换，深响应式代理是白付的税；shallowRef 只在整组换新的时候通知视图。
const videos = shallowRef([])

let pollTimer = null
let pollTick = 0

// ─── 首页排序：新货组置顶 + 各组内种子洗牌（同一次进站一个种子，轮询期间不乱跳）───
const homeSeed = ref((Date.now() ^ Math.floor(Math.random() * 0x7fffffff)) >>> 0)
const NEW_ARRIVAL_MS = 7 * 24 * 3600 * 1000 // 入站（建行）7 天内算新货

function shuffleScore(id, seed) {
  // xorshift 整数哈希：便宜且分布均匀，不需要密码学强度
  let h = (Math.imul(id, 0x9e3779b1) ^ seed) >>> 0
  h = Math.imul(h ^ (h >>> 15), 0x85ebca6b)
  h = Math.imul(h ^ (h >>> 13), 0xc2b2ae35)
  return (h ^ (h >>> 16)) >>> 0
}

function homeTier(v, now) {
  if (v.status === 2) {
    const t = v.createdAtUtc ? Date.parse(v.createdAtUtc) : 0
    return now - t <= NEW_ARRIVAL_MS ? 0 : 1
  }
  if (v.status === 1) return 2
  return 3
}

const orderedVideos = computed(() => {
  const now = Date.now()
  const seed = homeSeed.value
  // 悬停锁：鼠标停在某张卡上时把当下这份 id 名单钉住，轮询整组替换也不许让卡片在鼠标底下换人；
  // 名单外的新货排到队尾，等鼠标移开再归位。只锁顺序，取数与内容照常刷新。
  const lock = hoverLock.value
  const rank = lock ? new Map(lock.map((id, i) => [id, i])) : null
  return [...videos.value].sort((a, b) => {
    if (rank) {
      const ra = rank.has(a.id) ? rank.get(a.id) : rank.size
      const rb = rank.has(b.id) ? rank.get(b.id) : rank.size
      return ra !== rb ? ra - rb : b.id - a.id
    }
    const ta = homeTier(a, now)
    const tb = homeTier(b, now)
    if (ta !== tb) return ta - tb
    // 同组内全交给种子洗牌：分组只定谁在前（新货→老馆藏→进站中），组内换一批就重排；
    // 事务态（排队/取消等）不参与洗牌，稳定收尾方便站长盯队列。
    if (ta === 3) return b.id - a.id
    return shuffleScore(a.id, seed) - shuffleScore(b.id, seed)
  })
})

// 悬停进行中 = VideoCard 起了悬停预览（或正在 350ms 延时窗口里）。进出各翻一次，锁只在真正悬停时存在。
const hoverLock = ref(null)
watch(previewHovering, (on) => {
  hoverLock.value = on ? orderedVideos.value.map((v) => v.id) : null
})

function reshuffle() {
  homeSeed.value = (homeSeed.value * 1103515245 + 12345) >>> 0 ^ Math.floor(Math.random() * 0x7fffffff)
  hoverLock.value = null // 手动换一批是明确意图，悬停锁不许拦着
  featured.value = [] // 大幕跟卡阵一起换一批
  featIdx.value = 0
  buildFeatured()
}

// ─── 首格轮播：从已到站且有封面的馆藏里随机抽最多 5 件，横版大图轮换 ───
const FEATURED_COUNT = 5
const featured = ref([])
const featIdx = ref(0)
const featPaused = ref(false)
let featTimer = null

// 卡阵分两段：大幕区拿前 6 件（与大幕同网格紧凑），均匀区接其余（行距拉开呼吸）；
// 大幕缺席（馆藏不足两件）时全部交给均匀区，行为回到单网格。
const nearHero = computed(() => (featured.value.length >= 2 ? orderedVideos.value.slice(0, 6) : []))
const restVideos = computed(() => (featured.value.length >= 2 ? orderedVideos.value.slice(6) : orderedVideos.value))

// 懒加载帘子：一次只挂 40 张卡，摄簣（哨兵）接近视口底部再放下一批；
// 两百多张远程封面全量上 DOM，向下滚动就是拿主线程祭天。
const PAGE_SIZE = 40
const visibleCount = ref(PAGE_SIZE)
const restShown = computed(() => restVideos.value.slice(0, visibleCount.value))
const sentinelEl = ref(null)
let sentinelObserver = null

function loadMore() {
  if (visibleCount.value < restVideos.value.length) visibleCount.value += PAGE_SIZE
}

// 大幕走全站唯一出口的「原图档」（utils/coverSrc 里的 heroCoverSrc）：站长 11-03「首页大幕尽可能用原图，
// 毕竟这么大，压缩了就难看了」。所以它不走预切档位，直接本地原图→热链原样（热链也不贴 B 站缩放参数）；
// 模糊底与清晰面同源，不致两面不同尺寸。
const featCover = computed(() => heroCoverSrc(featured.value[featIdx.value]))

// 大幕加载态：帧/一批馆藏在手，新图未到位前转小环，不让黑帧裸切。
const heroReady = ref(false)
watch(
  () => featured.value[featIdx.value]?.bvid,
  () => {
    heroReady.value = false
  },
)

function buildFeatured() {
  // 抽定即固定：轮询刷新和新到站不打乱正在转的轮播，重排只随重新开站来一次。
  if (featured.value.length) return
  const arrived = videos.value.filter((v) => v.status === 2 && v.coverUrl)
  if (arrived.length < 2) return // 不足两件不配轮播，均匀卡阵自己伺候
  featured.value = [...arrived].sort(() => Math.random() - 0.5).slice(0, FEATURED_COUNT)
}

let lastPayload = ''

async function refreshVideos() {
  try {
    const res = await fetch('/api/videos')
    if (res.ok) {
      const text = await res.text()
      // 台账没变一个字就不整组替换：两百多张卡跟着全量重渲染，正是滚动时一卡一卡的根子。
      if (text === lastPayload) return
      lastPayload = text
      videos.value = JSON.parse(text)
      buildFeatured()
    }
  } catch {
    /* 下一轮再试 */
  }
}

const commentBusy = ref(new Set())

// 续下：救活卡死的下载（引擎任务崩溃遗留），后端自行判断软续/重投。
const reviveBusy = ref(new Set())

async function reviveVideo(video) {
  if (reviveBusy.value.has(video.id)) return
  const busy = new Set(reviveBusy.value)
  busy.add(video.id)
  reviveBusy.value = busy
  try {
    const res = await fetch(`/api/videos/${video.id}/revive`, { method: 'POST' })
    const body = await res.json().catch(() => null)
    showNotice(res.ok ? 'ok' : 'err', body?.message ?? (res.ok ? '已续下' : '续下失败'))
  } catch {
    showNotice('err', '分站后端没有应答')
  } finally {
    const done = new Set(reviveBusy.value)
    done.delete(video.id)
    reviveBusy.value = done
  }
}

async function requeueVideo(video) {
  if (reviveBusy.value.has(video.id)) return
  const busy = new Set(reviveBusy.value)
  busy.add(video.id)
  reviveBusy.value = busy
  try {
    const res = await fetch(`/api/videos/${video.id}/requeue`, { method: 'POST' })
    const body = await res.json().catch(() => null)
    showNotice(res.ok ? 'ok' : 'err', body?.message ?? (res.ok ? '已重新排队' : '排队没成'))
    if (res.ok) refreshVideos()
  } catch {
    showNotice('err', '分站后端没有应答')
  } finally {
    const done = new Set(reviveBusy.value)
    done.delete(video.id)
    reviveBusy.value = done
  }
}

async function fetchComments(video) {
  if (commentBusy.value.has(video.id)) return
  const busy = new Set(commentBusy.value)
  busy.add(video.id)
  commentBusy.value = busy
  try {
    // 一枚钮两件活：评论快照 + B 站弹幕快照（站内弹幕住在 danmaku_local，重拉碰不到它），与放映厅同形。
    const [cmt, dm] = await Promise.all([
      fetch(`/api/videos/${video.id}/comments`, { method: 'POST' }).then((r) => r.json().catch(() => null).then((b) => ({ ok: r.ok, body: b }))),
      fetch(`/api/videos/${video.id}/danmaku-refresh`, { method: 'POST' }).then((r) => r.json().catch(() => null).then((b) => ({ ok: r.ok, body: b }))),
    ])
    const parts = [
      cmt.body?.message ?? (cmt.ok ? '评论已归档' : '评论采集失败'),
      dm.body?.message ?? (dm.ok ? 'B 站弹幕已重拉' : '弹幕重拉失败'),
    ]
    const fails = [cmt.ok, dm.ok].filter((ok) => !ok).length
    showNotice(fails === 2 ? 'err' : 'ok', parts.join('；'))
  } catch {
    showNotice('err', '分站后端没有应答')
  } finally {
    const done = new Set(commentBusy.value)
    done.delete(video.id)
    commentBusy.value = done
  }
}

async function removeVideo(video) {
  const label = video.title || video.bvid
  if (!(await askDeleteVideo(label))) {
    return
  }
  try {
    const res = await fetch(`/api/videos/${video.id}`, { method: 'DELETE' })
    const body = await res.json().catch(() => null)
    if (res.ok) {
      showNotice('ok', body?.message ?? '已删除')
      // 删掉的馆藏在轮播里也销名，轮完的队尾缩到不足两件就不摆横幅了。
      featured.value = featured.value.filter((v) => v.id !== video.id)
      if (featIdx.value >= featured.value.length) featIdx.value = 0
      refreshVideos()
    } else {
      showNotice('err', body?.message ?? '删不动，稍后再试')
    }
  } catch {
    showNotice('err', '分站后端没有应答')
  }
}

const notice = ref(null)
let noticeTimer = null

function showNotice(kind, text) {
  notice.value = { kind, text }
  if (noticeTimer) clearTimeout(noticeTimer)
  noticeTimer = setTimeout(() => (notice.value = null), 6000)
}

onMounted(() => {
  // 进主页一枪（周报七日动静的「主页」柱）：每次挂载算一回（刷新、回首页都算），成员游客一视同仁；
  // 与观映账分家（subjectType=page），游客柱继续只数 watch.*。
  fetch('/api/events/ingest', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ events: [{ name: 'home.open', subjectType: 'page', subjectId: 'home', payload: null }] }),
  }).catch(() => {})
  refreshVideos()
  pollTimer = setInterval(() => {
    // 有排队/进站中的才快轮（下载进度需要）；全站安静时降到 15 秒一发，
    // 既能接住新投稿，又免得两百多张卡每 5 秒白白重绘一轮。
    const active = videos.value.some((v) => v.status === 0 || v.status === 1)
    if (!active && pollTick++ % 3 !== 0) return
    refreshVideos()
  }, 5000)
  featTimer = setInterval(() => {
    if (!featPaused.value && featured.value.length > 1) {
      featIdx.value = (featIdx.value + 1) % featured.value.length
    }
  }, 5000)

  // 摄簣 IntersectionObserver：提前 600px 接客，滚到底前下一批已经挂上；
  // 每次放帘后重挂一次，保证摄簣仍在提前区时能连拉几帘而不卡住。
  sentinelObserver = new IntersectionObserver(
    (entries) => {
      if (!entries.some((e) => e.isIntersecting)) return
      loadMore()
      const el = sentinelEl.value
      if (el && sentinelObserver) {
        sentinelObserver.unobserve(el)
        sentinelObserver.observe(el)
      }
    },
    { rootMargin: '600px 0px' },
  )
  if (sentinelEl.value) sentinelObserver.observe(sentinelEl.value)
})

function featPrev() {
  const n = featured.value.length
  if (n > 1) featIdx.value = (featIdx.value - 1 + n) % n
}

function featNext() {
  const n = featured.value.length
  if (n > 1) featIdx.value = (featIdx.value + 1) % n
}

onUnmounted(() => {
  if (pollTimer) clearInterval(pollTimer)
  if (featTimer) clearInterval(featTimer)
  if (sentinelObserver) {
    sentinelObserver.disconnect()
    sentinelObserver = null
  }
})

// 帘子可能因删馆/换一批而重新挂载，新摄簣出现就补登记。
watch(sentinelEl, (el) => {
  if (el && sentinelObserver) sentinelObserver.observe(el)
})
</script>

<template>
  <!-- 根上挂一道捕获拦：访客（member 为空）点卡片进放映厅、点分区行、点合集入口这些一律不跳页，
       改成开现成那扇登录窗（utils/guestGate.js，与顶栏「登录 / 注册」同一扇）。只认落在 /watch · /zones ·
       /collections · /collection · /dynamics 那些 <a> 上的点击，卡面、悬停预览、翻页箭头与站长那几枚 hover 钮一概不碰；
       大屏幕模式（BangumiHome）就挂在下面，一并罩住，成员则整个放行。 -->
  <div class="min-h-screen" @click.capture="guardGuestNav">
    <TopBar />

    <main class="mx-auto max-w-[1400px] px-6 py-6">
      <!-- 大屏幕模式：大幕轮播 + 入口卡 + 分区横滚行，整套交给 BangumiHome -->
      <BangumiHome
          v-if="viewMode === 'anime'"
          :videos="videos"
          :seed="homeSeed"
          :comment-busy="commentBusy"
          @fetch-comments="fetchComments"
          @remove="removeVideo"
        />

      <!-- 馆藏墙（网格模式专属；大屏幕模式整块换成上面的 BangumiHome） -->
      <template v-else>
      <div class="mb-3 flex items-center justify-between">
        <h2 class="text-base font-semibold text-inkMain">馆藏<span v-if="videos.length" class="ml-2 text-xs font-normal text-inkFaint">{{ videos.length }} 件</span></h2>
        <p
          v-if="notice"
          class="text-xs"
          :class="{ 'text-emerald-300': notice.kind === 'ok', 'text-amber-200': notice.kind === 'warn', 'text-rose-300': notice.kind === 'err' }"
        >
          {{ notice.text }}
        </p>
      </div>
      <div v-if="videos.length === 0" class="rounded-xl border border-dashed border-white/10 py-16 text-center">
        <p class="text-sm text-inkSub">还没有馆藏</p>
        <button
          class="mt-3 rounded-full border border-stationPink px-5 py-1.5 text-sm text-stationPink transition hover:bg-stationPink hover:text-white"
          @click="submitDialogOpen = true"
        >
          点这里投稿第一条
        </button>
      </div>
      <div v-else class="space-y-[38px]">
        <!-- 大幕区：大幕与右侧六张紧凑同行（gap-4），保持一个整体的呼吸 -->
        <div v-if="featured.length >= 2" class="grid grid-cols-2 gap-4 md:grid-cols-3 xl:grid-cols-5">
          <!-- 首格大幕（B 站官方同款骨架）：占两格宽两格高，三层渐焦——顶对齐原图 → 中模糊过渡带 → 重模糊底；
               随机抽馆藏轮换，悬停暂停，圆点直切，右下箭头换帧，点大幕跳观看页 -->
          <div
            class="group relative col-span-2 row-span-2 min-h-[300px] cursor-pointer overflow-hidden rounded-xl bg-black"
            @mouseenter="featPaused = true"
            @mouseleave="featPaused = false"
          >
            <RouterLink :to="`/watch/${featured[featIdx]?.bvid}`" class="absolute inset-0 block">
              <div :key="featured[featIdx]?.bvid ?? featIdx" class="hero-layers absolute inset-0">
                <div class="hero-blur" :style="{ backgroundImage: `url('${featCover}')` }"></div>
                <div class="hero-soft">
                  <img :src="featCover" :alt="featured[featIdx]?.title || featured[featIdx]?.bvid" referrerpolicy="no-referrer" />
                </div>
                <div class="hero-top">
                  <img
                    :src="featCover"
                    :alt="featured[featIdx]?.title || featured[featIdx]?.bvid"
                    referrerpolicy="no-referrer"
                    @load="heroReady = true"
                    @error="heroReady = true"
                  />
                </div>
                <div class="hero-dim"></div>
              </div>
              <!-- 新帧未到位：黑底上转小环，不裸切黑帧 -->
              <div v-if="!heroReady" class="absolute inset-0 z-[5] flex items-center justify-center">
                <span class="hero-spin"></span>
              </div>
              <div class="absolute bottom-3.5 left-4 right-[120px] z-10">
                <p class="truncate text-[15px] font-semibold leading-[21px] text-white drop-shadow-[0_1px_3px_rgba(0,0,0,0.45)]">
                  {{ featured[featIdx]?.title || featured[featIdx]?.bvid }}
                </p>
                <div class="mt-2 flex items-center gap-[7px]">
                  <button
                    v-for="(v, i) in featured"
                    :key="`dot-${v.id}`"
                    class="h-[9px] w-[9px] rounded-full transition"
                    :class="i === featIdx ? 'bg-white' : 'bg-white/40 hover:bg-white/70'"
                    :aria-label="`切到 ${v.title || v.bvid}`"
                    @click.prevent.stop="featIdx = i"
                  ></button>
                </div>
              </div>
            </RouterLink>
            <div class="absolute bottom-3 right-3 z-10 flex gap-2">
              <button class="hero-nav" aria-label="上一帧" @click.prevent.stop="featPrev">‹</button>
              <button class="hero-nav" aria-label="下一帧" @click.prevent.stop="featNext">›</button>
            </div>
          </div>
          <AdminVideoCell
            v-for="video in nearHero"
            :key="`near-${video.id}`"
            :video="video"
            :comment-busy="commentBusy.has(video.id)"
            :revive-busy="reviveBusy.has(video.id)"
            :can-manage="!!video.canManage"
            @fetch-comments="fetchComments"
            @revive="reviveVideo"
            @requeue="requeueVideo"
            @remove="removeVideo"
          />
        </div>

        <!-- 均匀卡阵：行距拉开呼吸（38px），列距不变；帘子分批渲染，摄簣接近就放下一批 -->
        <div class="grid grid-cols-2 gap-x-4 gap-y-[38px] md:grid-cols-3 xl:grid-cols-5">
          <AdminVideoCell
            v-for="video in restShown"
            :key="video.id"
            :video="video"
            :comment-busy="commentBusy.has(video.id)"
            :revive-busy="reviveBusy.has(video.id)"
            :can-manage="!!video.canManage"
            @fetch-comments="fetchComments"
            @revive="reviveVideo"
            @requeue="requeueVideo"
            @remove="removeVideo"
          />
        </div>
        <div
          v-if="restShown.length < restVideos.length"
          ref="sentinelEl"
          class="flex items-center justify-center py-8 text-xs text-inkFaint"
        >
          <span class="grid-spin mr-2"></span>放下帘子，迎接更多馆藏…
        </div>
      </div>
      </template>
    </main>

    <!-- 日夜开关：压在换一批正上方，白昼见月、夜里见日 -->
    <button
      class="fixed bottom-[9.25rem] right-6 z-40 flex h-11 w-11 items-center justify-center rounded-full border border-white/15 bg-black/60 text-white/70 shadow-lg backdrop-blur transition hover:border-stationPink/60 hover:text-stationPink"
      :title="nightMode ? '切回白昼' : '切进黑夜'"
      :aria-label="nightMode ? '切回白昼' : '切进黑夜'"
      @click="flipDayNight"
    >
      <svg v-if="nightMode" viewBox="0 0 24 24" class="h-5 w-5" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round">
        <circle cx="12" cy="12" r="4.2" />
        <path d="M12 2.5v2.2M12 19.3v2.2M2.5 12h2.2M19.3 12h2.2M5.2 5.2l1.6 1.6M17.2 17.2l1.6 1.6M18.8 5.2l-1.6 1.6M6.8 17.2l-1.6 1.6" />
      </svg>
      <svg v-else viewBox="0 0 24 24" class="h-5 w-5" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
        <path d="M20.8 13.4A8.6 8.6 0 1 1 10.6 3.2a6.8 6.8 0 0 0 10.2 10.2z" />
      </svg>
    </button>

    <!-- 换一批：重抽种子重洗卡阵与大幕（大屏幕模式下 banner 也跟着重抽），轮询期间顺序锁定 -->
    <button
      class="fixed bottom-[5.25rem] right-6 z-40 flex h-11 w-11 items-center justify-center rounded-full border border-white/15 bg-black/60 text-white/70 shadow-lg backdrop-blur transition hover:rotate-6 hover:border-stationPink/60 hover:text-stationPink"
      title="换一批：重新洗牌首页卡阵"
      aria-label="换一批"
      @click="reshuffle"
    >
      <svg viewBox="0 0 24 24" class="h-5 w-5" fill="none" stroke="currentColor" stroke-width="1.8">
        <path d="M16 3h5v5M4 20 21 3M21 16v5h-5M15 15l6 6M4 4l5 5" stroke-linecap="round" stroke-linejoin="round" />
      </svg>
    </button>

    <!-- 视图切换：压在换一批正下方，网格卡阵 ⇄ 大屏幕模式（图标跟着当前视图换脸；叫法 11-03 站长点名单改大屏幕） -->
    <button
      class="fixed bottom-6 right-6 z-40 flex h-11 w-11 items-center justify-center rounded-full border border-white/15 bg-black/60 text-white/70 shadow-lg backdrop-blur transition hover:border-stationPink/60 hover:text-stationPink"
      :title="viewMode === 'grid' ? '切到大屏幕模式' : '切回网格卡阵'"
      :aria-label="viewMode === 'grid' ? '切到大屏幕模式' : '切回网格卡阵'"
      @click="toggleView"
    >
      <svg v-if="viewMode === 'grid'" viewBox="0 0 24 24" class="h-5 w-5" fill="none" stroke="currentColor" stroke-width="1.8">
        <rect x="3" y="4" width="18" height="9" rx="2" />
        <path d="M3 16.5h7.5M3 20h11" stroke-linecap="round" />
        <path d="M15.5 16.5h5.5M15.5 20h3" stroke-linecap="round" opacity="0.45" />
      </svg>
      <svg v-else viewBox="0 0 24 24" class="h-5 w-5" fill="none" stroke="currentColor" stroke-width="1.8">
        <rect x="3.5" y="3.5" width="7" height="7" rx="1.8" />
        <rect x="13.5" y="3.5" width="7" height="7" rx="1.8" />
        <rect x="3.5" y="13.5" width="7" height="7" rx="1.8" />
        <rect x="13.5" y="13.5" width="7" height="7" rx="1.8" />
      </svg>
    </button>
  </div>
</template>

<style scoped>
/* 首格大幕三层渐焦：清晰原图（mask 下缘渐隐）→ 同图中模糊过渡带 → 全幅重模糊底，镜头拉开式的连续失焦 */
.hero-layers {
  animation: hero-in 0.45s ease;
}
@keyframes hero-in {
  from { opacity: 0.25; }
  to { opacity: 1; }
}
.hero-blur {
  position: absolute;
  inset: -14%;
  background-size: cover;
  background-position: center 30%;
  filter: blur(30px) saturate(1.15);
  transform: scale(1.15);
}
.hero-soft {
  position: absolute;
  top: 52%;
  left: -6%;
  right: -6%;
  height: 34%;
  overflow: hidden;
  filter: blur(11px) saturate(1.12);
  -webkit-mask-image: linear-gradient(to bottom, transparent, #000 34%, #000 66%, transparent);
  mask-image: linear-gradient(to bottom, transparent, #000 34%, #000 66%, transparent);
}
.hero-soft img {
  width: 100%;
  height: 100%;
  object-fit: cover;
  object-position: center 30%;
  display: block;
}
.hero-top {
  position: absolute;
  top: 0;
  left: 0;
  right: 0;
  height: 71%;
  overflow: hidden;
  -webkit-mask-image: linear-gradient(to bottom, #000 62%, rgba(0, 0, 0, 0.72) 84%, transparent 99%);
  mask-image: linear-gradient(to bottom, #000 62%, rgba(0, 0, 0, 0.72) 84%, transparent 99%);
}
.hero-top img {
  width: 100%;
  height: 100%;
  object-fit: cover;
  object-position: center 22%;
  display: block;
}
.hero-dim {
  position: absolute;
  left: 0;
  right: 0;
  bottom: 0;
  height: 38%;
  background: linear-gradient(transparent, rgba(10, 8, 6, 0.42));
  pointer-events: none;
}
.hero-nav {
  width: 34px;
  height: 34px;
  border-radius: 10px;
  border: none;
  background: rgba(255, 255, 255, 0.22);
  color: #fff;
  font-size: 16px;
  line-height: 1;
  cursor: pointer;
  backdrop-filter: blur(6px);
  transition: background 0.15s;
}
.hero-nav:hover {
  background: rgba(255, 255, 255, 0.38);
}
/* 加载转环：大幕新帧未到位转白环，帘子放批时转墨环 */
.hero-spin {
  display: inline-block;
  height: 34px;
  width: 34px;
  border-radius: 9999px;
  border: 3px solid rgba(255, 255, 255, 0.22);
  border-top-color: rgba(255, 255, 255, 0.92);
  animation: lbs-spin 0.8s linear infinite;
}
.grid-spin {
  display: inline-block;
  height: 14px;
  width: 14px;
  border-radius: 9999px;
  border: 2px solid rgba(0, 0, 0, 0.12);
  border-top-color: rgba(0, 0, 0, 0.45);
  animation: lbs-spin 0.8s linear infinite;
}
@keyframes lbs-spin {
  to {
    transform: rotate(360deg);
  }
}
</style>
