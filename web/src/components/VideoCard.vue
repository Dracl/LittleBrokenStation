<script setup>
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { colonTime, publishedDate } from '../utils/time'
import { coverThumb } from '../utils/cover'
import { claimPreview, previewHoverEnter, previewHoverLeave, releasePreview } from '../utils/previewHover'
import UpIcon from './UpIcon.vue'
import PlayCountIcon from './PlayCountIcon.vue'
import DanmakuCountIcon from './DanmakuCountIcon.vue'

// 纯展示视频卡：版式对齐 B 站首页卡（2026-10 基准）。
// 封面 16:10 左下「官方播放量图标 + 数 / 官方弹幕图标 + 数」（PlayCountIcon / DanmakuCountIcon，B 站首页同一枚）无底描边字，右下时长大号描边字；标题中黑两行；
// meta 行 UP 徽章 + 相对时间。hover 缩放交给调用方的 .group 容器；
// keyword 传入时标题与 UP 主高亮命中词；admin 打开状态角标/圆点与失败原因（站长视角）。
const props = defineProps({
  video: { type: Object, required: true },
  keyword: { type: String, default: '' },
  admin: { type: Boolean, default: false },
})

function escapeHtml(s) {
  return s.replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c])
}

// 高亮流程：先整体转义，再把每个搜索词（同样转义后）正则转义替换成 <mark>，注入无从谈起。
// 无关键词时调用方走纯文本渲染，不走 v-html——首页两百多张卡每轮轮询都 innerHTML 一遍是实打实的卡顿源。
function highlight(text) {
  if (!text) return ''
  const safe = escapeHtml(text)
  const words = props.keyword
    .split(/\s+/)
    .map((w) => escapeHtml(w).replace(/[.*+?^${}()|[\]\\]/g, '\\$&'))
    .filter(Boolean)
  if (!words.length) return safe
  return safe.replace(new RegExp(`(${words.join('|')})`, 'gi'), '<mark class="rounded-sm bg-stationPink/25 px-0.5 text-stationPink">$1</mark>')
}

const titleHtml = computed(() => highlight(props.video.title || props.video.bvid))
const upHtml = computed(() => (props.video.upName ? highlight(props.video.upName) : ''))

// 卡面上的 UP 名 → 分站里的 UP 空间（站长 11-02 点名：点一下就进）。
// 整张卡外层是 RouterLink，这里不能再塞一个 <a>（a 套 a 不合法），用 span 自己抢下这一发。
// 没带 mid 的行（老数据或没归档过档案）维持纯文本，不给死链。
const router = useRouter()
const canOpenUp = computed(() => props.video.upMid != null)

// 鼠标停在 UP 行上时，不染标题也不放大封面。整卡是一个 group，CSS 的 group-hover 分不出鼠标落在卡里哪一块，
// 只能自己记一下：站长 11-02 看到的一直是「动画说这块归卡片」，跟触发区归属无关（他这句判对了）。
const rowHover = ref(false)
function onUpNameClick(e) {
  if (!canOpenUp.value) return
  e.preventDefault() // 抢在「整张卡跳放映厅」之前
  e.stopPropagation()
  router.push(`/up/${props.video.upMid}`)
}

// 整行（徒章 · 名字 · 时间）都是 UP 空间的入口：站长 11-02 说名字只剩几个字宽，点偏一点就被卡接走。
// 命中区就取这一行自己的盒子，上下都不往外借——上一版往上垫了 7px，反而压到标题的空盒，被他退回来了。
function onUpRowClick(e) {
  if (!canOpenUp.value) return
  e.preventDefault()
  e.stopPropagation()
  router.push(`/up/${props.video.upMid}`)
}

// 封面取图：本地原图优先（后端已拼好 /media/…），没归档的才吃热链 CDN 缩图；两边都没有给空串走 BV 占位。
const coverSrc = computed(() => props.video.localCover || (props.video.coverUrl ? coverThumb(props.video.coverUrl, 480) : ''))

// 弹幕条数按 B 站口径：过万折成「1.2万」，整万去小数尾
function fmtCount(n) {
  if (n == null) return null
  if (n < 10000) return String(n)
  return (n / 10000).toFixed(1).replace(/\.0$/, '') + '万'
}

