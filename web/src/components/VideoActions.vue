<script setup>
// B 站式互动栏：填充大图标 + 数字，四枚均转灰（站长定案：不抢播放页视觉）。
// 数字全部来自 B 站 view 接口真采快照（到站时入库），采不到显示「—」，绝不编数。
defineProps({
  stat: { type: Object, default: null }, // { like, coin, favorite, share, reply }
})

function fmt(n) {
  if (n == null) return '—'
  if (n >= 10000) return (n / 10000).toFixed(1).replace(/\.0$/, '') + '万'
  return n.toLocaleString('zh-CN')
}
</script>

<template>
  <div class="flex items-center gap-10 py-2 text-base text-inkSub">
    <span class="flex items-center gap-2" title="B 站点赞（采自 view 接口）">
      <svg viewBox="0 0 24 24" class="h-[25px] w-[25px] shrink-0 text-[#757575]" fill="currentColor">
        <path d="M1 21h4V9H1v12zm22-11c0-1.1-.9-2-2-2h-6.31l.95-4.57.03-.32c0-.41-.17-.79-.44-1.06L14.17 1 7.59 7.59C7.22 7.95 7 8.45 7 9v10c0 1.1.9 2 2 2h9c.83 0 1.54-.5 1.84-1.22l3.02-7.05c.09-.23.14-.47.14-.73v-2z" />
      </svg>
      <span class="leading-none tabular-nums">{{ fmt(stat?.like) }}</span>
    </span>
    <span class="flex items-center gap-2" title="B 站投币（采自 view 接口）">
      <span class="flex h-[25px] w-[25px] shrink-0 items-center justify-center rounded-full bg-[#757575] text-[14px] font-medium leading-none text-white">币</span>
      <span class="leading-none tabular-nums">{{ fmt(stat?.coin) }}</span>
    </span>
    <span class="flex items-center gap-2" title="B 站收藏（采自 view 接口）">
      <svg viewBox="0 0 24 24" class="h-[25px] w-[25px] shrink-0 text-[#757575]" fill="currentColor">
        <path d="M12 2.5 14.9 8.4l6.5.9-4.7 4.6 1.1 6.5-5.8-3-5.8 3 1.1-6.5L2.6 9.3l6.5-.9z" />
      </svg>
      <span class="leading-none tabular-nums">{{ fmt(stat?.favorite) }}</span>
    </span>
    <span class="flex items-center gap-2" title="B 站分享（采自 view 接口）">
      <svg viewBox="0 0 24 24" class="h-[25px] w-[25px] shrink-0 text-[#757575]" fill="currentColor">
        <path d="M10 9V5l-7 7 7 7v-4.1c5 0 8.5 1.6 11 5.1-1-5-4-10-11-11z" />
      </svg>
      <span class="leading-none tabular-nums">{{ fmt(stat?.share) }}</span>
    </span>
  </div>
</template>
