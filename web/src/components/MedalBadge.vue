<script setup>
// B 站官方粉丝勋章，对齐评论区组件 bili-comment-user-medal 的 #contractor 规格：
//   height:12px · padding:0 2px · border-radius:2px · border:0.5px solid · 文字视觉 8px（13px 昵称那一行）
// 官方把文字按 16px 排好再 transform:scale(.5) 缩到 8px（防小字发糊）；那样文字脱流、牌宽要靠额外占位，
// 这里直接用等效字号渲染（与站内既有 9px/10px 小字同路子），外观尺寸一致、盒模型更稳。
// 官方基准那一行昵称是 13px，牌高 12 = 0.923em；size 收「牌的可见高度」，内部 padding/圆角/字号按 size/12 等比。
// 分站 15px 昵称 → 13.8；楼中楼 13px → 12；动态侧 12.5/12px → 11.5 / 11.1。
// 描边：官方 border-color 取 B 站 medals 的 border_color，分站只存了 color（CommentMedal(Level,Name,Color)），
// 故暂用同色近似（观感等同无边框）；等归档补上 border_color，改 hexOf 一处即可接上。
import { computed } from 'vue'

const props = defineProps({
  level: { type: Number, default: null },
  name: { type: String, default: '' },
  color: { type: String, default: null },
  size: { type: Number, default: 13.8 },
})

function hexOf(color) {
  const raw = (color || '').trim()
  if (!raw) return '#fb7299'
  return raw.startsWith('#') ? raw : `#${raw}`
}

const k = computed(() => props.size / 12)
const box = computed(() => ({
  height: `${props.size}px`,
  padding: `0 ${(2 * k.value).toFixed(2)}px`,
  borderRadius: `${(2 * k.value).toFixed(2)}px`,
  borderColor: hexOf(props.color),
  backgroundColor: hexOf(props.color),
}))
const label = computed(() => (props.level == null ? props.name : `${props.level} ${props.name}`))
</script>

<template>
  <span v-if="name" class="medal" :style="[box, { fontSize: `${(8 * k).toFixed(2)}px` }]">{{ label }}</span>
</template>

<style scoped>
.medal {
  display: inline-flex;
  flex: none;
  box-sizing: border-box;
  align-items: center;
  justify-content: center;
  border-width: 0.5px;
  border-style: solid;
  line-height: 1;
  white-space: nowrap;
  color: #fff;
}
</style>
