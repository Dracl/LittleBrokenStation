<script setup>
import { computed, onMounted, ref, watch } from 'vue'
import TopBar from '../components/TopBar.vue'
import DeltaTag from '../components/WeeklyDeltaTag.vue'
import { member } from '../memberStore'
import { coverThumb } from '../utils/cover'

// 分站周报：一张卡的工夫看完这一周——新货、弹幕增量、全站家底，外加 Top5 最多人看。
// 数据全走 /api/weekly（成员登录才给看）；周切换 0..4 **全是已过完的整周**：0=上一个刚过完的周，1..4 再往前数。
// 不给看「本周至今」：未过完的一周天天在长，同一个链接今天回看跟明天回看不一样，周报就不是凭据了。
const week = ref(0)
const data = ref(null)
const loading = ref(false)
const error = ref('')

// 页眉副标题：直接把桶界摆成人能读的日期区间——「已过完」这件事不用你信，自己看日期。
const weekCaption = computed(() => {
  const d = data.value
  if (!d) return week.value === 0 ? '最近一个已过完的整周' : `往前 ${week.value} 周`
  return `${localDay(d.sinceUtc)} ~ ${localDay(d.untilUtc, true)} · 已过完的一周`
})

// 桶界是 UTC 的周日 16:00（= 本地周一 00:00）；取日期先推回 +8 再按 UTC 切，不跟浏览器时区跑。
// 上界那一刀是开区间（本地次日 00:00），退一毫秒再取日，否则周报会多摆一天。
function localDay(isoUtc, endInclusive = false) {
  const t = new Date(new Date(isoUtc).getTime() + 8 * 3600e3 - (endInclusive ? 1 : 0))
  return `${String(t.getUTCMonth() + 1).padStart(2, '0')}-${String(t.getUTCDate()).padStart(2, '0')}`
}

// 环比基准：两桶都是已过完的整周（本桶再往前推七天），涨幅不偏不依，角标 title 里就写「上一周」
const deltaBase = computed(() => '上一周')
const isEmpty = computed(
  () => data.value && !data.value.news?.count && !data.value.hottest?.length && !data.value.danmakuAdded,
)

// 个人战报卡：只摆登录者自己干过的（>0 才露面）；全零时整卡隐身，不给空账摆谱。
// 时长三枚只认自己的心跳（别人与游客的一枪不进自己账）：text 存人话形（「1 小时 12 分」），value 仍给数字供零隐身判断。
const personalRows = computed(() => {
  const p = data.value?.personal
  if (!p) return []
  const pd = data.value?.personalDuration
  return [
    { label: '真看了', value: pd?.seconds ?? 0, text: pd && pd.seconds > 0 ? humanDuration(pd.seconds) : '', tip: '你自己心跳换算的墙钟时长（一枪 30 秒，看半分钟以下走人记 0，所以宁少不多）' },
    { label: '看完', value: pd?.finished ?? 0, unit: '部', tip: '看到片长 90% 以后就算看完（拖到片尾即认）' },
    { label: '每次进厅', value: pd?.avgOpenSeconds ?? 0, text: pd && pd.avgOpenSeconds > 0 ? humanDuration(pd.avgOpenSeconds) : '', tip: '真看时长 ÷ 进厅次数（进厅是母集，拿播放算会把时长压小）' },
    { label: '看了片子', value: p.videosWatched, unit: '部' },
    { label: '播放', value: p.plays, unit: '次' },
    { label: '进厅', value: p.opens, unit: '次' },
    { label: '搜索', value: p.searches, unit: '回' },
    { label: '投稿', value: p.submissions, unit: '次' },
    { label: '发弹幕', value: p.danmakuSent, unit: '条' },
    { label: '发私信', value: p.dmSent, unit: '封' },
    { label: '评论入库', value: p.commentsPosted, unit: '条' },
    { label: '登录', value: p.logins, unit: '回' },
  ].filter((r) => r.value > 0)
})

// 流水卡：只摆有动静（>0）的条目，零动条目隐身
const activityRows = computed(() => {
  const a = data.value?.activity
  if (!a) return []
  return [
    { label: '进厅', value: a.watchOpens, unit: '次' },
    { label: '播放', value: a.watchPlays, unit: '次' },
    { label: '观众人头', value: a.uniqueViewers, unit: '位' },
    { label: '搜索', value: a.searches, unit: '回' },
    { label: '投稿', value: a.submissions, unit: '次' },
    { label: '登录', value: a.logins, unit: '回' },
    { label: '评论入库', value: a.commentsArchived, unit: '条' },
    { label: '动态入库', value: a.dynamicsArchived, unit: '条' },
    { label: '私信', value: a.dmSent, unit: '封' },
    { label: '新席位', value: a.newMembers, unit: '位' },
  ].filter((r) => r.value > 0)
})

// 环比涨跌：上桶零数又本桶零数 = 无从比；上桶零、本桶有数 = 新增（大数卡留用，卡头角标不摆）。
// 注：分站开服首周的上桶必为零（那周还没开张），所以首周几乎处处无从比——这是真话，不编涨跌。
function deltaOf(key) {
  const d = (data.value?.deltas ?? []).find((x) => x.key === key)
  if (!d) return null
  if (d.last === 0 && d.current === 0) return null
  if (d.last === 0) return { dir: 'new', text: '上周没有', pct: null, baseValue: 0 }
  const diff = d.current - d.last
  const pct = Math.round((Math.abs(diff) / d.last) * 100)
  if (diff === 0) return { dir: 'flat', text: '持平', pct: 0, baseValue: d.last }
  return { dir: diff > 0 ? 'up' : 'down', text: `${pct}%`, pct, baseValue: d.last }
}

// 流水卡头的环比角标：只摆后端给了 delta 且真能比出大小的几枚（进厅/播放/人头/搜索/时长）
const activityDeltas = computed(() =>
  [
    { key: 'opens', label: '进厅' },
    { key: 'plays', label: '播放' },
    { key: 'viewers', label: '人头' },
    { key: 'searches', label: '搜索' },
    { key: 'watchTime', label: '时长' },
  ]
    .map((x) => ({ ...x, d: deltaOf(x.key) }))
    // 卡头角标只摆真能比出大小的：上周零数时「新增」只是「分站那周还没开张」，四枚一样的字没信息量，不摆
    .filter((x) => x.d && x.d.dir !== 'new'),
)

