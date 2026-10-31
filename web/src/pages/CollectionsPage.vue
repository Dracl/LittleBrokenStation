<script setup>
import { onMounted, ref } from 'vue'
import { RouterLink } from 'vue-router'
import TopBar from '../components/TopBar.vue'

// ─── 合集名录：全站公开的小抽屉陈列柜。抽屉由成员自建（个人空间或放映厅里建），这里只负责摆。
// 卡片脸面 = 最新入集那条的封面（后端 Cover 字段已算好），一条货都没有的抽屉就素面朝天。
const collections = ref(null) // null = 没拉到；[] = 确实没人建
const loading = ref(false)

async function load() {
  loading.value = true
  try {
    const res = await fetch('/api/collections')
    const data = res.ok ? await res.json().catch(() => null) : null
    collections.value = Array.isArray(data?.collections) ? data.collections : []
  } catch {
    collections.value = null
  } finally {
    loading.value = false
  }
}

onMounted(load)
</script>

<template>
  <div class="min-h-screen">
    <TopBar />

    <main class="mx-auto max-w-[1400px] px-6 py-6">
      <!-- 页头：柜签 + 标题 + 引导 -->
      <div class="mb-5 flex items-end justify-between gap-4">
        <div>
          <h1 class="flex items-center gap-2 text-[20px] font-semibold text-inkMain">
            <svg viewBox="0 0 24 24" class="h-[18px] w-[18px] text-stationPink" fill="none" stroke="currentColor" stroke-width="1.8">
              <path d="M3 7.5A2.5 2.5 0 0 1 5.5 5h3.2l1.8 2.2h8A2.5 2.5 0 0 1 21 9.7v8.8A2.5 2.5 0 0 1 18.5 21h-13A2.5 2.5 0 0 1 3 18.5v-11Z" stroke-linejoin="round" />
            </svg>
            合集
          </h1>
          <p class="mt-1 text-[12px] text-inkFaint">抽屉由成员自己建，建好就摆在这</p>
        </div>
        <RouterLink to="/me" class="shrink-0 rounded-full border border-stationPink px-3.5 py-1.5 text-[12px] text-stationPink transition hover:bg-stationPink hover:text-white">
          去我的空间建一个
        </RouterLink>
      </div>

      <!-- 拉柜中的骨架 -->
      <div v-if="loading" class="grid grid-cols-2 gap-x-4 gap-y-6 md:grid-cols-3 xl:grid-cols-5">
        <div v-for="i in 10" :key="i" class="animate-pulse">
          <div class="aspect-[16/10] rounded-xl bg-black/[0.05] dark:bg-white/[0.06]"></div>
          <div class="mt-2 h-3.5 w-3/4 rounded bg-black/[0.05] dark:bg-white/[0.06]"></div>
          <div class="mt-2 h-3 w-1/3 rounded bg-black/[0.05] dark:bg-white/[0.06]"></div>
        </div>
      </div>
      <div v-else-if="collections === null" class="rounded-xl border border-dashed border-black/10 dark:border-white/10 py-16 text-center">
        <p class="text-[13px] text-inkSub">名录没拉回来</p>
        <button class="mt-3 rounded-full border border-stationPink px-5 py-1.5 text-[13px] text-stationPink transition hover:bg-stationPink hover:text-white" @click="load">
          再拉一次
        </button>
      </div>
      <div v-else-if="collections.length === 0" class="rounded-xl border border-dashed border-black/10 dark:border-white/10 py-16 text-center">
        <svg viewBox="0 0 24 24" class="mx-auto h-10 w-10 text-inkFaint/50" fill="none" stroke="currentColor" stroke-width="1.4">
          <path d="M3 7.5A2.5 2.5 0 0 1 5.5 5h3.2l1.8 2.2h8A2.5 2.5 0 0 1 21 9.7v8.8A2.5 2.5 0 0 1 18.5 21h-13A2.5 2.5 0 0 1 3 18.5v-11Z" stroke-linejoin="round" />
        </svg>
        <p class="mt-3 text-[13px] text-inkSub">还没有人建过抽屉，第一个位置给你留着</p>
        <RouterLink to="/me" class="mt-3 inline-block rounded-full bg-stationPink px-5 py-1.5 text-[13px] text-white transition hover:opacity-90">
          建第一个抽屉
        </RouterLink>
      </div>

      <div v-else class="grid grid-cols-2 gap-x-4 gap-y-6 md:grid-cols-3 xl:grid-cols-5">
        <RouterLink
          v-for="c in collections"
          :key="c.id"
          :to="`/collection/${c.id}`"
          class="group block"
        >
          <div class="relative aspect-[16/10] overflow-hidden rounded-xl bg-[#f1f2f3] dark:bg-[#23262e]">
            <img
              v-if="c.cover"
              :src="c.cover"
              :alt="c.name"
              loading="lazy"
              referrerpolicy="no-referrer"
              class="h-full w-full object-cover transition duration-300 group-hover:scale-105"
            />
            <span v-else class="flex h-full items-center justify-center text-[13px] text-inkFaint">先占着，等货入集</span>
            <!-- 集章角标：一眼认出这是合集而不是单支视频 -->
            <span class="absolute left-1.5 top-1.5 flex items-center gap-1 rounded-md bg-gradient-to-br from-stationPink to-[#e05a86] px-1.5 py-0.5 text-[10px] font-medium text-white shadow-sm">
              <svg viewBox="0 0 24 24" class="h-2.5 w-2.5" fill="none" stroke="currentColor" stroke-width="2.6">
                <path d="M4 7.5A2.5 2.5 0 0 1 6.5 5h3l1.7 2h6.3A2.5 2.5 0 0 1 20 9.5v8A2.5 2.5 0 0 1 17.5 20h-11A2.5 2.5 0 0 1 4 17.5v-10Z" stroke-linejoin="round" />
              </svg>
              合集
            </span>
            <span class="absolute bottom-1.5 right-1.5 flex items-center gap-1 rounded bg-black/55 px-1.5 py-0.5 font-mono text-[10px] text-white/90 backdrop-blur">
              <svg viewBox="0 0 24 24" class="h-2.5 w-2.5" fill="currentColor">
                <path d="M4 6h4v14H4zM10 6h4v14h-4zM16.5 6.6l3.8 1-3.5 13.2-3.8-1z" />
              </svg>
              {{ c.itemCount }} 件
            </span>
            <!-- 悬停起帘：右下角浮一句「打开抽屉」 -->
            <span class="pointer-events-none absolute inset-0 bg-gradient-to-t from-black/45 via-transparent to-transparent opacity-0 transition duration-200 group-hover:opacity-100"></span>
          </div>
          <p class="mt-2 line-clamp-2 min-h-[40px] text-[14px] font-medium leading-snug text-inkMain transition group-hover:text-stationPink">{{ c.name }}</p>
          <p v-if="c.intro" class="mt-0.5 line-clamp-1 text-[12px] text-inkFaint">{{ c.intro }}</p>
          <p class="mt-0.5 flex items-center gap-1 text-[12px] text-inkFaint">
            <svg viewBox="0 0 24 24" class="h-3 w-3" fill="none" stroke="currentColor" stroke-width="1.8">
              <circle cx="12" cy="8.5" r="3.5" />
              <path d="M5 19.5c1.4-3.2 4-4.5 7-4.5s5.6 1.3 7 4.5" stroke-linecap="round" />
            </svg>
            {{ c.ownerName || '无名席位' }} 建
          </p>
        </RouterLink>
      </div>
    </main>
  </div>
</template>
