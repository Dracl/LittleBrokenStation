<script setup>
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import TopBar from '../components/TopBar.vue'
import DeltaTag from '../components/WeeklyDeltaTag.vue'
import { member } from '../memberStore'
import { coverSrcOf } from '../utils/coverSrc'
import { defaultAvatar } from '../utils/avatar'
import CountUpNum from '../components/CountUpNum.vue'
import WallWindow from '../components/WallWindow.vue'
import {
  POSTER_WIDTH,
  POSTER_ACCENTS,
  POSTER_CONSTRUCT_ACCENTS,
  POSTER_THEMES,
  POSTER_STYLES,
  paintPoster,
  posterBackdrop,
  posterFooter,
  posterTheme,
} from '../utils/weeklyPoster'

// 分站周报：一张卡的工夫看完这一周——新货、弹幕增量、全站家底，外加 Top5 最多人看。
// 数据全走 /api/weekly（成员登录才给看）；周切换 0..4 **全是已过完的整周**：0=上一个刚过完的周，1..4 再往前数。
// 不给看「本周至今」：未过完的一周天天在长，同一个链接今天回看跟明天回看不一样，周报就不是凭据了。
const week = ref(0)

// 绝对周模式：地址栏 ?w=2026-W44（周报公告那枚钮递的就是这个形）——进页读一次，认得就按周号取数（?w=），
// 认不得（没带、形不正）就当没这回事，照旧走相对偏移，不给白屏也不悄悄替站长挑一期。
// 绝对周下周切换没意义（那周的上一周不在可回看窗口里），所以那两钮换成「回到最近一期」。
const WEEK_ID_RE = /^\d{4}-W\d{2}$/
const route = useRoute()
const router = useRouter()

function weekIdFromQuery(raw) {
  const s = typeof raw === 'string' ? raw.trim() : ''
  return WEEK_ID_RE.test(s) ? s : ''
}

const absoluteWeek = ref(weekIdFromQuery(route.query.w))
const data = ref(null)
const loading = ref(false)
const error = ref('')

// 页眉副标题：直接把桶界摆成人能读的日期区间——「已过完」这件事不用你信，自己看日期。
const weekCaption = computed(() => {
  const d = data.value
  if (d) return `${localDay(d.sinceUtc)} ~ ${localDay(d.untilUtc, true)} · 已过完的一周`
  // 按周号定位还没拉到数（或根本不在可回看窗口）：把你要的那一期说在嘴上，不让人对空白猜
  if (absoluteWeek.value) return `${absoluteWeek.value} · 按周号定位`
  return week.value === 0 ? '最近一个已过完的整周' : `往前 ${week.value} 周`
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
    { label: '主页', value: p.home ?? 0, unit: '回', tip: '打开首页的回数（刷新、回首页都算一回）' },
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
    { label: '主页', value: a.home ?? 0, unit: '回', tip: '全站打开首页的枪数（成员游客一视同仁）' },
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
  const max = Math.max(1, ...bars.map((b) => Math.max(b.opens, b.plays, b.guestShots, b.home ?? 0)))
  return bars.map((b) => ({
    ...b,
    dayLabel: `${Number(b.localDate.slice(8, 10))}日`,
    playPct: Math.max(4, Math.round((b.plays / max) * 100)),
    openPct: Math.max(3, Math.round((b.opens / max) * 100)),
    homePct: (b.home ?? 0) > 0 ? Math.max(3, Math.round(((b.home ?? 0) / max) * 100)) : 0,
    guestPct: b.guestShots > 0 ? Math.max(3, Math.round((b.guestShots / max) * 100)) : 0,
    showHeads: b.heads > 1 || b.guestHeads > 0,
  }))
})

// 最多人看榜不垫 inline 条（站长 11-03 验货时圈图拆的）：观众数常常挤在个位数，条条拉满就成了隔行的噪音；
// 时长榜/UP榜/分区榜的条保留（那边数值分布拉得开，条才有高低可看）。

// ── 新三图（站长 11-03 点名）：热力墙 / 时段矩阵 / 迷你折线 ──
// 出没热力墙：自报周往回 8 周（周日收尾往前推 56 天，首日必是周一），一格一天；色深拿全墙最大枪数分四档。
const heatWallGrid = computed(() => {
  const d = data.value
  if (!d) return []
  const counts = new Map((d.heatWall ?? []).map((h) => [h.localDate, h.shots]))
  const max = Math.max(1, ...(d.heatWall ?? []).map((h) => h.shots))
  const lastDay = new Date(new Date(d.untilUtc).getTime() + 8 * 3600e3 - 1) // 报周周日（本地）
  const base = Date.UTC(lastDay.getUTCFullYear(), lastDay.getUTCMonth(), lastDay.getUTCDate()) - 55 * 86400e3
  const cells = []
  for (let i = 0; i < 56; i++) {
    const t = new Date(base + i * 86400e3)
    const key = `${t.getUTCFullYear()}-${String(t.getUTCMonth() + 1).padStart(2, '0')}-${String(t.getUTCDate()).padStart(2, '0')}`
    const shots = counts.get(key) ?? 0
    const level = shots === 0 ? 0 : shots >= max * 0.7 ? 4 : shots >= max * 0.45 ? 3 : shots >= max * 0.2 ? 2 : 1
    cells.push({ key, shots, level })
  }
  return cells
})

// 时段热度矩阵：只看报周，7 行（周一打头）× 6 档（每 4 小时）；色深连续缩放，tooltip 带枪数。
const heatMatrixGrid = computed(() => {
  const list = data.value?.heatMatrix ?? []
  const byKey = new Map(list.map((c) => [`${c.wday}-${c.bucket}`, c.shots]))
  const max = Math.max(1, ...list.map((c) => c.shots))
  const cells = []
  for (let w = 1; w <= 7; w++) {
    for (let b = 0; b < 6; b++) {
      const shots = byKey.get(`${w}-${b}`) ?? 0
      cells.push({ w, b, shots, bg: shots === 0 ? 'rgb(153 162 184 / 0.12)' : `rgb(0 161 214 / ${(0.15 + 0.85 * (shots / max)).toFixed(2)})` })
    }
  }
  return { cells, any: list.length > 0 }
})

