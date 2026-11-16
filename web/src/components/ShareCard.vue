<script setup>
// 私信里的分享融合气泡（C2 定案）：安利语、来源行、内容、通栏动作条一泡装下，
// 不再「气泡里套卡」。我发的右下带尾、对方来的左下带尾；点整泡跳转。
// kind='dynamic' 与 kind='video' 同副视觉：都是「来源行 + 通栏封面 + 粉三角标题 + 通栏动作条」。
// 动态那副多了报头（UP 正文）与图集两段；片卡没正文可报，就把 UP 名提到来源行当报头，时长退成 chip。
import { defaultAvatar, onAvatarError } from '../utils/avatar.js'
import { coverSrcOf } from '../utils/coverSrc'
import { colonTime, friendlyTime } from '../utils/time'
import { VIDEO_GONE_HINT } from '../utils/gone.js'

const props = defineProps({
  kind: { type: String, required: true }, // 'dynamic' | 'video'
  dyn: { type: Object, required: true },
  // 片子是否已不在架（服务端现算，只有 video 型有意义）：定格卡上的标题封面不会随删档变化，得靠它给个死活。
  videoGone: { type: Boolean, default: false },
  note: { type: String, default: '' },
  mine: { type: Boolean, default: false },
})
const emit = defineEmits(['open'])

function summary(dyn) {
  const raw = (dyn.text || '').split('\n\n转发自')[0].trim()
  return raw.length > 80 ? `${raw.slice(0, 80)}…` : raw
}
</script>