// 七日活动柱图：三根柱同看一个基准（当周最大的进厅/播放数）——进厅是播放的母集（播过的必定先进过厅），
// 拿子集做 100% 基准会让淡柱溢出柱座；游客柱拿枪数画（零动静的日子就是零高，不画桩）。
// 人头（成员去重 mid / 游客去重 IP）只进 tooltip：分站常年就站长一人在看，逐日摆「1 人」是重复噪音，
// 真多人同看那天（成员 > 1 或有游客）才在柱下显形。
const dayBars = computed(() => {
  const bars = data.value?.dayBars ?? []
  const max = Math.max(1, ...bars.map((b) => Math.max(b.opens, b.plays, b.guestShots)))
  return bars.map((b) => ({
    ...b,
    dayLabel: `${Number(b.localDate.slice(8, 10))}日`,
    playPct: Math.max(4, Math.round((b.plays / max) * 100)),
    openPct: Math.max(3, Math.round((b.opens / max) * 100)),
    guestPct: b.guestShots > 0 ? Math.max(3, Math.round((b.guestShots / max) * 100)) : 0,
    showHeads: b.heads > 1 || b.guestHeads > 0,
  }))
})

// 站外游客卡：只摆有动静的项（零隐身）；全零（这周没一个游客）整卡不摆。
// 口径：人数是 guest 行的去重 IP（同一出口会并成一人），次数是事件枪数；搜索只报回数，绝不摆关键词。
const guestRows = computed(() => {
  const g = data.value?.guest
  if (!g) return []
  return [
    { label: '游客人数', value: g.heads, unit: '位', tip: '未登录出没的去重 IP 数；同一出口网络会并成一人' },
    { label: '进厅', value: g.opens, unit: '次', tip: '游客打开片页的次数' },
    { label: '播放', value: g.plays, unit: '次', tip: '游客按下播放键的次数' },
    { label: '搜索', value: g.searches, unit: '回', tip: '只报回数，内容不入报' },
    { label: '想进片页被挡', value: g.blockedWatch, unit: '次', tip: '游客被门禁挡在片页外（哪天把片页锁进会员区，这枚就是想看却进不来的人数）' },
    { label: '弹幕被挡', value: g.blockedDanmaku, unit: '次', tip: '游客读弹幕被挡回的前端重试，当运维噪音看，不当观众' },
    { label: '想登录', value: g.loginTries, unit: '回', tip: '游客发扫码 / 密码试错等登录尝试的枪数' },
  ].filter((r) => r.value > 0)
})

// 时长榜：三栏同看一个量（真看时长），栏内拿榜首做 100% 缩短其余条长。
// 口径全在后端：只数成员心跳（游客看不了片，一枪不入账），一枪 = 30 秒墙钟。
const durationBars = computed(() => {
  const d = data.value?.duration
  if (!d) return { videos: [], ups: [], zones: [] }
  const shape = (rows) => {
    const list = rows ?? []
    const max = Math.max(1, ...list.map((r) => r.seconds))
    return list.map((r) => ({ ...r, pct: Math.max(4, Math.round((r.seconds / max) * 100)) }))
  }
  return { videos: shape(d.topVideos), ups: shape(d.topUps), zones: shape(d.topZones) }
})
const hasDuration = computed(() => {
  const d = data.value?.duration
  return !!d && (d.shots > 0 || durationBars.value.videos.length > 0)
})

// 订阅榜：后端已按「周内条数倒序、最后动态新的在前」排好（勤的排前）；全零时整卡转断更口吻
const staleRows = computed(() => (data.value?.staleUps ?? []).slice(0, 6))
const staleAllQuiet = computed(() => staleRows.value.length > 0 && staleRows.value.every((s) => s.dynThisWeek === 0))

// 分区占比条：拿榜首为 100%，其余按比例缩短（榜长固定 5，直接摆成横向条）
const zoneBars = computed(() => {
  const rows = data.value?.topZones ?? []
  const max = Math.max(1, ...rows.map((z) => z.plays))
  return rows.map((z) => ({ ...z, playPct: Math.max(6, Math.round((z.plays / max) * 100)) }))
})

const upBars = computed(() => {
  const rows = data.value?.topUps ?? []
  const max = Math.max(1, ...rows.map((u) => u.plays))
  return rows.map((u) => ({ ...u, playPct: Math.max(6, Math.round((u.plays / max) * 100)) }))
})

// 周报封面头图：这一周新货头一张的封面（没封面就不摆图，不摆黑块）
const heroCover = computed(() => {
  const hit = (data.value?.news?.items ?? []).find((n) => n.coverUrl)
  return hit ? coverSrc(hit.coverUrl) : ''
})

function fmtDays(n) {
  if (n == null) return '没档'
  if (n === 0) return '今天'
  if (n < 7) return `${n} 天`
  if (n < 30) return `${Math.floor(n / 7)} 周`
  return `${Math.floor(n / 30)} 个月`
}

// 封面口径与首页卡阵同源：后端给的是 localCover 优先的成品（/media/… 原图或热链），热链才折 B 站图床缩图。
function coverSrc(url) {
  if (!url) return ''
  return /^https?:\/\//.test(url) ? coverThumb(url, 480) : url
}

// 时长人话：够一小时报「1 小时 12 分」，不够报「35 分钟」，十分钟以下报「40 秒」。
function humanDuration(sec) {
  if (sec == null) return '—'
  if (sec < 60) return `${sec} 秒`
  const h = Math.floor(sec / 3600)
  const m = Math.floor((sec % 3600) / 60)
  if (h > 0) return m > 0 ? `${h} 小时 ${m} 分` : `${h} 小时`
  return `${m} 分钟`
}