// 迷你折线：8 个点拉成 0..100 宽的折线（高 22，上下各留 1）；全零的线不摆，免得一条直愣愣的平线冒充趋势。
function sparkPoints(arr) {
  if (!arr || !arr.length || !arr.some((v) => v > 0)) return ''
  const max = Math.max(...arr)
  return arr.map((v, i) => `${((i / (arr.length - 1)) * 100).toFixed(1)},${(21 - (v / max) * 20).toFixed(1)}`).join(' ')
}
const heroSparks = computed(() => {
  const s = data.value?.sparks
  if (!s) return null
  return { news: sparkPoints(s.news), danmaku: sparkPoints(s.danmaku), stock: sparkPoints(s.stock) }
})

// 游客漏斗（站长 11-03 点名 A3）：人数→逛主页→进厅→播放串成收窄的条，转化率拿相邻两级现算；
// 被挡那一笔不挤进阶梯（它是另一条路的岔口），继续留在瓦片里摆。
const guestFunnel = computed(() => {
  const g = data.value?.guest
  if (!g) return []
  const steps = [
    { label: '游客', value: g.heads, unit: '位', cls: 'bg-inkFaint/35', tip: '未登录出没的去重 IP 数；同一出口网络会并成一人' },
    { label: '逛主页', value: g.home ?? 0, unit: '枪', cls: 'bg-stationBlue/50', tip: '游客打开首页的枪数' },
    { label: '进厅', value: g.opens, unit: '次', cls: 'bg-stationPink/35', tip: '游客打开片页的次数' },
    { label: '播放', value: g.plays, unit: '次', cls: 'bg-stationPink', tip: '游客按下播放键的次数' },
  ]
  const max = Math.max(1, ...steps.map((s) => s.value))
  return steps.map((s, i) => ({
    ...s,
    pct: s.value > 0 ? Math.max(4, Math.round((s.value / max) * 100)) : 0,
    conv: i > 0 && steps[i - 1].value > 0 ? Math.round((s.value / steps[i - 1].value) * 100) : null,
  }))
})
const guestCardVisible = computed(() => guestFunnel.value.some((s) => s.value > 0) || guestRows.value.length > 0)

