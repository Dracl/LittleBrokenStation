<script setup>
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { colonTime, publishedDate } from '../utils/time'
import { coverSrcOf } from '../utils/coverSrc'
import { createPreviewHost } from '../utils/hoverPreview'
import { createCoverLedger } from '../utils/coverStates'
import UpIcon from './UpIcon.vue'
import PlayCountIcon from './PlayCountIcon.vue'
import DanmakuCountIcon from './DanmakuCountIcon.vue'

// 纯展示视频卡：版式对齐 B 站首页卡（2026-10 基准）。
// 封面 16:10 左下「官方播放量图标 + 数 / 官方弹幕图标 + 数」（PlayCountIcon / DanmakuCountIcon，B 站首页同一枚）无底描边字，右下时长大号描边字；标题中黑两行；
// meta 行 UP 徽章 + 相对时间。可点结构在卡自己手里（站长 11-02 在调参台上选的 coverTitleText）：
// 封面一个 RouterLink + 标题文字内联一个 RouterLink，读数行与标题方框里的空白是死区；
// 封面放大与悬停预览都只跟「鼠标在封面上」走，不再吃调用方的 .group 容器；
// keyword 传入时标题与 UP 主高亮命中词；admin 打开状态角标/圆点与失败原因（站长视角）。
const props = defineProps({
  video: { type: Object, required: true },
  keyword: { type: String, default: '' },
  admin: { type: Boolean, default: false },
  // 调用方借一只手按住封面放大：管理员钮浮在封面上，鼠标从封面移到钮上时封面会收到一次 mouseleave，
  // 不按住的话指针明明还在封面里、图却缩回去了（站长 11-02 收封面时碰到的）。
  coverHold: { type: Boolean, default: false },
})

// 封面 hover 状态透出给调用方（站长 11-02 把管理员钮收进封面）：AdminVideoCell 那四枚钮住在卡外面、
// absolute 贴在封面顶行，它们需要知道「鼠标此刻在不在封面上」才决定浮不浮出。
const emit = defineEmits(['cover-hover'])

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
// 命中区收口后外层不再有整卡链接，UP 行仍是 div + click 而不是 <a>：要给的是「整条行都可点」，
// 内联 <a> 只能覆盖文字（标题就是这么做的），整行靠 click 更顺手。代价是键盘 Tab 不到这一行，要补再说。
// 没带 mid 的行（老数据或没归档过档案）维持纯文本，不给死链。
const router = useRouter()
const canOpenUp = computed(() => props.video.upMid != null)

// 封面自己管 hover（站长 11-02 命中区收口）：放大与悬停预览都只认「鼠标在封面上」，
// 停在标题、UP 行、读数行都不再牵动封面。上一版靠调用方的 .group + rowHover 反推，
// 整卡一个 group 分不出鼠标落在哪一块，动画于是总在冒充归属（他那两句判对了）。
const coverHover = ref(false)
function onCoverEnter() {
  coverHover.value = true
  emit('cover-hover', true)
  onPreviewEnter()
}
function onCoverLeave() {
  coverHover.value = false
  emit('cover-hover', false)
  onPreviewLeave()
}

// 标题那抹粉只跟标题自己走：group-hover 会把停在封面、读数行、方框空白也算成「停在标题上」，
// 上一版只在鼠标进 UP 行时抑制、读数行那一排依旧漏（站长 11-02 红点第二发）；现在绑到标题自己的 mouseenter。
// 封面放大另由 coverHover 管，两者各管各的：鼠标停在标题上时封面不动。
const titleHover = ref(false)
function onUpNameClick(e) {
  if (!canOpenUp.value) return
  // prevent/stop 属防御性：卡外层已不是整块链接（命中区收口），留着防将来外层再变链接时两发互抢。
  e.preventDefault()
  e.stopPropagation()
  router.push(`/up/${props.video.upMid}`)
}

// 整条 UP 行（徽章 · 名字 · 时间）都是 UP 空间的入口：站长 11-02 说名字只剩几个字宽，点偏一点够不着。
// 命中区就取这一行自己的盒子（含上借 3px、下借 4px 的透明垫子），不往标题伸。
function onUpRowClick(e) {
  if (!canOpenUp.value) return
  e.preventDefault()
  e.stopPropagation()
  router.push(`/up/${props.video.upMid}`)
}

// 封面取图走全站唯一出口 utils/coverSrc：服务端缩略档位 → 本地原图 → 热链原样直出（B 站缩放参数已全站停用）；
// 257 是卡面 CSS 显示宽，内部按 DPR 2 折算挑档；一条封面都没有给空串走 BV 占位。
const coverSrc = computed(() => coverSrcOf(props.video, 257))

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
// 账本走 utils/coverStates（与大屏幕模式分区行同一份）：本卡一卡一实例，只记自己那一个键；
// 微光帘那条 CSS 也已从本文件的 scoped 里抬到 style.css，两副卡面共用一条。
const coverEl = ref(null)
const covers = createCoverLedger()
const cardKey = computed(() => props.video.bvid || String(props.video.id ?? ''))
const coverReady = computed(() => covers.isReady(cardKey.value))
onMounted(() => {
  covers.probe(cardKey.value, coverEl.value)
})

// ─── 悬停预览：后端备好了约 1MB 的 360p 无声片段（video.previewUrl）才启用这一层 ───
// 封面 img 全程不卸：预览没到位/加载失败时看到的就是封面，所以这层只有淡入，没有任何报错 UI。
// 起播延时（350ms）、续播窗口（200ms）、单路令牌、出错放弃、减少动效这些口径全住在 utils/hoverPreview 的机位里——
// 大屏幕模式的分区行卡（BangumiHome）用的是同一个机位，两副卡面对不起手感的缝就从这里焊上（站长 11-02 点名要对齐）。
const previewSrc = computed(() => props.video.previewUrl || '')
const previewEl = ref(null)
const preview = createPreviewHost()
const previewOn = computed(() => preview.isOn(cardKey.value))
const previewMount = computed(() => !!previewSrc.value && !preview.isGaveUp(cardKey.value))

