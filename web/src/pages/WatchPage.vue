<script setup>
import { mentionHtml } from '../utils/mention'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import TopBar from '../components/TopBar.vue'
import VideoPlayer from '../components/VideoPlayer.vue'
import CommentSection from '../components/CommentSection.vue'
import UpCard from '../components/UpCard.vue'
import PageList from '../components/PageList.vue'
import VideoActions from '../components/VideoActions.vue'
import DanmakuList from '../components/DanmakuList.vue'
import UpNextList from '../components/UpNextList.vue'
import EmptyTv from '../components/EmptyTv.vue'
import { member, loginDialogOpen } from '../memberStore'
import { submitDialogOpen } from '../submitStore'

const route = useRoute()
const router = useRouter()
const data = ref(null)
const gateState = ref(null) // null=正常 | 'login'=访客挡在放映厅门外 | 'missing'=馆藏确无此片
const loading = ref(true)
const activeIndex = ref(0)

const currentPage = computed(() => {
  const pages = data.value?.pages ?? []
  return pages.find((p) => p.index === activeIndex.value) ?? pages[0] ?? null
})

const hasNextPage = computed(() => {
  const pages = data.value?.pages ?? []
  return pages.some((p) => p.index > activeIndex.value && p.videoUrl)
})

function playNextPage() {
  const pages = data.value?.pages ?? []
  const next = pages.find((p) => p.index > activeIndex.value && p.videoUrl)
  if (next) activeIndex.value = next.index
}

async function loadWatch(silent = false) {
  if (!silent) {
    loading.value = true
    gateState.value = null
  }
  try {
    const res = await fetch(`/api/watch/${route.params.bvid}`)
    if (res.ok) {
      data.value = await res.json()
      loadRelated(data.value.id)
      loadLocalDanmaku()
      if (!openReported) {
        // 进入并加载成功：报一枪 watch.open（同一 BV 一次，换 P 不重报）
        openReported = true
        reportEvent('watch.open')
      }
      if (!silent) {
        const firstPlayable = data.value.pages.find((p) => p.videoUrl) ?? data.value.pages[0]
        activeIndex.value = firstPlayable ? firstPlayable.index : 0
      }
    } else if (!silent) {
      data.value = null
      // 门禁与查无此片是两回事：401=还没站籍，别拿「不存在」糊弄门外的客人
      gateState.value = res.status === 401 ? 'login' : 'missing'
    }
  } catch {
    if (!silent) {
      data.value = null
      gateState.value = 'missing'
    }
  } finally {
    if (!silent) loading.value = false
  }
}

function selectPage(index) {
  activeIndex.value = index
  // 换源后等 autoplay 无望，交给用户按播放；这里只把弹幕时间归零自然发生
}

// ─── 访客下载：播放器报什么编码就下什么编码，主版本缺省 ───
const playerCodec = ref(null)

const downloadInfo = computed(() => {
  const p = currentPage.value
  if (!p) return null
  const codec = playerCodec.value ?? p.codec ?? null
  if (codec && codec !== p.codec) {
    const row = (p.codecVersions ?? []).find((c) => c.codec === codec && c.status === 'Arrived')
    return row ? { codec, bytes: row.bytes ?? null } : null
  }
  return { codec, bytes: p.mainBytes ?? null }
})

function fmtDlBytes(b) {
  if (!b) return ''
  const units = ['B', 'KB', 'MB', 'GB']
  let v = b
  let i = 0
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024
    i++
  }
  return `${v >= 100 || i === 0 ? Math.round(v) : v.toFixed(1)}${units[i]}`
}

function downloadCurrent() {
  const p = currentPage.value
  if (!p || !downloadInfo.value) return
  const params = new URLSearchParams({ pageIdx: String(p.index) })
  if (downloadInfo.value.codec && downloadInfo.value.codec !== p.codec) {
    params.set('codec', downloadInfo.value.codec)
  }
  const a = document.createElement('a')
  a.href = `/api/videos/${data.value.id}/download?${params}`
  a.download = ''
  document.body.appendChild(a)
  a.click()
  a.remove()
}

// ─── 接下来播放：同 UP 优先的站内推荐，随 BV 切换刷新 ───
const related = ref([])

