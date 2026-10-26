<script setup>
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import TopBar from '../components/TopBar.vue'
import VideoCard from '../components/VideoCard.vue'

const route = useRoute()
const q = computed(() => (route.query.q ?? '').toString().trim())

const videos = ref([])
const loading = ref(false)
const sort = ref('') // '' = 最新入库，'plays' = 最多播放

async function search() {
  if (!q.value) {
    videos.value = []
    loading.value = false
    return
  }
  loading.value = true
  try {
    const res = await fetch(`/api/search?q=${encodeURIComponent(q.value)}&sort=${sort.value}`)
    videos.value = res.ok ? await res.json() : []
  } catch {
    videos.value = []
  } finally {
    loading.value = false
  }
}

onMounted(search)
watch(() => [route.query.q, sort.value], search)
</script>

<template>
  <div class="min-h-screen">
    <TopBar />

    <main class="mx-auto max-w-[1400px] px-6 py-6">
      <!-- 结果头部 -->
      <div class="mb-4 flex items-baseline gap-4">
        <h1 class="text-base font-semibold text-inkMain">
          <template v-if="q">「<span class="text-stationPink">{{ q }}</span>」的搜索结果</template>
          <template v-else>站内搜索</template>
          <span v-if="!loading && q" class="ml-2 text-sm font-normal text-inkFaint">共 {{ videos.length }} 件</span>
        </h1>
        <div class="flex items-center gap-3 text-sm">
          <button
            class="transition"
            :class="sort === '' ? 'font-semibold text-stationPink' : 'text-inkSub hover:text-stationPink'"
            @click="sort = ''"
          >
            最新入库
          </button>
          <span class="text-inkFaint">|</span>
          <button
            class="transition"
            :class="sort === 'plays' ? 'font-semibold text-stationPink' : 'text-inkSub hover:text-stationPink'"
            @click="sort = 'plays'"
          >
            最多播放
          </button>
        </div>
      </div>

      <!-- 引导态：顶栏还没输入 -->
      <div v-if="!q" class="rounded-xl border border-dashed border-white/10 py-16 text-center">
        <p class="text-sm text-inkSub">在顶栏输入关键词，回车开搜</p>
        <p class="mt-2 text-xs text-inkFaint">标题、UP 主、BV 号、分区都能命中；空格分词取交集</p>
      </div>

      <!-- 骨架格：搜索中保持版式不跳动 -->
      <div v-else-if="loading" class="grid grid-cols-2 gap-4 md:grid-cols-3 xl:grid-cols-5">
        <div v-for="i in 10" :key="i" class="animate-pulse">
          <div class="aspect-[16/10] w-full rounded-xl bg-white/5"></div>
          <div class="mt-2 h-4 w-full rounded bg-white/5"></div>
          <div class="mt-1.5 h-3 w-2/3 rounded bg-white/5"></div>
        </div>
      </div>

      <!-- 空手而归 -->
      <div v-else-if="videos.length === 0" class="rounded-xl border border-dashed border-white/10 py-16 text-center">
        <p class="text-sm text-inkSub">分站没有找到与「{{ q }}」相关的内容</p>
        <p class="mt-2 text-xs text-inkFaint">试试更短的关键词、UP 主名字或 BV 号；只搜已到站的视频</p>
      </div>

      <!-- 结果网格 -->
      <div v-else class="grid grid-cols-2 gap-4 md:grid-cols-3 xl:grid-cols-5">
        <RouterLink v-for="video in videos" :key="video.id" :to="`/watch/${video.bvid}`" class="group block">
          <VideoCard :video="video" :keyword="q" />
        </RouterLink>
      </div>
    </main>
  </div>
</template>