function humanBytes(n) {
  if (n == null) return '—'
  if (n < 1024) return `${n} B`
  const units = ['KB', 'MB', 'GB', 'TB']
  let v = n / 1024
  let i = 0
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024
    i += 1
  }
  return `${v >= 100 ? Math.round(v) : v.toFixed(1)} ${units[i]}`
}

// 新货分页：这一周超 8 件才分页；没分页时整摆不分页（避免 8 件恰好断成一页的尴尬）
const newsPage = ref(0)
const newsPaged = computed(() => (data.value?.news?.items?.length ?? 0) > 8)
const newsPages = computed(() => Math.max(1, Math.ceil((data.value?.news?.items?.length ?? 0) / 8)))
const newsShown = computed(() => {
  const items = data.value?.news?.items ?? []
  if (!newsPaged.value) return items
  const page = Math.min(newsPage.value, Math.max(0, newsPages.value - 1))
  return items.slice(page * 8, page * 8 + 8)
})
watch(week, () => { newsPage.value = 0 })

// 新货条目的命中区收到最严一档（站长 11-02 在调参台上定的收口，口径同「接下来播放」UpNextList）：
// 一条只有封面与「压在标题字上」两处可点，底下的 bvid 行、标题第二行之后的空白与条目之间的缝全是死区。
// 所以整条那层 RouterLink 撤了，拆成封面一枚 + 标题内联一枚；group-hover 跟着下岗（它分不出鼠标落在条目的哪一块，
// 停在 bvid 上也会把封面放大、把标题染粉）。两个 key 各存当前那条的 bvid（v-for 的 :key），一条的动作不波及邻条。
const newsCoverKey = ref(null) // 鼠标当前压在哪条的封面上 → 封面放大
const newsTitleKey = ref(null) // 鼠标当前压在哪条的标题字上 → 只有标题染粉

// 「最多人看」那一列同套手感（站长 11-02 定的基准：封面可点 + 标题字面可点带染粉 + 读数行死区）。
// 另开两个 key 而不搭 newsCoverKey / newsTitleKey 那趟车：同一条片子完全可能本周既入新货又上热榜，
// 共用一个变量存 bvid 就会让两张卡的 hover 互相牵连（那边鼠标压字、这边也跟着染粉），所以按榜各存各的。
const hotCoverKey = ref(null) // 鼠标当前压在哪条热榜的封面上 → 封面放大（倍率与缓动跟新货那条对齐）
const hotTitleKey = ref(null) // 鼠标当前压在哪条热榜的标题字上 → 只有标题染粉

async function load() {
  loading.value = true
  error.value = ''
  try {
    const res = await fetch(`/api/weekly?week=${week.value}`)
    if (res.ok) {
      data.value = await res.json()
    } else {
      data.value = null
      const body = await res.json().catch(() => null)
      error.value = body?.message || `周报没拉到（HTTP ${res.status}）`
    }
  } catch {
    data.value = null
    error.value = '分站没搭腔，周报没拉到'
  } finally {
    loading.value = false
  }
}

onMounted(load)
watch(week, load)

// 上一周 = 往历史里再翻一周（最远翻到五周前）；下一周 = 回到更近的一周，最近一个整周（0 档）就是头一档，不许再往后。
function goPrev() {
  if (week.value < 4) week.value += 1
}
function goNext() {
  if (week.value > 0) week.value -= 1
}
</script>

