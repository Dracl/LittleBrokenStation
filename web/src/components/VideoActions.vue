<script setup>
// B 站式互动栏：数字全部来自 B 站 view 接口真采快照（到站时入库），采不到显示「—」，绝不编数。
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
  <div class="flex items-center gap-8 py-1 text-sm text-inkSub">
    <span class="flex items-center gap-2" title="B 站点赞（采自 view 接口）">
      <svg viewBox="0 0 20 20" class="h-5 w-5" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linejoin="round">
        <path d="M6.5 9 9 2.8c1.4 0 2.2 1 2 2.4L10.4 8.5h4.9c1.2 0 2 1 1.8 2.2l-1 5c-.2 1-1 1.8-2 1.8H6.5M6.5 9v8.5M6.5 9H3.2v8.5h3.3" />
      </svg>
      <span class="tabular-nums">{{ fmt(stat?.like) }}</span>
    </span>
    <span class="flex items-center gap-2" title="B 站投币（采自 view 接口）">
      <svg viewBox="0 0 20 20" class="h-5 w-5" fill="none" stroke="currentColor" stroke-width="1.5">
        <circle cx="10" cy="10" r="7.2" />
        <circle cx="10" cy="10" r="3.2" />
      </svg>
      <span class="tabular-nums">{{ fmt(stat?.coin) }}</span>
    </span>
    <span class="flex items-center gap-2" title="B 站收藏（采自 view 接口）">
      <svg viewBox="0 0 20 20" class="h-5 w-5" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linejoin="round">
        <path d="m10 2.8 2.2 4.5 5 .7-3.6 3.5.8 5-4.4-2.3-4.4 2.3.8-5-3.6-3.5 5-.7z" />
      </svg>
      <span class="tabular-nums">{{ fmt(stat?.favorite) }}</span>
    </span>
    <span class="flex items-center gap-2" title="B 站分享（采自 view 接口）">
      <svg viewBox="0 0 20 20" class="h-5 w-5" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">
        <path d="M4 11.5 15.5 4.5M15.5 4.5h-6M15.5 4.5v6M4 8v6.5a1 1 0 0 0 1 1h9.5" />
      </svg>
      <span class="tabular-nums">{{ fmt(stat?.share) }}</span>
    </span>
  </div>
</template>
