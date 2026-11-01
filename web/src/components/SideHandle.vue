<script setup>
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { MAX_W, dockOf } from '../utils/sideDock.js'

// ─── 贴视口左缘的那半颗把手：外形与藏法全站只此一份（放映厅的「看封面」与「稿件设置」共用它）。
// 藏法照 B 站播放页左缘那两枚立方体：按钮中心压在视口 x=0，只留右半边露着，鼠标来才整颗滑出。
// 位置算法住在 utils/sideDock.js（封面卡的弹层要跟同一把尺子），这里只管把它套到一颗钮上。
// 装裱沿用首页右下那套悬浮钮：深底 + 描边 + backdrop-blur，hover 才上粉；active（面板开着）常亮粉。
const props = defineProps({
  // 悬停提示与 aria-label 一起用它（两颗把手各自说清自己是干什么的）
  label: { type: String, required: true },
  // 纵向错开：数字按 px 走（相对「垂直居中」那一档），字符串原样进 style。
  // 第二颗挂在第一颗下面，靠这个隔开，谁也不挡谁。
  offset: { type: [Number, String], default: 0 },
  // 面板开着：描边常亮粉，并且不许缩回视口外（正用着的面板不能靠鼠标跑位赶跑）
  active: { type: Boolean, default: false },
  // 点这一颗。按父代理定的接口收在 props 里（不用 emit('click')，免得与原生 click 冒撞）
  onClick: { type: Function, default: null },
  // 播放器全屏那一刻整颗藏起来：黑幕里不留把手，也不挡字幕
  hideOnFullscreen: { type: Boolean, default: true },
})
// 悬停进出报给调用方：封面浮窗要靠它驱动悬停门（进 120ms 开、离 260ms 收）
const emit = defineEmits(['enter', 'leave'])

const hot = ref(false) // 悬停立刻滑出：门有延时，这一滑不该等它

const winW = ref(typeof window !== 'undefined' ? window.innerWidth : MAX_W)
const full = ref(false)
function onResize() {
  winW.value = window.innerWidth
}
function onFullscreen() {
  full.value = !!document.fullscreenElement
}
onMounted(() => {
  window.addEventListener('resize', onResize)
  document.addEventListener('fullscreenchange', onFullscreen)
})
onBeforeUnmount(() => {
  window.removeEventListener('resize', onResize)
  document.removeEventListener('fullscreenchange', onFullscreen)
})

const visible = computed(() => !(props.hideOnFullscreen && full.value))
const revealed = computed(() => hot.value || props.active)
const fabLeft = computed(() => `${dockOf(winW.value).fabLeft}px`)
const margin = computed(() => (typeof props.offset === 'number' ? `${props.offset}px` : props.offset || '0px'))
</script>

<template>
  <!-- 平时只露右半颗贴在视口左缘，鼠标一碰整颗滑进来（18px = FAB 的一半，见 utils/sideDock.js） -->
  <div
    v-if="visible"
    class="absolute top-1/2 z-[60] -translate-y-1/2 transition-transform duration-200"
    :class="revealed ? 'translate-x-[18px]' : 'translate-x-0'"
    :style="{ left: fabLeft, marginTop: margin }"
  >
    <button
      type="button"
      class="flex h-9 w-9 items-center justify-center rounded-full border shadow-lg backdrop-blur transition"
      :class="
        active
          ? 'border-stationPink/60 bg-black/70 text-stationPink'
          : 'border-white/15 bg-black/60 text-white/70 hover:border-stationPink/60 hover:text-stationPink'
      "
      :title="label"
      :aria-label="label"
      :aria-expanded="active"
      aria-haspopup="dialog"
      @mouseenter="hot = true; emit('enter')"
      @mouseleave="hot = false; emit('leave')"
      @focus="hot = true"
      @blur="hot = false"
      @click="onClick && onClick($event)"
    >
      <!-- 图标一律描边 svg 直接内联（bicons.js 只收 B 站官方扒录件），stroke 走 currentColor 跟着钮色走 -->
      <slot name="icon" />
    </button>
  </div>
</template>
