<script setup>
// 放映厅的空态门面：仿 B 站「啊叻？视频不见了？」那台带天线的小电视，分站粉饰版。
// 大标题 + 一句副题 + 默认插槽放按钮。访客门与查无此片共用这副形象。
defineProps({
  title: { type: String, required: true },
  subtitle: { type: String, default: '' },
})
</script>

<template>
  <div class="flex flex-col items-center px-6 py-16 text-center">
    <svg viewBox="0 0 120 100" class="h-32 w-auto" fill="none" aria-hidden="true">
      <!-- 天线 -->
      <path d="M47 24 36 10M73 24l11-14" stroke="#fb7299" stroke-width="3" stroke-linecap="round" />
      <circle cx="36" cy="10" r="2.4" fill="#fb7299" />
      <circle cx="84" cy="10" r="2.4" fill="#fb7299" />
      <!-- 问号：左右各一，懵懵懂懂 -->
      <path d="M20 16c0-4 7.5-4.6 7.5.6 0 2.6-3.2 2.6-3.2 5.4" stroke="#fb7299" stroke-width="2.4" stroke-linecap="round" opacity="0.45" />
      <circle cx="24.2" cy="26.5" r="1.5" fill="#fb7299" opacity="0.45" />
      <path d="M96 12c0-4 7.5-4.6 7.5.6 0 2.6-3.2 2.6-3.2 5.4" stroke="#fb7299" stroke-width="2.4" stroke-linecap="round" opacity="0.45" />
      <circle cx="100.2" cy="22.5" r="1.5" fill="#fb7299" opacity="0.45" />
      <!-- 机身 -->
      <rect x="24" y="26" width="72" height="50" rx="8" stroke="#fb7299" stroke-width="3" fill="#fff" />
      <rect x="32" y="34" width="56" height="34" rx="4" fill="#fb7299" opacity="0.07" />
      <!-- 屏幕雪花 -->
      <path d="M36 44c4-3.5 7.5 3.5 11.5 0s7.5 3.5 11.5 0 7.5 3.5 11.5 0 7.5 3.5 11.5 0" stroke="#fb7299" stroke-width="2" stroke-linecap="round" opacity="0.4" />
      <path d="M36 54c4-3.5 7.5 3.5 11.5 0s7.5 3.5 11.5 0 7.5 3.5 11.5 0 7.5 3.5 11.5 0" stroke="#fb7299" stroke-width="2" stroke-linecap="round" opacity="0.28" />
      <path d="M36 63c4-3.5 7.5 3.5 11.5 0s7.5 3.5 11.5 0 7.5 3.5 11.5 0 7.5 3.5 11.5 0" stroke="#fb7299" stroke-width="2" stroke-linecap="round" opacity="0.18" />
      <!-- 底座 -->
      <path d="M44 76v8M76 76v8" stroke="#fb7299" stroke-width="3" stroke-linecap="round" />
      <path d="M38 88h44" stroke="#fb7299" stroke-width="3" stroke-linecap="round" />
    </svg>

    <p class="mt-8 text-[26px] font-bold text-inkMain">{{ title }}</p>
    <p v-if="subtitle" class="mt-4 text-sm text-inkSub">{{ subtitle }}</p>

    <div v-if="$slots.default" class="mt-8 flex flex-wrap items-center justify-center gap-3">
      <slot />
    </div>
  </div>
</template>