// 自动连播：本页（含全部 P）播完自动接接下来播放第一条；开关记忆在本地
const autoRelated = ref(localStorage.getItem('lbs.autoRelated') !== '0')
watch(autoRelated, (v) => localStorage.setItem('lbs.autoRelated', v ? '1' : '0'))

function playNextVideo() {
  const next = related.value.find((v) => v.bvid && v.bvid !== route.params.bvid)
  if (next) router.push(`/watch/${next.bvid}`)
}

async function loadRelated(id) {
  try {
    const res = await fetch(`/api/videos/${id}/related`)
    related.value = res.ok ? await res.json() : []
  } catch {
    related.value = []
  }
}

// ─── 站内弹幕：存分站库，刷新不丢；自己发的点一下可删 ───
const localDanmaku = ref([])

async function loadLocalDanmaku() {
  if (!data.value) return
  try {
    const res = await fetch(`/api/videos/${data.value.id}/danmaku`)
    localDanmaku.value = res.ok ? await res.json() : []
  } catch {
    localDanmaku.value = []
  }
}

const localForPage = computed(() => {
  const idx = currentPage.value?.index ?? 0
  return localDanmaku.value.filter((d) => d.pageIdx === idx)
})

async function onSendDanmaku({ text, pageIdx, t }) {
  if (!data.value) return
  try {
    const res = await fetch(`/api/videos/${data.value.id}/danmaku`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ pageIdx, t, text }),
    })
    const body = await res.json().catch(() => null)
    if (res.ok && body?.id) {
      localDanmaku.value.push({ id: body.id, pageIdx, t, text })
    }
  } catch {
    /* 后端没应答就静默：本地体验优先，不弹窗打扰看片 */
  }
}

async function onDeleteDanmaku(id) {
  localDanmaku.value = localDanmaku.value.filter((d) => d.id !== id)
  try {
    await fetch(`/api/videos/danmaku/${id}`, { method: 'DELETE' })
  } catch {
    /* 删不动拉倒，重进页面会自愈 */
  }
}

// ─── 编码加档：点单后轮询 watch，直到该编码所有分 P 都脱离 Downloading ───
let codecPoll = null

async function requestCodec(codec) {
  if (!data.value) return
  try {
    await fetch(`/api/videos/${data.value.id}/codecs`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ codec }),
    })
  } catch {
    return // 后端没应答，不启动轮询
  }
  startCodecPoll(codec)
}

function startCodecPoll(codec) {
  if (codecPoll) clearInterval(codecPoll)
  let ticks = 0
  codecPoll = setInterval(async () => {
    ticks += 1
    await loadWatch(true)
    const rows = (data.value?.pages ?? []).flatMap((p) => p.codecVersions ?? []).filter((c) => c.codec === codec)
    const settled = rows.length > 0 && rows.every((c) => c.status !== 'Downloading')
    if (settled || ticks > 240) {
      clearInterval(codecPoll)
      codecPoll = null
    }
  }, 5000)
}

// ─── 播放行为上报：只走白名单事件，失败一律静默（看片体验优先）───
let openReported = false
let heartTimer = null

function reportEvent(name, payload = null) {
  const bvid = route.params.bvid
  if (!bvid) return
  fetch('/api/events/ingest', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ events: [{ name, subjectType: 'video', subjectId: String(bvid), payload }] }),
  }).catch(() => {})
}

// play/pause 不冒泡，挂在 document 上用捕获监听兜住播放器内部的 <video>
function onMediaPlay() {
  reportEvent('watch.play')
}

function onMediaPause() {
  reportEvent('watch.pause')
}

onBeforeUnmount(() => {
  if (codecPoll) clearInterval(codecPoll)
  document.removeEventListener('play', onMediaPlay, true)
  document.removeEventListener('pause', onMediaPause, true)
  if (heartTimer) {
    clearInterval(heartTimer)
    heartTimer = null
  }
  reportEvent('watch.leave')
})