// B 站口径的相对时间：刚刚 / N分钟前 / N小时前 / N天前，满月之后给日期。
// 取数走 utils/time.js 的 publishedDate（与「接下来播放」卡共用一份口径）：
// 两个源都没就给空，整段不显（入站时间是站长台账的概念，不上首页）。

function relTime(d) {
  if (!d) return ''
  const diff = Date.now() - d.getTime()
  if (diff < 60e3) return '刚刚'
  if (diff < 3600e3) return `${Math.floor(diff / 60e3)}分钟前`
  if (diff < 86400e3) return `${Math.floor(diff / 3600e3)}小时前`
  if (diff < 30 * 86400e3) return `${Math.floor(diff / 86400e3)}天前`
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

// 时长冒号化统一用 utils/time.js 的 colonTime（与接下来播放卡共用）。

const STATUS_META = {
  0: { text: '排队待进站', dot: 'bg-inkFaint', tone: 'text-inkSub' },
  1: { text: '进站中', dot: 'bg-stationPink animate-pulse', tone: 'text-stationPink' },
  2: { text: '已到站', dot: 'bg-emerald-400', tone: 'text-emerald-300' },
  3: { text: '进站失败', dot: 'bg-rose-400', tone: 'text-rose-300' },
  4: { text: '无需进站', dot: 'bg-amber-300', tone: 'text-amber-200' },
  5: { text: '已取消', dot: 'bg-inkFaint', tone: 'text-inkFaint' },
}

function statusMeta(status) {
  return STATUS_META[status] ?? STATUS_META[0]
}

// 封面加载态：远程图到位前刷微光帘，到位后淡入，不闪白不裸切；
// 图命中缓存时 load 可能不补发，挂载后补探一次 complete。
const coverEl = ref(null)
const coverReady = ref(false)
onMounted(() => {
  if (coverEl.value?.complete) coverReady.value = true
})

// ─── 悬停预览：后端备好了约 1MB 的 360p 无声片段（video.previewUrl）才启用这一层 ───
// 封面 img 全程不卸：预览没到位/加载失败时看到的就是封面，所以这层只有淡入，没有任何报错 UI。
// 计时器照播放器音量条那套（volArmHide + volHideTimer）：CSS :hover 与「立即归零」都经不起鼠标在卡边上抖两下。
const PREVIEW_ARM_MS = 350 // 鼠标停够 350ms 才起播，路过卡阵的手指不算想看
const PREVIEW_RESET_MS = 200 // 离场 200ms 内回来就原地续播，不重新从 0 拉一遍流

// 系统要求减少动效：尊重到底，悬停一律不起播，卡面停在封面原样。
const reduceMotion = !!window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches

const previewSrc = computed(() => props.video.previewUrl || '')
const previewEl = ref(null)
const previewShown = ref(false) // 首帧到位才淡入，避免黑帧裸切
const previewGaveUp = ref(false) // 本卡彻底放弃：出错后连这层 <video> 都不再挂，绝不重试
let previewArm = null
let previewReset = null
let hoverHeld = false

function previewClearTimers() {
  if (previewArm) {
    clearTimeout(previewArm)
    previewArm = null
  }
  if (previewReset) {
    clearTimeout(previewReset)
    previewReset = null
  }
}

// 停车动作，同时充当全站单例的播放令牌：函数身份每张卡固定一个。
function stopPreview(resetTime) {
  const el = previewEl.value
  if (!el) return
  try {
    el.pause()
  } catch {
    /* 本来就没在播 */
  }
  if (resetTime) {
    try {
      el.currentTime = 0
    } catch {
      /* 流还没 ready，归零没意义 */
    }
  }
  previewShown.value = false
}

const stopForSingleton = () => stopPreview(true)

function giveUpPreview() {
  previewClearTimers()
  releasePreview(stopForSingleton)
  stopPreview(false)
  previewGaveUp.value = true
}

function onPreviewEnter() {
  if (previewSrc.value && !hoverHeld) {
    hoverHeld = true
    previewHoverEnter()
  }
  if (!previewSrc.value || previewGaveUp.value || reduceMotion) return
  previewClearTimers()
  previewArm = setTimeout(() => {
    previewArm = null
    const el = previewEl.value
    if (!el) return
    claimPreview(stopForSingleton) // 全站只许一路在播：先让上一路停车归零
    el.muted = true // 属性之外再钉一次，免得 autoplay policy 卡住无声起播
    const p = el.play()
    if (p && typeof p.catch === 'function') p.catch(giveUpPreview)
  }, PREVIEW_ARM_MS)
}

function onPreviewLeave() {
  if (hoverHeld) {
    hoverHeld = false
    previewHoverLeave()
  }
  if (!previewSrc.value || previewGaveUp.value) return
  if (previewArm) {
    clearTimeout(previewArm)
    previewArm = null
  }
  releasePreview(stopForSingleton)
  stopPreview(false) // 立即按停（淡回封面）
  if (previewReset) clearTimeout(previewReset)
  previewReset = setTimeout(() => {
    previewReset = null
    const el = previewEl.value
    if (el && el.currentTime) el.currentTime = 0
  }, PREVIEW_RESET_MS)
}

onBeforeUnmount(() => {
  previewClearTimers()
  releasePreview(stopForSingleton)
  if (hoverHeld) {
    hoverHeld = false
    previewHoverLeave()
  }
})
</script>

<template>
  <div class="cv-auto" @mouseenter="onPreviewEnter" @mouseleave="onPreviewLeave">
    <div class="relative aspect-[16/10] w-full overflow-hidden rounded-xl bg-[#f1f2f3] dark:bg-[#23262e]">
      <div v-if="coverSrc && !coverReady" class="cover-shimmer absolute inset-0" aria-hidden="true"></div>
      <img
        v-if="coverSrc"
        ref="coverEl"
        :src="coverSrc"
        :alt="video.title || video.bvid"
        loading="lazy"
        decoding="async"
        referrerpolicy="no-referrer"
        class="h-full w-full object-cover transition duration-300"
        :class="[rowHover ? '' : 'group-hover:scale-[1.03]', coverReady ? 'opacity-100' : 'opacity-0']"
        @load="coverReady = true"
        @error="coverReady = true"
      />
      <div v-else class="flex h-full w-full items-center justify-center">
        <span class="text-xs tracking-widest text-inkFaint">{{ video.bvid }}</span>
      </div>
      <!-- 悬停预览层：压在封面之上、数据区之下（DOM 顺序即层次），不吃点击也不抢焦点；
           :key 绑源址，卡位复用时不会拿上一条的流接着播。preload=none——不悬停就一个字节都不拉。 -->
      <video
        v-if="previewSrc && !previewGaveUp"
        :key="previewSrc"
        ref="previewEl"
        :src="previewSrc"
        muted
        loop
        playsinline
        preload="none"
        disablepictureinpicture
        tabindex="-1"
        aria-hidden="true"
        class="pointer-events-none absolute inset-0 h-full w-full object-cover transition-opacity duration-200"
        :class="previewShown ? 'opacity-100' : 'opacity-0'"
        @loadeddata="previewShown = true"
        @playing="previewShown = true"
        @error="giveUpPreview"
      ></video>
      <span
        v-if="admin && video.status !== 2"
        class="absolute left-2 top-2 rounded-md bg-black/75 px-2 py-0.5 text-[11px]"
        :class="statusMeta(video.status).tone"
      >
        {{ statusMeta(video.status).text }}
      </span>
      <!-- 封面底部半透渐变带：B 站同款，给数据区垫底，不吃点击 -->
      <div class="pointer-events-none absolute inset-x-0 bottom-0 h-12 bg-gradient-to-t from-black/55 via-black/25 to-transparent"></div>
      <!-- 封面底部数据区：B 站口径，无底色纯描边字；弹幕数没档案时整段不显 -->
      <div class="absolute inset-x-0 bottom-0 flex items-center justify-between px-2 pb-1.5">
        <span class="flex items-center gap-2 text-[12px] text-white drop-shadow-[0_1px_2px_rgba(0,0,0,0.85)]">
          <span class="flex items-center gap-1">
            <PlayCountIcon class="h-[18px] w-[18px]" />
            {{ video.viewCountText ?? '—' }}
          </span>
          <span v-if="video.danmakuCount != null" class="flex items-center gap-1">
            <DanmakuCountIcon class="h-[18px] w-[18px]" />
            {{ fmtCount(video.danmakuCount) }}
          </span>
        </span>
        <span v-if="video.durationText" class="text-[13px] font-medium text-white drop-shadow-[0_1px_2px_rgba(0,0,0,0.85)]">{{ colonTime(video.durationText) }}</span>
      </div>
    </div>
    <!-- 标题区 42px（调参台再一轮，站长 11-02）：热区 48 已盖满整块，所以整块都归卡片的触发区，点了进放映厅，
         不再垫透明热区；跟卡片分家的只有下面那行 UP：它自己可点，上借 3px、下借 4px。 -->
    <p v-if="keyword" class="mt-2 line-clamp-2 min-h-[42px] text-[15px] font-medium leading-[22px] text-inkMain transition" :class="rowHover ? '' : 'group-hover:text-stationPink'" :title="video.title || video.bvid" v-html="titleHtml"></p>
    <p v-else class="mt-2 line-clamp-2 min-h-[42px] text-[15px] font-medium leading-[22px] text-inkMain transition" :class="rowHover ? '' : 'group-hover:text-stationPink'" :title="video.title || video.bvid">{{ video.title || video.bvid }}</p>
    <div
      class="group/up relative mt-1 flex items-center gap-1.5 text-[13px] text-inkSub"
      :class="canOpenUp ? 'cursor-pointer' : ''"
      @mouseenter="rowHover = true"
      @mouseleave="rowHover = false"
      @click="onUpRowClick"
    >
      <!-- 命中垫子：行自己往上借 3px、往下借 4px（站长 11-02 在调参台上定的两个数）。垫子透明，不动排版。 -->
      <span v-if="canOpenUp" class="absolute -top-[3px] -bottom-[4px] inset-x-0" aria-hidden="true"></span>
      <span v-if="admin && video.status !== 2" class="h-1.5 w-1.5 shrink-0 rounded-full" :class="statusMeta(video.status).dot"></span>
      <template v-if="video.upName">
        <!-- UP 徽章：官方描边款（UpIcon 里那三段 path 直接刮自 B 站首页卡），尺寸照官方实测：13px 的 UP 名旁画 17×17。 -->
        <UpIcon class="h-[17px] w-[17px] shrink-0 text-inkSub" />
        <span
          v-if="keyword"
          class="max-w-[55%] truncate transition"
          :class="canOpenUp ? 'cursor-pointer hover:text-stationBlue group-hover/up:text-stationBlue' : 'hover:text-stationPink'"
          :title="canOpenUp ? '这位在分站的空间' : undefined"
          v-html="upHtml"
          @click="onUpNameClick"
        ></span>
        <span
          v-else
          class="max-w-[55%] truncate transition"
          :class="canOpenUp ? 'cursor-pointer hover:text-stationBlue group-hover/up:text-stationBlue' : 'hover:text-stationPink'"
          :title="canOpenUp ? '这位在分站的空间' : undefined"
          @click="onUpNameClick"
        >{{ video.upName }}</span>
        <span class="text-inkFaint">·</span>
      </template>
      <span>{{ relTime(publishedDate(video)) }}</span>
      <span v-if="admin && video.lastError" class="truncate text-rose-400" :title="video.lastError">{{ video.lastError }}</span>
    </div>
  </div>
</template>

<style scoped>
/* 离屏卡片跳过渲染与布局：两百多张卡的主线程大敌，浏览器自己按视口取惹 */
.cv-auto {
  content-visibility: auto;
  contain-intrinsic-size: auto 330px;
}
/* 封面微光帘：图未到位时扫一道高光。用 transform 而非 background-position，
   动画交给合成层，四十张同时闪也不吃主线程绘制。 */
.cover-shimmer {
  overflow: hidden;
  background: #f1f2f3;
}
html.dark .cover-shimmer {
  background: #23262e;
}
.cover-shimmer::after {
  content: '';
  position: absolute;
  inset: 0;
  transform: translateX(-100%);
  background: linear-gradient(100deg, transparent 25%, rgba(0, 0, 0, 0.05) 50%, transparent 75%);
  animation: cover-shimmer 1.2s ease infinite;
}
html.dark .cover-shimmer::after {
  background: linear-gradient(100deg, transparent 25%, rgba(255, 255, 255, 0.06) 50%, transparent 75%);
}
@keyframes cover-shimmer {
  to {
    transform: translateX(100%);
  }
}
</style>
