<script setup>
import { computed, onMounted, ref } from 'vue'
import { colonTime } from '../utils/time'
import { coverThumb } from '../utils/cover'

// 纯展示视频卡：版式对齐 B 站首页卡（2026-10 基准）。
// 封面 16:10 左下「▶播放 💬弹幕」无底描边字，右下时长大号描边字；标题中黑两行；
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

// 封面取图：本地原图优先（后端已拼好 /media/…），没归档的才吃热链 CDN 缩图；两边都没有给空串走 BV 占位。
const coverSrc = computed(() => props.video.localCover || (props.video.coverUrl ? coverThumb(props.video.coverUrl, 480) : ''))

// 弹幕条数按 B 站口径：过万折成「1.2万」，整万去小数尾
function fmtCount(n) {
  if (n == null) return null
  if (n < 10000) return String(n)
  return (n / 10000).toFixed(1).replace(/\.0$/, '') + '万'
}

// B 站口径的相对时间：刚刚 / N分钟前 / N小时前 / N天前，满月之后给日期。
// 展示的是真正的 B 站投稿时间：首取导入源自带的 publishedAtUtc，其次到站归档的 publishText，
// 两个都没就整段不显（入站时间是站长台账的概念，不上首页）。
function publishedDate(v) {
  if (v.publishedAtUtc) {
    const d = new Date(v.publishedAtUtc)
    if (!Number.isNaN(d.getTime())) return d
  }
  if (v.publishText) {
    const d = new Date(String(v.publishText).replace(' ', 'T'))
    if (!Number.isNaN(d.getTime())) return d
  }
  return null
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
</script>

<template>
  <div class="cv-auto">
    <div class="relative aspect-[16/10] w-full overflow-hidden rounded-xl bg-[#f1f2f3]">
      <div v-if="coverSrc && !coverReady" class="cover-shimmer absolute inset-0" aria-hidden="true"></div>
      <img
        v-if="coverSrc"
        ref="coverEl"
        :src="coverSrc"
        :alt="video.title || video.bvid"
        loading="lazy"
        decoding="async"
        referrerpolicy="no-referrer"
        class="h-full w-full object-cover transition duration-300 group-hover:scale-[1.03]"
        :class="coverReady ? 'opacity-100' : 'opacity-0'"
        @load="coverReady = true"
        @error="coverReady = true"
      />
      <div v-else class="flex h-full w-full items-center justify-center">
        <span class="text-xs tracking-widest text-inkFaint">{{ video.bvid }}</span>
      </div>
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
            <svg viewBox="0 0 24 24" class="h-3.5 w-3.5" fill="currentColor">
              <path d="M8 5v14l11-7z" />
            </svg>
            {{ video.viewCountText ?? '—' }}
          </span>
          <span v-if="video.danmakuCount != null" class="flex items-center gap-1">
            <svg viewBox="0 0 24 24" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="1.8">
              <rect x="2.5" y="5.5" width="19" height="13" rx="3" />
              <path d="M6.5 10h5M6.5 13.5h8" stroke-linecap="round" />
            </svg>
            {{ fmtCount(video.danmakuCount) }}
          </span>
        </span>
        <span v-if="video.durationText" class="text-[13px] font-medium text-white drop-shadow-[0_1px_2px_rgba(0,0,0,0.85)]">{{ colonTime(video.durationText) }}</span>
      </div>
    </div>
    <p v-if="keyword" class="mt-2 line-clamp-2 min-h-[44px] text-[15px] font-medium leading-[22px] text-inkMain transition group-hover:text-stationPink" :title="video.title || video.bvid" v-html="titleHtml"></p>
    <p v-else class="mt-2 line-clamp-2 min-h-[44px] text-[15px] font-medium leading-[22px] text-inkMain transition group-hover:text-stationPink" :title="video.title || video.bvid">{{ video.title || video.bvid }}</p>
    <div class="mt-1 flex items-center gap-1.5 text-[13px] text-inkSub">
      <span v-if="admin && video.status !== 2" class="h-1.5 w-1.5 shrink-0 rounded-full" :class="statusMeta(video.status).dot"></span>
      <template v-if="video.upName">
        <!-- UP 徽章：手绘圆角框 + UP 字，走 currentColor 跟着灰度 -->
        <svg viewBox="0 0 18 18" class="h-3.5 w-3.5 shrink-0 text-inkSub" aria-hidden="true">
          <rect x="0.9" y="0.9" width="16.2" height="16.2" rx="4.5" fill="none" stroke="currentColor" stroke-width="1.4" />
          <text x="9" y="12.6" text-anchor="middle" font-size="8" font-weight="700" font-family="Arial, sans-serif" fill="currentColor">UP</text>
        </svg>
        <span v-if="keyword" class="max-w-[55%] truncate transition hover:text-stationPink" v-html="upHtml"></span>
        <span v-else class="max-w-[55%] truncate transition hover:text-stationPink">{{ video.upName }}</span>
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
.cover-shimmer::after {
  content: '';
  position: absolute;
  inset: 0;
  transform: translateX(-100%);
  background: linear-gradient(100deg, transparent 25%, rgba(0, 0, 0, 0.05) 50%, transparent 75%);
  animation: cover-shimmer 1.2s ease infinite;
}
@keyframes cover-shimmer {
  to {
    transform: translateX(100%);
  }
}
</style>