onMounted(() => {
  loadWatch()
  document.addEventListener('play', onMediaPlay, true)
  document.addEventListener('pause', onMediaPause, true)
  // 播放中每 30 秒一条心跳，带当前秒数；没在播就不报，不充数
  heartTimer = setInterval(() => {
    const v = document.querySelector('video')
    if (v && !v.paused && !v.ended) reportEvent('watch.heartbeat', { t: Math.floor(v.currentTime) })
  }, 30000)
})
watch(() => route.params.bvid, () => {
  openReported = false
  loadWatch()
})

// 播放器壳宽（VideoPlayer 量好的）：两栏内容用它封顶，保证不超出视频卡片
const playerShellW = ref(0)
</script>

<template>
  <div class="min-h-screen">
    <TopBar />

    <main class="mx-auto max-w-[1600px] px-6 py-5">
      <p v-if="loading" class="py-20 text-center text-sm text-inkSub">正在打开馆藏……</p>

      <!-- 访客吃门禁：B 站式迷路小电视，指路去站籍 -->
      <EmptyTv
        v-else-if="gateState === 'login'"
        title="啊叻？想看视频？"
        subtitle="看片、弹幕、评论区都是分站成员的待遇——花两秒领个席位，或直接把门钥匙输进来"
      >
        <button
          class="rounded-lg bg-gradient-to-r from-stationPink to-[#f994b6] px-6 py-2 text-sm font-medium text-white shadow-md shadow-stationPink/30 transition hover:-translate-y-px hover:shadow-lg hover:shadow-stationPink/40 active:translate-y-0"
          @click="loginDialogOpen = true"
        >
          快速登录
        </button>
        <RouterLink
          to="/register"
          class="rounded-lg border border-lineSoft px-6 py-2 text-sm text-inkSub transition hover:border-stationPink hover:text-stationPink"
        >
          注册个站籍
        </RouterLink>
      </EmptyTv>

      <!-- 馆藏确无此片：同款小电视，给出路与投稿两条路 -->
      <EmptyTv
        v-else-if="gateState === 'missing'"
        title="啊叻？找不到这部视频"
        subtitle="馆藏里没有这个 BV 号，或者已经被删了"
      >
        <button
          class="rounded-lg bg-gradient-to-r from-stationPink to-[#f994b6] px-6 py-2 text-sm font-medium text-white shadow-md shadow-stationPink/30 transition hover:-translate-y-px hover:shadow-lg hover:shadow-stationPink/40 active:translate-y-0"
          @click="router.push('/')"
        >
          回分站大厅
        </button>
        <button
          v-if="member"
          class="rounded-lg border border-lineSoft px-6 py-2 text-sm text-inkSub transition hover:border-stationPink hover:text-stationPink"
          @click="submitDialogOpen = true"
        >
          投稿这条视频
        </button>
      </EmptyTv>

      <template v-else-if="data">
        <!-- B 站宽屏模式同款：播放器单独通栏，其下才分两栏 -->
        <div>
          <VideoPlayer
            :page="currentPage"
            :status="data.status"
            :title="data.title"
            :bvid="data.bvid"
            :aid="data.aid"
            :danmaku-count="data.danmakuCount"
            :has-next-page="hasNextPage"
            :auto-related="autoRelated"
            :local-items="localForPage"
            @next="playNextPage"
            @next-video="playNextVideo"
            @request-codec="requestCodec"
            @shell-width="playerShellW = $event"
            @codec-change="playerCodec = $event"
            @send-danmaku="onSendDanmaku"
            @delete-danmaku="onDeleteDanmaku"
          />

          <div class="mt-[30px] flex justify-center">
            <div
              class="flex w-full flex-col items-start gap-6 lg:flex-row"
              :style="playerShellW ? { maxWidth: `${playerShellW}px` } : undefined"
            >
            <!-- 左栏：标题→数据行→互动栏→简介→评论 -->
            <div class="min-w-0 flex-1">
              <!-- 标题与档案：B 站同款，标题重、数据行带小图标疏朗排开 -->
              <h1 class="text-[22px] font-semibold leading-8 text-inkMain">{{ data.title ?? data.bvid }}</h1>
              <div class="mt-2 flex flex-wrap items-center gap-x-5 gap-y-1.5 text-xs text-inkFaint">
                <span v-if="data.viewCountText" class="flex items-center gap-1.5">
                  <svg viewBox="0 0 16 16" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="1.3">
                    <rect x="1.2" y="2.6" width="13.6" height="10.8" rx="2" />
                    <path d="M6.6 6 10 8 6.6 10z" fill="currentColor" stroke="none" />
                  </svg>
                  {{ data.viewCountText }}
                </span>
                <span class="flex items-center gap-1.5">
                  <svg viewBox="0 0 16 16" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round">
                    <rect x="1.2" y="2.6" width="13.6" height="10.8" rx="2" />
                    <path d="M3.8 5.8h5.4M3.8 8.3h4" />
                  </svg>
                  {{ data.danmakuCount ?? 0 }}
                </span>
                <span v-if="data.publishText">{{ data.publishText }}</span>
                <span>{{ data.bvid }}</span>
                <span v-if="data.upName">UP 主：{{ data.upName }}</span>
                <span>入站：{{ new Date(data.createdAtUtc).toLocaleString('zh-CN') }}</span>
              </div>

              <!-- B 站式互动栏：真数来自 view 接口快照，采不到显「—」；右侧访客下载 -->
              <div class="mt-6 flex items-center justify-between gap-4 border-t border-b border-black/5 py-2">
                <VideoActions :stat="{ like: data.statLike, coin: data.statCoin, favorite: data.statFavorite, share: data.statShare, reply: data.statReply }" />
                <button
                  class="flex shrink-0 items-center gap-1.5 rounded-full border border-stationPink px-4 py-1.5 text-sm text-stationPink transition hover:bg-stationPink hover:text-white disabled:cursor-not-allowed disabled:border-black/10 disabled:text-inkFaint disabled:hover:bg-transparent"
                  :disabled="!currentPage?.videoUrl || !downloadInfo"
                  :title="downloadInfo ? `把正在看的版本（${(downloadInfo.codec ?? '主版本').toUpperCase()}）存到本地` : '这一 P 还没下载，没东西可拿'"
                  @click="downloadCurrent"
                >
                  <svg viewBox="0 0 24 24" class="h-4 w-4" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                    <path d="M12 4v11m0 0 4-4m-4 4-4-4M4 19h16" />
                  </svg>
                  <span>
                    下载{{ downloadInfo?.codec ? ' · ' + downloadInfo.codec.toUpperCase() : '' }}{{ downloadInfo?.bytes ? ' · ' + fmtDlBytes(downloadInfo.bytes) : '' }}
                  </span>
                </button>
              </div>

              <p
                v-if="data.description"
                class="mt-5 whitespace-pre-line text-sm leading-7 text-inkSub"
              >
                <span v-html="mentionHtml(data.description)"></span>
              </p>

              <CommentSection class="mt-5 border-t border-black/5 pt-5" :src="data.commentsUrl" :video-id="data.id" :up-mid="data.upMid" />
            </div>

            <!-- 右栏（不吸顶，随页滚动）：UP 卡、弹幕列表、分 P 列表、接下来播放 -->
            <aside class="w-full shrink-0 space-y-3 lg:-mt-3 lg:w-[360px] xl:w-[400px]">
              <UpCard
                v-if="data.upName"
                :up-name="data.upName"
                :up-mid="data.upMid"
                :up-face="data.upFace"
                :up-sign="data.upSign"
                :up-fans="data.upFans"
              />
              <DanmakuList v-if="currentPage?.danmakuUrl" :src="currentPage.danmakuUrl" />
              <PageList
                v-if="data.pages.length > 1"
                :pages="data.pages"
                :active-index="activeIndex"
                @select="selectPage"
              />
              <UpNextList v-if="related.length" :items="related" :autoplay="autoRelated" @update:autoplay="autoRelated = $event" />
            </aside>
            </div>
          </div>
        </div>
      </template>

      <ShareDialog v-if="shareFor" :target="shareFor" @close="shareFor = null" @done="onShared" />
      <p v-if="shareToast" class="fixed bottom-6 left-1/2 z-50 -translate-x-1/2 rounded-full bg-inkMain/85 px-4 py-2 text-xs text-white shadow-lg">{{ shareToast }}</p>
    </main>
  </div>
</template>
