<script setup>
// 席位冠冕：站长金冠、成员银冠——同形不同色，等级一眼可读。
// 两种形态共用 utils/crown.js 那一套几何：
//   standalone：行内小牌（列表里「成员」那种，旁边没头像可贴），保持 24 格 viewBox 的直边冠；
//   fit：贴在头像右上角，viewBox 就是头像方框（100 = 头像直径），冠的底边沿圆周走，画到框外靠 overflow 可见。
import { computed } from 'vue'
import { CROWN, crownPath } from '../utils/crown.js'

const props = defineProps({
  tier: { type: String, default: 'member' }, // owner | member
  mode: { type: String, default: 'standalone' }, // standalone | fit
})

const fit = computed(() => (props.mode === 'fit' ? crownPath(50, 50, 100) : null))
const label = computed(() => (props.tier === 'owner' ? '站长' : '成员'))
const color = computed(() => (props.tier === 'owner' ? CROWN.gold : CROWN.silver))
</script>

<template>
  <svg
    v-if="fit"
    viewBox="0 0 100 100"
    class="overflow-visible drop-shadow-[0_1px_1px_rgba(0,0,0,0.35)]"
    :aria-label="label"
  >
    <path
      :d="fit.path"
      :fill="color"
      :stroke="CROWN.stroke"
      :stroke-width="(CROWN.sw * 100).toFixed(2)"
      stroke-linejoin="round"
    />
    <!-- 金冠镶宝，银冠留素：细处再分一层尊卑 -->
    <circle v-if="props.tier === 'owner'" :cx="fit.gem.cx" :cy="fit.gem.cy" :r="fit.gem.r" :fill="CROWN.gem" />
  </svg>
  <svg
    v-else
    viewBox="0 0 24 24"
    class="drop-shadow-[0_1px_1px_rgba(0,0,0,0.35)]"
    :aria-label="label"
  >
    <path
      d="M3 17.5h18L22.5 8l-5.7 4L12 3.5 7.2 12 1.5 8 3 17.5Z"
      :fill="color"
      stroke="#ffffff"
      stroke-width="1.4"
      stroke-linejoin="round"
    />
    <circle v-if="props.tier === 'owner'" cx="12" cy="14.2" r="1.6" :fill="CROWN.gem" />
  </svg>
</template>
