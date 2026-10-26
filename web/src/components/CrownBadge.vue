<script setup>
// 席位冠冕：站长金冠、成员银冠——同形不同色，等级一眼可读。
// 只画冠不占布局，外层负责定位（角标用 absolute，徽章用行内）。
const props = defineProps({
  tier: { type: String, default: 'member' }, // owner | member
})
</script>

<template>
  <svg
    viewBox="0 0 24 24"
    class="drop-shadow-[0_1px_1px_rgba(0,0,0,0.35)]"
    :aria-label="tier === 'owner' ? '站长' : '成员'"
  >
    <path
      d="M3 17.5h18L22.5 8l-5.7 4L12 3.5 7.2 12 1.5 8 3 17.5Z"
      :fill="props.tier === 'owner' ? '#f5c542' : '#b7c2cf'"
      stroke="#ffffff"
      stroke-width="1.4"
      stroke-linejoin="round"
    />
    <!-- 金冠镶宝，银冠留素：细处再分一层尊卑 -->
    <circle v-if="props.tier === 'owner'" cx="12" cy="14.2" r="1.6" fill="#e0275f" />
  </svg>
</template>
