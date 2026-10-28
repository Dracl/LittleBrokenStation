<script setup>
import { useRouter } from 'vue-router'
import { colonTime } from '../utils/time'

// 接下来播放：分站自己的推荐流（同 UP 主优先 + 新入站补齐），卡形仿 B 站「封面左 + 信息右」。
const props = defineProps({
  items: { type: Array, default: () => [] },
  autoplay: { type: Boolean, default: false },
})
const emit = defineEmits(['update:autoplay'])

const router = useRouter()

function relDate(text) {
  if (!text) return ''
  const d = new Date(text)
  return Number.isNaN(d.getTime()) ? '' : d.toLocaleDateString('zh-CN', { year: 'numeric', month: '2-digit', day: '2-digit' })
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
    <ul class="space-y-1">
      <li v-for="v in items" :key="v.id">
        <button class="flex w-full gap-2.5 rounded-lg p-1.5 text-left transition hover:bg-white/5" @click="go(v.bvid)">
          <span class="relative block h-[76px] w-[124px] shrink-0 overflow-hidden rounded-md bg-[#f1f2f3] dark:bg-[#23262e]">
            <img
              v-if="v.coverUrl"
              :src="v.coverUrl"
              :alt="v.title ?? ''"
              loading="lazy"
              referrerpolicy="no-referrer"
              class="h-full w-full object-cover"
            />
            <span
              v-if="v.durationText"
              class="absolute bottom-1 right-1 rounded bg-black/65 px-1 py-px text-[10px] leading-4 text-white tabular-nums"
            >{{ colonTime(v.durationText) }}</span>
          </span>
          <span class="min-w-0 flex-1">
            <span class="line-clamp-2 text-[13px] font-medium leading-5 text-inkMain">{{ v.title ?? v.bvid }}</span>
            <span class="mt-0.5 block truncate text-xs text-inkFaint">{{ v.upName ?? '—' }} · {{ relDate(v.createdAt) }}</span>
            <span class="mt-0.5 flex items-center gap-2.5 text-xs text-inkFaint">
              <span class="flex items-center gap-1 tabular-nums">
                <svg viewBox="0 0 16 16" class="h-3 w-3" fill="currentColor"><path d="M4.5 2.8v10.4L13 8z" /></svg>
                {{ v.viewCountText ?? '—' }}
              </span>
              <span v-if="v.danmakuCount != null" class="flex items-center gap-1 tabular-nums">
                <svg viewBox="0 0 16 16" class="h-3 w-3" fill="none" stroke="currentColor" stroke-width="1.4">
                  <rect x="1.5" y="3" width="13" height="8.5" rx="2" />
                </svg>
                {{ v.danmakuCount }}
              </span>
            </span>
          </span>
        </button>
      </li>
    </ul>
  </div>
</template>
