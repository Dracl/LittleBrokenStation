<script setup>
// 全站公共图片灯箱：从视频评论区 CommentSection 的 zoomImage 那套抽出，观感一致
// （黑透遮罩 + 居中大图，点遮罩/Esc 关），并补上左右翻页（按钮 + ←/→ 键）。
// z-[100] 压过页面一切（TopBar、toast、登录窗都在 z-50 及以下）；热链图一律 no-referrer。
import { onBeforeUnmount, ref, watch } from 'vue'

const props = defineProps({
  images: { type: Array, required: true }, // 已按「本地原图优先」合并好的地址数组，灯箱只管画
  index: { type: Number, default: 0 },     // 初始下标
})
const emit = defineEmits(['close'])

const current = ref(0)

watch(
  () => [props.images, props.index],
  () => {
    current.value = Math.min(Math.max(0, props.index ?? 0), Math.max(0, props.images.length - 1))
  },
  { immediate: true },
)

function step(delta) {
  const n = props.images.length
  if (n <= 1) return
  current.value = (current.value + delta + n) % n
}

function onKeydown(event) {
  if (event.key === 'Escape') emit('close')
  else if (event.key === 'ArrowLeft') step(-1)
  else if (event.key === 'ArrowRight') step(1)
}

window.addEventListener('keydown', onKeydown)
onBeforeUnmount(() => window.removeEventListener('keydown', onKeydown))
</script>

<template>
  <div class="fixed inset-0 z-[100] flex items-center justify-center bg-black/80 p-6" @click="emit('close')">
    <img
      v-if="images[current]"
      :src="images[current]"
      referrerpolicy="no-referrer"
      class="max-h-[85vh] max-w-[85vw] rounded-lg"
      alt=""
      @click.stop
    />
    <button
      v-if="images.length > 1"
      class="absolute left-4 top-1/2 flex h-10 w-10 -translate-y-1/2 items-center justify-center rounded-full bg-black/50 text-xl leading-none text-white transition hover:bg-black/70"
      title="上一张（←）"
      @click.stop="step(-1)"
    >‹</button>
    <button
      v-if="images.length > 1"
      class="absolute right-4 top-1/2 flex h-10 w-10 -translate-y-1/2 items-center justify-center rounded-full bg-black/50 text-xl leading-none text-white transition hover:bg-black/70"
      title="下一张（→）"
      @click.stop="step(1)"
    >›</button>
    <span
      v-if="images.length > 1"
      class="absolute bottom-4 left-1/2 -translate-x-1/2 rounded-full bg-black/50 px-2.5 py-1 text-xs text-white"
    >{{ current + 1 }} / {{ images.length }}</span>
    <button
      class="absolute right-4 top-4 flex h-9 w-9 items-center justify-center rounded-full bg-black/50 text-lg leading-none text-white transition hover:bg-black/70"
      title="关闭（Esc）"
      @click.stop="emit('close')"
    >×</button>
  </div>
</template>