// 站外游客卡：只摆有动静的项（零隐身）；全零（这周没一个游客）整卡不摆。
// 口径：人数是 guest 行的去重 IP（同一出口会并成一人），次数是事件枪数；搜索只报回数，绝不摆关键词。
const guestRows = computed(() => {
  const g = data.value?.guest
  if (!g) return []
  return [
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

// 周报头图认的「头一张」：这一周新货里从新到旧第一条有封面的（没封面就不摆图，不摆黑块）。
// 图和底下那行标题都从 heroItem 取——11-03 前标题是另拿 items[0] 的，最新一条恰好没封面时
// 图会退到第二条、字还停在第一条，一张头图配两张皮的片。认同一个对象就不会再岔开。
const heroItem = computed(() => (data.value?.news?.items ?? []).find((n) => n.coverUrl) ?? null)
const heroCover = computed(() => coverSrc(heroItem.value, 700))

function fmtDays(n) {
  if (n == null) return '没档'
  if (n === 0) return '今天'
  if (n < 7) return `${n} 天`
  if (n < 30) return `${Math.floor(n / 7)} 周`
  return `${Math.floor(n / 30)} 个月`
}

// 封面口径与首页卡阵同源：全站唯一出口 utils/coverSrc（服务端缩略档位 → 本地原图 → 热链原样直出，B 站缩放参数全站停用）。
// 按稿件对象取（档位/localCover/coverUrl 都在它身上），needW 传卡面 CSS 显示宽，内部按 DPR 2 折算挑档。
function coverSrc(item, needW = 440) {
  return coverSrcOf(item, needW)
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

// 新货不再走「4 列横排卡 + 分页」那套（站长 11-03 定）：直接复用个人空间那面馆藏壁——
// CanvasWall 同一个布点内核，同一个窗口壳（洗牌/复位/±/全屏一套工具条都跟着）。
// 卡片身份靠 videoId：周报的新货只带 bvid，所以从 bvid 折一个稳定数（FNV-1a 32 位）——
// 同一条片子下次回来看，还在墙上老位置，不会洗一次牌就跳一家。
function wallId(bvid) {
  const s = String(bvid || '')
  let h = 2166136261
  for (let i = 0; i < s.length; i += 1) {
    h ^= s.charCodeAt(i)
    h = Math.imul(h, 16777619) >>> 0
  }
  return h || 1
}

// 墙料形如 WallItem：本地封面走 localCover，远程图床走 coverUrl（内核会自己转缩略与兑底）。
// 周报给的 coverUrl 已经后端归一，同源就当成本地那份。
const newsWallItems = computed(() =>
  (data.value?.news?.items ?? []).map((n) => {
    const url = n.coverUrl || ''
    const remote = /^https?:\/\//.test(url)
    return {
      videoId: wallId(n.bvid),
      bvid: n.bvid,
      title: n.title,
      localCover: remote ? null : url,
      coverUrl: remote ? url : null,
      source: 'week',
    }
  }),
)
const hotCoverKey = ref(null) // 鼠标当前压在哪条热榜的封面上 → 封面放大（倍率与缓动跟新货那条对齐）
const hotTitleKey = ref(null) // 鼠标当前压在哪条热榜的标题字上 → 只有标题染粉

async function load() {
  loading.value = true
  error.value = ''
  try {
    const url = absoluteWeek.value
      ? `/api/weekly?w=${encodeURIComponent(absoluteWeek.value)}`
      : `/api/weekly?week=${week.value}`
    const res = await fetch(url)
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
// 相对周切换才拉；绝对周模式下 week 不参与取数，别让它触发无谓请求（后端只认 ?week=0..4）。
watch(week, () => {
  if (absoluteWeek.value) return
  load()
})
// 已在本页时再点站内链接（顶栏「周报」、周报入口卡）不会再挂载组件，得跟着地址栏改模式；
// 「回到最近一期」自己清 w 后已手动拉过，这里比一下新旧、同值不重跑。
watch(
  () => route.query.w,
  (raw) => {
    const next = weekIdFromQuery(raw)
    if (next === absoluteWeek.value) return
    absoluteWeek.value = next
    load()
  },
)

// 上一周 = 往历史里再翻一周（最远翻到五周前）；下一周 = 回到更近的一周，最近一个整周（0 档）就是头一档，不许再往后。
function goPrev() {
  if (week.value < 4) week.value += 1
}
function goNext() {
  if (week.value > 0) week.value -= 1
}

// 退掉按周号定位：先钉回相对档（此时 absoluteWeek 还在，watch(week) 会跳过），再清模式与地址栏，最后只拉这一次。
function backToLatest() {
  if (!absoluteWeek.value) return
  week.value = 0
  absoluteWeek.value = ''
  router.replace({ path: '/weekly', query: {} })
  load()
}

// ── 海报导出（11-04 重写）─────────────────────────────────────────────
// 上一版是「数据年画」那套粗野主义：彩纸、歪贴纸、硬阴影、幽灵描边大字，每块都在抢戏，
// 封面被缩成角落里的一小块——站上卖的是视频，画上却看不见视频。
// 这一版换成默认风格（editorial）版面：顶部一张整幅头图（本周最热的那张封面铺满，压暗再叠浅模糊）、
// 一个巨数当锚点、往下是网格化的数据版块。整张海报只用一种强调色，其余全是中性灰阶。
// 画笔住在 utils/weeklyPoster.js，它只是个分派口；真画画的是 weeklyPosterEditorial / weeklyPosterConstruct，
// 再加风格只要往 SKINS 和 POSTER_STYLES 各添一行。这里只管喂数据、拉封面、开面板、导出 PNG。
const posterBusy = ref(false)
const posterOpen = ref(false)
const posterCanvas = ref(null)
const posterImgs = ref(new Map())
const posterSize = ref({ w: 0, h: 0 })
// 预览与导出同一个倍率：所见即所得，也免得预览看上去总是比 PNG 糊
const POSTER_RENDER_SCALE = 2
// 头图虚焦三档：填进 opts.heroBlur，由默认风格那套画笔取用
const POSTER_HERO_BLUR = [
  { px: 0, label: '清楚', hint: '封面原样，不放虚焦' },
  { px: 3, label: '薄雾', hint: '3px 高斯，默认' },
  { px: 8, label: '虚化', hint: '8px 高斯，最托字' },
]
const posterOpts = ref({
  title: '',
  subtitle: '',
  sign: '',
  showSign: true,
  style: 'editorial',
  accent: 'pink',
  theme: 'paper',
  heroBlur: 3,
  show: { hot: true, bars: true, wall: true, matrix: true, rank: true, news: true, chips: true },
})

// 风格与强调色是一对本命：默认风格吃分站自己的粉蓝琥珀，构成主义只认红黄蓝那一色大声，
// 切风格时把 accent 归到那一套的默认档，免得出现「米纸底配分站粉」这种两边都不像的组合。
const POSTER_DEFAULT_ACCENT = { editorial: 'pink', construct: 'red' }

function posterAccentList() {
  return posterOpts.value.style === 'construct' ? POSTER_CONSTRUCT_ACCENTS : POSTER_ACCENTS
}

function posterAccent(k) {
  return posterAccentList()[k] ?? POSTER_ACCENTS.pink
}

function pickPosterStyle(key) {
  if (posterOpts.value.style === key) return
  posterOpts.value.style = key
  posterOpts.value.accent = POSTER_DEFAULT_ACCENT[key] ?? 'pink'
}

// 纸样预览块：两套皮肤的三档纸色不一样，拿各自的那套颜色画给人看
// （纸样的 key 仍是 ink/paper/brand，只是看上去换了笔）。
function posterThemeSwatch(key) {
  if (posterOpts.value.style === 'construct') {
    if (key === 'ink') return 'linear-gradient(135deg,#1d1b18,#0f0e0c)'
    if (key === 'paper') return 'linear-gradient(135deg,#f7f3ea,#e2dbcc)'
    return 'linear-gradient(135deg,#fdeef3,#f4c2d4)' // 粉版：浅粉当纸
  }
  if (key === 'ink') return 'linear-gradient(135deg,#1b2130,#080b11)'
  if (key === 'paper') return 'linear-gradient(135deg,#fdfdfc,#e8eaee)'
  return 'linear-gradient(135deg,#fdf1f5,#f8dbe6)' // 粉版：不再是整张主题色
}
const posterSections = [
  { key: 'hot', label: '本周最多人看' },
  { key: 'bars', label: '七日动静' },
  { key: 'wall', label: '出没热力墙' },
  { key: 'matrix', label: '时段热度' },
  { key: 'rank', label: '本周人气榜' },
  { key: 'news', label: '本周新货' },
  { key: 'chips', label: '角落账' },
]

function posterName() {
  const o = posterOpts.value
  if (o.sign) return o.sign
  const m = member.value
  return m ? m.nickname || m.username || '旅人' : ''
}

// 一句人话：本周到底热在哪儿，挑最热闹那天说。
function posterQuip(d) {
  const bars = dayBars.value
  if (bars.length) {
    const top = bars.reduce((a, b) => (b.plays > a.plays ? b : a), bars[0])
    if (top.plays > 0) return `最热闹的是 ${Number(top.localDate.slice(5, 7))} 月 ${Number(top.localDate.slice(8, 10))} 日，单日 ${top.plays} 次播放`
  }
  const dp = deltaOf('plays')
  if (dp && dp.dir === 'up') return `播放比上周多了 ${dp.pct}%，这周火力全开`
  if ((d.activity?.watchPlays ?? 0) === 0) return '这一周分站静悄悄，连弹幕都去午休了'
  return '每一枪都记在台账上，这页就是证据'
}

// 海报要吃的全部数据，整理成一份 model 递给画笔。
// 画笔不认识接口，只认识这一份 model——以后加字段只改这一处。
function posterModel() {
  const d = data.value
  return {
    weekLabel: d.weekLabel ?? '',
    plays: d.activity?.watchPlays ?? 0,
    opens: d.activity?.watchOpens ?? 0,
    viewers: d.activity?.uniqueViewers ?? 0,
    newsCount: d.news?.count ?? 0,
    danmakuAdded: d.danmakuAdded ?? 0,
    hottest: (d.hottest ?? []).slice(0, 3),
    news: (d.news?.items ?? []).slice(0, 4),
    dayBars: dayBars.value,
    heatWall: heatWallGrid.value,
    heatMatrix: heatMatrixGrid.value,
    topUps: (d.topUps ?? []).slice(0, 3),
    topZones: (d.topZones ?? []).slice(0, 3),
    guest: d.guest ?? {},
    personal: d.personal ?? null,
    personalDuration: d.personalDuration ?? null,
    personalValue: humanDuration(d.personalDuration?.seconds ?? 0),
    totals: d.totals ?? {},
    deltas: { plays: deltaOf('plays') },
    quip: posterQuip(d),
    signName: posterName(),
    generatedAt: new Date().toLocaleDateString('zh-CN'),
  }
}

// 封面只认同源的那份（/media/…）：B 站图床跨域，直画进 canvas 是脏像素，
// toDataURL 会直接报错。热链一律不画，交给画笔摆灰渐变占位。
function posterCoverSrc(url) {
  if (!url) return ''
  return /^https?:\/\//.test(url) ? '' : url
}

// 拉齐海报要用的全部图：热榜三条 + 新货四条 + 署名头像。画两遍：探针量身高，正文照身高开画布。
function loadPosterImgs(d) {
  const jobs = []
  const wanted = new Map()
  for (const h of (d.hottest ?? []).slice(0, 3)) if (h.coverUrl) wanted.set(h.bvid, h.coverUrl)
  for (const n of (d.news?.items ?? []).slice(0, 4)) if (n.coverUrl) wanted.set(n.bvid, n.coverUrl)
  for (const [bvid, url] of wanted) {
    if (posterImgs.value.has(bvid)) continue
    const src = posterCoverSrc(url)
    if (!src) continue
    jobs.push(
      new Promise((resolve) => {
        const img = new Image()
        img.onload = () => resolve([bvid, img])
        img.onerror = () => resolve(null)
        img.src = src
      }),
    )
  }
  if (!posterImgs.value.has('__avatar')) {
    const face = member.value?.faceUrl
    const src = face ? `/api/weekly/poster-avatar?url=${encodeURIComponent(face)}` : defaultAvatar
    jobs.push(
      new Promise((resolve) => {
        const img = new Image()
        img.onload = () => resolve(['__avatar', img])
        img.onerror = () => resolve(null)
        img.src = src
      }),
    )
  }
  return Promise.all(jobs).then((rs) => {
    for (const r of rs) if (r) posterImgs.value.set(r[0], r[1])
    return posterImgs.value
  })
}

// 画一遍海报到指定画布。S 是倍率：预览与导出都用 2，看到的就是导出的那份。
// （预览原先走 1.6，在宽面板里被浏览器拉大重采样，封面看着比真图软一档。）
function renderPosterOn(canvas, S) {
  const d = data.value
  if (!d) return
  const opts = posterOpts.value
  const W = POSTER_WIDTH
  const model = posterModel()
  const probe = document.createElement('canvas').getContext('2d')
  const bodyH = paintPoster(probe, model, W, posterImgs.value, opts)
  const H = Math.max(560, bodyH) + 92
  canvas.width = Math.round(W * S)
  canvas.height = Math.round(H * S)
  const g = canvas.getContext('2d')
  g.setTransform(S, 0, 0, S, 0, 0)
  posterBackdrop(g, W, H, opts)
  paintPoster(g, model, W, posterImgs.value, opts)
  posterFooter(g, model, posterTheme(opts), W, H)
  posterSize.value = { w: Math.round(W * 2), h: Math.round(H * 2) }
}

async function openPosterStudio() {
  if (!data.value) return
  posterOpen.value = true
  posterBusy.value = true
  try {
    await loadPosterImgs(data.value)
  } finally {
    posterBusy.value = false
  }
  await nextTick()
  if (posterCanvas.value) renderPosterOn(posterCanvas.value, POSTER_RENDER_SCALE)
}

// 调参实时刷新：打字/拨开关都跟 150ms 防抖，免得每敲一个字重画一遍
let posterPreviewTimer = 0
watch(
  posterOpts,
  () => {
    clearTimeout(posterPreviewTimer)
    posterPreviewTimer = setTimeout(() => {
      if (posterOpen.value && posterCanvas.value) renderPosterOn(posterCanvas.value, 1.6)
    }, 150)
  },
  { deep: true },
)

function downloadPoster() {
  try {
    const cv = document.createElement('canvas')
    renderPosterOn(cv, 2)
    const a = document.createElement('a')
    a.href = cv.toDataURL('image/png')
    a.download = `小破分站周报-${data.value?.weekLabel}.png`
    a.click()
  } catch {
    alert('海报没画出来，换个浏览器再试')
  }
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
            分站周报<span v-if="data || absoluteWeek" class="ml-2 text-stationPink">{{ data?.weekLabel || absoluteWeek }}</span>
          </h1>
          <p class="mt-0.5 text-xs text-inkFaint">{{ weekCaption }}</p>
        </div>
        <div class="flex shrink-0 items-center gap-2">
          <button
            class="rounded-full border border-lineSoft px-3.5 py-1.5 text-sm text-inkSub transition hover:border-stationPink hover:text-stationPink disabled:opacity-50"
            :disabled="!data"
            title="打开海报调参台：标题、副标、署名、配色、版块开关都能拧，实时预览后下载 PNG"
            @click="openPosterStudio"
          >
            导出海报
          </button>
          <template v-if="absoluteWeek">
            <button
              class="rounded-full border border-lineSoft px-3.5 py-1.5 text-sm text-inkSub transition hover:border-stationPink hover:text-stationPink"
              title="退出按周号定位，回到最近一个已过完的整周"
              @click="backToLatest"
            >
              回到最近一期
            </button>
          </template>
          <template v-else>
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
          </template>
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
        <!-- 周报头图：这一周新货头一张的封面 + 它的标题（同认 heroItem 那一条，没封面整块不摆） -->
        <div
          v-if="heroCover"
          class="rise mt-4 h-[190px] overflow-hidden rounded-2xl border border-lineSoft bg-[#f1f2f3] dark:bg-[#23262e] sm:h-[230px]"
          style="--wi: 1"
        >
          <div class="relative h-full w-full">
            <img :src="heroCover" alt="" class="h-full w-full object-cover" loading="lazy" decoding="async" referrerpolicy="no-referrer" />
            <div class="absolute inset-0 bg-gradient-to-t from-black/55 via-black/10 to-transparent"></div>
            <div class="absolute bottom-0 left-0 right-0 flex items-end justify-between gap-3 p-4">
              <p class="line-clamp-2 max-w-[70%] text-[15px] font-semibold leading-[21px] text-white drop-shadow">
                {{ heroItem?.title || heroItem?.bvid }}
              </p>
              <span class="shrink-0 rounded-full bg-white/85 px-2.5 py-1 text-[11px] font-medium text-inkSub">{{ data.weekLabel }} 头一张</span>
            </div>
          </div>
        </div>

        <!-- 三行大数：新货 / 弹幕增量 / 全站家底（涨跌角标环比上周） -->
        <div class="rise mt-4 grid grid-cols-1 gap-4 sm:grid-cols-3" style="--wi: 2">
          <div class="rounded-2xl border border-lineSoft bg-cardBg px-5 py-4">
            <p class="text-xs text-inkFaint">新货</p>
            <p class="tabular-nums mt-1 flex items-baseline gap-2 text-[26px] font-semibold leading-8 text-inkMain">
              <CountUpNum :value="data.news.count" /><span class="text-sm font-normal text-inkFaint">件</span>
              <DeltaTag :d="deltaOf('news')" :base="deltaBase" />
            </p>
            <svg v-if="heroSparks?.news" viewBox="0 0 100 22" preserveAspectRatio="none" class="mt-1 h-5 w-full opacity-75" aria-hidden="true" title="近 8 周新货走势">
              <polyline :points="heroSparks.news" fill="none" stroke="#fb7299" stroke-width="1.6" vector-effect="non-scaling-stroke" />
            </svg>
          </div>
          <div class="rounded-2xl border border-lineSoft bg-cardBg px-5 py-4">
            <p class="text-xs text-inkFaint">站内弹幕新增</p>
            <p class="tabular-nums mt-1 flex items-baseline gap-2 text-[26px] font-semibold leading-8 text-stationPink">
              +<CountUpNum :value="data.danmakuAdded" /><span class="text-sm font-normal text-inkFaint">条</span>
              <DeltaTag :d="deltaOf('danmaku')" :base="deltaBase" />
            </p>
            <svg v-if="heroSparks?.danmaku" viewBox="0 0 100 22" preserveAspectRatio="none" class="mt-1 h-5 w-full opacity-75" aria-hidden="true" title="近 8 周弹幕增量走势">
              <polyline :points="heroSparks.danmaku" fill="none" stroke="#fb7299" stroke-width="1.6" vector-effect="non-scaling-stroke" />
            </svg>
          </div>
          <div class="rounded-2xl border border-lineSoft bg-cardBg px-5 py-4">
            <p class="text-xs text-inkFaint">全站家底</p>
            <p class="tabular-nums mt-1 text-[26px] font-semibold leading-8 text-inkMain">
              <CountUpNum :value="data.totals.videos" /><span class="ml-1 text-sm font-normal text-inkFaint">件稿件</span>
            </p>
            <svg v-if="heroSparks?.stock" viewBox="0 0 100 22" preserveAspectRatio="none" class="mt-0.5 h-5 w-full opacity-75" aria-hidden="true" title="近 8 周存底累加走势（每周末总量）">
              <polyline :points="heroSparks.stock" fill="none" stroke="#00a1d6" stroke-width="1.6" vector-effect="non-scaling-stroke" />
            </svg>
            <p class="mt-0.5 text-[11px] text-inkFaint">
              {{ data.totals.members }} 位成员 · {{ data.totals.danmaku }} 条弹幕 · 库房 {{ humanBytes(data.totals.bytes) }}
            </p>
          </div>
        </div>

        <!-- 七日活动柱图：柱序左起游客→主页→进厅→播放（站长 11-03 点名）；主页柱拿 home.open，观映账与闲逛账分家；没动静的日子不补零柱 -->
        <section v-if="dayBars.length" class="rise mt-4 overflow-hidden rounded-2xl border border-lineSoft bg-cardBg" style="--wi: 3">
          <div class="weekly-head flex items-center justify-between border-b border-lineSoft px-5 py-3">
            <h2 class="text-[15px] font-semibold text-inkMain">七日动静</h2>
            <span class="flex items-center gap-3 text-[11px] text-inkFaint" title="柱高全部拿当周最大量归一；鼠标停在某一天可看当日全部明细">
              <span class="flex items-center gap-1"><i class="h-2 w-2 rounded-[2px] bg-inkFaint/30"></i>游客</span>
              <span class="flex items-center gap-1"><i class="h-2 w-2 rounded-[2px] bg-stationBlue"></i>主页</span>
              <span class="flex items-center gap-1"><i class="h-2 w-2 rounded-[2px] bg-stationPink/30"></i>进厅</span>
              <span class="flex items-center gap-1"><i class="h-2 w-2 rounded-[2px] bg-stationPink"></i>播放</span>
            </span>
          </div>
          <div class="flex items-end gap-2.5 px-5 pb-3 pt-5 sm:gap-4">
            <div v-for="b in dayBars" :key="b.localDate" class="flex min-w-0 flex-1 flex-col items-center" :title="`${b.localDate}：主页 ${b.home ?? 0} 次 · 进厅 ${b.opens} 次 · 播放 ${b.plays} 次 · 游客 ${b.guestShots} 枪 · 观众 ${b.heads} 位 · 游客 ${b.guestHeads} 位`">
              <span class="text-[11px] font-semibold text-stationPink">{{ b.plays }}</span>
              <div class="mt-1 flex h-[104px] w-full max-w-[54px] items-end gap-[3px]">
                <div class="w-1/4 rounded-t-[3px] bg-inkFaint/30 transition-all duration-500" :style="{ height: b.guestPct + '%' }"></div>
                <div class="w-1/4 rounded-t-[3px] bg-stationBlue/55 transition-all duration-500" :style="{ height: b.homePct + '%' }"></div>
                <div class="w-1/4 rounded-t-[3px] bg-stationPink/30 transition-all duration-500" :style="{ height: b.openPct + '%' }"></div>
                <div class="w-1/4 rounded-t-[3px] bg-stationPink transition-all duration-500" :style="{ height: b.playPct + '%' }"></div>
              </div>
              <span class="mt-1.5 text-[11px] text-inkFaint">{{ b.dayLabel }}</span>
              <!-- 脚注区固定占一行高：外层是底对齐，各列不等高会让有脚注的那天平白浮起一截 -->
              <span class="block h-4 whitespace-nowrap text-[10px] leading-4 text-inkFaint/70">
                <template v-if="b.showHeads">{{ b.heads > 1 ? '观众 ' + b.heads : '' }}{{ b.heads > 1 && b.guestHeads > 0 ? ' · ' : '' }}{{ b.guestHeads > 0 ? '游客 ' + b.guestHeads : '' }}</template>
              </span>
            </div>
          </div>
        </section>

        <!-- 出没热力墙 + 时段热度（站长 11-03 点名 B5/B6）：墙看近 8 周的齿痕，矩阵照追番钟点；活动口径同一条心（HeatEvents） -->
        <div class="rise mt-4 grid grid-cols-1 gap-4 lg:grid-cols-2" style="--wi: 4">
          <section class="overflow-hidden rounded-2xl border border-lineSoft bg-cardBg">
            <div class="weekly-head flex items-center justify-between border-b border-lineSoft px-5 py-3">
              <h2 class="text-[15px] font-semibold text-inkMain">出没热力墙</h2>
              <span class="flex items-center gap-1 text-[11px] text-inkFaint" title="一格一天，周一打头，往回 8 周；只数真动静（进厅/播放/首页/搜索/投稿/发弹幕/登录），心跳与门禁噪音不掺">
                少 <i class="h-2 w-2 rounded-[2px] bg-[#eef0f3] dark:bg-[#23262e]"></i><i class="h-2 w-2 rounded-[2px] bg-stationPink/25"></i><i class="h-2 w-2 rounded-[2px] bg-stationPink/45"></i><i class="h-2 w-2 rounded-[2px] bg-stationPink/70"></i><i class="h-2 w-2 rounded-[2px] bg-stationPink"></i> 多
              </span>
            </div>
            <div class="flex items-center gap-2 px-5 py-4">
              <span class="flex flex-col justify-between text-[9px] leading-none text-inkFaint" style="height: 150px">
                <span>一</span><span>三</span><span>五</span><span>日</span>
              </span>
              <div class="grid flex-1 gap-1" style="grid-template-rows: repeat(7, 18px); grid-auto-flow: column; grid-auto-columns: 1fr">
                <i
                  v-for="c in heatWallGrid"
                  :key="c.key"
                  class="h-[18px] rounded-[4px]"
                  :class="c.level === 0 ? 'bg-[#eef0f3] dark:bg-[#23262e]' : c.level === 1 ? 'bg-stationPink/25' : c.level === 2 ? 'bg-stationPink/45' : c.level === 3 ? 'bg-stationPink/70' : 'bg-stationPink'"
                  :title="`${c.key} · ${c.shots} 枪`"
                ></i>
              </div>
            </div>
          </section>
          <section class="overflow-hidden rounded-2xl border border-lineSoft bg-cardBg">
            <div class="weekly-head flex items-center justify-between border-b border-lineSoft px-5 py-3">
              <h2 class="text-[15px] font-semibold text-inkMain">时段热度</h2>
              <span class="text-[11px] text-inkFaint" title="报周内每 4 小时一档的活动枪数（口径同热力墙）；色深 = 那个钟点更热闹">追番钟点表</span>
            </div>
            <div class="px-5 py-4">
              <div class="grid gap-[3px]" style="grid-template-columns: 22px repeat(6, 1fr)">
                <span></span>
                <span v-for="h in ['0', '4', '8', '12', '16', '20']" :key="'h' + h" class="pb-0.5 text-center text-[9px] text-inkFaint">{{ h }}点</span>
                <template v-for="w in 7" :key="'w' + w">
                  <span class="pr-1 text-right text-[9px] leading-4 text-inkFaint">{{ ['一', '二', '三', '四', '五', '六', '日'][w - 1] }}</span>
                  <i
                    v-for="b in 6"
                    :key="'c' + w + '-' + b"
                    class="h-4 rounded-[3px]"
                    :style="{ background: heatMatrixGrid.cells[(w - 1) * 6 + (b - 1)].bg }"
                    :title="`${['周一', '周二', '周三', '周四', '周五', '周六', '周日'][w - 1]} ${(b - 1) * 4}:00~${b * 4}:00 · ${heatMatrixGrid.cells[(w - 1) * 6 + (b - 1)].shots} 枪`"
                  ></i>
                </template>
              </div>
              <p v-if="!heatMatrixGrid.any" class="pt-3 text-center text-[11px] text-inkFaint">这一周没人动弹。</p>
            </div>
          </section>
        </div>

        <!-- 我的战报：登录者自己在这一周的动静（按 events 的 actor_member_id 统计），零动条目隐身 -->
        <div v-if="personalRows.length" class="rise mt-4 overflow-hidden rounded-2xl border border-stationPink/25 bg-cardBg" style="--wi: 6">
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
              <p class="tabular-nums mt-0.5 text-[19px] font-semibold leading-6 text-inkMain">
                <template v-if="row.text">{{ row.text }}</template><CountUpNum v-else :value="row.value" /><span v-if="!row.text" class="ml-1 text-[11px] font-normal text-inkFaint">{{ row.unit }}</span>
              </p>
            </div>
          </div>
        </div>

        <!-- 分站流水：有动静才摆卡，数全出自台账事件与入库时间行 -->
        <div v-if="activityRows.length" class="rise mt-4 overflow-hidden rounded-2xl border border-lineSoft bg-cardBg" style="--wi: 7">
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
              <p class="tabular-nums mt-0.5 text-[19px] font-semibold leading-6 text-inkMain">
                <CountUpNum :value="row.value" /><span class="ml-1 text-[11px] font-normal text-inkFaint">{{ row.unit }}</span>
              </p>
            </div>
          </div>
        </div>

        <!-- 站外游客：未登录的动静单开一卡，与成员账分列不混数；全零整卡隐身 -->
        <div v-if="guestCardVisible" class="rise mt-4 overflow-hidden rounded-2xl border border-lineSoft bg-cardBg" style="--wi: 8">
          <div class="weekly-head flex items-center justify-between border-b border-lineSoft px-5 py-3">
            <h2 class="text-[15px] font-semibold text-inkMain">站外游客</h2>
            <span class="text-[11px] text-inkFaint" title="人数→逛主页→进厅→播放逐级收窄，百分比是相邻两级的过路率；被挡与试登录在瓦片里单摆">游客过路率</span>
          </div>
          <div v-if="guestFunnel.some((s) => s.value > 0)" class="border-b border-lineSoft/60 px-5 pb-2.5 pt-4">
            <div v-for="s in guestFunnel" :key="s.label" v-show="s.value > 0" class="mb-1.5 flex items-center gap-2 last:mb-0">
              <span class="w-12 shrink-0 text-right text-[11px] text-inkSub">{{ s.label }}</span>
              <div class="h-[18px] rounded-[4px] transition-all duration-500" :class="s.cls" :style="{ width: s.pct + '%' }" :title="s.tip"></div>
              <span class="text-[11px] font-semibold tabular-nums text-inkMain">{{ s.value }}</span>
              <span class="text-[10px] text-inkFaint">{{ s.unit }}<template v-if="s.conv != null"> · 上步的 {{ s.conv }}%</template></span>
            </div>
          </div>
          <div class="grid grid-cols-2 gap-x-6 px-5 py-4 sm:grid-cols-4">
            <div v-for="row in guestRows" :key="row.label" class="py-1.5" :title="row.tip">
              <p class="text-[11px] text-inkFaint">{{ row.label }}</p>
              <p class="tabular-nums mt-0.5 text-[19px] font-semibold leading-6 text-inkMain">
                <CountUpNum :value="row.value" /><span class="ml-1 text-[11px] font-normal text-inkFaint">{{ row.unit }}</span>
              </p>
            </div>
          </div>
        </div>

        <!-- 家底明细：存货全表大数，一行摆齐 -->
        <div class="rise mt-4 rounded-2xl border border-lineSoft bg-cardBg px-5 py-3.5" style="--wi: 9">
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
          <!-- 新货：直接复用个人空间那面馆藏壁（站长 11-03 定）。原来 4 列横排卡加分页看着像清单，
               现在走同一个拼贴墙内核 CanvasWall：可拖、可缩放、点卡进放映厅，工具条与手感两边一致。
               喂料、分页、空牌都由窗口壳管，这里只把本周新货整份递过去。 -->
          <div class="rise mt-4" style="--wi: 10">
            <WallWindow
              :fed-items="newsWallItems"
              title="新货"
              empty-title="这一周没有新货入站"
              empty-subtitle="有人投稿进来，这面墙就活了"
            />
          </div>

          <!-- 最多人看 Top5 -->
          <section class="rise mt-4 overflow-hidden rounded-2xl border border-lineSoft bg-cardBg" style="--wi: 11">
            <div class="weekly-head flex items-center justify-between border-b border-lineSoft px-5 py-3">
              <h2 class="text-[15px] font-semibold text-inkMain">最多人看</h2>
            </div>
            <ol v-if="data.hottest.length">
              <li v-for="(h, i) in data.hottest" :key="h.bvid" class="border-b border-lineSoft/60 last:border-b-0">
                <!-- 整行不再是链接（站长 11-02 顺手定的命中区收口）：名次、「N 位观众」保持读数区，整行不给背景高亮；
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
                      v-if="coverSrc(h, 112)"
                      :src="coverSrc(h, 112)"
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
                  </span>
                  <span class="shrink-0 text-[13px] text-inkSub"><span class="font-semibold text-stationPink">{{ h.viewers }}</span> 位观众</span>
                </div>
              </li>
            </ol>
            <p v-else class="px-6 py-10 text-center text-sm text-inkFaint">这一周没人进厅。</p>
          </section>

          <!-- 时长榜：这一周真看了多久（只数成员心跳，一枪 30 秒；游客看不了片不入账）。三栏同看时长：片 / UP / 分区 -->
          <section v-if="hasDuration" class="rise mt-4 overflow-hidden rounded-2xl border border-lineSoft bg-cardBg" style="--wi: 12">
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
          <div v-if="data.topUps.length || data.topZones.length" class="rise mt-4 grid grid-cols-1 gap-4 lg:grid-cols-2" style="--wi: 13">
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
          <section v-if="staleRows.length" class="rise mt-4 overflow-hidden rounded-2xl border border-lineSoft bg-cardBg" style="--wi: 14">
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

    <!-- 海报调参台（11-04 重做）：左边拧参数、右边看实况，预览与下载走同一支笔，所见即所得 -->
    <div v-if="posterOpen" class="fixed inset-0 z-50 flex items-center justify-center bg-black/55 p-4" @click.self="posterOpen = false">
      <div class="flex max-h-[92vh] w-full max-w-[1010px] overflow-hidden rounded-3xl bg-white shadow-2xl ring-1 ring-black/[.06] dark:bg-[#1a1e27] dark:ring-white/10">
        <!-- 左栏：参数 -->
        <div class="w-[288px] shrink-0 overflow-y-auto border-r border-lineSoft px-5 py-5">
          <div class="flex items-baseline justify-between">
            <h3 class="text-[15px] font-semibold text-inkMain">海报调参台</h3>
            <span class="rounded-full bg-[#f1f2f4] px-2 py-0.5 text-[10px] font-medium text-inkFaint dark:bg-white/10">{{ posterSize.w }} × {{ posterSize.h }}</span>
          </div>
          <p class="mt-1.5 text-[11px] leading-[17px] text-inkFaint">左边拧参数，右边就是成品。预览与导出跑同一支笔。</p>

          <p class="mt-5 text-[11px] font-medium text-inkFaint">文字</p>
          <label class="mt-2 block text-[12px] text-inkSub">海报标题</label>
          <input v-model="posterOpts.title" maxlength="20" placeholder="小破分站周报" class="mt-1 w-full rounded-xl border border-lineSoft bg-white px-3 py-2 text-[13px] text-inkMain outline-none transition focus:border-stationPink dark:bg-white/5" />
          <label class="mt-2.5 block text-[12px] text-inkSub">副标语</label>
          <input v-model="posterOpts.subtitle" maxlength="30" placeholder="留空就不显示" class="mt-1 w-full rounded-xl border border-lineSoft bg-white px-3 py-2 text-[13px] text-inkMain outline-none transition focus:border-stationPink dark:bg-white/5" />
          <label class="mt-2.5 block text-[12px] text-inkSub">署名（昵称）</label>
          <input v-model="posterOpts.sign" maxlength="12" :placeholder="member ? member.nickname || member.username || '旅人' : '没登录'" class="mt-1 w-full rounded-xl border border-lineSoft bg-white px-3 py-2 text-[13px] text-inkMain outline-none transition focus:border-stationPink dark:bg-white/5" />
          <div class="mt-3 flex items-center justify-between py-1">
            <span class="text-[12px] text-inkSub">横幅上的署名头像</span>
            <button type="button" class="relative h-5 w-9 rounded-full transition" :class="posterOpts.showSign ? 'bg-stationPink' : 'bg-[#d9dde3]'" @click="posterOpts.showSign = !posterOpts.showSign">
              <span class="absolute top-0.5 h-4 w-4 rounded-full bg-white shadow transition-all" :style="{ left: posterOpts.showSign ? '18px' : '2px' }"></span>
            </button>
          </div>

          <p class="mt-5 text-[11px] font-medium text-inkFaint">风格</p>
          <div class="mt-2 flex gap-1.5">
            <button
              v-for="s in POSTER_STYLES"
              :key="s.key"
              type="button"
              class="flex flex-1 flex-col items-start gap-0.5 rounded-xl border px-2.5 py-2 text-left transition"
              :class="posterOpts.style === s.key ? 'border-stationPink bg-stationPink/[.07]' : 'border-lineSoft hover:bg-[#f5f6f7] dark:hover:bg-white/5'"
              :title="s.hint"
              @click="pickPosterStyle(s.key)"
            >
              <span class="text-[12px]" :class="posterOpts.style === s.key ? 'font-medium text-inkMain' : 'text-inkSub'">{{ s.label }}</span>
              <span class="text-[10px] leading-4 text-inkFaint">{{ s.hint }}</span>
            </button>
          </div>

          <p class="mt-5 text-[11px] font-medium text-inkFaint">头图虚焦</p>
          <div class="mt-2 flex gap-1.5">
            <button
              v-for="h in POSTER_HERO_BLUR"
              :key="h.px"
              type="button"
              class="flex flex-1 flex-col items-start gap-0.5 rounded-xl border px-2.5 py-2 text-left transition"
              :class="posterOpts.heroBlur === h.px ? 'border-stationPink bg-stationPink/[.07]' : 'border-lineSoft hover:bg-[#f5f6f7] dark:hover:bg-white/5'"
              :title="h.hint"
              @click="posterOpts.heroBlur = h.px"
            >
              <span class="text-[12px]" :class="posterOpts.heroBlur === h.px ? 'font-medium text-inkMain' : 'text-inkSub'">{{ h.label }}</span>
              <span class="text-[10px] leading-4 text-inkFaint">{{ h.hint }}</span>
            </button>
          </div>

          <p class="mt-5 text-[11px] font-medium text-inkFaint">纸样</p>
          <div class="mt-2 flex gap-1.5">
            <button
              v-for="t in POSTER_THEMES"
              :key="t.key"
              type="button"
              class="flex flex-1 flex-col items-center gap-1.5 rounded-xl border px-1.5 py-2 transition"
              :class="posterOpts.theme === t.key ? 'border-stationPink bg-stationPink/[.07]' : 'border-lineSoft hover:bg-[#f5f6f7] dark:hover:bg-white/5'"
              :title="t.hint"
              @click="posterOpts.theme = t.key"
            >
              <i
                class="block h-5 w-full rounded-md ring-1 ring-black/10"
                :style="{ background: posterThemeSwatch(t.key) }"
              ></i>
              <span class="text-[11px]" :class="posterOpts.theme === t.key ? 'font-medium text-inkMain' : 'text-inkFaint'">{{ t.label }}</span>
            </button>
          </div>

          <p class="mt-5 text-[11px] font-medium text-inkFaint">强调色</p>
          <div class="mt-2 flex items-center gap-2.5">
            <button
              v-for="(a, k) in posterAccentList()"
              :key="k"
              type="button"
              class="h-7 w-7 rounded-full transition"
              :class="posterOpts.accent === k ? 'ring-2 ring-offset-2 ring-offset-white dark:ring-offset-[#1a1e27]' : 'hover:scale-105'"
              :style="{ background: a.base, ...(posterOpts.accent === k ? { boxShadow: `0 0 0 2px ${a.base}` } : {}) }"
              :title="a.name || k"
              @click="posterOpts.accent = k"
            ></button>
            <span class="text-[11px] text-inkFaint">{{ posterOpts.style === 'construct' ? '骨架固定红黑纸，这里只换那一色大声' : '整张海报只用一种强调色' }}</span>
          </div>

          <p class="mt-5 text-[11px] font-medium text-inkFaint">版块</p>
          <div class="mt-1.5 space-y-0.5">
            <div v-for="t in posterSections" :key="t.key" class="flex items-center justify-between py-1">
              <span class="text-[12px] text-inkSub">{{ t.label }}</span>
              <button type="button" class="relative h-5 w-9 rounded-full transition" :class="posterOpts.show[t.key] ? 'bg-stationPink' : 'bg-[#d9dde3]'" @click="posterOpts.show[t.key] = !posterOpts.show[t.key]">
                <span class="absolute top-0.5 h-4 w-4 rounded-full bg-white shadow transition-all" :style="{ left: posterOpts.show[t.key] ? '18px' : '2px' }"></span>
              </button>
            </div>
          </div>
          <p class="mt-4 text-[10px] leading-4 text-inkFaint/80">导出为 2 倍分辨率 PNG（宽 {{ POSTER_WIDTH * 2 }} px）。</p>
        </div>

        <!-- 右栏：实况预览 -->
        <div class="flex min-w-0 flex-1 flex-col bg-[#eceef1] dark:bg-[#12151c]">
          <div class="flex min-h-0 flex-1 items-start justify-center overflow-y-auto px-8 py-7">
            <div class="relative">
              <div class="overflow-hidden rounded-xl shadow-[0_18px_48px_-12px_rgba(15,18,25,.45)] ring-1 ring-black/[.07] dark:ring-white/10">
                <canvas ref="posterCanvas" class="block w-full max-w-[380px]"></canvas>
              </div>
              <div v-if="posterBusy" class="absolute inset-0 flex items-center justify-center rounded-xl bg-white/70 text-[12px] text-inkFaint">封面加载中…</div>
            </div>
          </div>
          <div class="flex items-center justify-between gap-3 border-t border-black/[.06] bg-white/70 px-6 py-3 dark:border-white/10 dark:bg-white/[.03]">
            <p class="text-[11px] text-inkFaint">预览即成品 · 导出 {{ posterSize.w }} × {{ posterSize.h }} px</p>
            <div class="flex items-center gap-2">
              <button class="rounded-full border border-lineSoft bg-white px-4 py-1.5 text-sm text-inkSub transition hover:border-stationPink hover:text-stationPink dark:bg-white/5" @click="posterOpen = false">关闭</button>
              <button class="rounded-full bg-stationPink px-5 py-1.5 text-sm font-medium text-white transition hover:opacity-90" @click="downloadPoster">下载 PNG</button>
            </div>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
/* 错峰淡入（站长 11-03 点名 A2）：卡片按 --wi 序号隔 55ms 起跳，整页像拉卷帘而不是堆砖头；
   尊重系统减弱动效设置，那档用户直接见终态 */
.rise {
  animation: weekly-rise 0.5s cubic-bezier(0.22, 0.61, 0.36, 1) both;
  animation-delay: calc(var(--wi, 0) * 55ms);
}
@keyframes weekly-rise {
  from {
    opacity: 0;
    transform: translateY(10px);
  }
  to {
    opacity: 1;
    transform: none;
  }
}
@media (prefers-reduced-motion: reduce) {
  .rise {
    animation: none;
  }
}

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
