<script setup>
// B 站官方等级徽记（红底白 LV 黄数字，刮自 bilibili 现役 svg 资产）：紧贴昵称右侧、与勋章/UP 牌同排，高 ~14px 与行高齐平。
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
})

const badges = { 1: level1, 2: level2, 3: level3, 4: level4, 5: level5, 6: level6 }
const src = computed(() => badges[props.level] ?? null)
</script>

<template>
  <img
    v-if="src"
    :src="src"
    :alt="`LV${level}`"
    class="h-[14px] shrink-0 self-center"
    draggable="false"
  />
</template>
