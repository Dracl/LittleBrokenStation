<script setup>
import { richHtml } from '../utils/emote'
import { computed, ref, watch } from 'vue'
import ImageLightbox from './ImageLightbox.vue'
import AvatarImg from './AvatarImg.vue'
import LevelBadge from './LevelBadge.vue'

const props = defineProps({
  src: { type: String, default: null },
  videoId: { type: Number, default: null },
  upMid: { type: [String, Number], default: null },
})

const snapshot = ref(null)
const state = ref('idle') // idle | loading | ready | empty | failed
const emoteMap = ref({}) // 转义符 → /emotes/xx.png（下载失败时为远程热链）

// 正文渲染：转义→@高亮→[表情]换图，与动态评论区共用 utils/emote 同一根管线。
function rich(text) {
  return richHtml(text, emoteMap.value)
}

function picLocal(item, i) {
  return item.pictureLocals?.[i] || item.pictures?.[i] || ''
}
const sortBy = ref('hot') // hot | time
const floors = ref({}) // rpid -> { open, loaded, page, loading, hasMore }
const liked = ref(loadLiked())
// 图片放大：交给公共灯箱 ImageLightBox（同一楼多图可左右翻），图路本地原图优先（cmt-img）。
const zoom = ref(null) // { images: string[], index: number } | null

function openZoom(item, i) {
  const raw = (item.pictures ?? []).map((_, pi) => picLocal(item, pi))
  const images = []
  let click = 0
  raw.forEach((u, idx) => {
    if (!u) return
    if (idx < i) click++
    images.push(u)
  })
  if (!images.length) return
  zoom.value = { images, index: Math.min(click, images.length - 1) }
}

function loadLiked() {
  try {
    const raw = window.localStorage.getItem('lbs-comment-likes')
    return new Set(raw ? JSON.parse(raw) : [])
  } catch {
    return new Set()
  }
}

function persistLiked() {
  try {
    window.localStorage.setItem('lbs-comment-likes', JSON.stringify([...liked.value]))
  } catch {
    /* 存不下就算了 */
  }
}

async function load(src) {
  if (!src) {
    state.value = 'empty'
    snapshot.value = null
    return
  }

  state.value = 'loading'
  floors.value = {}
  try {
    const res = await fetch(src)
    if (!res.ok) {
      state.value = 'failed'
      return
    }

    const json = await res.json()
    const list = Array.isArray(json.replies) ? json.replies : []
    if (!list.length && !json.top) {
      snapshot.value = json
      state.value = 'empty'
      return
    }

    snapshot.value = json
    emoteMap.value = json.emotes ?? {}
    state.value = 'ready'
    autoLoadSmallFloors()
  } catch {
    state.value = 'failed'
  }
}

watch(() => props.src, (src) => load(src), { immediate: true })

const total = computed(() => snapshot.value?.total ?? 0)
const topReply = computed(() => snapshot.value?.top ?? null)

const replies = computed(() => {
  const list = [...(snapshot.value?.replies ?? [])]
  if (sortBy.value === 'time') {
    return list.sort((a, b) => (b.time ?? 0) - (a.time ?? 0))
  }

  return list.sort((a, b) => (b.like ?? 0) - (a.like ?? 0))
})

function floor(rpid) {
  if (!floors.value[rpid]) {
    floors.value[rpid] = { open: false, loaded: false, page: 0, loading: false, hasMore: true }
  }

  return floors.value[rpid]
}

function visibleSubs(reply) {
  const fl = floors.value[reply.rpid]
  const subs = reply.sub ?? []
  if (fl?.open) return subs
  // 一两三条的小楼直接全露，不藏链接；大楼折叠时只露前 3。
  if (replyCountOf(reply) <= 3) return subs
  return subs.slice(0, 3)
}

// 快照没预配到的小楼（≤3 条回复）静默自动补采，用户根本不需要点。
function autoLoadSmallFloors() {
  const list = snapshot.value?.replies ?? []
  for (const reply of list) {
    const n = replyCountOf(reply)
    const fl = floors.value[reply.rpid]
    if (n > 0 && n <= 3 && !(reply.sub?.length) && !fl?.loaded && !fl?.loading && !fl?.autoTried) {
      fetchSubs(reply, 1)
    }
  }
}

async function toggleFloor(reply) {
  const fl = floor(reply.rpid)
  if (fl.open) {
    fl.open = false
    return
  }

  fl.open = true
  const needFirst = !fl.loaded && !(reply.sub?.length)
  if (needFirst) await fetchSubs(reply, 1)
}