<template>
  <div class="min-h-screen bg-pageBg">
    <TopBar />

    <main class="mx-auto max-w-[1080px] px-6 py-6">
      <!-- 页眉：周号 + 上一周/下一周切换 -->
      <div class="mb-4 flex flex-wrap items-center justify-between gap-3 rounded-2xl border border-lineSoft bg-cardBg bg-gradient-to-r from-stationPink/10 via-stationPink/5 to-transparent px-5 py-4">
        <div>
          <h1 class="text-[17px] font-semibold text-inkMain">
            分站周报<span v-if="data" class="ml-2 text-stationPink">{{ data.weekLabel }}</span>
          </h1>
          <p class="mt-0.5 text-xs text-inkFaint">{{ weekCaption }}</p>
        </div>
        <div class="flex shrink-0 items-center gap-2">
          <button
            class="rounded-full border px-3.5 py-1.5 text-sm transition"
            :class="week < 4 ? 'border-lineSoft text-inkSub hover:border-stationPink hover:text-stationPink' : 'cursor-not-allowed border-lineSoft/60 text-inkFaint'"
            :disabled="week >= 4"
            title="往前翻一周（最远 4 周）"
            @click="goPrev"
          >
            上一周
          </button>
          <button
            class="rounded-full border px-3.5 py-1.5 text-sm transition"
            :class="week > 0 ? 'border-lineSoft text-inkSub hover:border-stationPink hover:text-stationPink' : 'cursor-not-allowed border-lineSoft/60 text-inkFaint'"
            :disabled="week <= 0"
            title="回到更近的一周（最近一个整周就是头一档）"
            @click="goNext"
          >
            下一周
          </button>
        </div>
      </div>

      <!-- 加载中 / 拉胯了 -->
      <div v-if="loading" class="grid grid-cols-1 gap-4 sm:grid-cols-3">
        <div v-for="i in 3" :key="i" class="h-24 animate-pulse rounded-2xl bg-[#f1f2f3] dark:bg-[#23262e]"></div>
      </div>
      <div v-else-if="error" class="rounded-2xl border border-dashed border-lineSoft bg-cardBg px-6 py-14 text-center">
        <p class="text-sm text-inkSub">{{ error }}</p>
        <p v-if="!member" class="mt-2 text-xs text-inkFaint">登录后才能看周报，右上角登录即可。</p>
      </div>

      <template v-else-if="data">
        <!-- 周报头图：这一周新货头一张的封面（没封面不摆图） -->
        <div
          v-if="heroCover"
          class="mt-4 h-[190px] overflow-hidden rounded-2xl border border-lineSoft bg-[#f1f2f3] dark:bg-[#23262e] sm:h-[230px]"
        >
          <div class="relative h-full w-full">
            <img :src="heroCover" alt="" class="h-full w-full object-cover" loading="lazy" decoding="async" referrerpolicy="no-referrer" />
            <div class="absolute inset-0 bg-gradient-to-t from-black/55 via-black/10 to-transparent"></div>
            <div class="absolute bottom-0 left-0 right-0 flex items-end justify-between gap-3 p-4">
              <p class="line-clamp-2 max-w-[70%] text-[15px] font-semibold leading-[21px] text-white drop-shadow">
                {{ data.news.items?.[0]?.title || data.news.items?.[0]?.bvid }}
              </p>
              <span class="shrink-0 rounded-full bg-white/85 px-2.5 py-1 text-[11px] font-medium text-inkSub">{{ data.weekLabel }} 头一张</span>
            </div>
          </div>
        </div>

        <!-- 三行大数：新货 / 弹幕增量 / 全站家底（涨跌角标环比上周） -->
        <div class="mt-4 grid grid-cols-1 gap-4 sm:grid-cols-3">
          <div class="rounded-2xl border border-lineSoft bg-cardBg px-5 py-4">
            <p class="text-xs text-inkFaint">新货</p>
            <p class="mt-1 flex items-baseline gap-2 text-[26px] font-semibold leading-8 text-inkMain">
              {{ data.news.count }}<span class="text-sm font-normal text-inkFaint">件</span>
              <DeltaTag :d="deltaOf('news')" :base="deltaBase" />
            </p>
          </div>
          <div class="rounded-2xl border border-lineSoft bg-cardBg px-5 py-4">
            <p class="text-xs text-inkFaint">站内弹幕新增</p>
            <p class="mt-1 flex items-baseline gap-2 text-[26px] font-semibold leading-8 text-stationPink">
              +{{ data.danmakuAdded }}<span class="text-sm font-normal text-inkFaint">条</span>
              <DeltaTag :d="deltaOf('danmaku')" :base="deltaBase" />
            </p>
          </div>
          <div class="rounded-2xl border border-lineSoft bg-cardBg px-5 py-4">
            <p class="text-xs text-inkFaint">全站家底</p>
            <p class="mt-1 text-[26px] font-semibold leading-8 text-inkMain">
              {{ data.totals.videos }}<span class="ml-1 text-sm font-normal text-inkFaint">件稿件</span>
            </p>
            <p class="mt-0.5 text-[11px] text-inkFaint">
              {{ data.totals.members }} 位成员 · {{ data.totals.danmaku }} 条弹幕 · 库房 {{ humanBytes(data.totals.bytes) }}
            </p>
          </div>
        </div>

        <!-- 七日活动柱图：柱高=当日播放数，淡柱=当日进厅数（播放必 ≤ 进厅），没动静的日子不补零柱 -->
        <section v-if="dayBars.length" class="mt-4 overflow-hidden rounded-2xl border border-lineSoft bg-cardBg">
          <div class="weekly-head flex items-center justify-between border-b border-lineSoft px-5 py-3">
            <h2 class="text-[15px] font-semibold text-inkMain">七日动静</h2>
            <span class="flex items-center gap-3 text-[11px] text-inkFaint" title="柱高全部拿当周最大量归一；鼠标停在某一天可看当日全部明细">
              <span class="flex items-center gap-1"><i class="h-2 w-2 rounded-[2px] bg-stationPink"></i>播放</span>
              <span class="flex items-center gap-1"><i class="h-2 w-2 rounded-[2px] bg-stationPink/30"></i>进厅</span>
              <span class="flex items-center gap-1"><i class="h-2 w-2 rounded-[2px] bg-inkFaint/30"></i>游客</span>
            </span>
          </div>
          <div class="flex items-end gap-2.5 px-5 pb-3 pt-5 sm:gap-4">
            <div v-for="b in dayBars" :key="b.localDate" class="flex min-w-0 flex-1 flex-col items-center" :title="`${b.localDate}：播放 ${b.plays} 次 · 进厅 ${b.opens} 次 · 游客 ${b.guestShots} 枪 · 观众 ${b.heads} 位 · 游客 ${b.guestHeads} 位`">
              <span class="text-[11px] font-semibold text-stationPink">{{ b.plays }}</span>
              <div class="mt-1 flex h-[104px] w-full max-w-[54px] items-end gap-[3px]">
                <div class="w-1/3 rounded-t-[3px] bg-stationPink transition-all duration-500" :style="{ height: b.playPct + '%' }"></div>
                <div class="w-1/3 rounded-t-[3px] bg-stationPink/30 transition-all duration-500" :style="{ height: b.openPct + '%' }"></div>
                <div class="w-1/3 rounded-t-[3px] bg-inkFaint/30 transition-all duration-500" :style="{ height: b.guestPct + '%' }"></div>
              </div>
              <span class="mt-1.5 text-[11px] text-inkFaint">{{ b.dayLabel }}</span>
              <!-- 脚注区固定占一行高：外层是底对齐，各列不等高会让有脚注的那天平白浮起一截 -->
              <span class="block h-4 whitespace-nowrap text-[10px] leading-4 text-inkFaint/70">
                <template v-if="b.showHeads">{{ b.heads > 1 ? '观众 ' + b.heads : '' }}{{ b.heads > 1 && b.guestHeads > 0 ? ' · ' : '' }}{{ b.guestHeads > 0 ? '游客 ' + b.guestHeads : '' }}</template>
              </span>
            </div>
          </div>
        </section>

        <!-- 我的战报：登录者自己在这一周的动静（按 events 的 actor_member_id 统计），零动条目隐身 -->
        <div v-if="personalRows.length" class="mt-4 overflow-hidden rounded-2xl border border-stationPink/25 bg-cardBg">
          <div class="weekly-head flex items-center justify-between border-b border-lineSoft px-5 py-3">
            <h2 class="text-[15px] font-semibold text-inkMain">我的战报</h2>
            <span v-if="data.personalVsAll" class="flex items-center gap-2 text-[11px] text-inkFaint">
              <span v-if="data.personalVsAll.rank">全场第 {{ data.personalVsAll.rank }} / {{ data.personalVsAll.headCount }} 人</span>
              <span class="h-1.5 w-16 overflow-hidden rounded-full bg-stationPink/15">
                <span class="block h-full rounded-full bg-stationPink" :style="{ width: Math.min(100, data.personalVsAll.sharePermille / 10) + '%' }"></span>
              </span>
              <span class="tabular-nums">占全站播放 {{ (data.personalVsAll.sharePermille / 10).toFixed(1) }}%</span>
            </span>
          </div>
          <div class="grid grid-cols-2 gap-x-6 px-5 py-4 sm:grid-cols-4">
            <div v-for="row in personalRows" :key="row.label" class="py-1.5" :title="row.tip || ''">
              <p class="text-[11px] text-inkFaint">{{ row.label }}</p>
              <p class="mt-0.5 text-[19px] font-semibold leading-6 text-inkMain">
                {{ row.text || row.value }}<span v-if="!row.text" class="ml-1 text-[11px] font-normal text-inkFaint">{{ row.unit }}</span>
              </p>
            </div>
          </div>
        </div>

        <!-- 分站流水：有动静才摆卡，数全出自台账事件与入库时间行 -->
        <div v-if="activityRows.length" class="mt-4 overflow-hidden rounded-2xl border border-lineSoft bg-cardBg">
          <div class="weekly-head flex items-center justify-between border-b border-lineSoft px-5 py-3">
            <h2 class="text-[15px] font-semibold text-inkMain">分站流水</h2>
            <span class="flex items-center gap-3 text-[11px] text-inkFaint">
              <span v-for="t in activityDeltas" :key="t.key" class="flex items-center gap-1">
                {{ t.label }}
                <DeltaTag :d="t.d" :base="deltaBase" />
              </span>
            </span>
          </div>
          <div class="grid grid-cols-2 gap-x-6 px-5 py-4 sm:grid-cols-4">
            <div v-for="row in activityRows" :key="row.label" class="py-1.5">
              <p class="text-[11px] text-inkFaint">{{ row.label }}</p>
              <p class="mt-0.5 text-[19px] font-semibold leading-6 text-inkMain">
                {{ row.value }}<span class="ml-1 text-[11px] font-normal text-inkFaint">{{ row.unit }}</span>
              </p>
            </div>
          </div>
        </div>

        <!-- 站外游客：未登录的动静单开一卡，与成员账分列不混数；全零整卡隐身 -->
        <div v-if="guestRows.length" class="mt-4 overflow-hidden rounded-2xl border border-lineSoft bg-cardBg">
          <div class="weekly-head flex items-center justify-between border-b border-lineSoft px-5 py-3">
            <h2 class="text-[15px] font-semibold text-inkMain">站外游客</h2>
          </div>
          <div class="grid grid-cols-2 gap-x-6 px-5 py-4 sm:grid-cols-4">
            <div v-for="row in guestRows" :key="row.label" class="py-1.5" :title="row.tip">
              <p class="text-[11px] text-inkFaint">{{ row.label }}</p>
              <p class="mt-0.5 text-[19px] font-semibold leading-6 text-inkMain">
                {{ row.value }}<span class="ml-1 text-[11px] font-normal text-inkFaint">{{ row.unit }}</span>
              </p>
            </div>
          </div>
        </div>

        <!-- 家底明细：存货全表大数，一行摆齐 -->
        <div class="mt-4 rounded-2xl border border-lineSoft bg-cardBg px-5 py-3.5">
          <p class="text-[11px] leading-5 text-inkFaint">
            <span class="mr-1 font-medium text-inkSub">存货总账</span>
            动态 {{ data.totals.dynamics }} 条 · 存档评论 {{ data.totals.dynamicComments }} 条 · 站内弹幕 {{ data.totals.danmaku }} 条 · 私信 {{ data.totals.dmMessages }} 封 · 成员 {{ data.totals.members }} 席 · 库房 {{ humanBytes(data.totals.bytes) }}
          </p>
        </div>

        <!-- 整周空账提示 -->
        <div v-if="isEmpty" class="mt-4 rounded-2xl border border-dashed border-lineSoft bg-cardBg px-6 py-14 text-center">
          <p class="text-sm text-inkSub">这一周分站没有任何动静。</p>
        </div>

        <template v-else>
          <!-- 新货：横排卡 -->
          <section class="mt-4 overflow-hidden rounded-2xl border border-lineSoft bg-cardBg">
            <div class="weekly-head flex items-center justify-between border-b border-lineSoft px-5 py-3">
              <h2 class="text-[15px] font-semibold text-inkMain">新货</h2>
              <span class="flex items-center gap-3 text-xs text-inkFaint">
                <span v-if="newsPaged" class="flex items-center gap-1.5">
                  <button
                    class="flex h-6 w-6 items-center justify-center rounded-full border border-lineSoft text-inkSub transition hover:border-stationPink hover:text-stationPink disabled:cursor-not-allowed disabled:opacity-40"
                    :disabled="newsPage <= 0"
                    title="上一页"
                    @click="newsPage--"
                  >
                    <svg viewBox="0 0 24 24" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="2"><path d="M14.5 5.5 8 12l6.5 6.5" stroke-linecap="round" stroke-linejoin="round" /></svg>
                  </button>
                  <span class="tabular-nums">{{ Math.min(newsPage, newsPages - 1) + 1 }}/{{ newsPages }}</span>
                  <button
                    class="flex h-6 w-6 items-center justify-center rounded-full border border-lineSoft text-inkSub transition hover:border-stationPink hover:text-stationPink disabled:cursor-not-allowed disabled:opacity-40"
                    :disabled="newsPage >= newsPages - 1"
                    title="下一页"
                    @click="newsPage++"
                  >
                    <svg viewBox="0 0 24 24" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="2"><path d="M9.5 5.5 16 12l-6.5 6.5" stroke-linecap="round" stroke-linejoin="round" /></svg>
                  </button>
                </span>
              </span>
            </div>
            <div v-if="newsShown.length" class="grid grid-cols-2 gap-3 p-4 sm:grid-cols-4">
              <div v-for="item in newsShown" :key="item.bvid" class="w-full">
                <!-- 封面一枚 RouterLink 指向播放页（另一枚在标题字上）：放大由 newsCoverKey 说话，鼠标真进封面才缩 -->
                <RouterLink
                  :to="`/watch/${item.bvid}`"
                  class="relative block aspect-[16/10] w-full overflow-hidden rounded-xl bg-[#f1f2f3] dark:bg-[#23262e]"
                  @mouseenter="newsCoverKey = item.bvid"
                  @mouseleave="newsCoverKey = null"
                >
                  <img
                    v-if="coverSrc(item.coverUrl)"
                    :src="coverSrc(item.coverUrl)"
                    :alt="item.title || item.bvid"
                    loading="lazy"
                    decoding="async"
                    referrerpolicy="no-referrer"
                    class="h-full w-full object-cover transition duration-300"
                    :class="newsCoverKey === item.bvid ? 'scale-[1.03]' : ''"
                  />
                  <span v-else class="flex h-full w-full items-center justify-center text-xs tracking-widest text-inkFaint">{{ item.bvid }}</span>
                </RouterLink>
                <!-- 标题：热区只给字面。外层 p 只管两行截断，真正可点的是内联那枚 RouterLink ——
                     第二行文字之后的空白、行下方的余量都不响应，光标也不变手型（染粉与手型同一个元素、同一块区域）。 -->
                <p class="mt-2 line-clamp-2 text-[13px] font-medium leading-[19px] text-inkMain" :title="item.title || item.bvid">
                  <RouterLink
                    :to="`/watch/${item.bvid}`"
                    class="transition"
                    :class="newsTitleKey === item.bvid ? 'text-stationPink' : ''"
                    @mouseenter="newsTitleKey = item.bvid"
                    @mouseleave="newsTitleKey = null"
                  >{{ item.title || item.bvid }}</RouterLink>
                </p>
                <!-- bvid 那行是死区：不响应点击、不给手型，只当读数。 -->
                <p class="mt-0.5 truncate text-[11px] text-inkFaint">{{ item.bvid }}</p>
              </div>
            </div>
            <p v-else class="px-6 py-10 text-center text-sm text-inkFaint">这一周没有新货入站。</p>
          </section>

          <!-- 最多人看 Top5 -->
          <section class="mt-4 overflow-hidden rounded-2xl border border-lineSoft bg-cardBg">
            <div class="weekly-head flex items-center justify-between border-b border-lineSoft px-5 py-3">
              <h2 class="text-[15px] font-semibold text-inkMain">最多人看</h2>
            </div>
            <ol v-if="data.hottest.length">
              <li v-for="(h, i) in data.hottest" :key="h.bvid" class="border-b border-lineSoft/60 last:border-b-0">
                <!-- 整行不再是链接（站长 11-02 顺手定的命中区收口）：名次、bvid、「N 位观众」保持读数区，整行不给背景高亮；
                     可点的只有封面与「压在标题字上」两处，写法与新货条目、UpNextList 同一套手感。 -->
                <div class="flex items-center gap-3 px-4 py-3">
                  <span
                    class="w-7 shrink-0 text-center text-[15px] font-semibold"
                    :class="i < 3 ? 'text-stationPink' : 'text-inkFaint'"
                  >{{ i + 1 }}</span>
                  <!-- 封面一枚 RouterLink 指向播放页：放大走 hotCoverKey，倍率与缓动跟同页新货那条一致（scale-[1.03] + transition duration-300），不自创 -->
                  <RouterLink
                    :to="`/watch/${h.bvid}`"
                    class="relative block h-16 w-28 shrink-0 overflow-hidden rounded-lg bg-[#f1f2f3] dark:bg-[#23262e]"
                    @mouseenter="hotCoverKey = h.bvid"
                    @mouseleave="hotCoverKey = null"
                  >
                    <img
                      v-if="coverSrc(h.coverUrl)"
                      :src="coverSrc(h.coverUrl)"
                      :alt="h.title || h.bvid"
                      loading="lazy"
                      decoding="async"
                      referrerpolicy="no-referrer"
                      class="h-full w-full object-cover transition duration-300"
                      :class="hotCoverKey === h.bvid ? 'scale-[1.03]' : ''"
                    />
                    <span v-else class="flex h-full w-full items-center justify-center text-[10px] tracking-widest text-inkFaint">{{ h.bvid }}</span>
                  </RouterLink>
                  <span class="min-w-0 flex-1">
                    <!-- 标题：热区只给字面。外层 span 只管一行截断与省略号，真正可点的是内联那枚 RouterLink ——
                         文字之后的空白、行下方的余量都不响应，光标也不变手型（染粉与手型同一个元素、同一块区域）。 -->
                    <span class="block truncate text-[14px] font-medium text-inkMain" :title="h.title || h.bvid">
                      <RouterLink
                        :to="`/watch/${h.bvid}`"
                        class="transition"
                        :class="hotTitleKey === h.bvid ? 'text-stationPink' : ''"
                        @mouseenter="hotTitleKey = h.bvid"
                        @mouseleave="hotTitleKey = null"
                      >{{ h.title || h.bvid }}</RouterLink>
                    </span>
                    <!-- bvid 行是读数区：不吃进链接、不响应点击、不给手型。 -->
                    <span class="mt-0.5 block truncate text-[11px] text-inkFaint">{{ h.bvid }}</span>
                  </span>
                  <span class="shrink-0 text-[13px] text-inkSub"><span class="font-semibold text-stationPink">{{ h.viewers }}</span> 位观众</span>
                </div>
              </li>
            </ol>
            <p v-else class="px-6 py-10 text-center text-sm text-inkFaint">这一周没人进厅。</p>
          </section>

          <!-- 时长榜：这一周真看了多久（只数成员心跳，一枪 30 秒；游客看不了片不入账）。三栏同看时长：片 / UP / 分区 -->
          <section v-if="hasDuration" class="mt-4 overflow-hidden rounded-2xl border border-lineSoft bg-cardBg">
            <div class="weekly-head flex items-center justify-between border-b border-lineSoft px-5 py-3">
              <h2 class="text-[15px] font-semibold text-inkMain">时长榜</h2>
              <span class="flex items-center gap-2 text-[11px] text-inkFaint" title="时长 = 心跳枪数×30 秒（前端没在播就不报，所以宁少不多）；看完 = 看到片长 90% 以后">
                真看了 <span class="font-semibold text-stationPink">{{ humanDuration(data.duration.seconds) }}</span>
                <span class="text-inkFaint/70">· 心跳 {{ data.duration.shots }} 枪 · 看完 {{ data.duration.finished }} 部</span>
              </span>
            </div>
            <div class="grid grid-cols-1 divide-y divide-lineSoft lg:grid-cols-3 lg:divide-x lg:divide-y-0">
              <div class="min-w-0 p-4">
                <p class="mb-1 text-[11px] font-medium text-inkFaint">最耗时的片</p>
                <ol v-if="durationBars.videos.length">
                  <li v-for="(v, i) in durationBars.videos" :key="v.bvid" class="border-b border-lineSoft/60 py-2 last:border-b-0">
                    <div class="flex items-center gap-2">
                      <span class="w-4 shrink-0 text-center text-[13px] font-semibold" :class="i < 3 ? 'text-stationPink' : 'text-inkFaint'">{{ i + 1 }}</span>
                      <RouterLink :to="`/watch/${v.bvid}`" class="min-w-0 flex-1">
                        <span class="block truncate text-[13px] font-medium text-inkMain" :title="v.title || v.bvid">{{ v.title || v.bvid }}</span>
                        <span class="mt-0.5 block text-[10px] text-inkFaint">{{ v.shots }} 枪心跳 · <template v-if="v.finishPermille != null">看到 {{ Math.round(v.finishPermille / 10) }}%</template><template v-else>片长没档</template></span>
                      </RouterLink>
                      <span class="shrink-0 text-[13px] font-semibold tabular-nums text-stationPink">{{ humanDuration(v.seconds) }}</span>
                    </div>
                    <div class="mt-1.5 ml-6 h-1 overflow-hidden rounded-full bg-stationPink/10">
                      <div class="h-full rounded-full bg-gradient-to-r from-stationPink to-[#fc9db6] transition-all duration-500" :style="{ width: v.pct + '%' }"></div>
                    </div>
                  </li>
                </ol>
                <p v-else class="py-6 text-center text-[12px] text-inkFaint">这周没人真看片。</p>
              </div>

              <div class="min-w-0 p-4">
                <p class="mb-1 text-[11px] font-medium text-inkFaint">最耗时的 UP</p>
                <ol v-if="durationBars.ups.length">
                  <li v-for="(u, i) in durationBars.ups" :key="u.key" class="border-b border-lineSoft/60 py-2 last:border-b-0">
                    <div class="flex items-center gap-2">
                      <span class="w-4 shrink-0 text-center text-[13px] font-semibold" :class="i < 3 ? 'text-stationPink' : 'text-inkFaint'">{{ i + 1 }}</span>
                      <span class="min-w-0 flex-1 truncate text-[13px] font-medium text-inkMain" :title="u.key">{{ u.key }}</span>
                      <span class="shrink-0 text-[10px] text-inkFaint">看完 {{ u.finished }}/{{ u.videos }}</span>
                      <span class="w-16 shrink-0 text-right text-[13px] font-semibold tabular-nums text-stationPink">{{ humanDuration(u.seconds) }}</span>
                    </div>
                    <div class="mt-1.5 ml-6 h-1 overflow-hidden rounded-full bg-stationPink/10">
                      <div class="h-full rounded-full bg-gradient-to-r from-stationPink to-[#fc9db6] transition-all duration-500" :style="{ width: u.pct + '%' }"></div>
                    </div>
                  </li>
                </ol>
                <p v-else class="py-6 text-center text-[12px] text-inkFaint">这周没人真看片。</p>
              </div>

              <div class="min-w-0 p-4">
                <p class="mb-1 text-[11px] font-medium text-inkFaint">最耗时的分区</p>
                <ol v-if="durationBars.zones.length">
                  <li v-for="(z, i) in durationBars.zones" :key="z.key" class="border-b border-lineSoft/60 py-2 last:border-b-0">
                    <div class="flex items-center gap-2">
                      <span class="w-4 shrink-0 text-center text-[13px] font-semibold" :class="i < 3 ? 'text-stationPink' : 'text-inkFaint'">{{ i + 1 }}</span>
                      <span class="min-w-0 flex-1 truncate text-[13px] font-medium text-inkMain" :title="z.key">{{ z.key }}</span>
                      <span class="shrink-0 text-[10px] text-inkFaint">看完 {{ z.finished }}/{{ z.videos }}</span>
                      <span class="w-16 shrink-0 text-right text-[13px] font-semibold tabular-nums text-stationPink">{{ humanDuration(z.seconds) }}</span>
                    </div>
                    <div class="mt-1.5 ml-6 h-1 overflow-hidden rounded-full bg-stationPink/10">
                      <div class="h-full rounded-full bg-gradient-to-r from-stationPink to-[#fc9db6] transition-all duration-500" :style="{ width: z.pct + '%' }"></div>
                    </div>
                  </li>
                </ol>
                <p v-else class="py-6 text-center text-[12px] text-inkFaint">这周没人真看片。</p>
              </div>
            </div>
          </section>

          <!-- 最受欢迎 UP / 最受欢迎分区：同榜同口径（观众去重），两卡并排，榜内带占比条 -->
          <div v-if="data.topUps.length || data.topZones.length" class="mt-4 grid grid-cols-1 gap-4 lg:grid-cols-2">
            <section class="overflow-hidden rounded-2xl border border-lineSoft bg-cardBg">
              <div class="weekly-head flex items-center justify-between border-b border-lineSoft px-5 py-3">
                <h2 class="text-[15px] font-semibold text-inkMain">最受欢迎 UP</h2>
              </div>
              <ol v-if="upBars.length">
                <li v-for="(u, i) in upBars" :key="u.upName" class="border-b border-lineSoft/60 px-4 py-2.5 last:border-b-0">
                  <div class="flex items-center gap-3">
                    <span
                      class="w-7 shrink-0 text-center text-[15px] font-semibold"
                      :class="i < 3 ? 'text-stationPink' : 'text-inkFaint'"
                    >{{ i + 1 }}</span>
                    <span class="min-w-0 flex-1 truncate text-[14px] font-medium text-inkMain" :title="u.upName">{{ u.upName }}</span>
                    <span class="shrink-0 text-[12px] text-inkFaint">{{ u.videos }} 部 · {{ u.plays }} 次播放</span>
                    <span class="w-16 shrink-0 text-right text-[13px] text-inkSub"><span class="font-semibold text-stationPink">{{ u.viewers }}</span> 人</span>
                  </div>
                  <div class="mt-1.5 ml-10 h-1.5 overflow-hidden rounded-full bg-stationPink/10">
                    <div class="h-full rounded-full bg-gradient-to-r from-stationPink to-[#fc9db6] transition-all duration-500" :style="{ width: u.playPct + '%' }"></div>
                  </div>
                </li>
              </ol>
              <p v-else class="px-6 py-10 text-center text-sm text-inkFaint">这周没人看过任何 UP 的稿件。</p>
            </section>

            <section class="overflow-hidden rounded-2xl border border-lineSoft bg-cardBg">
              <div class="weekly-head flex items-center justify-between border-b border-lineSoft px-5 py-3">
                <h2 class="text-[15px] font-semibold text-inkMain">最受欢迎分区</h2>
              </div>
              <ol v-if="zoneBars.length">
                <li v-for="(z, i) in zoneBars" :key="z.zone" class="border-b border-lineSoft/60 px-4 py-2.5 last:border-b-0">
                  <div class="flex items-center gap-3">
                    <span
                      class="w-7 shrink-0 text-center text-[15px] font-semibold"
                      :class="i < 3 ? 'text-stationPink' : 'text-inkFaint'"
                    >{{ i + 1 }}</span>
                    <span class="min-w-0 flex-1 truncate text-[14px] font-medium text-inkMain" :title="z.zone">{{ z.zone }}</span>
                    <span class="shrink-0 text-[12px] text-inkFaint">{{ z.videos }} 部 · {{ z.plays }} 次播放</span>
                    <span class="w-16 shrink-0 text-right text-[13px] text-inkSub"><span class="font-semibold text-stationPink">{{ z.viewers }}</span> 人</span>
                  </div>
                  <div class="mt-1.5 ml-10 h-1.5 overflow-hidden rounded-full bg-stationPink/10">
                    <div class="h-full rounded-full bg-gradient-to-r from-stationPink to-[#fc9db6] transition-all duration-500" :style="{ width: z.playPct + '%' }"></div>
                  </div>
                </li>
              </ol>
              <p v-else class="px-6 py-10 text-center text-sm text-inkFaint">这周没人看过任何分区的稿件。</p>
            </section>
          </div>

          <!-- 订阅更新榜：这一周冒泡的排前（勤->懒）；全周没人更新时同一张卡就是断更提醒 -->
          <section v-if="staleRows.length" class="mt-4 overflow-hidden rounded-2xl border border-lineSoft bg-cardBg">
            <div class="weekly-head flex items-center justify-between border-b border-lineSoft px-5 py-3">
              <h2 class="text-[15px] font-semibold text-inkMain">{{ staleAllQuiet ? '订阅断更' : '订阅更新' }}</h2>
            </div>
            <ul>
              <li v-for="s in staleRows" :key="s.mid" class="flex items-center gap-3 border-b border-lineSoft/60 px-4 py-2.5 last:border-b-0">
                <img
                  v-if="s.faceUrl"
                  :src="s.faceUrl"
                  :alt="s.nickname || String(s.mid)"
                  class="h-8 w-8 shrink-0 rounded-full bg-[#f1f2f3] object-cover dark:bg-[#23262e]"
                  loading="lazy"
                  decoding="async"
                  referrerpolicy="no-referrer"
                />
                <span v-else class="flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-stationPink/10 text-[11px] text-stationPink">{{ (s.nickname || String(s.mid)).slice(0, 1) }}</span>
                <span class="min-w-0 flex-1">
                  <span class="block truncate text-[14px] font-medium text-inkMain">{{ s.nickname || s.mid }}</span>
                  <span class="mt-0.5 block text-[11px] text-inkFaint">共 {{ s.dynTotal }} 条入档<span v-if="s.lastAtUtc"> · 最后 {{ s.lastAtUtc.slice(0, 10) }}</span></span>
                </span>
                <span
                  class="shrink-0 rounded-full px-2.5 py-1 text-[11px] font-medium"
                  :class="s.dynThisWeek > 0 ? 'bg-stationPink/10 text-stationPink' : 'bg-[#f1f2f3] text-inkFaint dark:bg-[#23262e]'"
                >
                  {{ s.dynThisWeek > 0 ? `周内 ${s.dynThisWeek} 条` : (s.lastDays == null ? '入档没动态' : `${fmtDays(s.lastDays)}没更`) }}
                </span>
              </li>
            </ul>
          </section>
        </template>
      </template>
    </main>
  </div>
</template>

<style scoped>
/* 卡头统一效果（照官方 bili-dyn-head 的路子）：标题左侧一道站粉渐变竖条，
   卡头背景再铺一层从粉到透的淡渐变，与「我的战报」那张粉卡同族 */
.weekly-head {
  background-image: linear-gradient(90deg, rgb(251 114 153 / 0.1), rgb(251 114 153 / 0.04) 46%, transparent 72%);
}
.weekly-head h2 {
  position: relative;
  padding-left: 10px;
}
.weekly-head h2::before {
  content: '';
  position: absolute;
  left: 0;
  top: 50%;
  transform: translateY(-50%);
  width: 3px;
  height: 14px;
  border-radius: 2px;
  background-image: linear-gradient(180deg, #fb7299, #fc9db6);
}
</style>
