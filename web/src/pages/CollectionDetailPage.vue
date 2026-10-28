<script setup>
import { computed, onMounted, ref } from 'vue'
import { RouterLink, useRoute, useRouter } from 'vue-router'
import VideoCard from '../components/VideoCard.vue'
import { member } from '../memberStore'

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
  if (!window.confirm(`把《${video.title || video.bvid}》请出这个抽屉？`)) return
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
  if (!window.confirm(`拆除整个抽屉「${detail.value?.name}」？里面的货回货架，不受损伤。`)) return
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
  <div class="mx-auto max-w-[1400px] px-6 py-6">
    <p v-if="loading" class="py-16 text-center text-[13px] text-inkFaint">正在拉开抽屉……</p>

    <div v-else-if="notfound" class="py-16 text-center">
      <p class="text-[15px] text-inkMain">没有这个抽屉，或者已经被拆了</p>
      <RouterLink to="/collections" class="mt-3 inline-block text-[13px] text-stationPink transition hover:opacity-80">回名录看看别的</RouterLink>
    </div>

    <template v-else-if="detail">
      <div class="mb-2 flex flex-wrap items-start justify-between gap-4">
        <div class="min-w-0">
          <h1 class="text-[20px] font-semibold text-inkMain">{{ detail.name }}</h1>
          <p v-if="detail.intro" class="mt-1 text-[13px] text-inkSub">{{ detail.intro }}</p>
          <p class="mt-1 text-[12px] text-inkFaint">
            {{ detail.ownerName || '无名席位' }} 建 · {{ detail.itemCount }} 件 ·
            {{ new Date(detail.createdAtUtc).toLocaleString('zh-CN') }}
          </p>
        </div>
        <button
          v-if="canManage"
          class="shrink-0 rounded-lg border border-rose-300 px-3 py-1.5 text-[12px] text-rose-500 transition hover:bg-rose-50 disabled:opacity-40"
          :disabled="busy"
          @click="dissolve"
        >拆除抽屉</button>
      </div>

      <p v-if="notice" :class="notice.kind === 'ok' ? 'text-emerald-600' : 'text-rose-500'" class="mb-2 text-[12px]">{{ notice.text }}</p>

      <div class="mt-4 grid grid-cols-2 gap-x-4 gap-y-[38px] md:grid-cols-3 xl:grid-cols-5">
        <div v-for="video in detail.items" :key="video.bvid" class="relative">
          <VideoCard :video="video" />
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
        <p v-if="!detail.items?.length" class="col-span-full py-12 text-center text-[13px] text-inkFaint">
          抽屉还空着。去片子里点「加入合集」，第一件货就落进来。
        </p>
      </div>
    </template>
  </div>
</template>
