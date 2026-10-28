<script setup>
import { computed, onMounted, ref, watch } from 'vue'
import TopBar from '../components/TopBar.vue'
import { member } from '../memberStore'
import { coverThumb } from '../utils/cover'

// 分站周报：一张卡的工夫看完这一周——新货、弹幕增量、全站家底，外加 Top5 最多人看。
// 数据全走 /api/weekly（成员登录才给看）；周切换 0=本周至今、1..4=往前数完整周，最远四周一页纸。
const week = ref(0)
const data = ref(null)
const loading = ref(false)
const error = ref('')

const weekCaption = computed(() => (week.value === 0 ? '本周 · 截至现在' : `往前 ${week.value} 周 · 完整一周`))
const isEmpty = computed(
  () => data.value && !data.value.news?.count && !data.value.hottest?.length && !data.value.danmakuAdded,
)

// 封面口径与首页卡阵同源：后端给的是 localCover 优先的成品（/media/… 原图或热链），热链才折 B 站图床缩图。
function coverSrc(url) {
  if (!url) return ''
  return /^https?:\/\//.test(url) ? coverThumb(url, 480) : url
}

function humanBytes(n) {
  if (n == null) return '—'
  if (n < 1024) return `${n} B`
  const units = ['KB', 'MB', 'GB', 'TB']
  let v = n / 1024
  let i = 0
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024
    i += 1
  }
  return `${v >= 100 ? Math.round(v) : v.toFixed(1)} ${units[i]}`
}

async function load() {
  loading.value = true
  error.value = ''
  try {
    const res = await fetch(`/api/weekly?week=${week.value}`)
    if (res.ok) {
      data.value = await res.json()
    } else {
      data.value = null
      const body = await res.json().catch(() => null)
      error.value = body?.message || `周报没拉到（HTTP ${res.status}）`
    }
  } catch {
    data.value = null
    error.value = '分站没搭腔，周报没拉到'
  } finally {
    loading.value = false
  }
}

onMounted(load)
watch(week, load)

// 上一周 = 往历史里再翻一周（最远 4 周前）；下一周 = 回到更近的一周，本周就是头一档，不许再往后。
function goPrev() {
  if (week.value < 4) week.value += 1
}
function goNext() {
  if (week.value > 0) week.value -= 1
}
</script>

