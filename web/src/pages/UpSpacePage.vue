<script setup>
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import TopBar from '../components/TopBar.vue'
import VideoCard from '../components/VideoCard.vue'
import DynamicFeedCard from '../components/DynamicFeedCard.vue'
import PlayCountIcon from '../components/PlayCountIcon.vue'
import { coverSrcOf } from '../utils/coverSrc'
import { createPreviewHost } from '../utils/hoverPreview'
import { defaultAvatar } from '../utils/avatar.js'
import { fmtFans } from '../utils/fans.js'
import { keepFeed, tailCursor } from '../utils/dynFeed.js'
import { faceFor, chosenFace, chooseFace } from '../utils/faceChoice.js'

// ─── UP 空间：这位 UP 在【本站】存了多少片。放映厅名片上那颗蓝钮进来。
// 版式 1:1 复刻 B 站空间主页（站长 11-05 点单）：全宽页头带 → 名片叠压（头像/名字/认证徽/签名/数字数据行）
// → tab 行 → 视频卡阵。本站没有的功能（关注/消息/音频/相簿/专栏）一律不造，tab 只留「视频」。
// 页头带（站长 11-06 点单案一+案三，头图已同日整体废除）：从这位的本地头像取主色，现调一版专属柔渐变
// （拿不到色落全站蓝调）+ 网点纹理；名片下挂「热门归档」条（按播放 top4，站内数据零出网）。
// 两条口径都在后端，这里不挑：只列已到货的行（没缓存的不给人看）、别人的私有稿等于不存在。
const route = useRoute()

const up = ref(null) // 站内归档的名片快照；null = 分站从没收过这位（或档案四字全空）
const items = ref([])
const loading = ref(false)
const failed = ref(false)
const faceFailed = ref(false)

const mid = computed(() => route.params.mid)
const name = computed(() => up.value?.name || `UP 主 ${mid.value}`)
const faceSrc = computed(() => {
  // 本机钦选压过服务器脸（小本本住 utils/faceChoice，全员可换只改本机）；取色页头也跟着钦选脸走
  const face = faceFor(mid.value, up.value?.face)
  return face && !faceFailed.value ? face : defaultAvatar
})
const spaceUrl = computed(() => `https://space.bilibili.com/${mid.value}`)
const officialTitle = computed(() => String(up.value?.official ?? '').trim())
function onFaceError() {
  // 钦选的件被清站拍掉了：收回本机钦选、让服务器现行脸顶上，再砸才落默认小人
  if (chosenFace(mid.value)) {
    chooseFace(mid.value, null)
    faceFailed.value = false
    return
  }
  faceFailed.value = true
}

// ── 案三·取色纹理（11-06 站长点单）：从头像现调这位的专属渐变，纯前端 canvas 不落盘。
// 取色失败（远程件 CORS 污染画布/默认头像/全灰头像）一律回空串，模板里的蓝调类就是天然兑底档。
const paletteCache = new Map() // faceSrc → css 渐变；同一位反复进出页不重解码
const bannerTint = ref('')
let paletteToken = 0 // 换 mid 快时旧解码不许骑到新解码头上

watch(
  faceSrc,
  async (src) => {
    const token = ++paletteToken
    bannerTint.value = ''
    if (!src || src === defaultAvatar) return
    if (paletteCache.has(src)) {
      bannerTint.value = paletteCache.get(src)
      return
    }
    try {
      const tint = await tintOfFace(src)
      if (token !== paletteToken) return
      if (tint) {
        paletteCache.set(src, tint)
        bannerTint.value = tint
      }
    } catch {
      /* 取不到色就蓝调：这不是故障，是常态（远程件/CORS/素人灰调头像） */
    }
  },
  { immediate: true },
)

async function tintOfFace(src) {
  const img = await loadImage(src)
  if (!img.width || !img.height) return null
  const c = document.createElement('canvas')
  c.width = 36
  c.height = 36
  const g = c.getContext('2d', { willReadFrequently: true })
  g.drawImage(img, 0, 0, 36, 36)
  // 远程件没给 CORS 许可时这一行直接抛 SecurityError → 外层 catch 落蓝调
  const { data } = g.getImageData(0, 0, 36, 36)
  const buckets = new Map() // 粗桶取众数：整张脸的主色赢，发丝杂色赢不了
  for (let i = 0; i < data.length; i += 4) {
    if (data[i + 3] < 128) continue
    const r = data[i]
    const gr = data[i + 1]
    const b = data[i + 2]
    const mx = Math.max(r, gr, b)
    const mn = Math.min(r, gr, b)
    if (mx < 60 || mn > 205 || mx - mn < 28) continue // 近黑/近白/低彩的灰不参与气质
    const key = `${r >> 5}:${gr >> 5}:${b >> 5}`
    const e = buckets.get(key) || { n: 0, r: 0, g: 0, b: 0 }
    e.n += 1
    e.r += r
    e.g += gr
    e.b += b
    buckets.set(key, e)
  }
  let best = null
  for (const e of buckets.values()) if (!best || e.n > best.n) best = e
  if (!best || best.n < 24) return null // 有彩像素不到 2% 不算有气质，不值得开妆
  const h = hueOf(best.r / best.n, best.g / best.n, best.b / best.n)
  // 三档都是同族色相的浅柔版：页头是幕布不是霓虹，名字与头像才是主角（深字压在浅底上永远读得清）
  return `linear-gradient(118deg, hsl(${Math.round(h)} 55% 80%) 0%, hsl(${Math.round((h + 24) % 360)} 46% 87%) 52%, hsl(${Math.round((h + 334) % 360)} 36% 92%) 100%)`
}

