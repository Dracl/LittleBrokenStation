<script setup>
import { onMounted, onUnmounted, ref } from 'vue'
import TopBar from '../components/TopBar.vue'
import VideoCard from '../components/VideoCard.vue'
import { submitDialogOpen } from '../submitStore'

const health = ref(null)
const healthFailed = ref(false)
const videos = ref([])

let pollTimer = null

async function refreshHealth() {
  try {
    const res = await fetch('/api/health')
    health.value = await res.json()
    healthFailed.value = false
  } catch {
    healthFailed.value = true
  }
}

async function refreshVideos() {
  try {
    const res = await fetch('/api/videos')
    if (res.ok) videos.value = await res.json()
  } catch {
    /* 下一轮再试 */
  }
}

const commentBusy = ref(new Set())

async function fetchComments(video) {
  if (commentBusy.value.has(video.id)) return
  const busy = new Set(commentBusy.value)
  busy.add(video.id)
  commentBusy.value = busy
  try {
    const res = await fetch(`/api/videos/${video.id}/comments`, { method: 'POST' })
    const body = await res.json().catch(() => null)
    showNotice(res.ok ? 'ok' : 'err', body?.message ?? (res.ok ? '评论已归档' : '评论采集失败'))
  } catch {
    showNotice('err', '分站后端没有应答')
  } finally {
    const done = new Set(commentBusy.value)
    done.delete(video.id)
    commentBusy.value = done
  }
}

async function removeVideo(video) {
  const label = video.title || video.bvid
  if (!window.confirm(`确定把「${label}」从分站彻底删除？本地文件（视频/弹幕/字幕/封面）会一并清掉，不可恢复。`)) {
    return
  }
  try {
    const res = await fetch(`/api/videos/${video.id}`, { method: 'DELETE' })
    const body = await res.json().catch(() => null)
    if (res.ok) {
      showNotice('ok', body?.message ?? '已删除')
      refreshVideos()
    } else {
      showNotice('err', body?.message ?? '删不动，稍后再试')
    }
  } catch {
    showNotice('err', '分站后端没有应答')
  }
}

const notice = ref(null)
let noticeTimer = null

function showNotice(kind, text) {
  notice.value = { kind, text }
  if (noticeTimer) clearTimeout(noticeTimer)
  noticeTimer = setTimeout(() => (notice.value = null), 6000)
}

const STATUS_META = {
  0: { text: '排队待进站', dot: 'bg-inkFaint', tone: 'text-inkSub' },
  1: { text: '进站中', dot: 'bg-stationPink animate-pulse', tone: 'text-stationPink' },
  2: { text: '已到站', dot: 'bg-emerald-400', tone: 'text-emerald-300' },
  3: { text: '进站失败', dot: 'bg-rose-400', tone: 'text-rose-300' },
  4: { text: '无需进站', dot: 'bg-amber-300', tone: 'text-amber-200' },
}

function statusMeta(status) {
  return STATUS_META[status] ?? STATUS_META[0]
}

function shortTime(iso) {
  if (!iso) return ''
  const d = new Date(iso)
  return `${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')} ${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`
}

onMounted(() => {
  refreshHealth()
  refreshVideos()
  pollTimer = setInterval(() => {
    refreshVideos()
    refreshHealth()
  }, 5000)
})

onUnmounted(() => {
  if (pollTimer) clearInterval(pollTimer)
})
</script>

