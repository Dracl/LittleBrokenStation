<script setup>
// 动态流里的视频卡：B 站同款「封面 + 时长角 + 灰底标题条」。
// 已入站走站内放映厅（馆藏封面优先，端点已合并好）；未入站的档案封面也吃分站缓存：本地优先（coverLocal），
// 热链兑底走 normUrl + coverThumb（hdslb CDN 缩图，不拉原图）；整块开 B 站外链。
import { coverThumb } from '../utils/cover'
import { normUrl } from '../utils/dynpic'

defineProps({
  d: { type: Object, required: true }, // { bvid, inStation, title, cover, coverLocal, duration, viewCount }
})

const clampStyle = 'display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;overflow:hidden'
const shell = 'block overflow-hidden rounded-md bg-[#f6f7f8] no-underline transition hover:shadow-md'

function coverSrc(d) {
  return d.coverLocal || coverThumb(normUrl(d.cover)) || ''
}
</script>

<template>
  <RouterLink v-if="d.inStation" :to="`/watch/${d.bvid}`" :class="shell">
    <div class="relative aspect-video bg-[#eef0f2]">
      <img
        v-if="d.cover || d.coverLocal"
        :src="coverSrc(d)"
        referrerpolicy="no-referrer"
        loading="lazy"
        class="absolute inset-0 h-full w-full object-cover"
        alt=""
        @error="$event.target.style.display = 'none'"
      />
      <span v-else class="absolute inset-0 flex items-center justify-center text-lg text-inkFaint">▶</span>
      <span v-if="d.duration" class="absolute bottom-1.5 right-1.5 rounded bg-black/60 px-1.5 py-px text-[11px] leading-4 text-white">{{ d.duration }}</span>
    </div>
    <div class="flex items-center gap-2 px-2.5 py-2">
      <span class="min-w-0 flex-1 text-sm leading-snug text-inkMain" :style="clampStyle">{{ d.title || d.bvid }}</span>
      <span v-if="d.viewCount" class="shrink-0 text-[11px] text-inkFaint">▶ {{ d.viewCount }}</span>
    </div>
  </RouterLink>
  <a
    v-else
    :href="`https://www.bilibili.com/video/${d.bvid}`"
    target="_blank"
    rel="noopener noreferrer"
    :class="shell"
  >
    <div class="relative aspect-video bg-[#eef0f2]">
      <img
        v-if="d.cover || d.coverLocal"
        :src="coverSrc(d)"
        referrerpolicy="no-referrer"
        loading="lazy"
        class="absolute inset-0 h-full w-full object-cover"
        alt=""
        @error="$event.target.style.display = 'none'"
      />
      <span v-else class="absolute inset-0 flex items-center justify-center text-lg text-inkFaint">▶</span>
      <span v-if="d.duration" class="absolute bottom-1.5 right-1.5 rounded bg-black/60 px-1.5 py-px text-[11px] leading-4 text-white">{{ d.duration }}</span>
    </div>
    <div class="flex items-center gap-2 px-2.5 py-2">
      <span class="min-w-0 flex-1 text-sm leading-snug text-inkMain" :style="clampStyle">{{ d.title || d.bvid }}</span>
      <span class="shrink-0 text-[11px] text-inkFaint">↗ B 站</span>
    </div>
  </a>
</template>