<template>
  <div class="min-h-screen bg-pageBg">
    <TopBar />

    <main class="mx-auto max-w-[1080px] px-6 py-6">
      <!-- 页眉：周号 + 上一周/下一周切换 -->
      <div class="mb-4 flex flex-wrap items-center justify-between gap-3 rounded-2xl border border-lineSoft bg-cardBg bg-gradient-to-r from-stationPink/10 via-stationPink/5 to-transparent px-5 py-4">
        <div>
          <h1 class="text-[17px] font-semibold text-inkMain">
            分站周报<span v-if="data" class="ml-2 text-stationPink">{{ data.weekLabel }}</span>
          </h1>
          <p class="mt-0.5 text-xs text-inkFaint">{{ weekCaption }} · 数都从台账现推，掌柜的不赊账</p>
        </div>
        <div class="flex shrink-0 items-center gap-2">
          <button
            class="rounded-full border px-3.5 py-1.5 text-sm transition"
            :class="week < 4 ? 'border-lineSoft text-inkSub hover:border-stationPink hover:text-stationPink' : 'cursor-not-allowed border-lineSoft/60 text-inkFaint'"
            :disabled="week >= 4"
            title="往前翻一周（最远 4 周）"
            @click="goPrev"
          >
            上一周
          </button>
          <button
            class="rounded-full border px-3.5 py-1.5 text-sm transition"
            :class="week > 0 ? 'border-lineSoft text-inkSub hover:border-stationPink hover:text-stationPink' : 'cursor-not-allowed border-lineSoft/60 text-inkFaint'"
            :disabled="week <= 0"
            title="回到更近的一周（本周就是头一档）"
            @click="goNext"
          >
            下一周
          </button>
        </div>
      </div>

      <!-- 加载中 / 拉胯了 -->
      <div v-if="loading" class="grid grid-cols-1 gap-4 sm:grid-cols-3">
        <div v-for="i in 3" :key="i" class="h-24 animate-pulse rounded-2xl bg-[#f1f2f3]"></div>
      </div>
      <div v-else-if="error" class="rounded-2xl border border-dashed border-lineSoft bg-cardBg px-6 py-14 text-center">
        <p class="text-sm text-inkSub">{{ error }}</p>
        <p v-if="!member" class="mt-2 text-xs text-inkFaint">周报是分站成员待遇——右上角点个码，进站就是一起守站的人。</p>
      </div>

      <template v-else-if="data">
        <!-- 三行大数：新货 / 弹幕增量 / 全站家底 -->
        <div class="grid grid-cols-1 gap-4 sm:grid-cols-3">
          <div class="rounded-2xl border border-lineSoft bg-cardBg px-5 py-4">
            <p class="text-xs text-inkFaint">{{ week === 0 ? '本周' : data.weekLabel }}新货</p>
            <p class="mt-1 text-[26px] font-semibold leading-8 text-inkMain">
              {{ data.news.count }}<span class="ml-1 text-sm font-normal text-inkFaint">件</span>
            </p>
          </div>
          <div class="rounded-2xl border border-lineSoft bg-cardBg px-5 py-4">
            <p class="text-xs text-inkFaint">站内弹幕新增</p>
            <p class="mt-1 text-[26px] font-semibold leading-8 text-stationPink">
              +{{ data.danmakuAdded }}<span class="ml-1 text-sm font-normal text-inkFaint">条</span>
            </p>
          </div>
          <div class="rounded-2xl border border-lineSoft bg-cardBg px-5 py-4">
            <p class="text-xs text-inkFaint">全站家底</p>
            <p class="mt-1 text-[26px] font-semibold leading-8 text-inkMain">
              {{ data.totals.videos }}<span class="ml-1 text-sm font-normal text-inkFaint">件稿件</span>
            </p>
            <p class="mt-0.5 text-[11px] text-inkFaint">
              {{ data.totals.members }} 位成员 · {{ data.totals.danmaku }} 条弹幕 · 库房 {{ humanBytes(data.totals.bytes) }}
            </p>
          </div>
        </div>

        <!-- 整周空账：一段话说清楚，不摆烂 -->
        <div v-if="isEmpty" class="mt-4 rounded-2xl border border-dashed border-lineSoft bg-cardBg px-6 py-14 text-center">
          <p class="text-sm text-inkSub">这一周分站安静得能听见灰落地——没上新、没弹幕、连个围观的都没有。</p>
          <p class="mt-2 text-xs text-inkFaint">别灰心，投一稿就能把周报盘活。下一周见真章。</p>
        </div>

        <template v-else>
          <!-- 本周新货：横排卡 -->
          <section class="mt-4 overflow-hidden rounded-2xl border border-lineSoft bg-cardBg">
            <div class="flex items-center justify-between border-b border-lineSoft px-5 py-3">
              <h2 class="text-[15px] font-semibold text-inkMain">本周新货</h2>
              <span class="text-xs text-inkFaint">按到站先后摆，最新在头</span>
            </div>
            <div v-if="data.news.items.length" class="flex gap-3 overflow-x-auto p-4">
              <RouterLink
                v-for="item in data.news.items"
                :key="item.bvid"
                :to="`/watch/${item.bvid}`"
                class="group w-44 shrink-0"
              >
                <div class="relative aspect-[16/10] w-full overflow-hidden rounded-xl bg-[#f1f2f3]">
                  <img
                    v-if="coverSrc(item.coverUrl)"
                    :src="coverSrc(item.coverUrl)"
                    :alt="item.title || item.bvid"
                    loading="lazy"
                    decoding="async"
                    referrerpolicy="no-referrer"
                    class="h-full w-full object-cover transition duration-300 group-hover:scale-[1.03]"
                  />
                  <span v-else class="flex h-full w-full items-center justify-center text-xs tracking-widest text-inkFaint">{{ item.bvid }}</span>
                </div>
                <p class="mt-2 line-clamp-2 text-[13px] font-medium leading-[19px] text-inkMain transition group-hover:text-stationPink" :title="item.title || item.bvid">
                  {{ item.title || item.bvid }}
                </p>
                <p class="mt-0.5 truncate text-[11px] text-inkFaint">{{ item.bvid }}</p>
              </RouterLink>
            </div>
            <p v-else class="px-6 py-10 text-center text-sm text-inkFaint">这一周没一件新货入站，货架擦得再亮也没用——催更去。</p>
          </section>

          <!-- 最多人看 Top5 -->
          <section class="mt-4 overflow-hidden rounded-2xl border border-lineSoft bg-cardBg">
            <div class="flex items-center justify-between border-b border-lineSoft px-5 py-3">
              <h2 class="text-[15px] font-semibold text-inkMain">最多人看</h2>
              <span class="text-xs text-inkFaint">观众按人头去重，访客凑不了数</span>
            </div>
            <ol v-if="data.hottest.length">
              <li v-for="(h, i) in data.hottest" :key="h.bvid" class="border-b border-lineSoft/60 last:border-b-0">
                <RouterLink :to="`/watch/${h.bvid}`" class="flex items-center gap-3 px-4 py-3 transition hover:bg-stationPink/5">
                  <span
                    class="w-7 shrink-0 text-center text-[15px] font-semibold"
                    :class="i < 3 ? 'text-stationPink' : 'text-inkFaint'"
                  >{{ i + 1 }}</span>
                  <div class="relative h-16 w-28 shrink-0 overflow-hidden rounded-lg bg-[#f1f2f3]">
                    <img
                      v-if="coverSrc(h.coverUrl)"
                      :src="coverSrc(h.coverUrl)"
                      :alt="h.title || h.bvid"
                      loading="lazy"
                      decoding="async"
                      referrerpolicy="no-referrer"
                      class="h-full w-full object-cover"
                    />
                    <span v-else class="flex h-full w-full items-center justify-center text-[10px] tracking-widest text-inkFaint">{{ h.bvid }}</span>
                  </div>
                  <span class="min-w-0 flex-1">
                    <span class="block truncate text-[14px] font-medium text-inkMain" :title="h.title || h.bvid">{{ h.title || h.bvid }}</span>
                    <span class="mt-0.5 block truncate text-[11px] text-inkFaint">{{ h.bvid }}</span>
                  </span>
                  <span class="shrink-0 text-[13px] text-inkSub"><span class="font-semibold text-stationPink">{{ h.viewers }}</span> 位观众</span>
                </RouterLink>
              </li>
            </ol>
            <p v-else class="px-6 py-10 text-center text-sm text-inkFaint">这一周没人开映——放映厅的幕布都落灰了。</p>
          </section>
        </template>
      </template>
    </main>
  </div>
</template>
