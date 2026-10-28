<script setup>
import { onMounted, ref } from 'vue'
import { RouterLink } from 'vue-router'

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
  <div class="mx-auto max-w-[1400px] px-6 py-6">
    <div class="mb-5 flex items-end justify-between gap-4">
      <h1 class="text-[20px] font-semibold text-inkMain">合集</h1>
      <p class="text-[12px] text-inkFaint">
        抽屉由成员自己建，建好就摆在这
        <RouterLink to="/me" class="ml-2 text-stationPink transition hover:opacity-80">去我的空间建一个</RouterLink>
      </p>
    </div>

    <p v-if="loading" class="py-16 text-center text-[13px] text-inkFaint">正在打开名录……</p>
    <p v-else-if="collections === null" class="py-16 text-center text-[13px] text-inkFaint">名录没拉回来，刷新再试</p>
    <p v-else-if="collections.length === 0" class="py-16 text-center text-[13px] text-inkFaint">还没有人建过抽屉，第一个位置给你留着。</p>

    <div v-else class="grid grid-cols-2 gap-x-4 gap-y-6 md:grid-cols-3 xl:grid-cols-5">
      <RouterLink
        v-for="c in collections"
        :key="c.id"
        :to="`/collection/${c.id}`"
        class="group block"
      >
        <div class="relative aspect-[16/10] overflow-hidden rounded-xl bg-[#f1f2f3]">
          <img
            v-if="c.cover"
            :src="c.cover"
            :alt="c.name"
            loading="lazy"
            class="h-full w-full object-cover transition duration-300 group-hover:scale-105"
          />
          <span v-else class="flex h-full items-center justify-center text-[13px] text-inkFaint">先占着，等货入集</span>
          <span class="absolute bottom-1.5 right-1.5 rounded bg-black/55 px-1.5 py-0.5 font-mono text-[10px] text-white/90 backdrop-blur">
            {{ c.itemCount }} 件
          </span>
        </div>
        <p class="mt-2 line-clamp-2 text-[14px] font-medium leading-snug text-inkMain transition group-hover:text-stationPink">{{ c.name }}</p>
        <p v-if="c.intro" class="mt-0.5 line-clamp-1 text-[12px] text-inkFaint">{{ c.intro }}</p>
        <p class="mt-0.5 text-[12px] text-inkFaint">{{ c.ownerName || '无名席位' }} 建</p>
      </RouterLink>
    </div>
  </div>
</template>
