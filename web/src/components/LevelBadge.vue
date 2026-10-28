<script setup>
// B 站官方等级徽记，刮自评论区组件现役资产 i0.hdslb.com/bfs/seed/jinkela/short/webui/user-profile/img/level_*.svg（与官方现网文件逐字节同源）。
// 官方原件是 30×30 画板（#user-level 也就摆成 30×30），色块只画在 x[1..21.1] y[10..20.1]，四周约 2/3 是透明留白；
// 已把 viewBox 裁到色块本身，于是这里的 height 就等于「肉眼看到的色块高度」，不再被留白稀释。
// 官方比例基准（昵称 13px 那一行）：LV 色块 10.1、UP 牌 11.5、勋章牌 12；这里取昵称字号的 ~0.8 倍对齐，
// 宽度按裁切后 1.97:1 自适应。
// level 不在 1~6（null/0/>6）渲染空、不占位。视频评论区与动态评论抽屉共用这一枚。
import { computed } from 'vue'
import level1 from '../assets/levels/level_1.svg'
import level2 from '../assets/levels/level_2.svg'
import level3 from '../assets/levels/level_3.svg'
import level4 from '../assets/levels/level_4.svg'
import level5 from '../assets/levels/level_5.svg'
import level6 from '../assets/levels/level_6.svg'

const props = defineProps({
  level: { type: Number, default: null },
  // 色块可见高度（CSS px），按所在行的昵称字号给：15px → 12、13px → 10.5、12px → 9.5。
  // 用行内样式落地，避免调用方再挂 h-* 类与组件内类打架（同为 height，胜负取决于打包顺序）。
  size: { type: Number, default: 12 },
})

const badges = { 1: level1, 2: level2, 3: level3, 4: level4, 5: level5, 6: level6 }
const src = computed(() => badges[props.level] ?? null)
const box = computed(() => ({ height: `${props.size}px` }))
</script>

<template>
  <img
    v-if="src"
    :src="src"
    :alt="`LV${level}`"
    :style="box"
    class="w-auto shrink-0 self-center"
    draggable="false"
  />
</template>
