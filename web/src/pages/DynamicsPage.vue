<script setup>
import { computed, onMounted, ref } from 'vue'
import TopBar from '../components/TopBar.vue'
import DynamicVideoCard from '../components/DynamicVideoCard.vue'
import { member } from '../memberStore'
import { friendlyTime } from '../utils/time'

const items = ref([])
const hasMore = ref(false)
const loading = ref(true)
const loadingMore = ref(false)
const targets = ref([])
const filterMid = ref(null) // null = 全部 UP
const activeTab = ref('all') // all | video
const toast = ref(null)
const roundBusy = ref(false)

const PAGE_SIZE = 20

// ─── 卡型判定（B 站式）───
function isLive(d) {
  return (d.type || '').toUpperCase().includes('LIVE')
}
function isForward(d) {
  return (d.type || '').toUpperCase().includes('FORWARD')
}
function isVideo(d) {
  return !!d.bvid
}

// 转发条正文是「评语\n\n转发自 XX：\n\n原文」三段式（解析器定格），卡头归转发人、灰块装原文
function splitForward(text) {
  if (!text) return { comment: '', orig: '' }
  const i = text.indexOf('\n\n转发自')
  if (i < 0) return { comment: text, orig: '' }
  return { comment: text.slice(0, i), orig: text.slice(i + 2) }
}

// B 站图格规矩：单图 1/3 宽、四图 2×2、其余三列，一律 3:4 裁切
function picsLayout(count) {
  if (count === 1) return 'grid grid-cols-3 gap-1 max-w-[340px]'
  if (count === 4) return 'grid grid-cols-2 gap-1 max-w-[340px]'
  return 'grid grid-cols-3 gap-1 max-w-[500px]'
}

const avatarPalette = ['bg-stationPink/70', 'bg-emerald-500/70', 'bg-sky-500/70', 'bg-amber-500/70', 'bg-violet-500/70']
function avatarClass(mid) {
  return avatarPalette[Math.abs(Number(mid ?? 0)) % avatarPalette.length]
}

// target 名片兜底：动态行没存脸时按 mid 找（转发条的原作者可能不在名单里，那就首字色块）
const faceByMid = computed(() => {
  const map = {}
  for (const t of targets.value) {
    if (t.faceUrl) map[t.mid] = t.faceUrl
  }
  return map
})

function authorFace(d) {
  return d.authorFaceUrl || faceByMid.value[d.authorMid] || ''
}

