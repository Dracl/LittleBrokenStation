<script setup>
import { computed, ref, watch } from 'vue'
import ImageLightbox from './ImageLightbox.vue'
import AvatarImg from './AvatarImg.vue'
import LevelBadge from './LevelBadge.vue'
import UpBadge from './UpBadge.vue'
import MedalBadge from './MedalBadge.vue'
import CollapsibleRichText from './CollapsibleRichText.vue'
import BIcon from './BIcon.vue'
import { biDynLike, biDynLikeFill } from '../assets/bicons'

const props = defineProps({
  src: { type: String, default: null },
  videoId: { type: Number, default: null },
  upMid: { type: [String, Number], default: null },
})

const snapshot = ref(null)
const state = ref('idle') // idle | loading | ready | empty | failed
const emoteMap = ref({}) // 转义符 → /emotes/xx.png（下载失败时为远程热链）
const emoteMeta = ref({}) // 转义符 → { jumpTitle, packageId, … }：B 站没给 URL 那批靠它念对名字，老快照没这字段就空着

// 正文渲染交给 CollapsibleRichText（它内部走 utils/emote 同一根管线 + 超行折叠）。

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

// 代次护城（照 VideoPage 的 relatedSeq）：切 P 时旧评论表的迟到回包不能盖新页
let loadSeq = 0