<template>
  <div class="min-h-screen">
    <TopBar />

    <main class="mx-auto max-w-[1400px] px-6 py-6">
      <!-- 开站状态条 -->
      <div class="mb-6 flex items-center gap-3 rounded-lg bg-cardBg px-4 py-3 text-sm">
        <template v-if="health">
          <span class="relative flex h-2.5 w-2.5">
            <span class="absolute inline-flex h-full w-full animate-ping rounded-full bg-stationPink opacity-60"></span>
            <span class="relative inline-flex h-2.5 w-2.5 rounded-full bg-stationPink"></span>
          </span>
          <span class="text-inkMain">{{ health.name }}已就位</span>
          <span class="text-inkFaint">版本 {{ health.version }}</span>
        </template>
        <template v-else-if="healthFailed">
          <span class="h-2.5 w-2.5 rounded-full bg-inkFaint"></span>
          <span class="text-inkSub">分站后端还没应答</span>
        </template>
        <template v-else>
          <span class="h-2.5 w-2.5 rounded-full bg-inkFaint"></span>
          <span class="text-inkSub">正在联系分站后端……</span>
        </template>
        <span v-if="videos.length" class="ml-auto text-inkFaint">馆藏 {{ videos.length }} 件</span>
      </div>

      <!-- 馆藏墙 -->
      <div class="mb-3 flex items-center justify-between">
        <h2 class="text-base font-semibold text-inkMain">馆藏</h2>
        <p
          v-if="notice"
          class="text-xs"
          :class="{ 'text-emerald-300': notice.kind === 'ok', 'text-amber-200': notice.kind === 'warn', 'text-rose-300': notice.kind === 'err' }"
        >
          {{ notice.text }}
        </p>
      </div>
      <div v-if="videos.length === 0" class="rounded-xl border border-dashed border-white/10 py-16 text-center">
        <p class="text-sm text-inkSub">还没有馆藏</p>
        <button
          class="mt-3 rounded-full border border-stationPink px-5 py-1.5 text-sm text-stationPink transition hover:bg-stationPink hover:text-white"
          @click="submitDialogOpen = true"
        >
          点这里投稿第一条
        </button>
      </div>
      <div v-else class="grid grid-cols-2 gap-4 md:grid-cols-3 xl:grid-cols-5">
        <div
          v-for="(video, idx) in videos"
          :key="video.id"
          class="group relative"
          :class="idx === 0 ? 'col-span-2 row-span-2' : ''"
        >
          <RouterLink :to="`/watch/${video.bvid}`" class="block h-full">
            <!-- 头条大卡：封面铺满、标题压在底部渐变上（B 站首页首格同款） -->
            <div v-if="idx === 0" class="relative h-full min-h-[260px] w-full overflow-hidden rounded-xl bg-[#f1f2f3]">
              <img
                v-if="video.coverUrl"
                :src="video.coverUrl"
                :alt="video.title || video.bvid"
                referrerpolicy="no-referrer"
                class="absolute inset-0 h-full w-full object-cover transition duration-300 group-hover:scale-[1.02]"
              />
              <div v-else class="absolute inset-0 flex items-center justify-center">
                <span class="text-xs tracking-widest text-inkFaint">{{ video.bvid }}</span>
              </div>
              <span
                class="absolute left-2 top-2 z-10 rounded-md bg-black/60 px-2 py-0.5 text-[11px] backdrop-blur"
                :class="statusMeta(video.status).tone"
              >
                {{ statusMeta(video.status).text }}
              </span>
              <div class="absolute inset-x-0 bottom-0 h-28 bg-gradient-to-t from-black/75 to-transparent"></div>
              <p class="absolute bottom-3 left-3 right-16 line-clamp-2 text-[15px] font-medium leading-[22px] text-white drop-shadow-[0_1px_3px_rgba(0,0,0,0.9)]">
                {{ video.title || video.bvid }}
              </p>
            </div>

            <!-- 普通推荐卡：与搜索结果页共用 VideoCard，站长视角多开状态点与失败原因 -->
            <VideoCard v-else :video="video" admin />
          </RouterLink>
          <button
            v-if="video.status === 2"
            class="absolute right-9 top-2 hidden rounded-md bg-black/60 p-1.5 text-white/70 backdrop-blur transition hover:bg-stationBlue hover:text-white group-hover:block disabled:opacity-40"
            :disabled="commentBusy.has(video.id)"
            title="重新采集评论区快照"
            @click.prevent.stop="fetchComments(video)"
          >
            <svg viewBox="0 0 24 24" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M21 12a8 8 0 0 1-11.6 7.1L4 21l1.9-5.4A8 8 0 1 1 21 12z" stroke-linecap="round" stroke-linejoin="round" />
            </svg>
          </button>
          <button
            class="absolute right-2 top-2 hidden rounded-md bg-black/60 p-1.5 text-white/70 backdrop-blur transition hover:bg-rose-500 hover:text-white group-hover:block"
            title="删掉这个馆藏（含本地文件）"
            @click.prevent.stop="removeVideo(video)"
          >
            <svg viewBox="0 0 24 24" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M4 7h16M9 7V5a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2m3 0-1 13a1 1 0 0 1-1 1H8a1 1 0 0 1-1-1L6 7" stroke-linecap="round" stroke-linejoin="round" />
            </svg>
          </button>
        </div>
      </div>
    </main>
  </div>
</template>