function hueOf(r, g, b) {
  const mx = Math.max(r, g, b)
  const mn = Math.min(r, g, b)
  const d = mx - mn
  if (!d) return 205 // 全灰：蓝调收场
  if (mx === r) return ((((g - b) / d) % 6) + 6) * 60
  if (mx === g) return (((b - r) / d) + 2) * 60
  return (((r - g) / d) + 4) * 60
}

function loadImage(src) {
  return new Promise((resolve, reject) => {
    const img = new Image()
    img.referrerPolicy = 'no-referrer'
    if (/^https?:/i.test(src)) img.crossOrigin = 'anonymous' // 远程件愿意给 CORS 就能取色，不给就按取不到算
    img.onload = () => resolve(img)
    img.onerror = reject
    img.src = src
  })
}

// ── 案一·热门归档条（11-06 站长点单）：站内已到货的稿按播放数取 top4，零出网；
// 不超过 4 条时不摆——网格本身就是全部货色，再摆一排就是复读。
// 播放在站内只有折万文本（video_meta.view_count TEXT，B 站口径「2.9万」），排序就按同族口径折数
//（与 StationDb.cs 的「文本播放数折算 SQL 表达式」同一把尺：亿 1e8 / 万 1e4，坏值 -1 沉底）。
const hotPicks = computed(() => {
  if (items.value.length <= 4) return []
  return [...items.value].sort((a, b) => viewNum(b.viewCountText) - viewNum(a.viewCountText)).slice(0, 4)
})

function viewNum(t) {
  const s = String(t ?? '').replace(/[,，\s]/g, '').trim()
  if (!s || s === '—') return -1
  const n = parseFloat(s)
  if (Number.isNaN(n)) return -1
  if (s.endsWith('亿')) return n * 1e8
  if (s.endsWith('万')) return n * 1e4
  return n
}

// ── 热门归档条悬停预览（站长 11-06 点单：这四枚迷你卡「没有悬停预览」）：
// 机位与网格 VideoCard、分区横滚行同一家（utils/hoverPreview）：停够 350ms 才起播，
// 走开立即按停淡回封面、200ms 内回来原地续播，全站只许一路在播，出错弃卡不重试。
// 一个机位守这四张卡，指针进谁的封面进谁的播（BangumiHome 横滚行同形）；
// 元素身份用 bvid 就够——top4 同源同列表无重号，不存在横滚行那种同片跨行串台。
const hotPreview = createPreviewHost()
const hotCoverKey = ref('') // 放大只跟「鼠标在封面上」走，不吃标题的悬停（与网格卡同一口径）
const hotPreviewEls = new Map() // bvid → <video>：机位要拿到当前这张卡的元素才能按停与起播
function bindHotPreview(bvid, el) {
  if (el) hotPreviewEls.set(bvid, el)
  else hotPreviewEls.delete(bvid) // 迟到的 null 只销自己那张，不抹别人
}
function onHotCoverEnter(v) {
  hotCoverKey.value = v.bvid
  hotPreview.enter(v.bvid, v.previewUrl || '', (id) => hotPreviewEls.get(id) || null)
}
function onHotCoverLeave(v) {
  if (hotCoverKey.value === v.bvid) hotCoverKey.value = ''
  hotPreview.leave(v.bvid)
}
onBeforeUnmount(() => hotPreview.dispose())

// ── 动态 tab（站长 11-06 点单）：与视频并排，摆这位 UP 本地已存的动态。
// 数据走橱窗同款口 /api/dynamics?mid=（target_mid=这位，转发算他头上），后端零新增；
// 卡与动态橱窗同一张 DynamicFeedCard，改卡面两边同亮。
// 进页先发一发 limit=1 的探测：空列表就把 tab 压灰不可点（没被动态关注盯过 / 真没发过，同口径都是没货）。
const tab = ref('videos') // videos | dynamics
const dyn = ref([])
const dynHasMore = ref(false)
const dynLoading = ref(false)
const dynLoadingMore = ref(false)
const dynEmpty = ref(false)
const DY_PAGE = 20
// 翻页游标拿原始返回的尾巴（含被滤的开播条）：防整页开播时原地打转（口径住 utils/dynFeed.js）
let dynCursor = null

