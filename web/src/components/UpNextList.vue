<script setup>
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { colonTime, publishedDate } from '../utils/time'
import UpIcon from './UpIcon.vue'
import PlayCountIcon from './PlayCountIcon.vue'
import DanmakuCountIcon from './DanmakuCountIcon.vue'

// 接下来播放：分站自己的推荐流（同 UP 主优先 + 层内随机抽），卡形仿 B 站「封面左 + 信息右」。
const props = defineProps({
  items: { type: Array, default: () => [] },
  autoplay: { type: Boolean, default: false },
})
const emit = defineEmits(['update:autoplay'])

const router = useRouter()

// 显的是 B 站投稿时间，取数口径与首页卡阵共用（utils/time.js 的 publishedDate）：
// createdAt 是入站时间，批量下载后满屏同一天，那是台账概念，不该顶替投稿时间上桌；
// 两个源都拿不到就整段不显（连「· 」分隔符一起掉，不留个孤零零的点）。
function pubDate(v) {
  const d = publishedDate(v)
  return d ? d.toLocaleDateString('zh-CN', { year: 'numeric', month: '2-digit', day: '2-digit' }) : ''
}

function go(bvid) {
  if (bvid) router.push(`/watch/${bvid}`)
}

// 行里那个 UP 名 → 分站里的 UP 空间（整行是个大 button，所以只能拿 span 抢这一发）。
// 没带 mid 的行维持纯文本，不给死链。
function goUp(mid) {
  if (mid != null) router.push(`/up/${mid}`)
}

// 整行（UP 徽标 · 名字 · 时间）归 UP 空间：外层是个大 button（点整行跳放映厅），
// 名字只得几个字宽，点偏一点就被它接走（站长 11-02）。命中区只取这一行本身，不往标题伸。
// 没归档 mid 的行原样交给外层 button（照旧跳放映厅）。
function onUpRow(v, e) {
  if (v.upMid == null) return
  e.preventDefault()
  e.stopPropagation()
  goUp(v.upMid)
}

// 停在 UP 行上时不染标题：整行都在那个大 button 的 group 里，CSS 的 group-hover 分不出鼠标落在哪一块，
// 与两张主页卡同一套路（站长 11-02：动画不能冒充归属）。多条行共用一个变量，存当前 hover 的那条 id。
const hoverKey = ref(null)
</script>

<template>
  <div v-if="items.length" class="rounded-xl bg-cardBg p-3">
    <div class="mb-2 flex items-center justify-between px-1">
      <p class="text-sm font-medium text-inkMain">接下来播放</p>
      <button
        class="flex items-center gap-1.5 text-xs text-inkFaint transition hover:text-inkMain"
        role="switch"
        :aria-checked="autoplay"
        :title="autoplay ? '播完自动接接下来播放第一条' : '已关：播完停在本页'"
        @click="emit('update:autoplay', !autoplay)"
      >
        自动连播
        <span
          class="relative inline-block h-[18px] w-[34px] rounded-full transition"
          :class="autoplay ? 'bg-stationPink' : 'bg-[#dcdee0] dark:bg-white/10'"
        >
          <span
            class="absolute top-[2px] h-[14px] w-[14px] rounded-full bg-white shadow transition-all"
            :style="{ left: autoplay ? '18px' : '2px' }"
          ></span>
        </span>
      </button>
    </div>
    <ul class="space-y-3">
      <li v-for="v in items" :key="v.id">
        <button class="group flex w-full text-left" @click="go(v.bvid)">
          <span class="relative block h-[80px] w-[141px] shrink-0 overflow-hidden rounded-[6px] bg-[#c9ccd0] dark:bg-[#23262e]">
            <img
              v-if="v.coverUrl"
              :src="v.coverUrl"
              :alt="v.title ?? ''"
              loading="lazy"
              referrerpolicy="no-referrer"
              class="h-full w-full object-cover"
            />
            <span class="pointer-events-none absolute inset-0 bg-black/[.07]"></span>
            <span
              v-if="v.durationText"
              class="absolute bottom-[6px] right-[6px] rounded-[2px] bg-black/40 px-1 text-[13px] leading-[20px] text-white tabular-nums"
            >{{ colonTime(v.durationText) }}</span>
          </span>
          <span class="ml-[10px] min-w-0 flex-1">
            <span class="line-clamp-2 text-[15px] font-medium leading-[19px] text-inkMain transition" :class="hoverKey === v.id ? '' : 'group-hover:text-stationPink'">{{ v.title ?? v.bvid }}</span>
            <span
              class="group/up my-[2px] flex items-center text-[13px] leading-[15px] text-inkFaint"
              :class="v.upMid != null ? 'cursor-pointer' : ''"
              @mouseenter="hoverKey = v.id"
              @mouseleave="hoverKey = null"
              @click="onUpRow(v, $event)"
            >
              <!-- UP 徽标：与两张主页卡同一枚官方描边款（站长 11-02 点名换掉自绘的那版）；官方口径 13px 字旁 17×17。 -->
              <UpIcon class="mr-1 h-[17px] w-[17px] shrink-0" />
              <span class="truncate">
                <span
                  v-if="v.upMid != null"
                  class="cursor-pointer transition hover:text-stationBlue group-hover/up:text-stationBlue"
                  title="这位在分站的空间"
                  @click.stop.prevent="goUp(v.upMid)"
                >{{ v.upName }}</span>
                <template v-else>{{ v.upName ?? '—' }}</template>
                <template v-if="pubDate(v)"> · {{ pubDate(v) }}</template>
              </span>
            </span>
            <span class="flex items-center text-[13px] leading-[15px] text-inkFaint">
              <!-- 播放量图标：官方圆角框 + 三角，与首页卡同一枚（站长 11-02 点名换掉自绘版），尺寸照官方 18×18。 -->
              <PlayCountIcon class="mr-1 h-[18px] w-[18px] shrink-0" />
              <span class="tabular-nums">{{ v.viewCountText ?? '—' }}</span>
              <span v-if="v.danmakuCount != null" class="ml-2 flex items-center">
                <!-- 弹幕量图标：官方圆角框 + 四条弹幕胶囊，同一批扒回来的。 -->
                <DanmakuCountIcon class="mr-1 h-[18px] w-[18px] shrink-0" />
                <span class="tabular-nums">{{ v.danmakuCount }}</span>
              </span>
            </span>
          </span>
        </button>
      </li>
    </ul>
  </div>
</template>
