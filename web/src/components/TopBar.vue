<script setup>
import { onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { submitDialogOpen } from '../submitStore'

const stationOwner = ref(null) // 登录态：{ isLogin, name, face }
const route = useRoute()
const router = useRouter()
const searchText = ref(typeof route.query.q === 'string' ? route.query.q : '')

// 结果页改词/回首页清词时，搜索框跟着页面走
watch(() => route.query.q, (v) => { searchText.value = typeof v === 'string' ? v : '' })

function doSearch() {
  const term = searchText.value.trim()
  if (!term) return
  router.push({ path: '/search', query: { q: term } })
}

onMounted(async () => {
  try {
    const res = await fetch('/api/login/status')
    if (res.ok) stationOwner.value = await res.json()
  } catch {
    /* 后端没在，灰头像照常可点 */
  }
})
// 顶栏壳：版式按 B 站网页版对齐的大致轮廓，M2 对基准截图逐像素打磨
</script>

<template>
  <header class="h-10 bg-cardBg shadow-[0_1px_0_0_rgba(0,0,0,0.05)]">
    <div class="mx-auto flex h-full max-w-[1400px] items-center gap-5 px-6">
      <RouterLink to="/" class="flex shrink-0 items-center gap-2" aria-label="分站首页">
        <svg viewBox="0 0 64 64" class="h-7 w-7">
          <rect x="4" y="4" width="56" height="56" rx="14" fill="#fb7299" />
          <path d="M20 22 L28 14 M44 22 L36 14" stroke="#ffffff" stroke-width="4" stroke-linecap="round" />
          <rect x="14" y="22" width="36" height="26" rx="6" fill="#ffffff" />
          <rect x="19" y="27" width="26" height="16" rx="3" fill="#fb7299" />
        </svg>
        <span class="text-[15px] font-medium text-inkMain">小破分站</span>
      </RouterLink>

      <nav class="flex items-center gap-4 text-[13px] text-inkMain">
        <span class="cursor-not-allowed text-inkFaint" title="M2 到站">番剧</span>
        <span class="cursor-not-allowed text-inkFaint" title="M2 到站">直播</span>
        <span class="cursor-not-allowed text-inkFaint" title="M2 到站">合集</span>
      </nav>

      <div class="mx-auto flex h-8 w-full max-w-[420px] items-center gap-2 rounded-full bg-[#f1f2f3] px-3.5 transition focus-within:ring-2 focus-within:ring-stationPink/40">
        <button class="shrink-0 text-inkFaint transition hover:text-stationPink" title="搜索" @click="doSearch">
          <svg viewBox="0 0 24 24" class="h-4 w-4" fill="none" stroke="currentColor" stroke-width="2">
            <circle cx="11" cy="11" r="7" />
            <path d="m20 20-3.5-3.5" stroke-linecap="round" />
          </svg>
        </button>
        <input
          v-model="searchText"
          class="w-full bg-transparent text-[13px] outline-none placeholder:text-inkFaint"
          placeholder="搜索小破分站视频"
          @keyup.enter="doSearch"
        />
      </div>

      <div class="flex shrink-0 items-center gap-3">
        <button
          class="flex h-7 items-center rounded-full border border-stationPink px-3.5 text-xs text-stationPink transition hover:bg-stationPink hover:text-white"
          @click="submitDialogOpen = true"
        >
          投稿
        </button>
        <RouterLink
          to="/admin"
          class="group block h-8 w-8 shrink-0 overflow-hidden rounded-full ring-1 ring-transparent transition hover:ring-stationPink/60"
          :title="stationOwner?.isLogin ? `站长「${stationOwner.name}」· 进站长室` : '进站长室'"
        >
          <img
            v-if="stationOwner?.isLogin && stationOwner.face"
            :src="stationOwner.face"
            :alt="stationOwner.name"
            referrerpolicy="no-referrer"
            class="h-full w-full object-cover"
          />
          <span v-else class="block h-full w-full bg-lineSoft transition group-hover:bg-stationPink/20"></span>
        </RouterLink>
      </div>
    </div>
  </header>
</template>