// 探测即预取（11-06 随开播条退场改版）：拉一整页、滤掉开播条再判货，有货直接灌进 feed，
// 点开动态零等待。limit=1 的老探测一发若打在开播条上，会误判没货错压灰 tab，所以改成整页同口径。
async function probeDynamics() {
  dynEmpty.value = false
  dyn.value = []
  dynHasMore.value = false
  dynCursor = null
  try {
    const res = await fetch(`/api/dynamics?mid=${encodeURIComponent(mid.value)}&limit=${DY_PAGE}`)
    if (!res.ok) return
    const data = await res.json().catch(() => null)
    const kept = keepFeed(data?.items)
    dynEmpty.value = !kept.length
    dynHasMore.value = !!data?.hasMore
    dynCursor = tailCursor(data?.items)
    dyn.value = kept
    // 兑底：万一开局就坐在动态 tab 上（深链预留）又实测没货，选中的旗退回视频。正常流永远 videos 起手，不触发。
    if (tab.value === 'dynamics' && !kept.length) tab.value = 'videos'
  } catch {
    /* 探测断线当未知：tab 亮着，真点开了拉不到再压灰 */
  }
}

async function loadDynamics() {
  if (dynLoading.value) return
  dynLoading.value = true
  try {
    const q = new URLSearchParams({ mid: String(mid.value), limit: String(DY_PAGE) })
    const res = await fetch(`/api/dynamics?${q}`)
    if (res.ok) {
      const data = await res.json().catch(() => null)
      dyn.value = keepFeed(data?.items)
      dynHasMore.value = !!data?.hasMore
      dynCursor = tailCursor(data?.items)
      if (!dyn.value.length) {
        dynEmpty.value = true // 实测确认没货 → tab 压灰
        tab.value = 'videos' // 不变量：压灰的旗不许站在选中态（探测当时未知、点开才测空的岔路也拉回视频）
      }
    }
  } catch {
    /* 拉砸静默，feed 空着由空态牌接住 */
  } finally {
    dynLoading.value = false
  }
}

// 翻页与橱窗同一对游标（beforeId + beforeTime），只多了 mid 点名
async function loadDynamicsMore() {
  if (!dynHasMore.value || !dynCursor || dynLoadingMore.value) return
  dynLoadingMore.value = true
  try {
    const q = new URLSearchParams({ mid: String(mid.value), limit: String(DY_PAGE), beforeId: String(dynCursor.id), beforeTime: String(dynCursor.time) })
    const res = await fetch(`/api/dynamics?${q}`)
    if (res.ok) {
      const data = await res.json().catch(() => null)
      dyn.value = dyn.value.concat(keepFeed(data?.items))
      dynHasMore.value = !!data?.hasMore
      dynCursor = tailCursor(data?.items) ?? dynCursor
    }
  } catch {
    /* 翻页断一下不吭声，按钮还亮着 */
  } finally {
    dynLoadingMore.value = false
  }
}

function pickTab(t) {
  if (t === 'dynamics' && dynEmpty.value) return
  tab.value = t
  if (t === 'dynamics' && !dyn.value.length && !dynLoading.value) loadDynamics()
}

// 卡里安利/补采的回话要有地方落：宿主不接 @toast 就静默丢了，同橱窗一条脾气浮一颗胶囊
const spaceToast = ref(null)
let spaceToastTimer = null
function showToast(msg) {
  spaceToast.value = msg
  clearTimeout(spaceToastTimer)
  spaceToastTimer = setTimeout(() => { spaceToast.value = null }, 2600)
}

// ── 头像相册弹层（切脸列表 11-06）：点名片脸开弹层，点谁是谁 —— 只写本机小本本，
// 服务器不记一笔（与动态本机制同脾气）。档案由周巡检逐周攒（up_faces），盘上没件的条目后端已滤。
const faceAlbumOpen = ref(false)
const faceAlbum = ref([])
const faceAlbumLoading = ref(false)
const faceAlbumLoaded = ref(false)
const faceAlbumFailed = ref(false)
async function openFaceAlbum() {
  faceAlbumOpen.value = true
  if (faceAlbumLoaded.value || faceAlbumLoading.value) return
  faceAlbumLoading.value = true
  faceAlbumFailed.value = false
  try {
    const res = await fetch(`/api/up/${mid.value}/faces`)
    if (res.ok) {
      faceAlbum.value = await res.json().catch(() => [])
      faceAlbumLoaded.value = true
    } else {
      faceAlbumFailed.value = true
    }
  } catch {
    faceAlbumFailed.value = true
  } finally {
    faceAlbumLoading.value = false
  }
}
function pickFace(rel) {
  chooseFace(mid.value, rel)
  faceFailed.value = false // 换脸重开失败位，新件拉不到由 onFaceError 接手
  showToast(rel ? `这台浏览器上，${name.value}就是这张脸了` : '回到跟服务器现行脸')
}
function isChosen(rel) {
  return chosenFace(mid.value) === rel
}

