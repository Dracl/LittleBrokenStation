<script setup>
import { computed, onMounted, ref } from 'vue'
import TopBar from '../components/TopBar.vue'
import { member } from '../memberStore'
import { friendlyTime } from '../utils/time'

const items = ref([])
const hasMore = ref(false)
const loading = ref(true)
const loadingMore = ref(false)
const targets = ref([])
const filterMid = ref(null) // null = 全部
const toast = ref(null)
const roundBusy = ref(false)

const PAGE_SIZE = 20

// type 徽标：视频/图文/专栏/转发/纯文字，认不出的灰底保底
function typeBadge(type) {
  const t = (type || '').toUpperCase()
  if (t.includes('FORWARD')) return { label: '转发', cls: 'bg-violet-500/10 text-violet-600' }
  if (t.includes('AV') || t.includes('UGC_SEASON')) return { label: '视频', cls: 'bg-stationPink/10 text-stationPink' }
  if (t.includes('OPUS') || t.includes('DRAW')) return { label: '图文', cls: 'bg-emerald-500/10 text-emerald-600' }
  if (t.includes('ARTICLE')) return { label: '专栏', cls: 'bg-sky-500/10 text-sky-600' }
  if (t.includes('WORD')) return { label: '纯文字', cls: 'bg-lineSoft text-inkSub' }
  return { label: '动态', cls: 'bg-lineSoft text-inkFaint' }
}

// target 名片现查：头像按 authorMid 找（转发条的原作者可能不在名单里，那就首字色块）
const faceByMid = computed(() => {
  const map = {}
  for (const t of targets.value) {
    if (t.faceUrl) map[t.mid] = t.faceUrl
  }
  return map
})

const avatarPalette = ['bg-stationPink/70', 'bg-emerald-500/70', 'bg-sky-500/70', 'bg-amber-500/70', 'bg-violet-500/70']
function avatarClass(mid) {
  return avatarPalette[Math.abs(Number(mid ?? 0)) % avatarPalette.length]
}

function picsLayout(count) {
  if (count <= 1) return { cls: 'max-w-[300px]', img: 'w-full' }
  if (count <= 3) return { cls: 'flex gap-1 max-w-[460px]', img: 'min-w-0 flex-1' }
  return { cls: 'grid grid-cols-2 gap-1 max-w-[460px]', img: 'w-full' }
}

async function loadTargets() {
  try {
    const res = await fetch('/api/dynamics/targets')
    targets.value = res.ok ? await res.json() : []
  } catch {
    targets.value = []
  }
}

async function loadFeed() {
  loading.value = true
  try {
    const q = new URLSearchParams({ limit: String(PAGE_SIZE) })
    if (filterMid.value) q.set('mid', String(filterMid.value))
    const res = await fetch(`/api/dynamics?${q}`)
    if (res.ok) {
      const data = await res.json()
      items.value = data.items ?? []
      hasMore.value = !!data.hasMore
    }
  } catch {
    toast.value = '动态没拉开，刷新再试'
    setTimeout(() => { toast.value = null }, 2500)
  } finally {
    loading.value = false
  }
}

async function loadMore() {
  if (!hasMore.value || !items.value.length || loadingMore.value) return
  loadingMore.value = true
  try {
    const q = new URLSearchParams({ limit: String(PAGE_SIZE), beforeId: String(items.value[items.value.length - 1].id ?? items.value[items.value.length - 1].dynamicId) })
    if (filterMid.value) q.set('mid', String(filterMid.value))
    const res = await fetch(`/api/dynamics?${q}`)
    if (res.ok) {
      const data = await res.json()
      items.value = items.value.concat(data.items ?? [])
      hasMore.value = !!data.hasMore
    }
  } catch {
    /* 翻页断一下不吭声，按钮还亮着 */
  } finally {
    loadingMore.value = false
  }
}

function pickFilter(mid) {
  filterMid.value = mid
  loadFeed()
}