function onPreviewEnter() {
  preview.enter(cardKey.value, previewSrc.value, () => previewEl.value)
}

function onPreviewLeave() {
  preview.leave(cardKey.value)
}

onBeforeUnmount(() => preview.dispose())
</script>

<template>
  <div class="cv-auto">
    <RouterLink
      :to="`/watch/${video.bvid}`"
      class="relative block aspect-[16/10] w-full overflow-hidden rounded-xl bg-[#f1f2f3] dark:bg-[#23262e]"
      @mouseenter="onCoverEnter"
      @mouseleave="onCoverLeave"
    >
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
        :class="[(coverHover || coverHold) ? 'scale-[1.03]' : '', coverReady ? 'opacity-100' : 'opacity-0']"
        @load="covers.markReady(cardKey)"
        @error="covers.markReady(cardKey)"
      />
      <div v-else class="flex h-full w-full items-center justify-center">
        <span class="text-xs tracking-widest text-inkFaint">{{ video.bvid }}</span>
      </div>
      <!-- 悬停预览层：压在封面之上、数据区之下（DOM 顺序即层次），不吃点击也不抢焦点；
           :key 绑源址，卡位复用时不会拿上一条的流接着播。preload=none——不悬停就一个字节都不拉。 -->
      <video
        v-if="previewMount"
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
        :class="previewOn ? 'opacity-100' : 'opacity-0'"
        @loadeddata="preview.markShown(cardKey)"
        @playing="preview.markShown(cardKey)"
        @error="preview.fail(cardKey)"
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
    </RouterLink>
    <!-- 标题区 42px：方框尺寸照旧（调参台上站长定的热区 48 已盖满，行高与 min-h 都不动），
         但可点只给字面 —— 外层 p 管两行截断与 tooltip，真正可点的是内联那枚 RouterLink，
         所以第二行文字之后的空白不响应；染粉与手型同一个元素、同一块区域。 -->
    <p v-if="keyword" class="mt-2 line-clamp-2 min-h-[42px] text-[15px] font-medium leading-[22px] text-inkMain" :title="video.title || video.bvid"><RouterLink :to="`/watch/${video.bvid}`" class="transition" :class="titleHover ? 'text-stationPink' : ''" @mouseenter="titleHover = true" @mouseleave="titleHover = false" v-html="titleHtml"></RouterLink></p>
    <p v-else class="mt-2 line-clamp-2 min-h-[42px] text-[15px] font-medium leading-[22px] text-inkMain" :title="video.title || video.bvid"><RouterLink :to="`/watch/${video.bvid}`" class="transition" :class="titleHover ? 'text-stationPink' : ''" @mouseenter="titleHover = true" @mouseleave="titleHover = false">{{ video.title || video.bvid }}</RouterLink></p>
    <div
      class="group/up relative mt-1 flex items-center gap-1 rounded-sm text-[13px] leading-[20px] text-inkFaint focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-stationBlue/60"
      :class="canOpenUp ? 'cursor-pointer' : ''"
      :role="canOpenUp ? 'link' : undefined"
      :tabindex="canOpenUp ? 0 : undefined"
      :aria-label="canOpenUp ? `进 ${video.upName} 的分站空间` : undefined"
      @click="onUpRowClick"
      @keydown.enter.prevent="onUpRowClick"
    >
      <!-- 命中垫子：行自己往上借 3px、往下借 4px（站长 11-02 在调参台上定的两个数）。垫子透明，不动排版。 -->
      <span v-if="canOpenUp" class="absolute -top-[3px] -bottom-[4px] inset-x-0" aria-hidden="true"></span>
      <span v-if="admin && video.status !== 2" class="h-1.5 w-1.5 shrink-0 rounded-full" :class="statusMeta(video.status).dot"></span>
      <template v-if="video.upName">
        <!-- UP 徽章：官方描边款（UpIcon 里那三段 path 直接刮自 B 站首页卡）。本行的数全站只此一处例外（站长 11-02 在网格微调台上定：
             字号 13 / 行高 20 / 图标 18 / 下移 0.5 / 图标与名字净距 3 / 子项间距 4 / 名字最宽 60%）：
             尺寸走 .up-ic 的变量，本处用 [--up-ic-size:18px] 顶到 18px（字比另三处大一号，17px 在这儿显轻）；
             子项间距由 gap-1 出 4px，图标与名字要的净距 3px 用 -mr-[1px] 抵出来；下移 0.5px 住在 .up-ic 里。
             其余三处（大幕胶囊、分区行卡、接下来播放）行内无 gap，拿默认 17px 各加 mr-[3px]。
             颜色仍单钉 text-inkFaint：本行字色现在也是 inkFaint（四处同一调，不再特立 inkSub），
             但徒标是 currentColor，钉一下就不怕以后有人改行色把这枚单独带跑。 -->
        <UpIcon class="up-ic [--up-ic-size:18px] -mr-[1px] text-inkFaint" />
        <span
          v-if="keyword"
          class="max-w-[60%] truncate transition"
          :class="canOpenUp ? 'cursor-pointer hover:text-stationBlue group-hover/up:text-stationBlue' : 'hover:text-stationPink'"
          :title="canOpenUp ? '这位在分站的空间' : undefined"
          v-html="upHtml"
          @click="onUpNameClick"
        ></span>
        <span
          v-else
          class="max-w-[60%] truncate transition"
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
</style>