async function load() {
  loading.value = true
  failed.value = false
  faceFailed.value = false
  try {
    const res = await fetch(`/api/up/${mid.value}/videos`)
    if (!res.ok) {
      failed.value = true
      up.value = null
      items.value = []
      return
    }
    const data = await res.json().catch(() => null)
    up.value = data?.up ?? null
    items.value = data?.items ?? []
    // 换人时相册与弹层一并归零，上一位的档案不许骑到新一位头上
    faceAlbumOpen.value = false
    faceAlbum.value = []
    faceAlbumLoaded.value = false
    faceAlbumFailed.value = false
    // 每次进页/换人都从视频 tab 起手，动态探测并行去探，不拦主棒
    tab.value = 'videos'
    probeDynamics()
  } catch {
    failed.value = true
    up.value = null
    items.value = []
  } finally {
    loading.value = false
  }
}

onMounted(load)
// 从别处换 mid 进来（书签、回退）也要重拉，不能顶着上一位的脸与货
watch(mid, load)
</script>

<template>
  <div class="min-h-screen">
    <TopBar />

    <main class="mx-auto max-w-[1400px] px-6 py-6">
      <!-- 骨架：与合集详情同款呼吸块，不摆转圈 -->
      <div v-if="loading" class="animate-pulse overflow-hidden rounded-2xl bg-black/[0.05] dark:bg-white/[0.06]">
        <div class="h-[180px] bg-black/[0.06] dark:bg-white/[0.08]"></div>
        <div class="p-6 pt-14">
          <div class="h-6 w-1/3 rounded bg-black/[0.06] dark:bg-white/[0.08]"></div>
          <div class="mt-3 h-3.5 w-1/2 rounded bg-black/[0.06] dark:bg-white/[0.08]"></div>
        </div>
      </div>

      <!-- 后端没应答 / 编号不是数字（路由 :long 约束不匹配会 404）：一句话 + 一颗重试 -->
      <div v-else-if="failed" class="rounded-xl border border-dashed border-black/10 dark:border-white/10 py-16 text-center">
        <svg viewBox="0 0 24 24" class="mx-auto h-10 w-10 text-inkFaint/50" fill="none" stroke="currentColor" stroke-width="1.4">
          <circle cx="12" cy="12" r="9" />
          <path d="M12 7.5v5M12 16h.01" stroke-linecap="round" />
        </svg>
        <p class="mt-3 text-[15px] text-inkMain">这一页没拉起来，可能分站后端没应答，也可能链接里的编号不对</p>
        <button class="mt-3 inline-block rounded-full border border-stationBlue px-5 py-1.5 text-[13px] text-stationBlue transition hover:bg-stationBlue hover:text-white" @click="load">
          再拉一次
        </button>
      </div>

      <template v-else>
        <!-- ── B 站式页头：全宽页头带 + 名片叠压 ── -->
        <section class="overflow-hidden rounded-2xl bg-cardBg shadow-[0_2px_12px_-4px_rgba(15,18,25,.12)] ring-1 ring-black/[0.05] dark:bg-[#1b1f27] dark:ring-white/10">
          <!-- 页头带（站长 11-06 点单案三）：头像取色专属渐变叠网点纹理，
               取不到色保留蓝调类兑底；暗色加一层压黑防浅底刺眼。纯妆面零数据零出网。
               名字大字水印二轮（11-06）退役：雾线正好从字腰切过，半融的名字读成噪声不读成妆，
               大封面本体也没有字；妆感若要找回见 git 历史（92/118px 黑体斜体 white/45）。
               外层 relative 包壳是给 .head-soft 定位用的：过渡带不能住在 head-fade 里面——
               mask 会连子层一起裁，带的下截（探进名片区那半截）会被页头带的 mask 杀在接缝上。 -->
          <div class="relative">
          <div
            class="head-fade relative h-[180px] bg-gradient-to-br from-stationBlue/25 via-stationBlue/10 to-transparent md:h-[210px]"
            :style="bannerTint ? { background: bannerTint } : null"
          >
            <div
              class="absolute inset-0"
              style="background-image: radial-gradient(rgba(255, 255, 255, 0.38) 1.1px, transparent 1.1px); background-size: 14px 14px"
              aria-hidden="true"
            ></div>
            <div class="absolute inset-0 bg-gradient-to-t from-black/[0.06] via-transparent to-transparent" aria-hidden="true"></div>
            <div class="absolute inset-0 hidden bg-black/25 dark:block" aria-hidden="true"></div>
          </div>
          <!-- 模糊过渡带（站长 11-06 二轮眼验：「光 mask 溶还太怪，像大封面那样做个模糊」）：
               横跨页头带与名片白底的接缝，用 backdrop-filter 把底下的渐变/网点就地雾化成一条磨砂带，
               与首页大封面 .hero-soft（HomePage）同一族手法：上缘渐进、下缘渐出，雾随色一起泄进白底。 -->
          <div class="head-soft" aria-hidden="true"></div>
          </div>

          <!-- 名片：头像骑压页头带下缘（B 站式 -mt-12 叠压），右侧数据横排，最右留去 B 站出口。
               relative z-10 不可省：名片是叠在页头带上的，不提前层就是名字与认证徽被背板活埋（11-05 真机截图实抓）。 -->
          <div class="relative z-10 flex flex-wrap items-end justify-between gap-x-6 px-6 pb-5">
            <div class="-mt-12 flex min-w-0 items-end gap-4">
              <!-- 点名片脸开头像档案弹层（切脸只改本机，住 utils/faceChoice）；「本机」角标 = 正用着钦选脸 -->
              <button
                class="relative shrink-0 cursor-pointer rounded-full border-0 bg-transparent p-0"
                :title="`看 ${name} 的头像档案（换脸只在这台浏览器生效）`"
                @click="openFaceAlbum"
              >
                <img
                  :src="faceSrc"
                  :alt="name"
                  loading="lazy"
                  referrerpolicy="no-referrer"
                  class="h-24 w-24 rounded-full bg-white/10 object-cover shadow-[0_4px_16px_-4px_rgba(15,18,25,.35)] ring-4 ring-white transition hover:brightness-105 dark:ring-[#1b1f27]"
                  @error="onFaceError"
                />
              </button>
              <div class="min-w-0 pb-1">
                <h1 class="flex flex-wrap items-center gap-2 text-[24px] font-semibold leading-tight text-inkMain md:text-[26px]">
                  <span class="min-w-0 truncate">{{ name }}</span>
                  <span
                    v-if="officialTitle"
                    class="inline-flex shrink-0 items-center gap-1 rounded-[4px] bg-stationBlue/10 px-1.5 py-[3px] text-[11px] font-normal text-stationBlue"
                    :title="officialTitle"
                  >
                    <svg viewBox="0 0 24 24" class="h-3 w-3" fill="currentColor">
                      <path d="M12 2l2.4 2.1 3.1-.5 1.2 2.9 2.9 1.2-.5 3.1L23.2 13l-2.1 2.4.5 3.1-2.9 1.2-1.2 2.9-3.1-.5L12 24.2 9.6 22.1l-3.1.5-1.2-2.9-2.9-1.2.5-3.1L.8 13l2.1-2.4-.5-3.1 2.9-1.2 1.2-2.9 3.1.5z" transform="scale(.92) translate(1 -1)" />
                      <path d="M10.6 14.6l-2.2-2.2-1.3 1.3 3.5 3.5 6-6-1.3-1.3z" fill="#fff" />
                    </svg>
                    {{ officialTitle }}
                  </span>
                  <span v-if="up" class="shrink-0 rounded-full bg-black/[0.04] px-2 py-0.5 text-[11px] font-normal text-inkFaint ring-1 ring-black/[0.06] dark:bg-white/[0.06] dark:ring-white/10">
                    站内归档
                  </span>
                </h1>
                <p class="mt-1.5 max-w-[720px] truncate text-[13px] text-inkSub">{{ up?.sign || '这个 UP 主还没有签名' }}</p>
                <!-- B 站式数据行：数字加粗 + 灰标签横排（本站有的说本站的，关注/点赞那套没有就不造） -->
                <div class="mt-3 flex items-center gap-7 text-[12px] text-inkFaint">
                  <span class="leading-none">
                    <b class="mr-1 text-[17px] font-semibold text-inkMain">{{ items.length }}</b>在库视频
                  </span>
                  <span v-if="up?.fans != null" class="leading-none">
                    <b class="mr-1 text-[17px] font-semibold text-inkMain">{{ fmtFans(up.fans) }}</b>粉丝
                  </span>
                  <span class="leading-none">
                    <b class="mr-1 font-mono text-[15px] font-medium text-inkSub">{{ mid }}</b>UID
                  </span>
                </div>
              </div>
            </div>
            <!-- 蓝钮把人从放映厅领进来，这里就得留一条回 B 站的路，否则原来点名字的那一下没了去处 -->
            <a
              :href="spaceUrl"
              target="_blank"
              rel="noreferrer"
              class="mb-1 shrink-0 rounded-lg border border-lineSoft bg-cardBg px-3.5 py-1.5 text-[12px] text-inkSub transition hover:border-stationBlue/50 hover:text-stationBlue"
            >
              去 B 站空间
            </a>
          </div>

          <!-- 热门归档条（站长 11-06 点单案一）：空白的页头下半区喂内容——本站归档里播放 top4，
               点击与网格卡同路（/watch/{bvid}）；不超 4 条整段不摆，复读机不上架。 -->
          <div v-if="hotPicks.length" class="px-6 pb-5">
            <div class="mb-2.5 flex items-baseline justify-between">
              <h2 class="text-[14px] font-semibold text-inkMain">TA 的热门归档</h2>
              <span class="text-[11px] text-inkFaint">按本站归档的播放数排</span>
            </div>
            <div class="grid grid-cols-2 gap-x-4 gap-y-3 md:grid-cols-4">
              <RouterLink v-for="v in hotPicks" :key="'hot-' + v.bvid" :to="`/watch/${v.bvid}`" class="group block min-w-0">
                <div
                  class="relative aspect-[16/10] overflow-hidden rounded-lg bg-[#f1f2f3] dark:bg-[#23262e]"
                  @mouseenter="onHotCoverEnter(v)"
                  @mouseleave="onHotCoverLeave(v)"
                >
                  <img
                    v-if="coverSrcOf(v, 260)"
                    :src="coverSrcOf(v, 260)"
                    :alt="v.title || v.bvid"
                    loading="lazy"
                    referrerpolicy="no-referrer"
                    class="h-full w-full object-cover transition duration-300"
                    :class="hotCoverKey === v.bvid ? 'scale-[1.04]' : ''"
                  />
                  <!-- 悬停预览层：与网格卡阵同一族——只挂在被悬停那一张的封面里，其余卡连元素都不建；
                       不抢点击不吃焦点，preload=none 不悬停一个字节都不拉；播放数徽标在 DOM 序后面，雾不起来盖它。 -->
                  <video
                    v-if="v.previewUrl && !hotPreview.isGaveUp(v.bvid) && hotPreview.isHost(v.bvid)"
                    :key="v.bvid + v.previewUrl"
                    :ref="(el) => bindHotPreview(v.bvid, el)"
                    :src="v.previewUrl"
                    muted
                    loop
                    playsinline
                    preload="none"
                    disablepictureinpicture
                    tabindex="-1"
                    aria-hidden="true"
                    class="pointer-events-none absolute inset-0 h-full w-full object-cover transition-opacity duration-200"
                    :class="hotPreview.isOn(v.bvid) ? 'opacity-100' : 'opacity-0'"
                    @loadeddata="hotPreview.markShown(v.bvid)"
                    @playing="hotPreview.markShown(v.bvid)"
                    @error="hotPreview.fail(v.bvid)"
                  ></video>
                  <span
                    v-if="v.viewCountText && v.viewCountText !== '—'"
                    class="absolute bottom-1 left-1.5 flex items-center gap-1 text-[11px] font-medium text-white"
                    style="text-shadow: 0 1px 2px rgba(0, 0, 0, 0.85)"
                  >
                    <PlayCountIcon class="h-[13px] w-[13px]" />{{ v.viewCountText }}
                  </span>
                </div>
                <p class="mt-1.5 line-clamp-1 text-[12.5px] leading-[18px] text-inkSub transition group-hover:text-stationPink">
                  {{ v.title || v.bvid }}
                </p>
              </RouterLink>
            </div>
          </div>
        </section>

        <!-- ── tab 行（站长 11-06 点单加「动态」）：B 站空间有音频/相簿/专栏一整排，本站没这些就不造；
             动态本地有货才立旗：探测为空就压灰不可点，不摆一旗空门 ── -->
        <nav class="mt-5 flex items-center gap-6 border-b border-black/[0.06] dark:border-white/10">
          <button
            class="-mb-px border-b-2 pb-2.5 text-[15px] transition"
            :class="tab === 'videos' ? 'border-stationPink font-semibold text-inkMain' : 'border-transparent text-inkSub hover:text-inkMain'"
            @click="pickTab('videos')"
          >
            视频 <span class="ml-0.5 text-[12px] font-normal text-inkFaint">{{ items.length }}</span>
          </button>
          <button
            class="-mb-px border-b-2 pb-2.5 text-[15px] transition"
            :class="tab === 'dynamics' ? 'border-stationPink font-semibold text-inkMain' : dynEmpty ? 'cursor-not-allowed border-transparent text-inkFaint/50' : 'border-transparent text-inkSub hover:text-inkMain'"
            :disabled="dynEmpty"
            :title="dynEmpty ? '分站本地还没存过这位的动态：没被「动态关注」盯过，或巡捕还没到货' : ''"
            @click="pickTab('dynamics')"
          >
            动态
          </button>
        </nav>

        <template v-if="tab === 'videos'">
        <!-- 卡阵：与合集详情同形的网格，卡面一根毛不改 -->
        <div v-if="items.length" class="mt-5 grid grid-cols-2 gap-x-4 gap-y-[38px] md:grid-cols-3 xl:grid-cols-5">
          <!-- 命中区收口（站长 11-02 定的 coverTitleText）：外层那根包整卡的 RouterLink 撤了，可点的是 VideoCard 内部的
               封面与标题字面；v-for / :key 直接落到卡上，网格子项就是卡自己（卡根是个 block 的 div，版式不变） -->
          <VideoCard v-for="video in items" :key="video.bvid" :video="video" />
        </div>

        <!-- 空态：分站还没收过这位。不摆 0 张空卡，也不假装这人不存在 —— 页头那张脸还在，说的是「这里没货」 -->
        <div v-else class="mt-5 rounded-xl border border-dashed border-black/10 dark:border-white/10 py-16 text-center">
          <svg viewBox="0 0 24 24" class="mx-auto h-10 w-10 text-inkFaint/50" fill="none" stroke="currentColor" stroke-width="1.4">
            <rect x="3" y="4" width="18" height="16" rx="3" />
            <path d="M3 9h18M8 14h8" stroke-linecap="round" />
          </svg>
          <p class="mt-3 text-[13px] text-inkSub">分站还没收过这位的稿件</p>
          <p class="mt-1 text-[12px] text-inkFaint">想收进来：放映厅投稿，或顶栏的投稿口连货架一起搬</p>
          <a
            :href="spaceUrl"
            target="_blank"
            rel="noreferrer"
            class="mt-3 inline-block rounded-full border border-stationBlue px-5 py-1.5 text-[13px] text-stationBlue transition hover:bg-stationBlue hover:text-white"
          >
            去 B 站看看这位的空间
          </a>
        </div>
        </template>

        <!-- 动态 feed：与橱窗同一条竖列（卡宽上限 560，窄屏自然铺满），卡本体就是 DynamicFeedCard -->
        <template v-else>
          <p v-if="dynLoading" class="mt-5 py-14 text-center text-sm text-inkFaint">正在掀这位的动态墙…</p>
          <div v-else-if="dyn.length" class="mx-auto mt-5 max-w-[560px]">
            <DynamicFeedCard v-for="dd in dyn" :key="dd.id ?? dd.dynamicId" :d="dd" @toast="showToast" />
            <div class="pb-4 pt-1 text-center">
              <button
                v-if="dynHasMore"
                class="rounded-full border border-lineSoft bg-cardBg px-6 py-1.5 text-xs text-inkSub transition hover:border-stationPink hover:text-stationPink disabled:opacity-50"
                :disabled="dynLoadingMore"
                @click="loadDynamicsMore"
              >
                {{ dynLoadingMore ? '翻页中…' : '看更多' }}
              </button>
              <p v-else class="text-xs text-inkFaint">这位的动态见底了，巡捕每小时来补货</p>
            </div>
          </div>
          <div v-else class="mt-5 rounded-xl border border-dashed border-black/10 dark:border-white/10 py-16 text-center">
            <p class="text-[13px] text-inkSub">分站本地还没存过这位的动态</p>
            <p class="mt-1 text-[12px] text-inkFaint">站长在站长室「动态关注」里登记这位后，等巡捕一轮就有货了</p>
          </div>
        </template>

        <!-- 头像档案弹层：点谁是谁，只写本机小本本（与动态本机赞同脾气，服务器不记一笔） -->
        <div v-if="faceAlbumOpen" class="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4" @click.self="faceAlbumOpen = false">
          <div class="w-full max-w-[420px] rounded-2xl bg-cardBg p-5 shadow-xl">
            <div class="flex items-start justify-between gap-3">
              <h3 class="text-[15px] font-semibold text-inkMain">{{ name }} 的头像档案</h3>
              <button class="rounded px-1 text-[15px] leading-none text-inkFaint transition hover:text-inkMain" aria-label="关闭" @click="faceAlbumOpen = false">✕</button>
            </div>
            <p class="mt-1 text-[12px] leading-relaxed text-inkFaint">点谁是谁——只存在这台浏览器，别人看不到。B 站没有历史头像接口，巡捕每周巡访时见到谁的脸就记谁，档案一天天厚起来。</p>
            <div v-if="faceAlbumLoading" class="py-9 text-center text-[13px] text-inkFaint">正在翻档案库…</div>
            <div v-else-if="faceAlbumFailed" class="py-9 text-center text-[13px] text-inkFaint">档案库没应声，稍后再翻</div>
            <template v-else>
              <div class="mt-4 grid grid-cols-3 gap-3">
                <!-- 头一格永远是服务器现行脸：点它 = 收回钦选、回到跟服务器 -->
                <button class="group block min-w-0 text-left" @click="pickFace(null)">
                  <img
                    :src="up?.face || defaultAvatar"
                    :alt="`${name} 服务器现行脸`"
                    referrerpolicy="no-referrer"
                    class="aspect-square w-full rounded-xl border-2 object-cover transition"
                    :class="!chosenFace(mid) ? 'border-stationPink' : 'border-transparent group-hover:border-stationPink/50'"
                  />
                  <span class="mt-1 block truncate text-[10.5px] text-inkFaint">服务器现行</span>
                </button>
                <button v-for="f in faceAlbum" :key="f.faceRel" class="group block min-w-0 text-left" @click="pickFace(f.faceRel)">
                  <img
                    :src="f.faceRel"
                    :alt="`${name} 档案脸`"
                    referrerpolicy="no-referrer"
                    loading="lazy"
                    class="aspect-square w-full rounded-xl border-2 object-cover transition"
                    :class="chosenFace(mid) === f.faceRel ? 'border-stationPink' : 'border-transparent group-hover:border-stationPink/50'"
                  />
                  <span class="mt-1 block truncate text-[10.5px] text-inkFaint">约 {{ String(f.firstSeen || '').slice(0, 10) }} 相识</span>
                </button>
              </div>
              <p v-if="!faceAlbum.length" class="mt-3 text-center text-[11.5px] text-inkFaint">档案库还空着——巡捕每周巡几轮，见过的脸就会记进来</p>
            </template>
          </div>
        </div>

      <p v-if="spaceToast" class="fixed bottom-6 left-1/2 z-50 -translate-x-1/2 rounded-full bg-inkMain/85 px-4 py-2 text-xs text-white shadow-lg">{{ spaceToast }}</p>
      </template>
    </main>
  </div>
