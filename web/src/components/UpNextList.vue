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

// 命中区收到最严一档（站长 11-02 在调参台上选的 coverTitleText）：只有封面与「压在标题字上」两处可点，
// 统计行、底部余量、封面与文字之间那条缝全是死区。所以整行那颗大 button 撤了，
// 改成封面一个 RouterLink + 标题文字内联一个 RouterLink（内联才不给方框里的空白留热区）。
// 键盘可达性也跟着回来了：两个 <a> 本来就能 Tab 到，比一颗大 button 把整块包揽下来更说得清。

// 行里那个 UP 名 → 分站里的 UP 空间：整条 UP 行都可点（下面 onUpRow 接），名字再用 span 抢一发上色。
// 没带 mid 的行维持纯文本，不给死链。
function goUp(mid) {
  if (mid != null) router.push(`/up/${mid}`)
}

// 整条 UP 行（徽标 · 名字 · 时间）归 UP 空间：名字只得几个字宽，点偏一点就够不着（站长 11-02），
// 所以命中区给到整行，但不往标题伸。外层那颗大 button 已撤（见上面 coverTitleText 那一段）；
// 没归档 mid 的行这条退回不可点，不给死链。
function onUpRow(v, e) {
  if (v.upMid == null) return
  e.preventDefault()
  e.stopPropagation()
  goUp(v.upMid)
}

// 标题那抹粉只跟标题自己走：整行都在那个大 button 的 group 里，CSS 的 group-hover 分不出鼠标落在哪一块 ——
// 停在统计行（播数/弹幕数那一排）、封面、行内空白上都会把标题染粉，那是动画在冒充归属（站长 11-02 红点那一发）。
// 上一版只给 UP 行打了补丁，统计行还漏着；现在改成鼠标真停在标题上才染。
// 多条行共用一个变量，存当前 hover 的那条 id；UP 名的蓝另走 group/up，不掺在这里。
const titleKey = ref(null)
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
        <div class="flex w-full">
          <RouterLink :to="`/watch/${v.bvid}`" class="relative block h-[80px] w-[141px] shrink-0 overflow-hidden rounded-[6px] bg-[#c9ccd0] dark:bg-[#23262e]">
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
          </RouterLink>
          <span class="ml-[10px] min-w-0 flex-1">
            <!-- 标题：热区只给字面。外层 span 只管两行截断，真正可点的是内联那枚 RouterLink ——
                 第二行文字之后的空白、行下方的余量都不响应，光标也不变手型（染粉与手型同一个元素，同一块区域）。 -->
            <span class="line-clamp-2 text-[15px] font-medium leading-[19px] text-inkMain">
              <RouterLink
                :to="`/watch/${v.bvid}`"
                class="transition"
                :class="titleKey === v.id ? 'text-stationPink' : ''"
                @mouseenter="titleKey = v.id"
                @mouseleave="titleKey = null"
              >{{ v.title ?? v.bvid }}</RouterLink>
            </span>
            <span
              class="group/up my-[2px] flex items-center text-[13px] leading-[15px] text-inkFaint"
              :class="v.upMid != null ? 'cursor-pointer' : ''"
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
            <!-- 统计行是死区（coverTitleText 那一档）：不响应点击、不给手型，只当读数。 -->
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
        </div>
      </li>
    </ul>
  </div>
</template>
