<script setup>
// B 站官方 UP 主牌，刮自评论区组件现役资产 i0.hdslb.com/bfs/seed/jinkela/short/webui/comments/img/icons/up_pb.svg。
// 官方原件是 24×24 画板，色块只占 x[3..21] y[6..17.5]（18 × 11.5 单位，#FF6699 底 + 矢量 UP 字形，不依赖字体）；
// 官方 CSS 把元素摆成 #user-up{width:24px;height:24px}。同 LevelBadge 一路：viewBox 裁到色块，height 就是肉眼看到的牌高。
// 官方比例基准（昵称 13px 那一行）：LV 色块 10.1、UP 牌 11.5、勋章牌 12。分站昵称 15px，按 k=15/13 放大取 13.7。
import { computed } from 'vue'
import upBadge from '../assets/icons/up-badge.svg'

const props = defineProps({
  // 牌的可见高度（CSS px），按所在行昵称字号的 ~0.885 倍给：15px → 13.7、13px → 11.9、12.5px → 11.4、12px → 11。
  size: { type: Number, default: 13.7 },
})

const box = computed(() => ({ height: `${props.size}px` }))
</script>

<template>
  <img
    :src="upBadge"
    alt="UP"
    :style="box"
    class="w-auto shrink-0 self-center"
    draggable="false"
  />
</template>
