<script setup>
import { computed, onMounted, ref } from 'vue'
import { RouterLink, useRoute, useRouter } from 'vue-router'
import TopBar from '../components/TopBar.vue'
import VideoCard from '../components/VideoCard.vue'
import { member } from '../memberStore'
import { requestConfirm } from '../confirmStore'

// ─── 抽屉正面：公开可逛，货主（或站长）在卡角有 ✕ 能出货，页头有整柜拆除钮。
// 身份口径与服务端一致：owner_member_id === member.id 或站长席位才可动刀。
const route = useRoute()
const router = useRouter()

const detail = ref(null) // null = 未拉/查无；notfound 单独记
const notfound = ref(false)
const loading = ref(false)
const notice = ref(null)
const busy = ref(false)

const canManage = computed(() => {
  if (!member.value || !detail.value) return false
  return member.value.role === 'owner' || member.value.id === detail.value.ownerMemberId
})

function fmtDate(s) {
  if (!s) return ''
  const d = new Date(s)
  if (Number.isNaN(d.getTime())) return ''
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

async function load() {
  loading.value = true
  notfound.value = false
  try {
    const res = await fetch(`/api/collections/${route.params.id}`)
    detail.value = res.ok ? await res.json().catch(() => null) : null
    notfound.value = !detail.value
  } catch {
    notfound.value = true
  } finally {
    loading.value = false
  }
}

async function removeItem(video) {
  if (busy.value) return
  // 原生 confirm 顶着「localhost 显示」的浏览器外壳，跟站内装裱两套皮，换成投稿窗同款那张
  if (
    !(await requestConfirm({
      title: '请出这个抽屉',
      sub: '只是从抽屉里摸出去，片子本身一根毛都不会掉',
      message: `把《${video.title || video.bvid}》从抽屉「${detail.value?.name}」里摸出去？`,
      confirmText: '请出去',
      cancelText: '再放放',
      icon: 'out',
    }))
  ) {
    return
  }
  busy.value = true
  notice.value = null
  try {
    const res = await fetch(`/api/member/collections/${route.params.id}/items/${encodeURIComponent(video.bvid)}`, { method: 'DELETE' })
    const data = await res.json().catch(() => null)
    notice.value = { kind: res.ok ? 'ok' : 'err', text: data?.message || (res.ok ? '已请出' : '请不出去') }
    if (res.ok) await load()
  } catch {
    notice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    busy.value = false
  }
}

async function dissolve() {
  if (busy.value) return
  if (
    !(await requestConfirm({
      title: '拆除整个抽屉',
      sub: '货回货架不受损伤，但抽屉本身连同名字、简介一起消失',
      message: `拆掉抽屉「${detail.value?.name}」？${detail.value?.items?.length ?? 0} 件货会回到货架，想收回来得重建一座抽屉。`,
      confirmText: '拆掉它',
      cancelText: '再想想',
      danger: true,
      icon: 'trash',
    }))
  ) {
    return
  }
  busy.value = true
  notice.value = null
  try {
    const res = await fetch(`/api/member/collections/${route.params.id}`, { method: 'DELETE' })
    const data = await res.json().catch(() => null)
    if (res.ok) {
      router.push('/collections')
      return
    }
    notice.value = { kind: 'err', text: data?.message || '抽屉拆不掉' }
  } catch {
    notice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    busy.value = false
  }
}

onMounted(load)
</script>

<template>
  <div class="min-h-screen">
    <TopBar />

    <main class="mx-auto max-w-[1400px] px-6 py-6">
      <div v-if="loading" class="animate-pulse rounded-2xl bg-black/[0.05] dark:bg-white/[0.06] p-6">
        <div class="h-6 w-1/3 rounded bg-black/[0.06] dark:bg-white/[0.08]"></div>
        <div class="mt-3 h-3.5 w-1/2 rounded bg-black/[0.06] dark:bg-white/[0.08]"></div>
      </div>

      <div v-else-if="notfound" class="rounded-xl border border-dashed border-black/10 dark:border-white/10 py-16 text-center">
        <svg viewBox="0 0 24 24" class="mx-auto h-10 w-10 text-inkFaint/50" fill="none" stroke="currentColor" stroke-width="1.4">
          <path d="M3 7.5A2.5 2.5 0 0 1 5.5 5h3.2l1.8 2.2h8A2.5 2.5 0 0 1 21 9.7v8.8A2.5 2.5 0 0 1 18.5 21h-13A2.5 2.5 0 0 1 3 18.5v-11Z" stroke-linejoin="round" />
        </svg>
        <p class="mt-3 text-[15px] text-inkMain">没有这个抽屉，或者已经被拆了</p>
        <RouterLink to="/collections" class="mt-3 inline-block rounded-full border border-stationPink px-5 py-1.5 text-[13px] text-stationPink transition hover:bg-stationPink hover:text-white">
          回名录看看别的
        </RouterLink>
      </div>

      <template v-else-if="detail">
        <!-- 页头：淡粉渐变柜面，名/简介/统计一屉摆清 -->
        <div class="rounded-2xl bg-gradient-to-br from-stationPink/12 via-stationPink/[0.05] to-transparent p-5 ring-1 ring-stationPink/10">
          <div class="flex flex-wrap items-start justify-between gap-4">
            <div class="min-w-0">
              <h1 class="flex items-center gap-2.5 text-[20px] font-semibold text-inkMain">
                <span class="flex h-8 w-8 shrink-0 items-center justify-center rounded-xl bg-gradient-to-br from-stationPink to-[#e05a86] text-white shadow-sm">
                  <svg viewBox="0 0 24 24" class="h-4 w-4" fill="none" stroke="currentColor" stroke-width="2">
                    <path d="M4 7.5A2.5 2.5 0 0 1 6.5 5h3l1.7 2h6.3A2.5 2.5 0 0 1 20 9.5v8A2.5 2.5 0 0 1 17.5 20h-11A2.5 2.5 0 0 1 4 17.5v-10Z" stroke-linejoin="round" />
                  </svg>
                </span>
                <span class="min-w-0 truncate">{{ detail.name }}</span>
              </h1>
              <p v-if="detail.intro" class="mt-2 max-w-[720px] text-[13px] leading-relaxed text-inkSub">{{ detail.intro }}</p>
              <div class="mt-2.5 flex flex-wrap items-center gap-2 text-[12px] text-inkFaint">
                <span class="flex items-center gap-1 rounded-full bg-cardBg px-2.5 py-1 ring-1 ring-black/[0.06] dark:ring-white/10">
                  <svg viewBox="0 0 24 24" class="h-3 w-3" fill="none" stroke="currentColor" stroke-width="1.8">
                    <circle cx="12" cy="8.5" r="3.5" />
                    <path d="M5 19.5c1.4-3.2 4-4.5 7-4.5s5.6 1.3 7 4.5" stroke-linecap="round" />
                  </svg>
                  {{ detail.ownerName || '无名席位' }} 建
                </span>
                <span class="flex items-center gap-1 rounded-full bg-cardBg px-2.5 py-1 ring-1 ring-black/[0.06] dark:ring-white/10">
                  <svg viewBox="0 0 24 24" class="h-3 w-3" fill="currentColor">
                    <path d="M4 6h4v14H4zM10 6h4v14h-4zM16.5 6.6l3.8 1-3.5 13.2-3.8-1z" />
                  </svg>
                  {{ detail.itemCount }} 件
                </span>
                <span v-if="fmtDate(detail.createdAtUtc)" class="rounded-full bg-cardBg px-2.5 py-1 ring-1 ring-black/[0.06] dark:ring-white/10">
                  {{ fmtDate(detail.createdAtUtc) }} 开屉
                </span>
              </div>
            </div>
            <button
              v-if="canManage"
              class="shrink-0 rounded-lg border border-rose-300 px-3 py-1.5 text-[12px] text-rose-500 transition hover:bg-rose-50 disabled:opacity-40 dark:border-rose-500/40 dark:text-rose-400 dark:hover:bg-rose-500/10"
              :disabled="busy"
              @click="dissolve"
            >拆除抽屉</button>
          </div>
        </div>

        <p v-if="notice" :class="notice.kind === 'ok' ? 'text-emerald-600 dark:text-emerald-400' : 'text-rose-500 dark:text-rose-400'" class="mt-3 text-[12px]">{{ notice.text }}</p>

        <div v-if="detail.items?.length" class="mt-5 grid grid-cols-2 gap-x-4 gap-y-[38px] md:grid-cols-3 xl:grid-cols-5">
          <div v-for="video in detail.items" :key="video.bvid" class="relative">
            <RouterLink :to="`/watch/${video.bvid}`" class="group block">
              <VideoCard :video="video" />
            </RouterLink>
            <button
              v-if="canManage"
              class="absolute right-1.5 top-1.5 z-10 flex h-6 w-6 items-center justify-center rounded-full bg-black/55 text-white/75 backdrop-blur transition hover:bg-rose-500 hover:text-white disabled:opacity-40"
              title="请出抽屉"
              :disabled="busy"
              @click="removeItem(video)"
            >
              <svg viewBox="0 0 24 24" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round">
                <path d="M6 6l12 12M18 6L6 18" />
              </svg>
            </button>
          </div>
        </div>
        <div v-else class="mt-5 rounded-xl border border-dashed border-black/10 dark:border-white/10 py-16 text-center">
          <svg viewBox="0 0 24 24" class="mx-auto h-10 w-10 text-inkFaint/50" fill="none" stroke="currentColor" stroke-width="1.4">
            <rect x="3" y="4" width="18" height="16" rx="3" />
            <path d="M3 9h18M8 14h8" stroke-linecap="round" />
          </svg>
          <p class="mt-3 text-[13px] text-inkSub">抽屉还空着</p>
          <RouterLink to="/" class="mt-3 inline-block rounded-full bg-stationPink px-5 py-1.5 text-[13px] text-white transition hover:opacity-90">
            去片子里点「加入合集」，第一件货就落进来
          </RouterLink>
        </div>
      </template>
    </main>
  </div>
</template>
