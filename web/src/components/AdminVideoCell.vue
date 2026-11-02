<script setup>
import { computed, ref } from 'vue'
import VideoCard from './VideoCard.vue'
import { HINT_DELETE_VIDEO, HINT_FETCH_COMMENTS } from '../confirmStore'

// 首页站长视角的卡位：VideoCard + 悬停三键（续下/补采评论/删除），事件交回页面处理。
// 首页两处卡阵（大幕区、均匀区）共用，免得按钮组复制两遍。
defineProps({
  video: { type: Object, required: true },
  commentBusy: { type: Boolean, default: false },
  reviveBusy: { type: Boolean, default: false },
  // 四枚悬停钮的可见性共用这一把钥匙：服务端按「投稿人本人 ∥ 站长」算好随列表下发，访客一律假
  // （ManageGate.CanManage：站长短路真、无席位假、非站长只认投稿人席位 id 相等，无主行只站长）。
  // 续下/重新排队这两枚原先只看 status、不看本钥匙，访客停在卡死/被取消的卡上也能把钮顶出来（点了靠后端 403 接住）；
  // 站长 11-02 拍板「统一成投稿本人 + 站长」，现在四枚同一口径。后端那两口（/revive /requeue）端点里本来就核 CanManageVideo。
  canManage: { type: Boolean, default: false },
})
defineEmits(['fetch-comments', 'revive', 'remove', 'requeue'])

// 管理员钮收进封面（站长 11-02 在卡片上点的 adminBtnOnCover）：这四枚钮都 absolute 贴在封面顶行，
// 视觉归属就是封面，浮出条件跟着收进封面——停在标题、UP 行、读数行不再把它们顶出来。
// 但钮在 DOM 上是封面链接的兄弟：鼠标从封面移到钮上时封面的 mouseleave 先响，钮会在指尖底下消失、永远点不到，
// 所以钮自己也投一票（btnOn），两张票任一成立就留着。
const coverOn = ref(false)
const btnOn = ref(false)
const toolsOn = computed(() => coverOn.value || btnOn.value)
</script>

<template>
  <div class="relative">
    <!-- 命中区收口（站长 11-02 在调参台上定的 coverTitleText）：外层这颗整卡大链接撤了，可点结构归 VideoCard 自己
         （封面一枚 RouterLink + 标题字面一枚），调用方不再替它包一层 a。
         本层的 group 也下岗了（站长 11-02 把下面四枚悬停管理钮也收进封面，改由封面 hover 驱动，见 toolsOn）；
         relative 原样留着：四枚钮都 absolute 贴在封面顶行，靠它定位。
         均匀卡阵与搜索结果页共用 VideoCard（B 站首页同款版式），站长视角多开状态点与失败原因 -->
    <VideoCard :video="video" admin :cover-hold="btnOn" @cover-hover="coverOn = $event" />
    <button
      v-if="video.status === 1 && canManage"
      class="absolute left-2 top-2 rounded-md bg-black/60 p-1.5 text-white/70 backdrop-blur transition hover:bg-amber-400 hover:text-black disabled:opacity-40"
      :class="toolsOn ? 'block' : 'hidden'"
      :disabled="reviveBusy"
      title="续下：救活卡死的下载"
      @mouseenter="btnOn = true"
      @mouseleave="btnOn = false"
      @click.prevent.stop="$emit('revive', video)"
    >
      <svg viewBox="0 0 24 24" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="2">
        <path d="M13 2 3 14h9l-1 8 10-12h-9l1-8z" stroke-linecap="round" stroke-linejoin="round" />
      </svg>
    </button>
    <!-- 重新排队：被取消的（status 5）捐回队列；被引擎判为「已下过」的（status 4）另挂重下旗绕开判重 -->
    <button
      v-if="(video.status === 5 || video.status === 4) && canManage"
      class="absolute left-2 top-2 rounded-md bg-black/60 p-1.5 text-white/70 backdrop-blur transition hover:text-black"
      :class="[video.status === 4 ? 'hover:bg-amber-400' : 'hover:bg-emerald-400', toolsOn ? 'block' : 'hidden']"
      :title="video.status === 4
        ? '销档重下：引擎说这部已经下过，绕开判重再下一遍（正片会重下）'
        : '重新排队：被取消的馆藏捐回队列'"
      @mouseenter="btnOn = true"
      @mouseleave="btnOn = false"
      @click.prevent.stop="$emit('requeue', video)"
    >
      <svg viewBox="0 0 24 24" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="2">
        <path d="M20 12a8 8 0 1 1-2.34-5.66M20 4v4h-4" stroke-linecap="round" stroke-linejoin="round" />
      </svg>
    </button>
    <button
      v-if="video.status === 2 && canManage"
      class="absolute right-9 top-2 rounded-md bg-black/60 p-1.5 text-white/70 backdrop-blur transition hover:bg-stationBlue hover:text-white disabled:opacity-40"
      :class="toolsOn ? 'block' : 'hidden'"
      :disabled="commentBusy"
      :title="HINT_FETCH_COMMENTS"
      @mouseenter="btnOn = true"
      @mouseleave="btnOn = false"
      @click.prevent.stop="$emit('fetch-comments', video)"
    >
      <svg viewBox="0 0 24 24" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="2">
        <path d="M21 12a8 8 0 0 1-11.6 7.1L4 21l1.9-5.4A8 8 0 1 1 21 12z" stroke-linecap="round" stroke-linejoin="round" />
      </svg>
    </button>
    <button
      v-if="canManage"
      class="absolute right-2 top-2 rounded-md bg-black/60 p-1.5 text-white/70 backdrop-blur transition hover:bg-rose-500 hover:text-white"
      :class="toolsOn ? 'block' : 'hidden'"
      :title="HINT_DELETE_VIDEO"
      @mouseenter="btnOn = true"
      @mouseleave="btnOn = false"
      @click.prevent.stop="$emit('remove', video)"
    >
      <svg viewBox="0 0 24 24" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="2">
        <path d="M4 7h16M9 7V5a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2m3 0-1 13a1 1 0 0 1-1 1H8a1 1 0 0 1-1-1L6 7" stroke-linecap="round" stroke-linejoin="round" />
      </svg>
    </button>
  </div>
</template>