async function fetchSubs(reply, page) {
  const fl = floor(reply.rpid)
  if (fl.loading) return
  if (page === 1) fl.autoTried = true
  fl.loading = true
  try {
    const res = await fetch(`/api/videos/${props.videoId}/comments/subs`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ rpid: reply.rpid, page }),
    })
    const body = await res.json().catch(() => null)
    if (res.ok && body) {
      reply.sub = body.replies ?? []
      fl.loaded = true
      fl.page = page
      fl.hasMore = !!body.hasMore
    }
  } catch {
    /* 静默：按钮还在，可以再点 */
  } finally {
    fl.loading = false
  }
}

function replyCountOf(reply) {
  return reply.replyCount ?? reply.sub?.length ?? 0
}

function isUp(item) {
  return props.upMid != null && item.mid != null && String(item.mid) === String(props.upMid)
}

function medalStyle(color) {
  const hex = color && color.startsWith('#') ? color : `#${color || 'fb7299'}`
  return { backgroundColor: hex }
}

function timeText(unix) {
  if (!unix) return ''
  const d = new Date(unix * 1000)
  const p = (n) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`
}

function toggleLike(item) {
  const next = new Set(liked.value)
  if (next.has(item.rpid)) next.delete(item.rpid)
  else next.add(item.rpid)
  liked.value = next
  persistLiked()
}

function likeCount(item) {
  return (item.like ?? 0) + (liked.value.has(item.rpid) ? 1 : 0)
}

</script>

<template>
  <section v-if="state !== 'empty'" class="mt-6">
    <!-- 头部：评论 N · 最热 | 最新 -->
    <div class="mb-4 flex items-baseline gap-5">
      <h2 class="text-xl font-semibold text-inkMain">
        评论
        <span class="ml-1.5 text-sm font-normal text-inkFaint">{{ total }}</span>
      </h2>
      <div class="flex items-center gap-2 text-[15px]">
        <button class="transition" :class="sortBy === 'hot' ? 'font-semibold text-inkMain' : 'text-inkSub hover:text-inkMain'" @click="sortBy = 'hot'">最热</button>
        <span class="text-lineSoft">|</span>
        <button class="transition" :class="sortBy === 'time' ? 'font-semibold text-inkMain' : 'text-inkSub hover:text-inkMain'" @click="sortBy = 'time'">最新</button>
      </div>
      <span v-if="snapshot?.fetchedAtUtc" class="ml-auto text-xs text-inkFaint">
        采于 {{ new Date(snapshot.fetchedAtUtc).toLocaleString('zh-CN') }}
      </span>
    </div>

    <p v-if="state === 'loading'" class="py-8 text-center text-sm text-inkFaint">翻开评论区……</p>
    <p v-else-if="state === 'empty'" class="py-8 text-center text-sm text-inkFaint">这一站还没有评论</p>
    <p v-else-if="state === 'failed'" class="py-8 text-center text-sm text-inkFaint">评论快照读不出来，文件可能坏了</p>

    <div v-else class="space-y-5">
      <!-- 置顶：浅灰底块 -->
      <div v-if="topReply" class="rounded-xl bg-[#f6f7f8] p-4">
        <div class="flex gap-3">
          <AvatarImg :face="topReply.face" :face-local="topReply.faceLocal" :name="topReply.name" class="h-11 w-11 shrink-0 rounded-full object-cover bg-white text-sm text-inkFaint" />
          <div class="min-w-0 flex-1">
            <div class="flex flex-wrap items-center gap-1.5">
              <span class="text-[15px] text-inkMain">{{ topReply.name }}</span>
              <span v-for="(medal, mi) in topReply.medals ?? []" :key="mi" class="rounded-[3px] px-1 py-[2px] text-[10px] leading-none text-white" :style="medalStyle(medal.color)">{{ medal.level }} {{ medal.name }}</span>
              <LevelBadge :level="topReply.level" />
              <span v-if="isUp(topReply)" class="rounded-[3px] bg-[#fb7299] px-1 py-[2px] text-[10px] font-medium leading-none text-white">UP</span>
            </div>
            <p class="mt-1.5 text-[15px] leading-7 text-inkMain">
              <span class="mr-1.5 rounded border border-stationPink px-1 py-0.5 align-middle text-xs text-stationPink">置顶</span>
              <span class="whitespace-pre-line" v-html="rich(topReply.content)"></span>
            </p>
            <div v-if="topReply.pictures?.length" class="mt-2 flex flex-wrap gap-1">
              <img v-for="(pic, pi) in topReply.pictures" :key="pi" :src="picLocal(topReply, pi)" referrerpolicy="no-referrer" class="h-24 w-24 cursor-zoom-in rounded object-cover" loading="lazy" @click="openZoom(topReply, pi)" />
            </div>
            <div class="mt-2 flex items-center gap-5 text-xs text-inkFaint">
              <span>{{ timeText(topReply.time) }}</span>
              <span v-if="topReply.ipLocation">IP属地：{{ topReply.ipLocation }}</span>
              <button class="flex items-center gap-1 transition" :class="liked.has(topReply.rpid) ? 'text-stationPink' : 'hover:text-inkMain'" @click="toggleLike(topReply)">
                <svg viewBox="0 0 24 24" class="h-3.5 w-3.5" :fill="liked.has(topReply.rpid) ? 'currentColor' : 'none'" stroke="currentColor" stroke-width="2">
                  <path d="M7 10v11H4a1 1 0 0 1-1-1v-9a1 1 0 0 1 1-1h3zm0 0l4-7a2.5 2.5 0 0 1 2.5 2.5V9H20a2 2 0 0 1 2 2.3l-1.2 7A2 2 0 0 1 18.8 20H7" stroke-linecap="round" stroke-linejoin="round" />
                </svg>
                {{ likeCount(topReply) }}
              </button>
            </div>
            <div v-if="topReply.upLiked" class="mt-1.5">
              <span class="inline-block rounded-md bg-[#f4f5f6] px-2 py-[3px] text-xs text-inkSub">UP主觉得很赞</span>
            </div>
          </div>
        </div>
      </div>

      <!-- 主楼列表 -->
      <div
        v-for="(reply, ri) in replies"
        :key="reply.rpid"
        class="flex gap-3"
        :class="ri > 0 || topReply ? 'border-t border-lineSoft pt-5' : ''"
      >
        <AvatarImg :face="reply.face" :face-local="reply.faceLocal" :name="reply.name" class="h-11 w-11 shrink-0 rounded-full object-cover bg-[#f1f2f3] text-sm text-inkFaint" />

        <div class="min-w-0 flex-1">
          <div class="flex flex-wrap items-center gap-1.5">
            <span class="text-[15px] text-inkMain">{{ reply.name }}</span>
            <span v-for="(medal, mi) in reply.medals ?? []" :key="mi" class="rounded-[3px] px-1 py-[2px] text-[10px] leading-none text-white" :style="medalStyle(medal.color)">{{ medal.level }} {{ medal.name }}</span>
            <LevelBadge :level="reply.level" />
            <span v-if="isUp(reply)" class="rounded-[3px] bg-[#fb7299] px-1 py-[2px] text-[10px] font-medium leading-none text-white">UP</span>
          </div>

          <p class="mt-1 whitespace-pre-line text-[15px] leading-7 text-inkMain" v-html="rich(reply.content)"></p>

          <div v-if="reply.pictures?.length" class="mt-2 flex flex-wrap gap-1">
            <img v-for="(pic, pi) in reply.pictures" :key="pi" :src="picLocal(reply, pi)" referrerpolicy="no-referrer" class="h-24 w-24 cursor-zoom-in rounded object-cover" loading="lazy" @click="openZoom(reply, pi)" />
          </div>

          <div class="mt-2 flex items-center gap-5 text-xs text-inkFaint">
            <span>#{{ ri + 1 }}</span>
            <span>{{ timeText(reply.time) }}</span>
            <span v-if="reply.ipLocation">IP属地：{{ reply.ipLocation }}</span>
            <button class="flex items-center gap-1 transition" :class="liked.has(reply.rpid) ? 'text-stationPink' : 'hover:text-inkMain'" @click="toggleLike(reply)">
              <svg viewBox="0 0 24 24" class="h-3.5 w-3.5" :fill="liked.has(reply.rpid) ? 'currentColor' : 'none'" stroke="currentColor" stroke-width="2">
                <path d="M7 10v11H4a1 1 0 0 1-1-1v-9a1 1 0 0 1 1-1h3zm0 0l4-7a2.5 2.5 0 0 1 2.5 2.5V9H20a2 2 0 0 1 2 2.3l-1.2 7A2 2 0 0 1 18.8 20H7" stroke-linecap="round" stroke-linejoin="round" />
              </svg>
              {{ likeCount(reply) }}
            </button>
          </div>
          <div v-if="reply.upLiked" class="mt-1.5">
            <span class="inline-block rounded-md bg-[#f4f5f6] px-2 py-[3px] text-xs text-inkSub">UP主觉得很赞</span>
          </div>

          <!-- 楼中楼 -->
          <div v-if="replyCountOf(reply) > 0" class="mt-3 space-y-3">
            <div v-for="sub in visibleSubs(reply)" :key="sub.rpid" class="flex gap-2.5">
              <AvatarImg :face="sub.face" :face-local="sub.faceLocal" :name="sub.name" class="mt-0.5 h-7 w-7 shrink-0 rounded-full object-cover bg-[#f1f2f3] text-[10px] text-inkFaint" />
              <div class="min-w-0 flex-1">
                <div class="flex flex-wrap items-center gap-1.5">
                  <span class="text-[13px]" :class="isUp(sub) ? 'text-inkMain' : 'text-stationPink'">{{ sub.name }}</span>
                  <span v-for="(medal, mi) in sub.medals ?? []" :key="mi" class="rounded-[3px] px-1 py-[1px] text-[9px] leading-none text-white" :style="medalStyle(medal.color)">{{ medal.level }} {{ medal.name }}</span>
                  <LevelBadge :level="sub.level" class="h-3" />
                  <span v-if="isUp(sub)" class="rounded-[3px] bg-[#fb7299] px-1 py-[1px] text-[9px] font-medium leading-none text-white">UP</span>
                </div>
                <p class="mt-0.5 whitespace-pre-line text-sm leading-6 text-inkMain">
                  <template v-if="sub.parentName"><span class="text-inkMain">回复 </span><span class="text-stationBlue">@{{ sub.parentName }}</span><span class="text-inkMain">：</span></template>
                  <span v-html="rich(sub.content)"></span>
                </p>
                <div class="mt-1 flex items-center gap-5 text-xs text-inkFaint">
                  <span>{{ timeText(sub.time) }}</span>
                  <span v-if="sub.ipLocation">IP属地：{{ sub.ipLocation }}</span>
                  <button class="flex items-center gap-1 transition" :class="liked.has(sub.rpid) ? 'text-stationPink' : 'hover:text-inkMain'" @click="toggleLike(sub)">
                    <svg viewBox="0 0 24 24" class="h-3 w-3" :fill="liked.has(sub.rpid) ? 'currentColor' : 'none'" stroke="currentColor" stroke-width="2">
                      <path d="M7 10v11H4a1 1 0 0 1-1-1v-9a1 1 0 0 1 1-1h3zm0 0l4-7a2.5 2.5 0 0 1 2.5 2.5V9H20a2 2 0 0 1 2 2.3l-1.2 7A2 2 0 0 1 18.8 20H7" stroke-linecap="round" stroke-linejoin="round" />
                    </svg>
                    {{ likeCount(sub) }}
                  </button>
                </div>
                <div v-if="sub.upLiked" class="mt-1">
                  <span class="inline-block rounded-md bg-[#f4f5f6] px-1.5 py-[2px] text-[11px] text-inkSub">UP主觉得很赞</span>
                </div>
              </div>
            </div>

            <template v-if="replyCountOf(reply) > 3">
              <button
                v-if="!floors[reply.rpid]?.open"
                class="text-[13px] text-stationBlue transition hover:opacity-80"
                :disabled="floors[reply.rpid]?.loading"
                @click="toggleFloor(reply)"
              >
                {{ floors[reply.rpid]?.loading ? '加载中……' : `共 ${replyCountOf(reply)} 条回复，点击查看` }}
              </button>
              <button
                v-else-if="floors[reply.rpid]?.hasMore"
                class="text-[13px] text-stationBlue transition hover:opacity-80"
                :disabled="floors[reply.rpid]?.loading"
                @click="fetchSubs(reply, (floors[reply.rpid]?.page ?? 0) + 1)"
              >
                {{ floors[reply.rpid]?.loading ? '加载中……' : '查看更多回复' }}
              </button>
            </template>
          </div>
        </div>
      </div>
    </div>

    <!-- 图片放大：公共灯箱（点遮罩/Esc 关，多图 ←/→ 翻） -->
    <ImageLightbox v-if="zoom" :images="zoom.images" :index="zoom.index" @close="zoom = null" />
  </section>
</template>


