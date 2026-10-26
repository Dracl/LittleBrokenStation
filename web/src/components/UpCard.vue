<script setup>
import { ref, watch } from 'vue'

const props = defineProps({
  upName: { type: String, default: null },
  upMid: { type: Number, default: null },
  upFace: { type: String, default: null },
  upSign: { type: String, default: null },
  upFans: { type: Number, default: null },
})

const faceFailed = ref(false)
watch(() => props.upFace, () => (faceFailed.value = false))

function fmtFans(n) {
  if (n == null) return '—'
  if (n >= 10000) {
    const wan = n / 10000
    return (wan >= 100 ? Math.round(wan) : Math.round(wan * 10) / 10).toString().replace(/\.0$/, '') + '万'
  }
  return n.toString()
}
</script>

<template>
  <!-- B 站同款结构：左头像，右三行（名字+发消息 / 签名 / 关注钮通栏）；充电钮按站长旨意不做 -->
  <div class="rounded-xl bg-cardBg p-4">
    <div class="flex gap-3.5">
      <img
        v-if="upFace && !faceFailed"
        :src="upFace"
        :alt="upName ?? 'UP 主头像'"
        loading="lazy"
        referrerpolicy="no-referrer"
        class="h-14 w-14 shrink-0 rounded-full bg-white/5 object-cover"
        @error="faceFailed = true"
      />
      <span
        v-else
        class="flex h-14 w-14 shrink-0 items-center justify-center rounded-full bg-stationPink/20 text-xl font-semibold text-stationPink"
      >
        {{ (upName ?? '?').slice(0, 1) }}
      </span>
      <div class="min-w-0 flex-1">
        <div class="flex items-center justify-between gap-2">
          <a
            v-if="upMid"
            :href="`https://space.bilibili.com/${upMid}`"
            target="_blank"
            rel="noreferrer"
            class="min-w-0 truncate text-base font-semibold text-inkMain transition hover:text-stationPink"
            :title="upName"
          >
            {{ upName ?? '未知 UP 主' }}
          </a>
          <p v-else class="min-w-0 truncate text-base font-semibold text-inkMain">{{ upName ?? '未知 UP 主' }}</p>
        </div>
        <p class="mt-0.5 line-clamp-2 text-xs leading-5 text-inkFaint">{{ upSign || '这个 UP 主还没有签名' }}</p>
        <a
          v-if="upMid"
          :href="`https://space.bilibili.com/${upMid}`"
          target="_blank"
          rel="noreferrer"
          class="mt-2.5 flex w-full items-center justify-center gap-1.5 rounded-md bg-stationPink py-1.5 text-xs font-medium text-white transition hover:opacity-85"
        >
          <svg viewBox="0 0 16 16" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="1.5">
            <path d="M8 3v10M3 8h10" stroke-linecap="round" />
          </svg>
          去 B 站关注{{ upFans != null ? ` · ${fmtFans(upFans)}` : '' }}
        </a>
      </div>
    </div>
  </div>
</template>
