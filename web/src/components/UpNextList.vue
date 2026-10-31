<script setup>
import { useRouter } from 'vue-router'
import { colonTime, publishedDate } from '../utils/time'

// 接下来播放：分站自己的推荐流（同 UP 主优先 + 层内随机抽），卡形仿 B 站「封面左 + 信息右」。
const props = defineProps({
  items: { type: Array, default: () => [] },
  autoplay: { type: Boolean, default: false },
})
const emit = defineEmits(['update:autoplay'])

const router = useRouter()

// 显的是 B 站投稿时间，取数口径与首页卡阵共用（utils/time.js 的 publishedDate）：
// createdAt 是入站时间，批量下载后满屏同一天，那是台账概念，不该顶替投稿时间上桌；
// 两个源都拿不到就整段不显（连「· 」分隔符一起掉，不留个孤零零的点）。
function pubDate(v) {
  const d = publishedDate(v)
  return d ? d.toLocaleDateString('zh-CN', { year: 'numeric', month: '2-digit', day: '2-digit' }) : ''
}

function go(bvid) {
  if (bvid) router.push(`/watch/${bvid}`)
}
</script>

<template>
  <div v-if="items.length" class="rounded-xl bg-cardBg p-3">
    <div class="mb-2 flex items-center justify-between px-1">
      <p class="text-sm font-medium text-inkMain">接下来播放</p>
      <button
        class="flex items-center gap-1.5 text-xs text-inkFaint transition hover:text-inkMain"
        role="switch"
        :aria-checked="autoplay"
        :title="autoplay ? '播完自动接接下来播放第一条' : '已关：播完停在本页'"
        @click="emit('update:autoplay', !autoplay)"
      >
        自动连播
        <span
          class="relative inline-block h-[18px] w-[34px] rounded-full transition"
          :class="autoplay ? 'bg-stationPink' : 'bg-[#dcdee0] dark:bg-white/10'"
        >
          <span
            class="absolute top-[2px] h-[14px] w-[14px] rounded-full bg-white shadow transition-all"
            :style="{ left: autoplay ? '18px' : '2px' }"
          ></span>
        </span>
      </button>
    </div>
    <ul class="space-y-3">
      <li v-for="v in items" :key="v.id">
        <button class="group flex w-full text-left" @click="go(v.bvid)">
          <span class="relative block h-[80px] w-[141px] shrink-0 overflow-hidden rounded-[6px] bg-[#c9ccd0] dark:bg-[#23262e]">
            <img
              v-if="v.coverUrl"
              :src="v.coverUrl"
              :alt="v.title ?? ''"
              loading="lazy"
              referrerpolicy="no-referrer"
              class="h-full w-full object-cover"
            />
            <span class="pointer-events-none absolute inset-0 bg-black/[.07]"></span>
            <span
              v-if="v.durationText"
              class="absolute bottom-[6px] right-[6px] rounded-[2px] bg-black/40 px-1 text-[13px] leading-[20px] text-white tabular-nums"
            >{{ colonTime(v.durationText) }}</span>
          </span>
          <span class="ml-[10px] min-w-0 flex-1">
            <span class="line-clamp-2 text-[15px] font-medium leading-[19px] text-inkMain transition group-hover:text-stationBlue">{{ v.title ?? v.bvid }}</span>
            <span class="my-[2px] flex items-center text-[13px] leading-[15px] text-inkFaint">
              <!-- UP 徽标（18×18）：自绘描边圆角方框 + U、P 两笔，尺寸距距对齐 B 站官方实测 -->
              <svg viewBox="0 0 18 18" class="mr-1 h-[18px] w-[18px] shrink-0" fill="currentColor">
                <path d="M5.18 6.75v2.83a2.31 2.31 0 0 0 4.62 0V6.75a.58.58 0 0 1 1.15 0v2.83a3.46 3.46 0 0 1-6.92 0V6.75a.58.58 0 0 1 1.15 0z" />
                <path d="M9.49 6.75c0-.32.26-.57.58-.57h1.74a2.16 2.16 0 0 1 0 4.31h-1.17v.75a.58.58 0 0 1-1.15 0V6.75zm1.15 2.62h1.17a1.01 1.01 0 0 0 0-2.03h-1.17v2.03z" />
                <path d="M9 2.62c1.93 0 3.61.1 4.79.2 1.35.1 2.41 1.15 2.53 2.5.09.98.18 2.27.18 3.68s-.09 2.7-.18 3.67c-.12 1.36-1.18 2.4-2.53 2.51-1.18.1-2.86.19-4.79.19s-3.61-.09-4.79-.19c-1.35-.11-2.41-1.15-2.53-2.51C1.59 11.7 1.5 10.4 1.5 9s.09-2.7.18-3.67c.12-1.36 1.18-2.4 2.53-2.51C5.39 2.72 7.07 2.62 9 2.62zm0 1.13c-1.89 0-3.54.1-4.7.19-.81.07-1.44.69-1.51 1.49-.09.95-.17 2.2-.17 3.57s.08 2.62.17 3.57c.07.8.7 1.42 1.51 1.49 1.16.09 2.81.19 4.7.19s3.54-.1 4.7-.19c.81-.07 1.44-.69 1.51-1.49.09-.95.17-2.2.17-3.57s-.08-2.62-.17-3.57c-.07-.8-.7-1.42-1.51-1.49-1.16-.09-2.81-.19-4.7-.19z" />
              </svg>
              <span class="truncate">{{ v.upName ?? '—' }}<template v-if="pubDate(v)"> · {{ pubDate(v) }}</template></span>
            </span>
            <span class="flex items-center text-[13px] leading-[15px] text-inkFaint">
              <!-- 播放量图标（18×18）：自绘描边方框 + 实心三角 -->
              <svg viewBox="0 0 18 18" class="mr-1 h-[18px] w-[18px] shrink-0" fill="currentColor">
                <path d="M9 2.62c1.93 0 3.61.1 4.79.2 1.35.1 2.41 1.15 2.53 2.5.09.98.18 2.27.18 3.68s-.09 2.7-.18 3.67c-.12 1.36-1.18 2.4-2.53 2.51-1.18.1-2.86.19-4.79.19s-3.61-.09-4.79-.19c-1.35-.11-2.41-1.15-2.53-2.51C1.59 11.7 1.5 10.4 1.5 9s.09-2.7.18-3.67c.12-1.36 1.18-2.4 2.53-2.51C5.39 2.72 7.07 2.62 9 2.62zm0 1.13c-1.89 0-3.54.1-4.7.19-.81.07-1.44.69-1.51 1.49-.09.95-.17 2.2-.17 3.57s.08 2.62.17 3.57c.07.8.7 1.42 1.51 1.49 1.16.09 2.81.19 4.7.19s3.54-.1 4.7-.19c.81-.07 1.44-.69 1.51-1.49.09-.95.17-2.2.17-3.57s-.08-2.62-.17-3.57c-.07-.8-.7-1.42-1.51-1.49-1.16-.09-2.81-.19-4.7-.19z" />
                <path d="M7.31 7.63v2.74c0 .69.75 1.12 1.34.77l2.38-1.37a.9.9 0 0 0 0-1.54L8.65 6.86a.9.9 0 0 0-1.34.77z" />
              </svg>
              <span class="tabular-nums">{{ v.viewCountText ?? '—' }}</span>
              <span v-if="v.danmakuCount != null" class="ml-2 flex items-center">
                <!-- 弹幕量图标（18×18）：自绘描边方框 + 四条弹幕胶囊 -->
                <svg viewBox="0 0 18 18" class="mr-1 h-[18px] w-[18px] shrink-0" fill="currentColor">
                  <path d="M9 2.62c1.93 0 3.61.1 4.79.2 1.35.1 2.41 1.15 2.53 2.5.09.98.18 2.27.18 3.68s-.09 2.7-.18 3.67c-.12 1.36-1.18 2.4-2.53 2.51-1.18.1-2.86.19-4.79.19s-3.61-.09-4.79-.19c-1.35-.11-2.41-1.15-2.53-2.51C1.59 11.7 1.5 10.4 1.5 9s.09-2.7.18-3.67c.12-1.36 1.18-2.4 2.53-2.51C5.39 2.72 7.07 2.62 9 2.62zm0 1.13c-1.89 0-3.54.1-4.7.19-.81.07-1.44.69-1.51 1.49-.09.95-.17 2.2-.17 3.57s.08 2.62.17 3.57c.07.8.7 1.42 1.51 1.49 1.16.09 2.81.19 4.7.19s3.54-.1 4.7-.19c.81-.07 1.44-.69 1.51-1.49.09-.95.17-2.2.17-3.57s-.08-2.62-.17-3.57c-.07-.8-.7-1.42-1.51-1.49-1.16-.09-2.81-.19-4.7-.19z" />
                  <path d="M11.91 8.06h-4.5a.57.57 0 1 1 0-1.14h4.5a.57.57 0 1 1 0 1.14z" />
                  <path d="M13.03 11.06h-4.5a.57.57 0 1 1 0-1.14h4.5a.57.57 0 1 1 0 1.14z" />
                  <path d="M5.34 8.06h-.37a.57.57 0 1 1 0-1.14h.37a.57.57 0 1 1 0 1.14z" />
                  <path d="M6.47 11.06h-.37a.57.57 0 1 1 0-1.14h.37a.57.57 0 1 1 0 1.14z" />
                </svg>
                <span class="tabular-nums">{{ v.danmakuCount }}</span>
              </span>
            </span>
          </span>
        </button>
      </li>
    </ul>
  </div>
</template>
