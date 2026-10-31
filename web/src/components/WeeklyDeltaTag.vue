<script setup>
import { computed } from 'vue'

// 环比涨跌角标：上周零数时不摆百分比（除零没意义），只标「新增」；持平标平，涨粉降灰。
const props = defineProps({
  d: { type: Object, default: null },
  base: { type: String, default: '上周' },
})

// tooltip 把口径说白：本周至今的角标拿「上一个完整周」做基准，涨幅天生只可能偏小
const tip = computed(() => {
  if (!props.d) return ''
  if (props.d.dir === 'new') return `对比${props.base}：${props.base}整周没数据（分站那时还没开张），比不出涨跌`
  if (props.d.dir === 'flat') return `对比${props.base}：持平（${props.d.baseValue}）`
  return `对比${props.base} ${props.d.baseValue}：${props.d.dir === 'up' ? '涨' : '跌'} ${props.d.text}`
})
</script>

<template>
  <span v-if="d" :title="tip" class="inline-flex items-center gap-0.5 self-center rounded-full px-1.5 py-0.5 text-[11px] font-normal leading-none"
    :class="{
      'bg-stationPink/10 text-stationPink': d.dir === 'up',
      'bg-[#f1f2f3] text-inkFaint dark:bg-[#23262e]': d.dir === 'down' || d.dir === 'flat' || d.dir === 'new',
    }"
  >
    <svg v-if="d.dir === 'up'" viewBox="0 0 24 24" class="h-3 w-3" fill="none" stroke="currentColor" stroke-width="2.4">
      <path d="M12 19V6M6 11l6-6 6 6" stroke-linecap="round" stroke-linejoin="round" />
    </svg>
    <svg v-else-if="d.dir === 'down'" viewBox="0 0 24 24" class="h-3 w-3" fill="none" stroke="currentColor" stroke-width="2.4">
      <path d="M12 5v13M6 13l6 6 6-6" stroke-linecap="round" stroke-linejoin="round" />
    </svg>
    {{ d.dir === 'new' ? '上周没有' : d.dir === 'flat' ? '持平' : d.text }}
  </span>
</template>