const shown = computed(() => (activeTab.value === 'video' ? items.value.filter(isVideo) : items.value))

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
    const q = new URLSearchParams({ limit: String(PAGE_SIZE), beforeId: String(items.value[items.value.length - 1].id ?? 0) })
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
    <main class="mx-auto max-w-[560px] px-4 py-6">
      <div class="mb-3 flex flex-wrap items-center justify-between gap-3">
        <h1 class="text-lg font-medium text-inkMain">动态</h1>
        <button
          v-if="member?.role === 'owner'"
          class="rounded-full border border-stationPink px-3.5 py-1 text-xs text-stationPink transition hover:bg-stationPink hover:text-white disabled:cursor-not-allowed disabled:opacity-50"
          :disabled="roundBusy"
          @click="refreshRound"
        >
          {{ roundBusy ? '催轮中…' : '⟳ 刷新' }}
        </button>
      </div>

      <!-- 盯梢 UP 过滤器：全部 + 各头像胶囊 -->
      <div class="mb-3 flex flex-wrap items-center gap-2">
        <button
          class="rounded-full px-3 py-1 text-xs transition"
          :class="filterMid === null ? 'bg-stationPink text-white' : 'border border-lineSoft bg-cardBg text-inkSub hover:border-stationPink hover:text-stationPink'"
          @click="pickFilter(null)"
        >
          全部 UP
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
          <span class="max-w-[110px] truncate">{{ t.nickname || `B站号 ${t.mid}` }}</span>
        </button>
      </div>

      <!-- B 站式文字 tab：全部 / 视频 -->
      <div class="mb-3 flex items-center gap-7 rounded-lg border border-lineSoft bg-cardBg px-5">
        <button
          class="relative py-3 text-sm transition"
          :class="activeTab === 'all' ? 'font-semibold text-inkMain' : 'text-inkSub hover:text-inkMain'"
          @click="activeTab = 'all'"
        >
          全部
          <span v-if="activeTab === 'all'" class="absolute bottom-0 left-1/2 h-[3px] w-6 -translate-x-1/2 rounded-t-full bg-stationPink"></span>
        </button>
        <button
          class="relative py-3 text-sm transition"
          :class="activeTab === 'video' ? 'font-semibold text-inkMain' : 'text-inkSub hover:text-inkMain'"
          @click="activeTab = 'video'"
        >
          视频
          <span v-if="activeTab === 'video'" class="absolute bottom-0 left-1/2 h-[3px] w-6 -translate-x-1/2 rounded-t-full bg-stationPink"></span>
        </button>
      </div>

      <p v-if="loading" class="py-16 text-center text-sm text-inkFaint">正在掀 UP 主的动态墙…</p>

      <div v-else-if="!shown.length" class="rounded-2xl border border-lineSoft bg-cardBg px-6 py-16 text-center">
        <svg viewBox="0 0 64 48" class="mx-auto mb-4 h-14 w-16 text-lineSoft" fill="none" stroke="currentColor" stroke-width="2.4">
          <rect x="6" y="6" width="52" height="36" rx="8" />
          <path d="M14 30l10-9 8 7 8-10 10 12" stroke-linecap="round" stroke-linejoin="round" />
          <circle cx="22" cy="17" r="3" />
        </svg>
        <p class="text-sm text-inkFaint">{{ activeTab === 'video' ? '这批橱窗里还没有视频条。' : '橱窗还空着。站长在站长室「动态关注」里登记 UP 后，等巡捕一轮就有货了。' }}</p>
      </div>

      <template v-else>
        <article
          v-for="d in shown"
          :key="d.id ?? d.dynamicId"
          class="mb-2.5 rounded-lg border bg-cardBg p-4"
          :class="isLive(d) ? 'border-stationPink/50' : 'border-lineSoft'"
        >
          <!-- 头行：头像（抓取定格的缓存脸）+ 昵称 + 时间；视频卡右上挂内外签 -->
          <header class="mb-2 flex items-center gap-2.5">
            <img
              v-if="authorFace(d)"
              :src="authorFace(d)"
              referrerpolicy="no-referrer"
              loading="lazy"
              class="h-10 w-10 shrink-0 rounded-full object-cover"
              alt=""
              @error="$event.target.style.display = 'none'"
            />
            <span
              v-else
              class="flex h-10 w-10 shrink-0 items-center justify-center rounded-full text-sm text-white"
              :class="avatarClass(d.authorMid)"
            >{{ (d.authorName || String(d.authorMid ?? '?')).slice(0, 1) }}</span>
            <span class="min-w-0 flex-1">
              <span class="block truncate text-sm font-semibold text-inkMain">{{ d.authorName || `B站号 ${d.authorMid}` }}</span>
              <span class="block text-xs text-inkFaint">{{ friendlyTime(d.publishedAtUtc) }}</span>
            </span>
            <span
              v-if="!isLive(d) && d.bvid"
              class="shrink-0 rounded-md px-2 py-0.5 text-xs"
              :class="d.inStation
                ? 'border border-stationPink/50 bg-stationPink/5 text-stationPink'
                : 'border border-lineSoft bg-[#f6f7f8] text-inkSub'"
            >{{ d.inStation ? '站内可看' : '去 B 站看 ↗' }}</span>
          </header>

          <!-- 开播提醒：粉边卡 + 渐变直播条 -->
          <div
            v-if="isLive(d)"
            class="flex items-center gap-3 rounded-lg bg-gradient-to-r from-stationPink/10 to-stationPink/[0.02] px-3.5 py-3"
          >
            <span class="shrink-0 rounded bg-stationPink px-1.5 py-0.5 text-[11px] text-white">直播</span>
            <span class="min-w-0 flex-1 text-sm leading-relaxed text-inkMain" style="display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;overflow:hidden">{{ splitForward(d.text).comment || '开播了' }}</span>
            <a
              :href="d.authorMid ? `https://space.bilibili.com/${d.authorMid}` : 'https://live.bilibili.com'"
              target="_blank"
              rel="noopener noreferrer"
              class="shrink-0 rounded-full bg-stationPink px-3.5 py-1 text-xs text-white transition hover:bg-stationPink/90"
              title="直播间地址未缓存，跳作者空间"
            >看直播</a>
          </div>

          <!-- 转发条：评语 + 灰块内嵌原文（B 站同款） -->
          <template v-else-if="isForward(d)">
            <p v-if="splitForward(d.text).comment" class="mb-2 whitespace-pre-wrap break-words text-[15px] leading-relaxed text-inkMain">{{ splitForward(d.text).comment }}</p>
            <div class="rounded-md bg-[#f6f7f8] p-3">
              <p class="whitespace-pre-wrap break-words text-sm leading-relaxed text-inkSub">{{ splitForward(d.text).orig || '原文已被删' }}</p>
              <DynamicVideoCard v-if="d.bvid" :d="d" class="mt-2" />
              <div v-else-if="d.pics?.length" :class="['mt-2', picsLayout(d.pics.length)]">
                <img
                  v-for="(pic, i) in d.pics"
                  :key="i"
                  :src="pic"
                  referrerpolicy="no-referrer"
                  loading="lazy"
                  class="aspect-[3/4] w-full rounded object-cover"
                  :alt="`原动态配图 ${i + 1}`"
                  @error="$event.target.style.display = 'none'"
                />
              </div>
            </div>
          </template>

          <!-- 平常条：正文 → 图格 → 视频卡 -->
          <template v-else>
            <p v-if="d.text" class="mb-2 whitespace-pre-wrap break-words text-[15px] leading-relaxed text-inkMain">{{ d.text }}</p>
            <div v-if="d.pics?.length" :class="['mb-1', picsLayout(d.pics.length)]">
              <img
                v-for="(pic, i) in d.pics"
                :key="i"
                :src="pic"
                referrerpolicy="no-referrer"
                loading="lazy"
                class="aspect-[3/4] w-full rounded object-cover"
                :alt="`动态配图 ${i + 1}`"
                @error="$event.target.style.display = 'none'"
              />
            </div>
            <DynamicVideoCard v-if="d.bvid" :d="d" class="mt-1" />
          </template>
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
