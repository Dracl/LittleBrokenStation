<script setup>
import { computed, onMounted, ref, watch } from 'vue'
import { RouterLink, useRoute } from 'vue-router'
import TopBar from '../components/TopBar.vue'
import VideoCard from '../components/VideoCard.vue'
import { defaultAvatar } from '../utils/avatar.js'
import { fmtFans } from '../utils/fans.js'

// ─── UP 空间：这位 UP 在【本站】存了多少片。放映厅名片上那颗蓝钮进来。
// 两条口径都在后端，这里不挑：只列已到货的行（没缓存的不给人看）、别人的私有稿等于不存在。
// 一口拿全（页头名片 + 卡阵），不分页 —— 分站总量还小，一次给完少一条错误分支。
const route = useRoute()

const up = ref(null) // 站内归档的名片快照；null = 分站从没收过这位（或档案四字全空）
const items = ref([])
const loading = ref(false)
const failed = ref(false)
const faceFailed = ref(false)

const mid = computed(() => route.params.mid)
const name = computed(() => up.value?.name || `UP 主 ${mid.value}`)
const faceSrc = computed(() => (up.value?.face && !faceFailed.value ? up.value.face : defaultAvatar))
const spaceUrl = computed(() => `https://space.bilibili.com/${mid.value}`)
function onFaceError() {
  faceFailed.value = true
}

async function load() {
  loading.value = true
  failed.value = false
  faceFailed.value = false
  try {
    const res = await fetch(`/api/up/${mid.value}/videos`)
    if (!res.ok) {
      failed.value = true
      up.value = null
      items.value = []
      return
    }
    const data = await res.json().catch(() => null)
    up.value = data?.up ?? null
    items.value = data?.items ?? []
  } catch {
    failed.value = true
    up.value = null
    items.value = []
  } finally {
    loading.value = false
  }
}

onMounted(load)
// 从别处换 mid 进来（书签、回退）也要重拉，不能顶着上一位的脸与货
watch(mid, load)
</script>

