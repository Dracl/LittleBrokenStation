<script setup>
import { mentionHtml } from '../utils/mention'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import TopBar from '../components/TopBar.vue'
import VideoPlayer from '../components/VideoPlayer.vue'
import CommentSection from '../components/CommentSection.vue'
import UpCard from '../components/UpCard.vue'
import PageList from '../components/PageList.vue'
import VideoActions from '../components/VideoActions.vue'
import DanmakuList from '../components/DanmakuList.vue'
import UpNextList from '../components/UpNextList.vue'

const route = useRoute()
const data = ref(null)
const loadFailed = ref(false)
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
    loadFailed.value = false
  }
  try {
    const res = await fetch(`/api/watch/${route.params.bvid}`)
    if (res.ok) {
      data.value = await res.json()
      loadRelated(data.value.id)
      if (!silent) {
        const firstPlayable = data.value.pages.find((p) => p.videoUrl) ?? data.value.pages[0]
        activeIndex.value = firstPlayable ? firstPlayable.index : 0
      }
    } else if (!silent) {
      data.value = null
      loadFailed.value = true
    }
  } catch {
    if (!silent) {
      data.value = null
      loadFailed.value = true
    }
  } finally {
    if (!silent) loading.value = false
  }
}

function selectPage(index) {
  activeIndex.value = index
  // 换源后等 autoplay 无望，交给用户按播放；这里只把弹幕时间归零自然发生
}

// ─── 接下来播放：同 UP 优先的站内推荐，随 BV 切换刷新 ───
const related = ref([])

async function loadRelated(id) {
  try {
    const res = await fetch(`/api/videos/${id}/related`)
    related.value = res.ok ? await res.json() : []
  } catch {
    related.value = []
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

onBeforeUnmount(() => {
  if (codecPoll) clearInterval(codecPoll)
})

onMounted(() => {
  loadWatch()
})
watch(() => route.params.bvid, () => loadWatch())

// 播放器壳宽（VideoPlayer 量好的）：两栏内容用它封顶，保证不超出视频卡片
const playerShellW = ref(0)
</script>

<template>
  <div class="min-h-screen">
    <TopBar />

    <main class="mx-auto max-w-[1600px] px-6 py-5">
      <p v-if="loading" class="py-20 text-center text-sm text-inkSub">正在打开馆藏……</p>
      <p v-else-if="loadFailed" class="py-20 text-center text-sm text-inkSub">馆藏里没有这个 BV 号，或者已经被删了</p>

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
            @next="playNextPage"
            @request-codec="requestCodec"
            @shell-width="playerShellW = $event"
          />

          <div class="mt-[30px] flex justify-center">
            <div
              class="flex w-full flex-col items-start gap-6 lg:flex-row"
              :style="playerShellW ? { maxWidth: `${playerShellW}px` } : undefined"
            >
            <!-- 左栏：标题→数据行→互动栏→简介→评论 -->
            <div class="min-w-0 flex-1">
              <!-- 标题与档案 -->
              <h1 class="text-xl font-semibold leading-8 text-inkMain">{{ data.title ?? data.bvid }}</h1>
              <div class="mt-1.5 flex flex-wrap items-center gap-x-4 gap-y-1 text-xs text-inkFaint">
                <span>{{ data.bvid }}</span>
                <span v-if="data.upName">UP 主：{{ data.upName }}</span>
                <span v-if="data.zone">{{ data.zone }}</span>
                <span v-if="data.publishText">{{ data.publishText }}</span>
                <span v-if="data.viewCountText">B 站当年播放 {{ data.viewCountText }}</span>
                <span>入站：{{ new Date(data.createdAtUtc).toLocaleString('zh-CN') }}</span>
              </div>

              <!-- B 站式互动栏：真数来自 view 接口快照，采不到显「—」 -->
              <VideoActions
                class="mt-3 border-t border-black/5 pt-3"
                :stat="{ like: data.statLike, coin: data.statCoin, favorite: data.statFavorite, share: data.statShare, reply: data.statReply }"
              />

              <p
                v-if="data.description"
                class="mt-3 whitespace-pre-line rounded-lg bg-cardBg px-4 py-3 text-sm leading-6 text-inkSub"
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
              <UpNextList v-if="related.length" :items="related" />
            </aside>
            </div>
          </div>
        </div>
      </template>
    </main>
  </div>
</template>
