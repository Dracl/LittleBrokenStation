<script setup>
// 放映厅左侧的「看封面」浮窗：贴在视口左缘、藏掉半颗的一颗钮，悬停滑出全貌，点开是封面卡。
// 站长拍板的两颗钮：浮窗悬停即开（点开则钉住，不让鼠标赶跑）、播放器全屏那一刻整颗藏起来。
// 那颗钮本身的外形与藏法不在这里，抽成了 SideHandle（下面新挂的「稿件设置」把手共用同一颗），
// 尺子在 utils/sideDock.js；本组件只管什么时候该有这颗钮、以及封面卡开在哪儿。
// 图不经 coverThumb —— 库里 154 条稿子全部有本地 .Cover.jpg（artifacts kind='cover'），
// watch 载荷里的 coverUrl 已是「本地原图优先」的成品，直出就是原图，热链只是补员没赶到时的兜底。
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import ImageLightbox from './ImageLightbox.vue'
import SideHandle from './SideHandle.vue'
import { useHoverGate } from '../utils/useHoverGate.js'
import { MAX_W, dockOf } from '../utils/sideDock.js'

const props = defineProps({
  cover: { type: String, default: null }, // 本地原图优先的成品地址；空 = 这颗钮不出现
  bvid: { type: String, default: null },
  title: { type: String, default: null },
})

const open = ref(false)
const pinned = ref(false) // 点击开的钉住：鼠标移出去不收（与顶栏头像菜单同脾气）
const zoom = ref(false)
const full = ref(false)
const dims = ref(null) // [w, h]：img @load 才量得到，原图多大就说多大

const gate = useHoverGate(open, {
  onOpen: () => { pinned.value = false },
  shouldClose: () => !pinned.value,
})

function toggle() {
  if (open.value) {
    close()
  } else {
    open.value = true
    pinned.value = true
    gate.cancel()
  }
}

function close() {
  open.value = false
  pinned.value = false
  zoom.value = false
  gate.cancel()
}

const winW = ref(typeof window !== 'undefined' ? window.innerWidth : MAX_W)
function onResize() { winW.value = window.innerWidth }
function onFullscreen() {
  full.value = !!document.fullscreenElement
  if (full.value) close() // 「全屏那刻藏起来」：黑幕里不留这颗钮，也不挡字幕
}
function onKey(e) {
  if (e.key === 'Escape' && open.value && !zoom.value) close() // 灯箱开着时让灯箱自己收
}
onMounted(() => {
  window.addEventListener('resize', onResize)
  document.addEventListener('fullscreenchange', onFullscreen)
  document.addEventListener('keydown', onKey)
})
onBeforeUnmount(() => {
  window.removeEventListener('resize', onResize)
  document.removeEventListener('fullscreenchange', onFullscreen)
  document.removeEventListener('keydown', onKey)
})

// ─── 定位 ───
// 钮的位置交给 SideHandle（它自己量窗口宽度），这里只剩封面卡那一头：还是同一把尺子算出来的数。
const visible = computed(() => !!props.cover && !full.value)
// 封面卡开在钮的右侧（贴着视口左缘那一竖条，与 B 站那两枚的弹层同位置）
const panelLeft = computed(() => `${dockOf(winW.value).panelLeft}px`)