</template>

<style scoped>
/* 页头带下缘溶进名片白底：与首页大幕 .hero-frame（BangumiHome）同一家族的手法——
   mask 从下半程逐步透到全透，比硬切边柔和得多。条纹/暗层都在被 mask 的容器里，
   一起淡出不会各自截断；头像区自带提层不受影响。
   二轮眼验（11-06）：单靠 mask 只是 alpha 衰减，网点和水印的底缘会露出「正在消融」的脏边，
   所以另铺一条 .head-soft 磨砂带把真模糊叠在接缝上，mask 只负责颜色收尾。 */
.head-fade {
  -webkit-mask-image: linear-gradient(to bottom, #000 0%, #000 52%, rgb(0 0 0 / 0.78) 66%, rgb(0 0 0 / 0.55) 77%, rgb(0 0 0 / 0.33) 86%, rgb(0 0 0 / 0.14) 93%, transparent 100%);
  mask-image: linear-gradient(to bottom, #000 0%, #000 52%, rgb(0 0 0 / 0.78) 66%, rgb(0 0 0 / 0.55) 77%, rgb(0 0 0 / 0.33) 86%, rgb(0 0 0 / 0.14) 93%, transparent 100%);
}

/* 模糊过渡带：大封面模式（HomePage .hero-soft）的跨缝版——同一条 11px 雾 + 1.12 饱和，
   只是这里雾的不是图而是页头带自己：backdrop-filter 就地取底下已画的渐变/网点雾化重绘，
   零复制零双影。左右各出 4% 防雾在裁切边变暗；上缘压在页头带 56% 高处渐进，下缘探进名片区
   四成多处渐出，头像（z-10）骑在雾上不受扰。不支持 backdrop-filter 的浏览器退化回纯 mask 淡出。 */
.head-soft {
  position: absolute;
  top: 56%;
  left: -4%;
  right: -4%;
  height: 88%;
  pointer-events: none;
  -webkit-backdrop-filter: blur(11px) saturate(1.12);
  backdrop-filter: blur(11px) saturate(1.12);
  -webkit-mask-image: linear-gradient(to bottom, transparent 0%, #000 22%, #000 58%, transparent 100%);
  mask-image: linear-gradient(to bottom, transparent 0%, #000 22%, #000 58%, transparent 100%);
}
</style>