<template>
  <button
    class="group block w-full min-w-0 overflow-hidden rounded-[16px] border border-lineSoft bg-cardBg px-3 pb-0 pt-2.5 text-left transition hover:border-stationPink/50 hover:shadow-sm"
    :class="mine ? 'rounded-br-[5px]' : 'rounded-bl-[5px]'"
    @click="emit('open')"
  >
    <!-- 人话段：安利语（没写就整段缺席，来源行直接当头，泡不秃） -->
    <template v-if="note">
      <p class="whitespace-pre-wrap break-words text-[13px] leading-relaxed text-inkMain">{{ note }}</p>
      <div class="mb-2 mt-2 border-t border-dashed border-lineSoft"></div>
    </template>

    <!-- 来源行（11-17 站长丙档升格）：头像 26px 当半个报头用，名号 13.5px 半粗、pill 与时分同步步提号；
         私信与看板娘荐片卡共用本件，改一处全域同步；私信定格快照没存 upFace 的老卡，后端读口已按 bvid 现取补脸。 -->
    <div class="flex items-center gap-2">
      <img
        :src="kind === 'dynamic' ? (dyn.authorFace || defaultAvatar) : (dyn.upFace || defaultAvatar)"
        referrerpolicy="no-referrer"
        class="h-[26px] w-[26px] shrink-0 rounded-full object-cover"
        alt=""
        @error="onAvatarError"
      />
      <span class="truncate text-[13.5px] font-semibold text-inkSub">{{ kind === 'dynamic' ? (dyn.authorName || 'B站作者') : (dyn.upName || '站内放映厅') }}</span>
      <span class="shrink-0 rounded bg-stationPink/10 px-1.5 text-[11.5px] leading-[19px] text-stationPink">{{ kind === 'dynamic' ? '动态' : '馆藏' }}</span>
      <span class="shrink-0 text-[12px] text-inkFaint">· {{ kind === 'dynamic' ? friendlyTime(dyn.publishedAtUtc) : dyn.bvid }}</span>
    </div>

    <!-- 内容段：有图集走图集（单图大图/两列九宫，比例自适应不裁切）；片卡封面并排；无图动态只剩文字 -->
    <template v-if="kind === 'dynamic'">
      <!-- 报头（④″ 定案）：粉竖条装 UP 正文——话是活的，配竖条顺眼；视频标题退封面下当读法。 -->
      <div v-if="summary(dyn)" class="mt-2 flex items-start gap-[7px]">
        <span class="mt-[2px] h-[1.5em] w-[3px] shrink-0 self-start rounded-sm bg-gradient-to-b from-stationPink to-[#ff9ebb]"></span>
        <p class="text-[13.5px] font-semibold leading-[1.5] text-inkMain">{{ summary(dyn) }}</p>
      </div>
      <!-- 单图：按原图比例铺开（封顶 260），不再定高硬裁 -->
      <span v-if="dyn.pics?.length === 1" class="relative mt-2 block max-h-[260px] overflow-hidden rounded-lg bg-[#eef0f2] dark:bg-[#23262e]">
        <img :src="dyn.pics[0].local || dyn.pics[0].url" referrerpolicy="no-referrer" class="block max-h-[260px] w-full object-cover" alt="" @error="$event.target.style.display = 'none'" />
      </span>
      <!-- 多图：两列拼盘，方切 -->
      <div v-else-if="dyn.pics?.length" class="mt-2 grid grid-cols-2 gap-1">
        <img
          v-for="(pic, i) in dyn.pics.slice(0, 9)"
          :key="i"
          :src="pic.local || pic.url"
          referrerpolicy="no-referrer"
          class="aspect-square w-full rounded-md object-cover bg-[#eef0f2] dark:bg-[#23262e]"
          alt=""
          @error="$event.target.style.display = 'none'"
        />
      </div>
      <!-- 无图有视频：用馆藏/定格封面铺一张 16:9 -->
      <span v-else-if="dyn.cover && dyn.bvid" class="relative mt-2 block aspect-[16/10] overflow-hidden rounded-lg bg-[#eef0f2] dark:bg-[#23262e]">
        <img :src="coverSrcOf({ coverUrl: dyn.cover }, 450)" referrerpolicy="no-referrer" class="absolute inset-0 h-full w-full object-cover" alt="" @error="$event.target.style.display = 'none'" />
      </span>
      <!-- 标题跟封面（④″）：粉三角 + 两行截断 + 时长 chip；老消息没 videoTitle 就不加行。 -->
      <div v-if="dyn.videoTitle" class="mt-2 flex items-start gap-1.5">
        <span class="mt-[5px] h-0 w-0 shrink-0 border-y-[4.5px] border-l-[7px] border-y-transparent border-l-stationPink"></span>
        <p class="min-w-0 flex-1 text-[13px] font-semibold leading-[1.45] text-inkMain" style="display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;overflow:hidden">{{ dyn.videoTitle }}</p>
        <span v-if="dyn.videoDuration" class="mt-[2px] shrink-0 rounded bg-black/5 px-1.5 text-[10.5px] leading-4 text-inkFaint dark:bg-white/10">{{ colonTime(dyn.videoDuration) }}</span>
      </div>
    </template>
    <template v-else>
      <!-- 封面通栏 16:9（与动态卡的定格封面同尺寸）；定格时没封面就整段缺席，泡不空转。 -->
      <span v-if="dyn.cover" class="relative mt-2 block aspect-[16/10] overflow-hidden rounded-lg bg-[#eef0f2] dark:bg-[#23262e]">
        <img :src="coverSrcOf({ coverUrl: dyn.cover }, 450)" referrerpolicy="no-referrer" class="absolute inset-0 h-full w-full object-cover" alt="" @error="$event.target.style.display = 'none'" />
      </span>
      <!-- 标题跟封面：粉三角 + 两行截断 + 时长 chip，与动态卡的 videoTitle 行同规格；没定格到标题就退回 BV 号。 -->
      <div class="mt-2 flex items-start gap-1.5">
        <span class="mt-[5px] h-0 w-0 shrink-0 border-y-[4.5px] border-l-[7px] border-y-transparent border-l-stationPink"></span>
        <p class="min-w-0 flex-1 text-[13px] font-semibold leading-[1.45] text-inkMain" style="display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;overflow:hidden">{{ dyn.title || dyn.bvid }}</p>
        <span v-if="dyn.durationText" class="mt-[2px] shrink-0 rounded bg-black/5 px-1.5 text-[10.5px] leading-4 text-inkFaint dark:bg-white/10">{{ colonTime(dyn.durationText) }}</span>
      </div>
    </template>

    <!-- 动作条：贴底通栏，hover 泛粉，跟泡一体；片子下架了就改灰字、不抖粉、不带箭头 -->
    <span
      class="mt-2 -mx-3 flex items-center justify-between border-t border-lineSoft/70 bg-black/[0.02] px-3 py-1.5 text-[11.5px] font-semibold transition dark:bg-white/[0.03]"
      :class="videoGone ? 'text-inkFaint' : 'text-stationPink group-hover:bg-stationPink/5'"
    >
      <span>{{ kind === 'dynamic' ? '去橱窗看这条' : videoGone ? VIDEO_GONE_HINT : '去放映厅看' }}</span>
      <span v-if="!videoGone" aria-hidden="true">→</span>
    </span>
  </button>
</template>
