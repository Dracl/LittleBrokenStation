<script setup>
// 到站通报卡（11-03 路线图 🔭 首件「到站通知」的信箱侧脸面）：
// 订阅新货落站，播报员往信箱播一条 kind='arrival' 的信，payload_json 里定格着这批货的卡面快照；
// 这里把它排成一版正经的到货单——一件一行：左 16:10 封面缩略、右标题两行截断、meta 行「UP · 时长」，
// 整行点击直落放映厅。站长点名「不要一堆字混着」：纯文本那份留在 body 里当兜底（载荷认不出时才摊它）。
//
// 快照语义与 ShareCard 的 dyn_json 同族：卡面是播送那一刻定格的，读路径不现查（顶栏 30 秒轮询不陪跑）；
// 日后删了片，点卡由观看页现成的「查无此片」分诊接住，通报是到站那一刻的记事，不随删档改口。
import { coverSrcOf } from '../utils/coverSrc'
import { friendlyTime } from '../utils/time'

const props = defineProps({
  // 载荷数组：[{ bvid, title, cover, coverUrl, coverThumbs, up, duration }]（服务端播送/寄出那刻定格）
  cards: { type: Array, required: true },
  // 这批货的到站时刻（信件落库时间）：报头只报一次，行内不再各报各的
  arrivedAt: { type: String, default: '' },
  // 报头面孔：arrivals＝到货播报（粉徽＋件数＋巡站语）；receipt＝入档回执（绿徽，与信箱「入档」徽同色族，站长 11-03「这条也做个排版」）
  mode: { type: String, default: 'arrivals' },
})

// 缩略取数走全站同一张嘴 coverSrcOf：档位件 → 本地原图 → 热链，缺哪级降哪级（与馆藏壁卡同族口径）。
// 显示宽 ~132 CSS 像素，DPR 2 要 264 → 递 132 让它自己挑 352 档。
function thumbOf(card) {
  return coverSrcOf({ bvid: card.bvid, localCover: card.cover, coverUrl: card.coverUrl, coverThumbs: card.coverThumbs }, 132)
}

function titleOf(card) {
  const t = (card.title || '').trim()
  return t || card.bvid
}
</script>

<template>
  <div class="overflow-hidden rounded-xl border border-lineSoft bg-cardBg">
    <!-- 报头：面孔随 mode 换。到货报「N 件＋巡站语」；回执报「已入档＋上架语」（绿徽与列表行的入档徽同色族）。时刻只在报头报一次。 -->
    <div class="flex items-center gap-2 border-b border-lineSoft bg-stationPink/[0.04] px-4 py-2.5">
      <span
        v-if="mode === 'receipt'"
        class="shrink-0 rounded-md bg-emerald-500/10 px-1.5 py-0.5 text-[11px] font-medium text-emerald-600"
      >已入档</span>
      <span
        v-else
        class="shrink-0 rounded-md bg-stationPink/10 px-1.5 py-0.5 text-[11px] font-medium text-stationPink"
      >到货 {{ cards.length }} 件</span>
      <span v-if="mode === 'receipt'" class="min-w-0 flex-1 truncate text-[11px] text-inkFaint">正片、弹幕、字幕与封面都已上架，随时可放映</span>
      <span v-else-if="arrivedAt" class="min-w-0 flex-1 truncate text-[11px] text-inkFaint">{{ friendlyTime(arrivedAt) }}落站 · 订阅巡站的新货</span>
      <span v-else class="min-w-0 flex-1 text-[11px] text-inkFaint">订阅巡站的新货</span>
    </div>

    <!-- 货单：一件一行，行与行细线分隔；整行是个链接，hover 整行泛粉底（与信箱列表行同款手感） -->
    <ul>
      <li v-for="(card, i) in cards" :key="card.bvid" :class="i > 0 ? 'border-t border-lineSoft/60' : ''">
        <RouterLink :to="`/watch/${card.bvid}`" class="group flex items-stretch gap-3 px-4 py-3 transition hover:bg-stationPink/5">
          <!-- 封面：16:10 定宽，与首页卡阵同比例；没料时整块缺席（文字顶上来，不留灰坑） -->
          <span v-if="thumbOf(card)" class="relative h-[62px] w-[99px] shrink-0 overflow-hidden rounded-lg bg-[#eef0f2] dark:bg-[#23262e]">
            <img :src="thumbOf(card)" referrerpolicy="no-referrer" loading="lazy" class="absolute inset-0 h-full w-full object-cover" alt="" @error="$event.target.style.display = 'none'" />
            <span v-if="card.duration" class="absolute bottom-1 right-1 rounded bg-black/60 px-1 text-[10px] leading-[15px] text-white">{{ card.duration }}</span>
          </span>
          <!-- 右栏：标题（两行截断，粉三角当家）+ meta 行（UP · 时长，压灰让位） -->
          <span class="flex min-w-0 flex-1 flex-col justify-center gap-1">
            <span class="flex items-start gap-1.5">
              <span class="mt-[5px] h-0 w-0 shrink-0 border-y-[4.5px] border-l-[7px] border-y-transparent border-l-stationPink"></span>
              <span class="min-w-0 flex-1 text-[13px] font-semibold leading-[1.45] text-inkMain" style="display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;overflow:hidden">{{ titleOf(card) }}</span>
            </span>
            <span class="flex min-w-0 items-center gap-1.5 text-[11px] text-inkFaint">
              <span v-if="card.up" class="truncate">{{ card.up }}</span>
              <span v-if="card.up && card.duration" class="shrink-0">·</span>
              <span v-if="card.duration" class="shrink-0">{{ card.duration }}</span>
              <span v-if="!card.up && !card.duration" class="truncate">{{ card.bvid }}</span>
            </span>
          </span>
          <span class="mt-auto mb-auto shrink-0 text-[13px] text-inkFaint transition group-hover:text-stationPink" aria-hidden="true">→</span>
        </RouterLink>
      </li>
    </ul>
  </div>
</template>
