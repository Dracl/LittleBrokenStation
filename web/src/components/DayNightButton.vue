<script setup>
// 日夜开关（11-17 站长令抽成组件）：原是首页右下角那颗黑玻离圆钮，白昼见月、夜里见日；
// 如今放映厅也要坐镇，首页与放映厅共用这一颗。只管把 html.dark 掰上掰下（真活在 style.css 的变量里）。
// .home-dock 一族：看板娘聊天卡展开时（html.agent-open）跟着淡出让位，规矩长在 style.css。
import { ref } from 'vue'
import { isDark, toggleTheme } from '../theme'
import { dockHover, dockReveal } from '../agentDockStore'

// 落点可择（11-17 站长令：放映厅没有切换模式打底，两颗钮不能还站在首页的半空）：
// 首页留原位 5.25rem，放映厅递 1.5rem 落底。faded：放映厅的影院渐隐（useIdleFade 发令，首页不传即永亮）。
const props = defineProps({
  bottom: { type: String, default: '5.25rem' },
  faded: { type: Boolean, default: false },
})
function setHover(v) { dockHover.value = v }

const nightMode = ref(isDark())
function flipDayNight() {
  toggleTheme()
  nightMode.value = isDark()
}
</script>

<template>
  <!-- 三钮同闸（11-17 站长令）：status 落定那刻与看板娘/切换模式同刻揭幕（dock-in 同族入场） -->
  <button
    v-if="dockReveal"
    class="home-dock dock-in fixed right-6 z-40 flex h-11 w-11 items-center justify-center rounded-full border border-white/15 bg-black/60 text-white/70 shadow-lg backdrop-blur transition hover:border-stationPink/60 hover:text-stationPink"
    :class="faded ? 'dock-faded' : ''"
    :style="{ bottom: props.bottom }"
    @mouseenter="setHover(true)"
    @mouseleave="setHover(false)"
    :title="nightMode ? '切回白昼' : '切进黑夜'"
    :aria-label="nightMode ? '切回白昼' : '切进黑夜'"
    @click="flipDayNight"
  >
    <svg v-if="nightMode" viewBox="0 0 24 24" class="h-5 w-5" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round">
      <circle cx="12" cy="12" r="4.2" />
      <path d="M12 2.5v2.2M12 19.3v2.2M2.5 12h2.2M19.3 12h2.2M5.2 5.2l1.6 1.6M17.2 17.2l1.6 1.6M18.8 5.2l-1.6 1.6M6.8 17.2l-1.6 1.6" />
    </svg>
    <svg v-else viewBox="0 0 24 24" class="h-5 w-5" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
      <path d="M20.8 13.4A8.6 8.6 0 1 1 10.6 3.2a6.8 6.8 0 0 0 10.2 10.2z" />
    </svg>
  </button>
</template>
