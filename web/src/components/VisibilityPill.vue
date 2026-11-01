<script setup>
import { computed } from 'vue'

// ─── 「私有」那枚小徽标：放映厅标题旁与合集详情页标题旁共用（站长选的 A 案）。
// 形状只此一份：方角 6px + 一道粉描边把轮廓收住，底色只留 7% 不抢 22px 标题，语义交给前面那枚小锁。
// 不写 dark: 变体 —— stationPink 是硬编码色（tailwind.config.js），日夜同一值，粉描边夜里照样读得清。
// 全站只有标可见性的这两处用它：卡阵 / 搜索 / 分区的卡片一律不标（那些列表本就不下发私有稿）。
const props = defineProps({
  // 后端下发的档位字符串；缺字段 / 认不出的值一律当公开（不显），与后端 FromColumn 同一克制。
  visibility: { type: String, default: 'public' },
})

const META = {
  private: {
    text: '私有',
    class:
      'rounded-md border border-stationPink/25 bg-stationPink/[0.07] px-[7px] py-px text-[11px] font-semibold tracking-[0.06em] text-stationPink',
  },
}

const meta = computed(() => META[props.visibility] ?? null)
</script>

<template>
  <span v-if="meta" class="shrink-0" :class="meta.class">
    <!-- 小锁：stroke 走 currentColor，跟着粉字同色，夜里不用另配 -->
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      stroke-width="2.4"
      stroke-linecap="round"
      class="mr-[3px] inline-block h-[10px] w-[10px] align-[-1px]"
    >
      <rect x="4" y="10" width="16" height="11" rx="2.5" />
      <path d="M8 10V7a4 4 0 0 1 8 0v3" />
    </svg>
    {{ meta.text }}
  </span>
</template>
