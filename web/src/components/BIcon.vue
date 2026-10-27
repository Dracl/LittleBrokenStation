<script setup>
// B 站官方图标渲染器：吃 bicons.js 的 { vb, inner } 元组。
// 用法：<BIcon :meta="biLike" class="h-6 w-6 text-inkSub" />
// 交互四件套（like/coin/fav/share）与弹幕系 fill=currentColor，颜色跟 text-* 走；
// 播放器系（play/volume/setting/pip/wide/web/full/subtitle）自带白色 fill，用于深色控制条。
defineProps({
  meta: { type: Object, required: true },
})
</script>

<template>
  <svg
    xmlns="http://www.w3.org/2000/svg"
    :viewBox="meta.vb"
    aria-hidden="true"
    focusable="false"
    v-html="meta.inner"
  />
</template>
