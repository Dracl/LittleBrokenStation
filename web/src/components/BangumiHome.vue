<script setup>
import { computed, nextTick, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import { RouterLink, useRouter } from 'vue-router'
import { coverThumb } from '../utils/cover'
import { colonTime, publishedDate } from '../utils/time'
import { HINT_DELETE_VIDEO, HINT_FETCH_COMMENTS } from '../confirmStore'
import UpIcon from './UpIcon.vue'
import PlayCountIcon from './PlayCountIcon.vue'
import DanmakuCountIcon from './DanmakuCountIcon.vue'

// ─── 番剧模式：B 站番剧区（bilibili.com/anime）的同款开局——
// 顶部大 banner（模糊底铺满 + 清晰封面居右 + 右下黑玻离信息胶囊），
// 底下一排横版小图 tab（选中放大浮起、名字落到卡下），
// 三张浅色渐变入口卡，再往下每个顶级分区一条横滚行（悬停出左右圆箭头）。
// 封面全部保持 16:10 横版原样，一张不裁。
// 数据：分区行吃 /api/zones 两口端点（那边才带 zone 字段），时长/播放数从首页已拉的全集按 id 认领。
const props = defineProps({
  videos: { type: Array, default: () => [] }, // /api/videos 全集（含 durationText / viewCountText）
  seed: { type: Number, default: 0 }, // 首页换一批的种子：banner 跟着一起重抽
  // 补采在途集合（与网格卡阵共用）：按 videoId 查，拉的是首页那一份 Set。
  commentBusy: { type: Object, default: () => new Set() },
})

// 投稿人自管两钮（补采评论 / 删档）只挂在分区横滚行与网格卡阵上；大幕右上角的两钮已按站长要求收起。
// 能不能按由服务端算好的 canManage 说话：分区行那口 /api/zones/videos 没这列，按 id 从全集认领（与时长/播放数同一套路）。
defineEmits(['fetch-comments', 'remove'])
const byId = computed(() => new Map(props.videos.map((v) => [v.id, v])))

// 大幕与卡行里的 UP 名 → 分站里的 UP 空间（站长 11-02 点名：点一下就进）。
// 整条 UP 行都要可点（不只几个字宽的名字），所以仍是 span + click 而不是内联 <a>；
// prevent/stop 那两句属防御：卡行外层已不是链接（命中区收口），大幕那颗胶囊还是，留着免得两发互抢。
const router = useRouter()
function goUp(mid) {
  if (mid != null) router.push(`/up/${mid}`)
}

// /api/zones/videos 那口只投影了展示字段（id/bvid/title/cover/upName/zone……），没带 up_mid，
// 所以 UP 空间的入口按 id 从首页已拉的全集认领（与时长/播放数/canManage 同一套路）。
function upMidOf(item) {
  if (item.upMid != null) return item.upMid
  const full = byId.value.get(item.id)
  return full && full.upMid != null ? full.upMid : null
}

// 卡行那一整行（名字 · 时间）归 UP 空间，不靠用户射那几个字（站长 11-02）。
// 命中区是行自己的盒子 + 上借 3px / 下借 5px 的透明垫子（调参台定的数）；没归档 mid 的行不给死链（整行不可点，
// 外层大链接已在命中区收口（站长 11-02）里撤掉，卡面的去处在封面与标题字面上）。
function onUpRow(item, e) {
  const mid = upMidOf(item)
  if (mid == null) return
  e.preventDefault()
  e.stopPropagation()
  goUp(mid)
}

// 封面放大只认「鼠标在封面上」（站长 11-02 命中区收口）：上一版靠卡层的 group-hover 反推，停在标题、UP 行、行内空白上也会把封面拉大，
// 那是动画在冒充归属（他那两句判对了）。横滚行里多张卡共用一个变量，存当前 hover 封面的那条 bvid（写法同 titleKey）。
const coverKey = ref('')

// 标题那抹粉只跟标题自己走：可点已经收到字面（外层 p 只管两行截断，内联 RouterLink 才是热区），
// 染粉与手型同一个元素、同一块区域（站长 11-02 红点那一发）。横滚行里多张卡共用一个变量，存当前 hover 标题的那条 bvid。
const titleKey = ref('')

// 大幕那行尾巴（播数与弹幕数）：图标交给模板（官方那两枚），这里只管数——取不到的那段整段不显。
// 与 UP 名拆开后各自拼，UP 名在才补那个「 · 」。
function heroViews(v) {
  return v.viewCountText || ''
}

function heroDanmaku(v) {
  return v.danmakuCount != null ? fmtCount(v.danmakuCount) : ''
}

function canManageOf(item) {
  return Boolean(byId.value.get(item.id)?.canManage ?? item.canManage)
}

function isArrived(item) {
  const full = byId.value.get(item.id)
  return full ? full.status === 2 : true // 分区行本就是已到站清单，认不到全集也当已到站
}

// ─── banner：从到站有封面的馆藏里按种子抽 6 件轮转 ───
const HERO_COUNT = 5
const heroList = shallowRef([])
const heroIdx = ref(0)
const heroPaused = ref(false)
let heroTimer = null

function shuffleScore(id, seed) {
  let h = (Math.imul(id, 0x9e3779b1) ^ seed) >>> 0
  h = Math.imul(h ^ (h >>> 15), 0x85ebca6b)
  h = Math.imul(h ^ (h >>> 13), 0xc2b2ae35)
  return (h ^ (h >>> 16)) >>> 0
}

function buildHero() {
  const arrived = props.videos.filter((v) => v.status === 2 && (v.localCover || v.coverUrl))
  if (arrived.length === 0) {
    heroList.value = []
    return
  }
  heroList.value = [...arrived].sort((a, b) => shuffleScore(a.id, props.seed) - shuffleScore(b.id, props.seed)).slice(0, HERO_COUNT)
  if (heroIdx.value >= heroList.value.length) heroIdx.value = 0
}

const heroCur = computed(() => heroList.value[heroIdx.value] || null)

watch(heroIdx, () => {
  const jump = new Set()
  heroList.value.forEach((v, i) => {
    const rel = frameOffset(i)
    const prev = lastOffsets.get(v.bvid)
    if (prev !== undefined && Math.abs(rel - prev) > 1) jump.add(v.bvid)
    lastOffsets.set(v.bvid, rel)
  })
  teleporting.value = jump
  // 瞬位完成（两帧后）再把过渡还给它们
  requestAnimationFrame(() => requestAnimationFrame(() => { teleporting.value = new Set() }))
})
// banner 大图吃到 1280 缩略：模糊底和清晰面同源，不拉 MB 原图
function heroCoverOf(v) {
  if (!v) return ''
  return v.localCover || coverThumb(v.coverUrl, 1280)
}

function heroPrev() {
  const n = heroList.value.length
  if (n > 1) heroIdx.value = (heroIdx.value - 1 + n) % n
}
function heroNext() {
  const n = heroList.value.length
  if (n > 1) heroIdx.value = (heroIdx.value + 1) % n
}

watch(
  () => [props.seed, props.videos.length],
  buildHero,
  { immediate: true },
)

// ─── 分区行：瓷砖序 + 每区 items；新到站（与首页 7 天口径一致）置顶 ───
const NEW_ARRIVAL_MS = 7 * 24 * 3600 * 1000
const zones = ref([])
const zoneItems = ref({}) // 区名 → items（来自 /api/zones/videos）
const rowsReady = ref(false)
const videoIndex = computed(() => new Map(props.videos.map((v) => [v.id, v])))

const newArrivals = computed(() =>
  props.videos
    .filter((v) => v.status === 2 && v.createdAtUtc && Date.now() - Date.parse(v.createdAtUtc) <= NEW_ARRIVAL_MS)
    .sort((a, b) => (b.publishedAtUtc || b.createdAtUtc || '').localeCompare(a.publishedAtUtc || a.createdAtUtc || ''))
    .slice(0, 24),
)

// 分区行条目：zone 端点没带时长/播放数/弹幕数，拿全集按 id 补上（对不上就整段不显，不编数）
function patchItem(it) {
  const full = videoIndex.value.get(it.id)
  return full ? { ...it, durationText: full.durationText, viewCountText: full.viewCountText, danmakuCount: full.danmakuCount } : it
}

// 弹幕条数按 B 站口径：过万折成「1.2万」，整万去小数尾（与 VideoCard 同一张嘴）
function fmtCount(n) {
  if (n == null) return null
  if (n < 10000) return String(n)
  return (n / 10000).toFixed(1).replace(/\.0$/, '') + '万'
}

const rows = computed(() => {
  const out = []
  // 新到站按时间倒序，语义就是「刚来的」，不参与洗牌
  if (newArrivals.value.length) out.push({ key: '__new', name: '新到站', items: newArrivals.value.map(patchItem), fresh: true })
  // 分区行跟着换一批的种子重排：seed 变，每行卡序重洗（与 banner 同频）
  for (const z of zones.value) {
    const items = [...(zoneItems.value[z.name] || [])]
      .sort((a, b) => shuffleScore(a.id, props.seed) - shuffleScore(b.id, props.seed))
      .map(patchItem)
    out.push({ key: z.name, name: z.name, count: z.count, items })
  }
  return out
})

// 分区总览入口卡的底图：后端按次数的冠军分区里最火那条的封面（只是封面，
// 随 /api/zones 同口带回，不另发请求；拿不到就回落深渐变）
const zoneHotCover = ref('')

async function loadRows() {
  rowsReady.value = false
  try {
    const res = await fetch('/api/zones')
    const data = res.ok ? await res.json().catch(() => null) : null
    zones.value = Array.isArray(data?.zones) ? data.zones : []
    const hot = data?.hot?.cover
    // 后端给的 cover 已是「本地原图优先」的成品，只有热链才需要折 B 站图床缩图（与另两张入口卡同口径）
    zoneHotCover.value = typeof hot === 'string' && hot ? (/^https?:\/\//.test(hot) ? coverThumb(hot, 480) : hot) : ''
    const lists = await Promise.all(
      zones.value.map(async (z) => {
        try {
          const r = await fetch(`/api/zones/videos?name=${encodeURIComponent(z.name)}`)
          const d = r.ok ? await r.json().catch(() => null) : null
          return [z.name, Array.isArray(d?.items) ? d.items : []]
        } catch {
          return [z.name, []]
        }
      }),
    )
    zoneItems.value = Object.fromEntries(lists)
  } catch {
    zones.value = []
    zoneHotCover.value = ''
  } finally {
    rowsReady.value = true
    nextTick(measureAllRows)
  }
}

// ─── 横滚行：只有溢出的行才有左右圆箭头；滚到头尾那头箭头就收起来 ───
const rowEls = new Map()
const rowMeta = ref({}) // key → { overflow, atStart, atEnd }

function setRowEl(key, el) {
  if (el) rowEls.set(key, el)
  else rowEls.delete(key)
}

function measureRow(key) {
  const el = rowEls.get(key)
  if (!el) return
  const overflow = el.scrollWidth - el.clientWidth > 8
  const atStart = el.scrollLeft <= 4
  const atEnd = el.scrollLeft + el.clientWidth >= el.scrollWidth - 4
  const prev = rowMeta.value[key]
  if (!prev || prev.overflow !== overflow || prev.atStart !== atStart || prev.atEnd !== atEnd) {
    rowMeta.value = { ...rowMeta.value, [key]: { overflow, atStart, atEnd } }
  }
}

function measureAllRows() {
  for (const key of rowEls.keys()) measureRow(key)
}

function scrollRow(key, dir) {
  const el = rowEls.get(key)
  if (el) el.scrollBy({ left: dir * el.clientWidth * 0.8, behavior: 'smooth' })
}

function onWindowResize() {
  measureAllRows()
}

// ─── 入口卡里的合集统计：抽屉数 + 总件数，拉不到就整行小字不显 ───
const collStat = ref(null)
async function loadCollStat() {
  try {
    const res = await fetch('/api/collections')
    const data = res.ok ? await res.json().catch(() => null) : null
    if (Array.isArray(data?.collections)) {
      collStat.value = {
        count: data.collections.length,
        items: data.collections.reduce((s, c) => s + (c.itemCount || 0), 0),
      }
      collCover.value = pickDailyCover(data.collections)
    }
  } catch {
    /* 入口卡小字缺席而已 */
  }
}

// ─── 抽屉入口卡也铺底图：每日一抽，从抽屉的现成封面里挑一张（同一天刷新来回去的是同一张）───
// 抽屉的 cover 就是「最新到货那条的封面」（后端相关子查询给的现成 /media 成品），本就是某件视频的图；
// 要摊平到“一切收藏里抽”得逐座抽屉再拉详情，几十座就是几十次请求，不划算。
// /api/collections 本来就是公开两口，游客一样能看到底图，不写任何权限分支。
const collCover = ref('')

// 种子用本地（UTC+8）日期，跟周报同一条换日尺；零点一过自然换一张，服务端不存任何东西
function daySeed() {
  const t = new Date(Date.now() + 8 * 3600e3)
  return t.getUTCFullYear() * 10000 + (t.getUTCMonth() + 1) * 100 + t.getUTCDate()
}

function pickDailyCover(list) {
  let best = ''
  let bestScore = -1
  for (const c of list) {
    if (!c.cover) continue
    const score = shuffleScore(c.id, daySeed())
    if (score > bestScore) {
      bestScore = score
      best = c.cover
    }
  }
  return best
}

// ─── 分站周报入口卡（案 C）：底图铺这一周新货头一张的封面，横向压暗，文字全左 ───
// /api/weekly 是成员专口：游客拉不到、或那一周没新货，就拿不到底图，整卡回落原来的蓝渐变，不摆黑块。
const weeklyEntry = ref(null)
async function loadWeeklyEntry() {
  try {
    const res = await fetch('/api/weekly?week=0')
    const data = res.ok ? await res.json().catch(() => null) : null
    if (!data?.weekLabel) return
    const hit = (data.news?.items ?? []).find((n) => n.coverUrl)
    // 后端给的 coverUrl 已是「本地原图优先」的成品，只有热链才需要折 B 站图床缩图（与周报页头图同口径）
    const cover = hit ? (/^https?:\/\//.test(hit.coverUrl) ? coverThumb(hit.coverUrl, 480) : hit.coverUrl) : ''
    weeklyEntry.value = { week: data.weekLabel, cover }
  } catch {
    /* 底图缺席而已：卡回落蓝渐变 */
  }
}

// 封面与时间的口径与 VideoCard 一致：本地原图优先，热链走 480 缩图
function coverOf(v) {
  return v.localCover || (v.coverUrl ? coverThumb(v.coverUrl, 480) : '')
}

function relTime(d) {
  if (!d) return ''
  const diff = Date.now() - d.getTime()
  if (diff < 60e3) return '刚刚'
  if (diff < 3600e3) return `${Math.floor(diff / 60e3)}分钟前`
  if (diff < 86400e3) return `${Math.floor(diff / 3600e3)}小时前`
  if (diff < 30 * 86400e3) return `${Math.floor(diff / 86400e3)}天前`
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

onMounted(() => {
  loadRows()
  loadCollStat()
  loadWeeklyEntry()
  heroTimer = setInterval(() => {
    if (!heroPaused.value && heroList.value.length > 1) heroIdx.value = (heroIdx.value + 1) % heroList.value.length
  }, 10000)
  window.addEventListener('resize', onWindowResize)
})

onBeforeUnmount(() => {
  if (heroTimer) clearInterval(heroTimer)
  window.removeEventListener('resize', onWindowResize)
})
</script>

<template>
  <div>
    <!-- ═══ 顶部 banner：通屏出血（拆出 1400 容器，左右贴到视口边缘，官网同款）——
         模糊底铺满 + 清晰封面居右 + 右下信息胶囊；hover 暂停轮转；高度随视口 300~560px 伸缩 ═══ -->
    <section
      v-if="heroCur"
      class="group relative -mt-6 left-1/2 h-[clamp(300px,50vh,560px)] w-screen max-w-none -translate-x-1/2 overflow-hidden bg-pageBg"
      @mouseenter="heroPaused = true"
      @mouseleave="heroPaused = false"
    >
      <!-- 五帧常驻淡入接力（D 档巨图毛玻璃·四季盖章版）：每帧 = 同图放大铺满的 11px 薄雾底 + 居中清晰封面；
           图早已预载，切帧只换透明度，无重建闪白；底缘由 .hero-frame 自泄光接入 pageBg -->
      <div
        v-for="(v, i) in heroList"
        :key="v.bvid"
        class="hero-frame transition-opacity duration-700 ease-out will-change-[opacity]"
        :class="i === heroIdx ? 'opacity-100' : 'opacity-0'"
      >
        <div class="absolute inset-[-60px] bg-cover bg-center blur-[11px] saturate-[1.05]" :style="{ backgroundImage: `url('${heroCoverOf(v)}')` }"></div>
        <div
          class="absolute inset-y-0 left-1/2 aspect-[16/10] -translate-x-1/2 bg-cover bg-center"
          :style="{ backgroundImage: `url('${heroCoverOf(v)}')`, maskImage: 'linear-gradient(to right, transparent, black 16%, black 84%, transparent)', WebkitMaskImage: 'linear-gradient(to right, transparent, black 16%, black 84%, transparent)' }"
        ></div>
      </div>

      <!-- 信息胶囊：▶ + 标题 + UP/播放数，整颗点进放映厅；住在 tab 排头顶，不被小图咬住副标题 -->
      <RouterLink
        :to="`/watch/${heroCur.bvid}`"
        class="absolute bottom-[52px] right-[calc(max(20px,(100vw-1400px)/2+20px))] z-10 flex max-w-[min(560px,72%)] items-center gap-2.5 rounded-full bg-black/60 py-1.5 pl-1.5 pr-4 text-white shadow-lg backdrop-blur transition hover:bg-black/72"
      >
        <span class="flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-[#1ba4d6]">
          <svg viewBox="0 0 24 24" class="ml-0.5 h-4 w-4" fill="currentColor">
            <path d="M8 5v14l11-7z" />
          </svg>
        </span>
        <span class="min-w-0">
          <span class="block truncate text-[14px] font-medium">{{ heroCur.title || heroCur.bvid }}</span>
          <span class="block truncate text-[11.5px] text-white/60">
            <span
              v-if="heroCur.upMid != null"
              class="-mx-1 cursor-pointer px-1 transition hover:text-stationBlue"
              title="这位在分站的空间"
              @click.stop.prevent="goUp(heroCur.upMid)"
            ><UpIcon v-if="heroCur.upName" class="mr-1 inline-block h-[14px] w-[14px] align-[-2px] opacity-80" />{{ heroCur.upName }}</span>
            <template v-else><UpIcon v-if="heroCur.upName" class="mr-1 inline-block h-[14px] w-[14px] align-[-2px] opacity-80" />{{ heroCur.upName }}</template>
            <template v-if="heroViews(heroCur)">{{ heroCur.upName ? ' · ' : '' }}<span class="inline-flex items-center gap-0.5"><PlayCountIcon class="h-[13px] w-[13px]" />{{ heroViews(heroCur) }}</span></template>
            <template v-if="heroDanmaku(heroCur)">{{ heroCur.upName || heroViews(heroCur) ? ' · ' : '' }}<span class="inline-flex items-center gap-0.5"><DanmakuCountIcon class="h-[13px] w-[13px]" />{{ heroDanmaku(heroCur) }}</span></template>
          </span>
        </span>
      </RouterLink>

      <!-- 左右圆箭头：hover 大幕才现身 -->
      <button
        v-if="heroList.length > 1"
        class="absolute left-3 top-1/2 z-10 flex h-9 w-9 -translate-y-1/2 items-center justify-center rounded-full bg-black/35 text-white/85 opacity-0 backdrop-blur transition hover:bg-black/55 group-hover:opacity-100"
        aria-label="上一张"
        @click="heroPrev"
      >
        <svg viewBox="0 0 24 24" class="h-4 w-4" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M14.5 5.5 8 12l6.5 6.5" /></svg>
      </button>
      <button
        v-if="heroList.length > 1"
        class="absolute right-3 top-1/2 z-10 flex h-9 w-9 -translate-y-1/2 items-center justify-center rounded-full bg-black/35 text-white/85 opacity-0 backdrop-blur transition hover:bg-black/55 group-hover:opacity-100"
        aria-label="下一张"
        @click="heroNext"
      >
        <svg viewBox="0 0 24 24" class="h-4 w-4" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M9.5 5.5 16 12l-6.5 6.5" /></svg>
      </button>
      <!-- 大幕右上角的投稿人自管两钮已按站长要求收起（大幕保持干净）：
           补采评论与删档在分区横滚行的卡片上仍有同款入口，网格卡阵也照常能操作 -->
    </section>
    <!-- ═══ 小图 tab 排：压住大幕下缘，选中放大浮起、名字落到卡下 ═══ -->
    <!-- ═══ 小图 tab 排：五颗居中放大（186px，较 124 放大一半），压住大幕下缘，选中再浮起、名字落到卡下 ═══ -->
    <div v-if="heroList.length > 1" class="no-scrollbar relative z-10 -mt-12 flex items-start justify-center gap-3 overflow-x-auto px-3 pb-9 pt-5">
      <div
        v-for="(v, i) in heroList"
        :key="`tab-${v.id}`"
        class="group/tab relative shrink-0 text-left transition-transform duration-300"
        :class="i === heroIdx ? 'z-10 scale-[1.13]' : 'opacity-95 hover:opacity-100 hover:-translate-y-0.5'"
        @mouseenter="heroIdx = i"
      >
        <!-- hover 选中、点击进放映厅：与 banner 胶囊同一套互动语法（悬停管选中，点击不浪费） -->
        <RouterLink :to="`/watch/${v.bvid}`" :aria-label="`打开 ${v.title || v.bvid}`">
          <div
            class="aspect-[153/83] w-[186px] overflow-hidden rounded-lg bg-[#f1f2f3] dark:bg-[#23262e]"
            :class="i === heroIdx ? 'ring-2 ring-white shadow-xl dark:ring-white/80' : 'ring-1 ring-black/10 dark:ring-white/10'"
          >
            <img v-if="coverOf(v)" :src="coverOf(v)" :alt="v.title || v.bvid" loading="lazy" decoding="async" referrerpolicy="no-referrer" class="h-full w-full object-cover" />
            <!-- 未选中：名字白字压在图内底缘 -->
            <span v-if="i !== heroIdx" class="absolute inset-x-1 bottom-1 truncate rounded bg-gradient-to-t from-black/65 to-transparent pb-0.5 pl-1 text-[11px] text-white drop-shadow-[0_1px_2px_rgba(0,0,0,0.8)]">{{ v.title || v.bvid }}</span>
          </div>
        </RouterLink>
        <!-- 选中：名字落到卡下的白地上 -->
        <p v-if="i === heroIdx" class="mt-1 w-[186px] truncate text-[12px] font-medium text-inkMain">{{ v.title || v.bvid }}</p>
      </div>
    </div>

    <!-- ═══ 三张浅色渐变入口卡（官网 番剧索引/类型风格/首播时间 的同款站位） ═══ -->
    <div class="mt-8 grid grid-cols-1 gap-4 md:grid-cols-3">
      <RouterLink
        to="/zones"
        class="entry entry-pink entry-cover group"
        :class="{ 'has-cover': !!zoneHotCover }"
        :style="zoneHotCover ? { backgroundImage: `url('${zoneHotCover}')` } : null"
      >
        <div v-if="zoneHotCover" class="cover-blur" :style="{ backgroundImage: `url('${zoneHotCover}')` }"></div>
        <div class="entry-scrim"></div>
        <p class="entry-title"><span>分区</span><span>总览 <span class="chev">›</span></span></p>
        <p class="entry-sub">
          <span v-for="z in zones.slice(0, 4)" :key="z.name">{{ z.name }} {{ z.count }}</span>
          <span v-if="!zones.length">翻遍每一户</span>
        </p>
      </RouterLink>
      <RouterLink
        to="/collections"
        class="entry entry-violet entry-cover group"
        :class="{ 'has-cover': !!collCover }"
        :style="collCover ? { backgroundImage: `url('${collCover}')` } : null"
      >
        <div v-if="collCover" class="cover-blur" :style="{ backgroundImage: `url('${collCover}')` }"></div>
        <div class="entry-scrim"></div>
        <p class="entry-title"><span>合集</span><span>抽屉 <span class="chev">›</span></span></p>
        <p class="entry-sub">{{ collStat ? `${collStat.count} 座抽屉 · ${collStat.items} 件收藏` : '成员自建的小书柜' }}</p>
      </RouterLink>
      <RouterLink
        to="/weekly"
        class="entry entry-blue entry-cover group"
        :class="{ 'has-cover': !!weeklyEntry?.cover }"
        :style="weeklyEntry?.cover ? { backgroundImage: `url('${weeklyEntry.cover}')` } : null"
      >
        <div v-if="weeklyEntry?.cover" class="cover-blur" :style="{ backgroundImage: `url('${weeklyEntry.cover}')` }"></div>
        <div class="entry-scrim"></div>
        <p class="entry-title"><span>分站</span><span>周报 <span class="chev">›</span></span></p>
        <p v-if="weeklyEntry?.week" class="entry-sub">{{ weeklyEntry.week }}</p>
      </RouterLink>
    </div>

    <!-- ═══ 分区横滚行：新到站置顶，其余按瓷砖计数降序；行头右侧「查看全部」跳分区页 ═══ -->
    <section v-for="row in rows" :key="row.key" class="group/row relative mt-9">
      <div class="mb-3 flex items-center gap-2">
        <span v-if="!row.fresh" class="text-[19px] leading-none" aria-hidden="true">🔥</span>
        <svg v-else viewBox="0 0 24 24" class="h-[18px] w-[18px] text-amber-400" fill="currentColor">
          <path d="M12 2.5 13.9 8l5.6 1.6-4.4 3.7 1.3 5.7-4.4-3.3-4.4 3.3 1.3-5.7L4.5 9.6 10.1 8 12 2.5Z" />
        </svg>
        <h2 class="text-[21px] font-semibold text-inkMain">{{ row.name }}</h2>
        <span v-if="row.count != null" class="mt-1 text-[12px] text-inkFaint">{{ row.count }} 件</span>
        <RouterLink
          v-if="!row.fresh"
          :to="`/zones?zone=${encodeURIComponent(row.name)}`"
          class="ml-auto shrink-0 rounded-lg border border-black/10 px-3 py-1.5 text-[12.5px] text-inkSub transition hover:border-stationPink hover:text-stationPink dark:border-white/15"
        >查看全部 ›</RouterLink>
      </div>

      <div :ref="(el) => setRowEl(row.key, el)" class="no-scrollbar flex gap-4 overflow-x-auto scroll-smooth pb-1" @scroll.passive="measureRow(row.key)">
        <div v-for="item in row.items" :key="item.bvid" class="group relative w-[228px] shrink-0">
          <!-- 命中区收口（站长 11-02 在调参台上定的 coverTitleText）：外层那根包整卡的 RouterLink 撤了——
               整块可点会把统计行与标题方框里的空白也算成热区。可点的只剩封面（下面那枚）与标题的字面（内联那枚），
               UP 行自己可点，统计行与底部余量都是死区。本层的 group 与 relative 留着：右上那两枚悬停管理钮靠 group 显隐、靠 relative 定位。 -->
          <!-- 封面：指向放映厅的那枚链接。放大只跟「鼠标在封面上」走（coverKey），停在标题、UP 行上都不再牵动它 -->
          <RouterLink
            :to="`/watch/${item.bvid}`"
            class="relative block aspect-[16/10] overflow-hidden rounded-lg bg-[#f1f2f3] dark:bg-[#23262e]"
            @mouseenter="coverKey = item.bvid"
            @mouseleave="coverKey = ''"
          >
            <img v-if="coverOf(item)" :src="coverOf(item)" :alt="item.title || item.bvid" loading="lazy" decoding="async" referrerpolicy="no-referrer" class="h-full w-full object-cover transition duration-300" :class="coverKey === item.bvid ? 'scale-[1.04]' : ''" />
            <span v-else class="flex h-full w-full items-center justify-center text-[11px] tracking-widest text-inkFaint">{{ item.bvid }}</span>
            <!-- 封面底部数据区：与 VideoCard 同款——半透渐变带垫底，左「官方播放量图标 + 数 / 官方弹幕图标 + 数」右时长，描边白字 -->
            <div class="pointer-events-none absolute inset-x-0 bottom-0 h-12 bg-gradient-to-t from-black/55 via-black/25 to-transparent"></div>
            <div class="absolute inset-x-0 bottom-0 flex items-center justify-between px-2 pb-1.5">
              <span class="flex items-center gap-2 text-[12px] text-white drop-shadow-[0_1px_2px_rgba(0,0,0,0.85)]">
                <span class="flex items-center gap-1">
                  <PlayCountIcon class="h-[18px] w-[18px]" />
                  {{ item.viewCountText ?? '—' }}
                </span>
                <span v-if="item.danmakuCount != null" class="flex items-center gap-1">
                  <DanmakuCountIcon class="h-[18px] w-[18px]" />
                  {{ fmtCount(item.danmakuCount) }}
                </span>
              </span>
              <span v-if="item.durationText" class="text-[13px] font-medium text-white drop-shadow-[0_1px_2px_rgba(0,0,0,0.85)]">{{ colonTime(item.durationText) }}</span>
            </div>
          </RouterLink>
          <!-- 标题区 42px（调参台再一轮，站长 11-02）：方框尺寸一个都不动（min-h / leading / 字号照旧），
               可点只给字面——外层 p 管两行截断与 tooltip，真正可点的是内联那枚 RouterLink，染粉与手型同一个元素、同一块区域。
               跟标题分家的只有下面那行 UP——它自己可点，上借 3px、下借 4px。 -->
          <p class="mt-2 line-clamp-2 min-h-[42px] text-[14.5px] font-medium leading-[20px] text-inkMain" :title="item.title || item.bvid">
            <RouterLink
              :to="`/watch/${item.bvid}`"
              class="transition"
              :class="titleKey === item.bvid ? 'text-stationPink' : ''"
              @mouseenter="titleKey = item.bvid"
              @mouseleave="titleKey = ''"
            >{{ item.title || item.bvid }}</RouterLink>
          </p>
          <div
            class="group/up relative mt-0.5"
            :class="upMidOf(item) != null ? 'cursor-pointer' : ''"
            @click="onUpRow(item, $event)"
          >
            <span v-if="upMidOf(item) != null" class="absolute -top-[3px] -bottom-[4px] inset-x-0" aria-hidden="true"></span>
            <p class="truncate text-[12px] text-inkFaint">
              <!-- UP 徽标：与主页卡同一枚官方描边款（站长 11-02：没有的就直接补上），12px 字旁按官方比例取 15px。 -->
              <UpIcon v-if="item.upName" class="mr-1 inline-block h-[15px] w-[15px] align-[-2px]" />
              <span
                v-if="upMidOf(item) != null"
                class="transition hover:text-stationBlue group-hover/up:text-stationBlue"
                title="这位在分站的空间"
              >{{ item.upName }}</span>
              <template v-else>{{ item.upName }}</template>
              <template v-if="relTime(publishedDate(item))">{{ item.upName ? ' · ' : '' }}{{ relTime(publishedDate(item)) }}</template>
            </p>
          </div>
          <!-- 投稿人自管两钮：样式与网格卡阵（AdminVideoCell）那两枚同一套——hover 浮现、右上并排、黑玻离底。
               它们住在封面与标题链接的外面，靠卡层的 group 显隐、靠 prevent.stop 抢下点击。 -->
          <button
            v-if="canManageOf(item) && isArrived(item)"
            class="absolute right-9 top-2 hidden rounded-md bg-black/60 p-1.5 text-white/70 backdrop-blur transition hover:bg-stationBlue hover:text-white group-hover:block disabled:opacity-40"
            :disabled="commentBusy.has(item.id)"
            :title="HINT_FETCH_COMMENTS"
            @click.prevent.stop="$emit('fetch-comments', item)"
          >
            <svg viewBox="0 0 24 24" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M21 12a8 8 0 0 1-11.6 7.1L4 21l1.9-5.4A8 8 0 1 1 21 12z" stroke-linecap="round" stroke-linejoin="round" />
            </svg>
          </button>
          <button
            v-if="canManageOf(item)"
            class="absolute right-2 top-2 hidden rounded-md bg-black/60 p-1.5 text-white/70 backdrop-blur transition hover:bg-rose-500 hover:text-white group-hover:block"
            :title="HINT_DELETE_VIDEO"
            @click.prevent.stop="$emit('remove', item)"
          >
            <svg viewBox="0 0 24 24" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M4 7h16M9 7V5a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2m3 0-1 13a1 1 0 0 1-1 1H8a1 1 0 0 1-1-1L6 7" stroke-linecap="round" stroke-linejoin="round" />
            </svg>
          </button>
        </div>
      </div>

      <!-- 溢出才有的左右圆箭头：hover 整行浮现，滚到头那侧收起 -->
      <template v-if="rowMeta[row.key]?.overflow">
        <button
          v-if="!rowMeta[row.key]?.atStart"
          class="absolute left-[-10px] top-[116px] z-10 flex h-8 w-8 items-center justify-center rounded-full bg-white text-inkSub opacity-0 shadow-[0_2px_10px_rgba(0,0,0,0.16)] transition hover:text-stationPink group-hover/row:opacity-100 dark:bg-[#23262e] dark:text-white/70"
          :aria-label="`${row.name}往前滚`"
          @click="scrollRow(row.key, -1)"
        >
          <svg viewBox="0 0 24 24" class="h-4 w-4" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M14.5 5.5 8 12l6.5 6.5" /></svg>
        </button>
        <button
          v-if="!rowMeta[row.key]?.atEnd"
          class="absolute right-[-10px] top-[116px] z-10 flex h-8 w-8 items-center justify-center rounded-full bg-white text-inkSub opacity-0 shadow-[0_2px_10px_rgba(0,0,0,0.16)] transition hover:text-stationPink group-hover/row:opacity-100 dark:bg-[#23262e] dark:text-white/70"
          :aria-label="`${row.name}往后滚`"
          @click="scrollRow(row.key, 1)"
        >
          <svg viewBox="0 0 24 24" class="h-4 w-4" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M9.5 5.5 16 12l-6.5 6.5" /></svg>
        </button>
      </template>
    </section>

    <!-- 骨架与空态 -->
    <div v-if="!rowsReady" class="mt-9 space-y-6">
      <div v-for="i in 2" :key="i" class="animate-pulse">
        <div class="h-6 w-32 rounded bg-black/[0.05] dark:bg-white/[0.06]"></div>
        <div class="mt-3 flex gap-4">
          <div v-for="j in 5" :key="j" class="w-[228px] shrink-0">
            <div class="aspect-[16/10] rounded-lg bg-black/[0.05] dark:bg-white/[0.06]"></div>
            <div class="mt-2 h-3.5 w-11/12 rounded bg-black/[0.05] dark:bg-white/[0.06]"></div>
          </div>
        </div>
      </div>
    </div>
    <div v-else-if="!rows.length && !heroCur" class="rounded-xl border border-dashed border-black/10 dark:border-white/10 py-16 text-center">
      <p class="text-[13px] text-inkSub">馆藏还空着，番剧区先谢客</p>
    </div>
  </div>
</template>

<style scoped>
/* 帧层底缘自泄光：整帧（模糊底+清晰面）下半截按近余弦曲线渐透到 0，
   露出的是 banner 底下的 pageBg 本身——图与页之间没有任何盖层，这才是真连续 */
.hero-frame {
  position: absolute;
  inset: 0;
  -webkit-mask-image: linear-gradient(to bottom, #000 0%, #000 52%, rgb(0 0 0 / 0.78) 66%, rgb(0 0 0 / 0.55) 77%, rgb(0 0 0 / 0.33) 86%, rgb(0 0 0 / 0.14) 93%, transparent 100%);
  mask-image: linear-gradient(to bottom, #000 0%, #000 52%, rgb(0 0 0 / 0.78) 66%, rgb(0 0 0 / 0.55) 77%, rgb(0 0 0 / 0.33) 86%, rgb(0 0 0 / 0.14) 93%, transparent 100%);
}

/* 横滚行那根滚动条藏起来的事，交给 style.css 全站的 .no-scrollbar 一份 */

/* 入口卡：三张统一深底白字的海报卡。有封面（合集每日一抽 / 周报新货头一张）铺封面 + 左糊 + 左压暗；
   没封面回落到各自色系的深渐变，压暗层常驻，白字永远有兜底。 */
.entry {
  position: relative;
  display: block;
  overflow: hidden;
  border-radius: 12px;
  min-height: 121px;
  padding: 16px;
  transition:
    transform 0.2s ease,
    box-shadow 0.2s ease;
}
.entry:hover {
  transform: translateY(-2px);
  box-shadow: 0 10px 26px rgba(24, 25, 28, 0.16);
}
/* 回落底一律用 background-image 单写，不用 background 简写：
   简写会把 background-size 一并重置成 auto，html.dark 那条特异性比 .entry-cover 高，
   会把封面的 background-size: cover 顶掉（黑夜下封面“出毛病”就是这么来的）。 */
.entry-pink {
  background-image: linear-gradient(135deg, #5a2033 0%, #2e1019 100%);
}
.entry-violet {
  background-image: linear-gradient(135deg, #382d66 0%, #1c1633 100%);
}
.entry-blue {
  background-image: linear-gradient(135deg, #1e4266 0%, #0f1e2e 100%);
}
html.dark .entry-pink {
  background-image: linear-gradient(135deg, #4a1a2b 0%, #240c14 100%);
}
html.dark .entry-violet {
  background-image: linear-gradient(135deg, #2f2557 0%, #161028 100%);
}
html.dark .entry-blue {
  background-image: linear-gradient(135deg, #193a59 0%, #0b1725 100%);
}
/* 标题：两字一行当海报排，77px 大到卡装不下，越框的那截由卡自身 overflow:hidden 裁平 */
.entry .entry-title {
  position: absolute;
  left: 4px;
  top: calc(50% - 2px);
  transform: translateY(-50%);
  z-index: 10;
  margin: 0;
  font-size: 77px;
  font-weight: 800;
  line-height: 0.88;
  letter-spacing: -0.03em;
  opacity: 0.8;
  color: #fff;
  text-shadow:
    0 1px 0 rgba(0, 0, 0, 0.45),
    0 0 16px rgba(0, 0, 0, 0.55);
}
.entry .entry-title > span {
  display: block;
  white-space: nowrap;
}
/* 标题跟小字走同一套主色淡彩（参数台那三支），不写死白 */
.entry-pink .entry-title {
  color: #ffb3c8;
}
.entry-violet .entry-title {
  color: #c3b9ff;
}
.entry-blue .entry-title {
  color: #a8d4ff;
}
.entry .entry-title .chev {
  font-size: 0.6em;
  font-weight: 600;
  color: inherit;
  opacity: 0.8;
  transition:
    transform 0.2s ease,
    color 0.2s ease;
}
.entry:hover .entry-title .chev {
  transform: translateX(3px);
  color: #fff;
}
/* 右下小字：站长按参数台拧定的这版 */
.entry .entry-sub {
  position: absolute;
  right: 16px;
  bottom: 10px;
  z-index: 10;
  margin: 0;
  display: flex;
  flex-wrap: wrap;
  justify-content: flex-end;
  gap: 4px 13px;
  font-size: 12px;
  font-weight: 400;
  letter-spacing: 0.05em;
  line-height: 1.4;
  font-variant-numeric: normal;
  text-transform: none;
  font-family: inherit;
  opacity: 0.9;
  padding: 0;
  border-radius: 8px;
  background: none;
  text-shadow: 0 1px 8px rgba(0, 0, 0, 1);
}
/* 小字配色跟各自卡片同色系（参数台上的「主色淡彩」：分区粉 / 抽屉紫 / 周报蓝） */
.entry-pink .entry-sub {
  color: #ffb3c8;
}
.entry-violet .entry-sub {
  color: #c3b9ff;
}
.entry-blue .entry-sub {
  color: #a8d4ff;
}
/* 入口卡右下角的大水印（.entry-mark）已全面撤掉：三张都上了底图，
   水印压在封面上只会脏，它想说的意思也被右下那行小字说尽了。 */

/* 封面特效：原样铺底（不放大、不位移），上叠两层、都沿左右铺开——一糊一暗，重的都在左边（文字区），
   右边大半完全透原图。糊是「一层同图 + 横向 mask」：整张 blur 后用 mask 留左挖右，
   露出卡身那张清晰原图，所以是「左糊右清」，不是整张糊，也不是往中心吸的径向糊。 */
.entry-cover {
  background-size: cover;
  background-position: center;
  background-repeat: no-repeat;
}
.entry .cover-blur {
  position: absolute;
  inset: 0;
  background-size: cover;
  background-position: center;
  filter: blur(5px);
  -webkit-mask-image: linear-gradient(90deg, #000 0%, #000 22%, rgba(0, 0, 0, 0) 42%);
  mask-image: linear-gradient(90deg, #000 0%, #000 22%, rgba(0, 0, 0, 0) 42%);
}
/* 压暗常驻：有封面时按定稿的 .78，纯渐变卡收到 .55，免得把色相压没 */
.entry .entry-scrim {
  position: absolute;
  inset: 0;
  background: linear-gradient(
    90deg,
    rgba(12, 14, 18, 0.55) 0%,
    rgba(12, 14, 18, 0.28) 45%,
    rgba(12, 14, 18, 0) 80%
  );
}
.entry-cover.has-cover .entry-scrim {
  background: linear-gradient(
    90deg,
    rgba(18, 19, 22, 0.78) 0%,
    rgba(18, 19, 22, 0.45) 45%,
    rgba(18, 19, 22, 0) 80%
  );
}
</style>