const isLocal = computed(() => !!props.cover && !/^https?:\/\//i.test(props.cover))
const source = computed(() => (isLocal.value ? '站内原图' : 'B 站图床'))
const dimText = computed(() => (dims.value ? `${dims.value[0]} × ${dims.value[1]}` : '尺寸待量'))
const line = computed(() => [props.bvid, props.title].filter(Boolean).join(' · '))
const dlName = computed(() => `${props.bvid || 'cover'}-cover.jpg`)

function onLoad(e) {
  const el = e.target
  dims.value = el && el.naturalWidth && el.naturalHeight ? [el.naturalWidth, el.naturalHeight] : null
}
</script>

<template>
  <!-- 圆钮：外形与藏法都在 SideHandle 里（那颗「稿件设置」把手用的是同一颗，不许出第二份样式）。
       悬停门留在这边：进出由把手报上来，开面板的延时与钉住逻辑是封面浮窗自己的事。 -->
  <SideHandle
    v-if="visible"
    :label="open ? '收起封面' : '看封面'"
    :active="open"
    :on-click="toggle"
    @enter="gate.onEnter()"
    @leave="gate.onLeave()"
  >
    <template #icon>
      <!-- 图片图标 -->
      <svg viewBox="0 0 24 24" class="h-4 w-4" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">
        <rect x="3" y="4.5" width="18" height="15" rx="2.5" />
        <circle cx="8.6" cy="9.8" r="1.7" />
        <path d="M3.6 17.2 9 12l4.2 4 2.6-2.4 4.6 4" />
      </svg>
    </template>
  </SideHandle>

  <!-- 封面卡：ShareDialog 那套轻装裱，z-[60] 压过 z-50 弹窗、输给灯箱 z-[100] -->
  <div
    v-if="visible && open"
    class="absolute top-1/2 z-[60] w-[268px] -translate-y-1/2 rounded-2xl border border-lineSoft bg-cardBg p-3 shadow-[0_20px_60px_-12px_rgba(0,0,0,0.35)]"
    :style="{ left: panelLeft }"
    role="dialog"
    aria-label="封面"
    @mouseenter="gate.onEnter()"
    @mouseleave="gate.onLeave()"
  >
    <img
      :src="cover"
      referrerpolicy="no-referrer"
      decoding="async"
      alt=""
      class="block w-full rounded-lg bg-black/5"
      @load="onLoad"
    />
    <div class="mt-2 flex items-center gap-1.5">
      <b class="font-mono text-xs font-medium text-inkMain">{{ dimText }}</b>
      <span
        class="rounded-full px-1.5 py-px text-[10px]"
        :class="isLocal ? 'bg-emerald-500/10 text-emerald-600 dark:text-emerald-400' : 'bg-amber-500/10 text-amber-600 dark:text-amber-400'"
        :title="isLocal ? '已随片入站，存在 data/videos' : '还没补到本地，直连 B 站图床'"
      >{{ source }}</span>
    </div>
    <p v-if="line" class="mt-1 truncate text-[11px] text-inkFaint" :title="line">{{ line }}</p>
    <div class="mt-2.5 flex gap-1.5">
      <button
        type="button"
        class="flex-1 rounded-lg border border-stationPink/40 bg-stationPink/5 py-1.5 text-[11px] text-stationPink transition hover:bg-stationPink/10"
        @click="zoom = true"
      >
        看大图
      </button>
      <a
        class="flex-1 rounded-lg border border-lineSoft py-1.5 text-center text-[11px] text-inkSub transition hover:border-stationPink/40 hover:text-stationPink"
        :href="cover"
        target="_blank"
        rel="noreferrer"
        title="新标签打开原图"
      >新标签</a>
      <a
        class="flex-1 rounded-lg border border-lineSoft py-1.5 text-center text-[11px] text-inkSub transition hover:border-stationPink/40 hover:text-stationPink"
        :href="cover"
        :download="dlName"
        :title="isLocal ? '存一张到本地' : '热链存不下，会改成打开'"
      >下载</a>
    </div>
    <button
      type="button"
      class="absolute right-1.5 top-1 flex h-6 w-6 items-center justify-center rounded-full text-inkFaint transition hover:bg-black/5 hover:text-inkMain"
      title="关闭（Esc）"
      aria-label="关闭"
      @click="close"
    >
      ×
    </button>
  </div>

  <!-- 看大图：捡现成的公共灯箱（评论区与动态页同一套，Esc 关、←/→ 翻页） -->
  <ImageLightbox v-if="visible && zoom && cover" :images="[cover]" :index="0" @close="zoom = false" />
</template>