<template>
  <div class="min-h-screen">
    <TopBar />

    <main class="mx-auto max-w-[1400px] px-6 py-6">
      <!-- 骨架：与合集详情同款呼吸块，不摆转圈 -->
      <div v-if="loading" class="animate-pulse rounded-2xl bg-black/[0.05] dark:bg-white/[0.06] p-6">
        <div class="h-6 w-1/3 rounded bg-black/[0.06] dark:bg-white/[0.08]"></div>
        <div class="mt-3 h-3.5 w-1/2 rounded bg-black/[0.06] dark:bg-white/[0.08]"></div>
      </div>

      <!-- 后端没应答 / 编号不是数字（路由 :long 约束不匹配会 404）：一句话 + 一颗重试 -->
      <div v-else-if="failed" class="rounded-xl border border-dashed border-black/10 dark:border-white/10 py-16 text-center">
        <svg viewBox="0 0 24 24" class="mx-auto h-10 w-10 text-inkFaint/50" fill="none" stroke="currentColor" stroke-width="1.4">
          <circle cx="12" cy="12" r="9" />
          <path d="M12 7.5v5M12 16h.01" stroke-linecap="round" />
        </svg>
        <p class="mt-3 text-[15px] text-inkMain">这一页没拉起来，可能分站后端没应答，也可能链接里的编号不对</p>
        <button class="mt-3 inline-block rounded-full border border-stationBlue px-5 py-1.5 text-[13px] text-stationBlue transition hover:bg-stationBlue hover:text-white" @click="load">
          再拉一次
        </button>
      </div>

      <template v-else>
        <!-- 页头：蓝调柜面（与名片上那颗蓝钮同色），头像 / 名字 / 签名 / 三枚胶囊，右侧留一个去 B 站的出口 -->
        <div class="rounded-2xl bg-gradient-to-br from-stationBlue/12 via-stationBlue/[0.05] to-transparent p-5 ring-1 ring-stationBlue/10">
          <div class="flex flex-wrap items-start justify-between gap-4">
            <div class="flex min-w-0 gap-3.5">
              <img
                :src="faceSrc"
                :alt="name"
                loading="lazy"
                referrerpolicy="no-referrer"
                class="h-14 w-14 shrink-0 rounded-full bg-white/5 object-cover ring-2 ring-stationBlue/20"
                @error="onFaceError"
              />
              <div class="min-w-0">
                <h1 class="flex items-center gap-2 text-[20px] font-semibold text-inkMain">
                  <span class="min-w-0 truncate">{{ name }}</span>
                  <span v-if="up" class="shrink-0 rounded-full bg-cardBg px-2 py-0.5 text-[11px] font-normal text-inkFaint ring-1 ring-black/[0.06] dark:ring-white/10">
                    站内归档
                  </span>
                </h1>
                <p class="mt-1 max-w-[720px] text-[13px] leading-relaxed text-inkSub">{{ up?.sign || '这个 UP 主还没有签名' }}</p>
                <div class="mt-2.5 flex flex-wrap items-center gap-2 text-[12px] text-inkFaint">
                  <span class="flex items-center gap-1 rounded-full bg-cardBg px-2.5 py-1 ring-1 ring-black/[0.06] dark:ring-white/10">
                    <svg viewBox="0 0 24 24" class="h-3 w-3" fill="none" stroke="currentColor" stroke-width="1.8">
                      <rect x="3" y="5" width="18" height="14" rx="2.5" />
                      <path d="M10 9.5l5 2.5-5 2.5z" fill="currentColor" stroke="none" />
                    </svg>
                    在库 {{ items.length }} 件
                  </span>
                  <span v-if="up?.fans != null" class="flex items-center gap-1 rounded-full bg-cardBg px-2.5 py-1 ring-1 ring-black/[0.06] dark:ring-white/10">
                    <svg viewBox="0 0 24 24" class="h-3 w-3" fill="none" stroke="currentColor" stroke-width="1.8">
                      <circle cx="12" cy="8.5" r="3.5" />
                      <path d="M5 19.5c1.4-3.2 4-4.5 7-4.5s5.6 1.3 7 4.5" stroke-linecap="round" />
                    </svg>
                    粉丝 {{ fmtFans(up.fans) }}
                  </span>
                  <span class="rounded-full bg-cardBg px-2.5 py-1 font-mono ring-1 ring-black/[0.06] dark:ring-white/10">UID {{ mid }}</span>
                </div>
              </div>
            </div>
            <!-- 蓝钮把人从放映厅领进来，这里就得留一条回 B 站的路，否则原来点名字的那一下没了去处 -->
            <a
              :href="spaceUrl"
              target="_blank"
              rel="noreferrer"
              class="shrink-0 rounded-lg border border-lineSoft bg-cardBg px-3 py-1.5 text-[12px] text-inkSub transition hover:border-stationBlue/50 hover:text-stationBlue"
            >
              去 B 站空间
            </a>
          </div>
        </div>

        <!-- 卡阵：与合集详情同形的网格，卡面一根毛不改 -->
        <div v-if="items.length" class="mt-5 grid grid-cols-2 gap-x-4 gap-y-[38px] md:grid-cols-3 xl:grid-cols-5">
          <RouterLink v-for="video in items" :key="video.bvid" :to="`/watch/${video.bvid}`" class="group block">
            <VideoCard :video="video" />
          </RouterLink>
        </div>

        <!-- 空态：分站还没收过这位。不摆 0 张空卡，也不假装这人不存在 —— 页头那张脸还在，说的是「这里没货」 -->
        <div v-else class="mt-5 rounded-xl border border-dashed border-black/10 dark:border-white/10 py-16 text-center">
          <svg viewBox="0 0 24 24" class="mx-auto h-10 w-10 text-inkFaint/50" fill="none" stroke="currentColor" stroke-width="1.4">
            <rect x="3" y="4" width="18" height="16" rx="3" />
            <path d="M3 9h18M8 14h8" stroke-linecap="round" />
          </svg>
          <p class="mt-3 text-[13px] text-inkSub">分站还没收过这位的稿件</p>
          <p class="mt-1 text-[12px] text-inkFaint">想收进来：放映厅投稿，或站长室「UP 主整搬」连货架一起搬</p>
          <a
            :href="spaceUrl"
            target="_blank"
            rel="noreferrer"
            class="mt-3 inline-block rounded-full border border-stationBlue px-5 py-1.5 text-[13px] text-stationBlue transition hover:bg-stationBlue hover:text-white"
          >
            去 B 站看看这位的空间
          </a>
        </div>
      </template>
    </main>
  </div>
</template>
