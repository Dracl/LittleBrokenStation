<script setup>
defineProps({
  pages: { type: Array, required: true },
  activeIndex: { type: Number, default: 0 },
})
const emit = defineEmits(['select'])
</script>

<template>
  <div class="rounded-xl bg-cardBg p-3">
    <p class="mb-2 px-1 text-xs text-inkFaint">分 P 列表 · {{ pages.length }} 集</p>
    <ul class="max-h-[480px] space-y-1 overflow-y-auto">
      <li v-for="page in pages" :key="page.index">
        <button
          class="flex w-full items-center gap-2 rounded-lg px-3 py-2 text-left text-sm transition"
          :class="page.index === activeIndex ? 'bg-stationPink/15 text-stationPink' : 'text-inkSub hover:bg-white/5'"
          @click="emit('select', page.index)"
        >
          <span class="w-6 shrink-0 text-xs text-inkFaint">{{ page.index }}</span>
          <span class="min-w-0 flex-1 truncate">{{ page.title }}</span>
          <span v-if="!page.videoUrl" class="shrink-0 text-[10px] text-inkFaint">缺片</span>
        </button>
      </li>
    </ul>
  </div>
</template>