async function load(src) {
  const seq = ++loadSeq
  if (!src) {
    state.value = 'empty'
    snapshot.value = null
    return
  }

  state.value = 'loading'
  floors.value = {}
  try {
    const res = await fetch(src)
    if (seq !== loadSeq) return
    if (!res.ok) {
      state.value = 'failed'
      return
    }

    const json = await res.json()
    if (seq !== loadSeq) return
    const list = Array.isArray(json.replies) ? json.replies : []
    if (!list.length && !json.top) {
      snapshot.value = json
      state.value = 'empty'
      return
    }

    snapshot.value = json
    emoteMap.value = json.emotes ?? {}
    emoteMeta.value = json.emoteMeta ?? {}
    state.value = 'ready'
    autoLoadSmallFloors()
  } catch {
    if (seq !== loadSeq) return
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
  const srcAtRequest = props.src // 这发楼子是为哪一页拉的：回包迟到时只对得上号才准写进 reply
  try {
    const res = await fetch(`/api/videos/${props.videoId}/comments/subs`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ rpid: reply.rpid, page }),
    })
    const body = await res.json().catch(() => null)
    if (props.src !== srcAtRequest) return
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
      <div v-if="topReply" class="rounded-xl bg-[#f6f7f8] dark:bg-[#23262e] p-4">
        <div class="flex gap-3">
          <AvatarImg :face="topReply.face" :face-local="topReply.faceLocal" :name="topReply.name" class="h-11 w-11 shrink-0 rounded-full object-cover bg-white dark:bg-white/10 text-sm text-inkFaint" />
          <div class="min-w-0 flex-1">
            <div class="flex flex-wrap items-center gap-1.5">
              <span class="text-[15px]" :class="isUp(topReply) ? 'text-stationPink' : 'text-inkMain'">{{ topReply.name }}</span>
              <MedalBadge v-for="(medal, mi) in topReply.medals ?? []" :key="mi" :level="medal.level" :name="medal.name" :color="medal.color" :size="13.8" />
              <LevelBadge :level="topReply.level" />
              <!-- UP 标只给视频作者本人（isUp 拿 mid 对 props.upMid）：B 站就这一枚，以前无条件渲染成了全员 UP -->
              <UpBadge v-if="isUp(topReply)" />
            </div>
            <CollapsibleRichText
              :text="topReply.content"
              :emotes="emoteMap"
              :emote-meta="emoteMeta"
              content-class="mt-1.5 whitespace-pre-line text-[15px] leading-7 text-inkMain"
            >
              <template #prefix>
                <span class="mr-1.5 inline-flex h-[18px] w-[30px] items-center justify-center rounded-[3px] border border-stationPink align-middle -translate-y-0.5 text-[12px] leading-none text-stationPink">置顶</span>
              </template>
            </CollapsibleRichText>
            <div v-if="topReply.pictures?.length" class="mt-2 flex flex-wrap gap-1">
              <img v-for="(pic, pi) in topReply.pictures" :key="pi" :src="picLocal(topReply, pi)" alt="评论配图 {{ pi + 1 }}" referrerpolicy="no-referrer" class="h-24 w-24 cursor-zoom-in rounded object-cover" loading="lazy" @click="openZoom(topReply, pi)" />
            </div>
            <div class="mt-2 flex items-center gap-5 text-xs text-inkFaint">
              <span>{{ timeText(topReply.time) }}</span>
              <span v-if="topReply.ipLocation">IP属地：{{ topReply.ipLocation }}</span>
              <button class="flex items-center gap-1 transition" :class="liked.has(topReply.rpid) ? 'text-stationPink' : 'hover:text-inkMain'" @click="toggleLike(topReply)">
                <BIcon :meta="liked.has(topReply.rpid) ? biDynLikeFill : biDynLike" class="h-3.5 w-3.5 shrink-0" />
                {{ likeCount(topReply) }}
              </button>
            </div>
            <div v-if="topReply.upLiked" class="mt-1.5">
              <span class="inline-block rounded-md bg-[#f4f5f6] dark:bg-white/5 px-2 py-[3px] text-xs text-inkSub">UP主觉得很赞</span>
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
        <AvatarImg :face="reply.face" :face-local="reply.faceLocal" :name="reply.name" class="h-11 w-11 shrink-0 rounded-full object-cover bg-[#f1f2f3] dark:bg-white/10 text-sm text-inkFaint" />

        <div class="min-w-0 flex-1">
          <div class="flex flex-wrap items-center gap-1.5">
            <span class="text-[15px]" :class="isUp(reply) ? 'text-stationPink' : 'text-inkMain'">{{ reply.name }}</span>
            <MedalBadge v-for="(medal, mi) in reply.medals ?? []" :key="mi" :level="medal.level" :name="medal.name" :color="medal.color" :size="13.8" />
            <LevelBadge :level="reply.level" />
            <UpBadge v-if="isUp(reply)" />
          </div>

          <CollapsibleRichText :text="reply.content" :emotes="emoteMap" :emote-meta="emoteMeta" content-class="mt-1 whitespace-pre-line text-[15px] leading-7 text-inkMain" />

          <div v-if="reply.pictures?.length" class="mt-2 flex flex-wrap gap-1">
            <img v-for="(pic, pi) in reply.pictures" :key="pi" :src="picLocal(reply, pi)" alt="评论配图 {{ pi + 1 }}" referrerpolicy="no-referrer" class="h-24 w-24 cursor-zoom-in rounded object-cover" loading="lazy" @click="openZoom(reply, pi)" />
          </div>

          <div class="mt-2 flex items-center gap-5 text-xs text-inkFaint">
            <span>#{{ ri + 1 }}</span>
            <span>{{ timeText(reply.time) }}</span>
            <span v-if="reply.ipLocation">IP属地：{{ reply.ipLocation }}</span>
            <button class="flex items-center gap-1 transition" :class="liked.has(reply.rpid) ? 'text-stationPink' : 'hover:text-inkMain'" @click="toggleLike(reply)">
              <BIcon :meta="liked.has(reply.rpid) ? biDynLikeFill : biDynLike" class="h-3.5 w-3.5 shrink-0" />
              {{ likeCount(reply) }}
            </button>
          </div>
          <div v-if="reply.upLiked" class="mt-1.5">
            <span class="inline-block rounded-md bg-[#f4f5f6] dark:bg-white/5 px-2 py-[3px] text-xs text-inkSub">UP主觉得很赞</span>
          </div>

          <!-- 楼中楼 -->
          <div v-if="replyCountOf(reply) > 0" class="mt-3 space-y-3">
            <div v-for="sub in visibleSubs(reply)" :key="sub.rpid" class="flex gap-2.5">
              <AvatarImg :face="sub.face" :face-local="sub.faceLocal" :name="sub.name" class="mt-0.5 h-7 w-7 shrink-0 rounded-full object-cover bg-[#f1f2f3] dark:bg-white/10 text-[10px] text-inkFaint" />
              <div class="min-w-0 flex-1">
                <div class="flex flex-wrap items-center gap-1.5">
                  <!-- B 站口径：UP 主本人昵称才吃主题粉，普通观众是正文色（以前正好反着） -->
                  <span class="text-[13px]" :class="isUp(sub) ? 'text-stationPink' : 'text-inkMain'">{{ sub.name }}</span>
                  <MedalBadge v-for="(medal, mi) in sub.medals ?? []" :key="mi" :level="medal.level" :name="medal.name" :color="medal.color" :size="12" />
                  <LevelBadge :level="sub.level" :size="10.5" />
                  <UpBadge v-if="isUp(sub)" :size="11.9" />
                </div>
                <CollapsibleRichText :text="sub.content" :emotes="emoteMap" :emote-meta="emoteMeta" content-class="mt-0.5 whitespace-pre-line text-sm leading-6 text-inkMain">
                  <template v-if="sub.parentName" #prefix>
                    <span class="text-inkMain">回复 </span><span class="text-stationBlue">@{{ sub.parentName }}</span><span class="text-inkMain">：</span>
                  </template>
                </CollapsibleRichText>
                <div class="mt-1 flex items-center gap-5 text-xs text-inkFaint">
                  <span>{{ timeText(sub.time) }}</span>
                  <span v-if="sub.ipLocation">IP属地：{{ sub.ipLocation }}</span>
                  <button class="flex items-center gap-1 transition" :class="liked.has(sub.rpid) ? 'text-stationPink' : 'hover:text-inkMain'" @click="toggleLike(sub)">
                    <BIcon :meta="liked.has(sub.rpid) ? biDynLikeFill : biDynLike" class="h-3 w-3 shrink-0" />
                    {{ likeCount(sub) }}
                  </button>
                </div>
                <div v-if="sub.upLiked" class="mt-1">
                  <span class="inline-block rounded-md bg-[#f4f5f6] dark:bg-white/5 px-1.5 py-[2px] text-[11px] text-inkSub">UP主觉得很赞</span>
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


