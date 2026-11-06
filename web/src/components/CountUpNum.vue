<script setup>
import { onMounted, onUnmounted, ref, watch } from 'vue'

// 周报大数滚动入场：挂载时从 0 滚到目标值（ease-out 三次缓出），换周数值变了再滚一回，让「涨没涨」有体感。
// 等宽数字防抖：滚动途中位数宽度会变，tabular-nums 钉住字宽，数字不左右抖。
// prefers-reduced-motion 的用户直接给终值，不献动效。
const props = defineProps({
  value: { type: Number, default: 0 },
  duration: { type: Number, default: 550 },
})
const shown = ref(props.value)
let raf = 0

function run(target) {
  const to = Number.isFinite(target) ? target : 0
  cancelAnimationFrame(raf) // 上一段还没跑完就先掐掉，免得它把刚落的终值又拖回去
  // 无动效偏好、还有 duration<=0 这两种都直接落终值，不起 rAF 循环
  if (!(props.duration > 0) || window.matchMedia?.('(prefers-reduced-motion: reduce)').matches) {
    shown.value = to
    return
  }
  const t0 = performance.now()
  const step = (t) => {
    const k = Math.min(1, (t - t0) / props.duration)
    const eased = 1 - (1 - k) ** 3
    shown.value = Math.round(to * eased)
    if (k < 1) raf = requestAnimationFrame(step)
  }
  raf = requestAnimationFrame(step)
}

onMounted(() => run(props.value))
watch(() => props.value, (v) => run(v ?? 0))
onUnmounted(() => cancelAnimationFrame(raf)) // 收了帘就不再回调，也不让循环替已消失的实例继续跑
</script>

<template>
  <span class="tabular-nums">{{ shown }}</span>
</template>
