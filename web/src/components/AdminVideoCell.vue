<script setup>
import { RouterLink } from 'vue-router'
import VideoCard from './VideoCard.vue'

// 首页站长视角的卡位：VideoCard + 悬停三键（续下/补采评论/删除），事件交回页面处理。
// 首页两处卡阵（大幕区、均匀区）共用，免得按钮组复制两遍。
defineProps({
  video: { type: Object, required: true },
  commentBusy: { type: Boolean, default: false },
  reviveBusy: { type: Boolean, default: false },
  // 补采/删档两钮的可见性：服务端按「投稿人本人 ∥ 站长」算好随列表下发，访客一律假。
  // 续下/重新排队/加档仍归站长（只有站长打得动，藏不藏都是 403）。
  canManage: { type: Boolean, default: false },
})
defineEmits(['fetch-comments', 'revive', 'remove', 'requeue'])
</script>

<template>
  <div class="group relative">
    <RouterLink :to="`/watch/${video.bvid}`" class="block">
      <!-- 均匀卡阵与搜索结果页共用 VideoCard（B 站首页同款版式），站长视角多开状态点与失败原因 -->
      <VideoCard :video="video" admin />
    </RouterLink>
    <button
      v-if="video.status === 1"
      class="absolute left-2 top-2 hidden rounded-md bg-black/60 p-1.5 text-white/70 backdrop-blur transition hover:bg-amber-400 hover:text-black group-hover:block disabled:opacity-40"
      :disabled="reviveBusy"
      title="续下：救活卡死的下载"
      @click.prevent.stop="$emit('revive', video)"
    >
      <svg viewBox="0 0 24 24" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="2">
        <path d="M13 2 3 14h9l-1 8 10-12h-9l1-8z" stroke-linecap="round" stroke-linejoin="round" />
      </svg>
    </button>
    <button
      v-if="video.status === 5"
      class="absolute left-2 top-2 hidden rounded-md bg-black/60 p-1.5 text-white/70 backdrop-blur transition hover:bg-emerald-400 hover:text-black group-hover:block"
      title="重新排队：被取消的馆藏捞回队列"
      @click.prevent.stop="$emit('requeue', video)"
    >
      <svg viewBox="0 0 24 24" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="2">
        <path d="M20 12a8 8 0 1 1-2.34-5.66M20 4v4h-4" stroke-linecap="round" stroke-linejoin="round" />
      </svg>
    </button>
    <button
      v-if="video.status === 2 && canManage"
      class="absolute right-9 top-2 hidden rounded-md bg-black/60 p-1.5 text-white/70 backdrop-blur transition hover:bg-stationBlue hover:text-white group-hover:block disabled:opacity-40"
      :disabled="commentBusy"
      title="重新采集评论区快照与 B 站弹幕（分站朋友发的站内弹幕不受影响）"
      @click.prevent.stop="$emit('fetch-comments', video)"
    >
      <svg viewBox="0 0 24 24" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="2">
        <path d="M21 12a8 8 0 0 1-11.6 7.1L4 21l1.9-5.4A8 8 0 1 1 21 12z" stroke-linecap="round" stroke-linejoin="round" />
      </svg>
    </button>
    <button
      v-if="canManage"
      class="absolute right-2 top-2 hidden rounded-md bg-black/60 p-1.5 text-white/70 backdrop-blur transition hover:bg-rose-500 hover:text-white group-hover:block"
      title="删掉这个馆藏（含本地文件），仅投稿人本人与站长可删"
      @click.prevent.stop="$emit('remove', video)"
    >
      <svg viewBox="0 0 24 24" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="2">
        <path d="M4 7h16M9 7V5a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2m3 0-1 13a1 1 0 0 1-1 1H8a1 1 0 0 1-1-1L6 7" stroke-linecap="round" stroke-linejoin="round" />
      </svg>
    </button>
  </div>
</template>