// 一键巡站只认站长：访客看到的是橱窗，不是控制台
async function refreshRound() {
  if (roundBusy.value) return
  roundBusy.value = true
  try {
    const res = await fetch('/api/admin/dynamics/refresh', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' })
    const data = await res.json().catch(() => null)
    if (res.ok && data?.started) {
      toast.value = '已催巡捕出门，稍后刷新看新货'
    } else {
      toast.value = res.ok ? '巡捕正在外头跑，等等再来' : (data?.message ?? '催轮没递上去')
    }
    setTimeout(() => { toast.value = null }, 2600)
  } catch {
    toast.value = '分站后端没有应答'
    setTimeout(() => { toast.value = null }, 2600)
  } finally {
    roundBusy.value = false
  }
}

onMounted(async () => {
  await Promise.all([loadTargets(), loadFeed()])
})
</script>

<template>
  <div class="min-h-screen bg-pageBg">
    <TopBar />
    <main class="mx-auto max-w-[720px] px-6 py-6">
      <div class="mb-4 flex flex-wrap items-center justify-between gap-3">
        <h1 class="text-lg font-medium text-inkMain">UP 主动态</h1>
        <button
          v-if="member?.role === 'owner'"
          class="rounded-full border border-stationPink px-3.5 py-1 text-xs text-stationPink transition hover:bg-stationPink hover:text-white disabled:cursor-not-allowed disabled:opacity-50"
          :disabled="roundBusy"
          @click="refreshRound"
        >
          {{ roundBusy ? '催轮中…' : '⟳ 刷新' }}
        </button>
      </div>

      <!-- target 过滤器：全部 + 各关注 UP 头像按钮 -->
      <div class="mb-5 flex flex-wrap items-center gap-2">
        <button
          class="rounded-full px-3 py-1 text-xs transition"
          :class="filterMid === null ? 'bg-stationPink text-white' : 'border border-lineSoft bg-cardBg text-inkSub hover:border-stationPink hover:text-stationPink'"
          @click="pickFilter(null)"
        >
          全部
        </button>
        <button
          v-for="t in targets"
          :key="t.mid"
          class="flex items-center gap-1.5 rounded-full py-1 pl-1 pr-3 text-xs transition"
          :class="filterMid === t.mid ? 'bg-stationPink text-white' : 'border border-lineSoft bg-cardBg text-inkSub hover:border-stationPink hover:text-stationPink'"
          :title="`${t.nickname || t.mid} · ${t.count} 条`"
          @click="pickFilter(t.mid)"
        >
          <img v-if="t.faceUrl" :src="t.faceUrl" referrerpolicy="no-referrer" loading="lazy" class="h-5 w-5 rounded-full object-cover" alt="" />
          <span v-else class="flex h-5 w-5 items-center justify-center rounded-full text-[10px] text-white" :class="avatarClass(t.mid)">{{ (t.nickname || String(t.mid)).slice(0, 1) }}</span>
          <span class="max-w-[110px] truncate">{{ t.nickname || `UID ${t.mid}` }}</span>
        </button>
      </div>

      <p v-if="loading" class="py-16 text-center text-sm text-inkFaint">正在掀 UP 主的动态墙…</p>

      <div v-else-if="!items.length" class="rounded-2xl border border-lineSoft bg-cardBg px-6 py-16 text-center">
        <svg viewBox="0 0 64 48" class="mx-auto mb-4 h-14 w-16 text-lineSoft" fill="none" stroke="currentColor" stroke-width="2.4">
          <rect x="6" y="6" width="52" height="36" rx="8" />
          <path d="M14 30l10-9 8 7 8-10 10 12" stroke-linecap="round" stroke-linejoin="round" />
          <circle cx="22" cy="17" r="3" />
        </svg>
        <p class="text-sm text-inkFaint">橱窗还空着。站长在站长室「动态关注」里登记 UP 后，等巡捕一轮就有货了。</p>
      </div>

      <template v-else>
        <article
          v-for="d in items"
          :key="d.id ?? d.dynamicId"
          class="mb-3 rounded-2xl border border-lineSoft bg-cardBg p-4"
          :class="(d.type || '').toUpperCase().includes('FORWARD') ? 'ml-4 border-l-[3px] border-l-stationPink/40 bg-stationPink/[0.02]' : ''"
        >
          <header class="mb-2.5 flex items-center gap-2.5">
            <img
              v-if="faceByMid[d.authorMid]"
              :src="faceByMid[d.authorMid]"
              referrerpolicy="no-referrer"
              loading="lazy"
              class="h-8 w-8 shrink-0 rounded-full object-cover"
              alt=""
            />
            <span
              v-else
              class="flex h-8 w-8 shrink-0 items-center justify-center rounded-full text-[13px] text-white"
              :class="avatarClass(d.authorMid)"
            >{{ (d.authorName || String(d.authorMid ?? '?')).slice(0, 1) }}</span>
            <span class="min-w-0 flex-1">
              <span class="block truncate text-[13px] font-medium text-inkMain">{{ d.authorName || `UID ${d.authorMid}` }}</span>
              <span class="block text-[11px] text-inkFaint">{{ friendlyTime(d.publishedAtUtc) }}</span>
            </span>
            <span class="shrink-0 rounded-md px-1.5 py-0.5 text-[11px]" :class="typeBadge(d.type).cls">{{ typeBadge(d.type).label }}</span>
          </header>

          <p v-if="d.text" class="mb-2.5 whitespace-pre-wrap break-words text-[13.5px] leading-relaxed text-inkMain">{{ d.text }}</p>

          <div v-if="d.pics?.length" class="mb-2.5" :class="picsLayout(d.pics.length).cls">
            <img
              v-for="(pic, i) in d.pics"
              :key="i"
              :src="pic"
              referrerpolicy="no-referrer"
              loading="lazy"
              class="rounded-lg object-cover"
              :class="picsLayout(d.pics.length).img"
              :alt="`动态配图 ${i + 1}`"
              @error="$event.target.style.display = 'none'"
            />
          </div>

          <footer v-if="d.bvid" class="mt-1">
            <RouterLink
              v-if="d.inStation"
              :to="`/watch/${d.bvid}`"
              class="inline-flex items-center gap-1 rounded-full bg-stationPink px-3.5 py-1.5 text-xs text-white transition hover:bg-stationPink/90"
            >
              ▶ 站内放映
            </RouterLink>
            <a
              v-else
              :href="`https://www.bilibili.com/video/${d.bvid}`"
              target="_blank"
              rel="noopener noreferrer"
              class="inline-flex items-center gap-1 rounded-full border border-lineSoft px-3.5 py-1.5 text-xs text-inkSub transition hover:border-stationPink hover:text-stationPink"
            >
              ↗ B 站原链
            </a>
          </footer>
        </article>

        <div class="pb-4 pt-1 text-center">
          <button
            v-if="hasMore"
            class="rounded-full border border-lineSoft bg-cardBg px-6 py-1.5 text-xs text-inkSub transition hover:border-stationPink hover:text-stationPink disabled:opacity-50"
            :disabled="loadingMore"
            @click="loadMore"
          >
            {{ loadingMore ? '翻页中…' : '看更多' }}
          </button>
          <p v-else class="text-xs text-inkFaint">橱窗见底了，巡捕每小时来补货</p>
        </div>
      </template>

      <p v-if="toast" class="fixed bottom-6 left-1/2 z-50 -translate-x-1/2 rounded-full bg-inkMain/85 px-4 py-2 text-xs text-white shadow-lg">{{ toast }}</p>
    </main>
  </div>
</template>
