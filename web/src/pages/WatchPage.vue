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
        // 续播只在进页（含换 BV）这一刻问一次：静默刷新（编码轮询这些）不能把正在看的位子抓走。
        loadResumePoint(data.value.id, activeIndex.value)
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

// ─── 续播：进页问一次「上次看到哪」，够条件就把播放器丢过去并在播放器下缘浮一枚小泡 ───
// 判据都在这一层：刚开头（≤10 秒）与接近片尾（≥95%）都不跳；位置不在落地那一分 P 上也不跳
// （手动切选集是另一件事，不该被续播插一脚）。真正的 seek 由播放器在元数据就位后落。
const resumeSec = ref(0)
const resumeTip = ref(null) // { sec }：null = 不摆泡
const resumeTipFading = ref(false)
let resumeTipFadeTimer = 0
let resumeTipGoneTimer = 0

function clearResumeTip() {
  if (resumeTipFadeTimer) clearTimeout(resumeTipFadeTimer)
  if (resumeTipGoneTimer) clearTimeout(resumeTipGoneTimer)
  resumeTipFadeTimer = 0
  resumeTipGoneTimer = 0
  resumeTip.value = null
  resumeTipFading.value = false
}

async function loadResumePoint(videoId, pageIndex) {
  resumeSec.value = 0
  clearResumeTip()
  try {
    const res = await fetch(`/api/videos/${videoId}/progress`)
    const p = res.ok ? await res.json() : null
    if (!p) return // 没看过（或后端没应答）：从头播就是正确答案
    if (Number(p.pageIdx) !== pageIndex) return
    const sec = Number(p.positionSec)
    const dur = Number(p.durationSec)
    if (!Number.isFinite(sec) || sec <= 10) return
    if (Number.isFinite(dur) && dur > 0 && sec >= dur * 0.95) return
    resumeSec.value = sec
    resumeTip.value = { sec }
    // 五秒自 fade：先淡出去再从 DOM 里拿掉，不引新依赖。
    resumeTipFadeTimer = setTimeout(() => {
      resumeTipFading.value = true
    }, 4500)
    resumeTipGoneTimer = setTimeout(() => {
      clearResumeTip()
    }, 5000)
  } catch {
    /* 后端打盹：不弹不闹，从头播 */
  }
}

// 泡上那个时间：分:秒，过小时补时（与播放器里的口径一致）。
function clockOf(sec) {
  const n = Math.max(0, Math.floor(Number(sec) || 0))
  const pad = (x) => String(x).padStart(2, '0')
  const h = Math.floor(n / 3600)
  return h > 0 ? `${h}:${pad(Math.floor((n % 3600) / 60))}:${pad(n % 60)}` : `${pad(Math.floor(n / 60))}:${pad(n % 60)}`
}

// ─── 加入合集：成员待遇。面板里勾一勾就进出抽屉；没抽屉当场建。
// 后端 /api/member/collections?bvid= 回我的抽屉并带 contains 标记，勾选即反向操作。
const collectionsOpen = ref(false)
const myCollections = ref(null) // null = 没拉到（在飞）；[] = 确实没建过
const collBusy = ref(false)
const collNotice = ref(null)
const newCollName = ref('')

async function toggleCollections() {
  collectionsOpen.value = !collectionsOpen.value
  if (collectionsOpen.value && myCollections.value === null) await loadMyCollections()
}

async function loadMyCollections() {
  collBusy.value = true
  try {
    const bvid = encodeURIComponent(data.value?.bvid || '')
    const res = await fetch(`/api/member/collections?bvid=${bvid}`)
    const d = res.ok ? await res.json().catch(() => null) : null
    myCollections.value = Array.isArray(d?.collections) ? d.collections : []
  } catch {
    myCollections.value = []
  } finally {
    collBusy.value = false
  }
}

async function toggleCollection(c) {
  if (collBusy.value || !data.value?.bvid) return
  collBusy.value = true
  collNotice.value = null
  try {
    const bvid = encodeURIComponent(data.value.bvid)
    let res
    if (c.contains) {
      res = await fetch(`/api/member/collections/${c.id}/items/${bvid}`, { method: 'DELETE' })
    } else {
      res = await fetch(`/api/member/collections/${c.id}/items`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ bvid: data.value.bvid }),
      })
    }
    const d = await res.json().catch(() => null)
    if (!res.ok) collNotice.value = { kind: 'err', text: d?.message || '抽屉没应' }
    await loadMyCollections()
  } catch {
    collNotice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    collBusy.value = false
  }
}

async function createAndAdd() {
  const name = newCollName.value.trim()
  if (!name || collBusy.value || !data.value?.bvid) return
  collBusy.value = true
  collNotice.value = null
  try {
    const res = await fetch('/api/member/collections', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name }),
    })
    const d = await res.json().catch(() => null)
    if (!res.ok) {
      collNotice.value = { kind: 'err', text: d?.message || '抽屉建不起来' }
      return
    }
    newCollName.value = ''
    const add = await fetch(`/api/member/collections/${d.id}/items`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ bvid: data.value.bvid }),
    })
    if (!add.ok) {
      const ad = await add.json().catch(() => null)
      collNotice.value = { kind: 'err', text: ad?.message || '抽屉建好了，但这部没进去' }
    }
    await loadMyCollections()
  } catch {
    collNotice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    collBusy.value = false
  }
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

// ─── 站内弹幕：存分站库，刷新不丢；自己发的点一下可删，别人发的点一下举报 ───
const localDanmaku = ref([])
const reportedDmIds = ref(new Set()) // 本页已经举报过的弹幕：重拉列表也不让钮变回「举报这条」
const reportingDmIds = ref(new Set()) // 在飞的那几发：网络没回来前连点也不重发

async function loadLocalDanmaku() {
  if (!data.value) return
  try {
    const res = await fetch(`/api/videos/${data.value.id}/danmaku`)
    localDanmaku.value = res.ok ? await res.json() : []
    // 服务端不存举报态（举报进的是 danmaku_reports），这一页的举报印记得自己贴回去。
    for (const d of localDanmaku.value) {
      if (reportedDmIds.value.has(d.id)) d.reported = true
    }
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
      // 服务端把身份一并算好回过来：刚发的这条立刻挂自己的脸与冠，不用等下一轮重拉。
      localDanmaku.value.push({
        id: body.id,
        pageIdx,
        t,
        text,
        fromOwner: body.fromOwner === true,
        face: typeof body.face === 'string' && body.face ? body.face : null,
        canDelete: true,
      })
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

// 举报只递单给站长：不预先藏掉这条（pending 的弹幕照旧飘），也不弹 alert。
async function onReportDanmaku(id) {
  if (!data.value || reportedDmIds.value.has(id) || reportingDmIds.value.has(id)) return
  const item = localDanmaku.value.find((d) => d.id === id)
  if (!item) return
  reportingDmIds.value = new Set([...reportingDmIds.value, id])
  try {
    const res = await fetch(`/api/videos/${data.value.id}/danmaku/${id}/report`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({}), // 最小闭环不填理由（reason 可选，上限 50 字由服务端把关）
    })
    if (res.ok) {
      item.reported = true
      reportedDmIds.value = new Set([...reportedDmIds.value, id])
    }
  } catch {
    /* 后端没应答就静默：与删除的乐观风格一致，不弹窗打扰看片 */
  } finally {
    reportingDmIds.value = new Set([...reportingDmIds.value].filter((v) => v !== id))
  }
}

// ─── 投稿人自管：重拉 B 站快照（评论区 + 弹幕）与删自己的稿 ───
// 可见性由服务端算好的 data.canManage 说了算（投稿人本人 ∥ 站长），前端只是藏钮，真门在端点里。
const manageBusy = ref(false)
const removing = ref(false)
const commentsRev = ref(0)
const notice = ref(null)
let noticeTimer = null

function showNotice(kind, text) {
  notice.value = { kind, text }
  if (noticeTimer) clearTimeout(noticeTimer)
  noticeTimer = setTimeout(() => (notice.value = null), 7000)
}

onBeforeUnmount(() => {
  if (noticeTimer) clearTimeout(noticeTimer)
})

// 弹幕覆写在同一路径，DanmakuLayer 只认 src 变化；换个 ?v= 既顶它重取，又绕过 /media 的 ETag 缓存。
function bumpDanmaku() {
  const page = data.value?.pages?.find((p) => p.index === activeIndex.value)
  if (page?.danmakuUrl) {
    page.danmakuUrl = `${String(page.danmakuUrl).split('?')[0]}?v=${Date.now()}`
  }
}

async function refreshSnapshots() {
  if (manageBusy.value || !data.value) return
  manageBusy.value = true
  const id = data.value.id
  const parts = []
  try {
    const [cmt, dm] = await Promise.all([
      fetch(`/api/videos/${id}/comments`, { method: 'POST' }).then((r) => r.json().catch(() => null).then((b) => ({ ok: r.ok, body: b }))),
      fetch(`/api/videos/${id}/danmaku-refresh`, { method: 'POST' }).then((r) => r.json().catch(() => null).then((b) => ({ ok: r.ok, body: b }))),
    ])
    parts.push(cmt.body?.message ?? (cmt.ok ? '评论已归档' : '评论采集失败'))
    parts.push(dm.body?.message ?? (dm.ok ? 'B 站弹幕已重拉' : '弹幕重拉失败'))
    if (cmt.ok) commentsRev.value += 1
    if (dm.ok) bumpDanmaku()
    const fails = [cmt.ok, dm.ok].filter((ok) => !ok).length
    showNotice(fails === 2 ? 'err' : fails ? 'warn' : 'ok', parts.join('；'))
  } catch {
    showNotice('err', '分站后端没有应答')
  } finally {
    manageBusy.value = false
  }
}

async function removeSubmission() {
  if (!data.value || removing.value) return
  const label = data.value.title || data.value.bvid
  if (!window.confirm(`确定把「${label}」从分站彻底删除？本地文件（视频/弹幕/字幕/封面）会一并清掉，不可恢复。`)) {
    return
  }
  removing.value = true
  try {
    const res = await fetch(`/api/videos/${data.value.id}`, { method: 'DELETE' })
    const body = await res.json().catch(() => null)
    if (res.ok) {
      // 这条馆藏已经不存在了，把用户送回首页卡阵，别留在空页面上。
      router.push('/')
      return
    }
    showNotice('err', body?.message ?? '删不动，稍后再试')
  } catch {
    showNotice('err', '分站后端没有应答')
  } finally {
    removing.value = false
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
  clearResumeTip()
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
        <!-- B 站宽屏模式同款：播放器单独通栏，其下才分两栏。
             小泡的锚定要贴着播放器：单独给播放器+泡套一层 tight relative，
             外层相对定位包住整段时 bottom 会从页底算，泡会飞出去看不见。 -->
        <div>
          <div class="relative">
          <VideoPlayer
            :page="currentPage"
            :status="data.status"
            :title="data.title"
            :bvid="data.bvid"
            :aid="data.aid"
            :video-id="data.id"
            :resume-sec="resumeSec"
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
            @report-danmaku="onReportDanmaku"
          />

          <!-- 续播小泡：站长亲手拖过定位（EditHere 反馈）：贴播放器壳左缘、进度条上方一格。
               横向锁在壳子而非外层容器：播放器限宽居中，窗口越宽容器与壳的左缘裂缝越大，锚容器会出框；
               playerShellW 是 VideoPlayer 报来的实测壳宽，壳居中故其左缘在 50% - 壳宽/2。未报到前退 left-[15px]。10 秒嫌久、6 秒仍嫌久，现行 5 秒自 fade，pointer-events-none 不挡点画面 -->
          <div
            v-if="resumeTip"
            class="pointer-events-none absolute bottom-[122px] left-[15px] z-20 whitespace-nowrap rounded-full border border-white/15 bg-[#1b1d23]/95 px-3.5 py-1.5 text-xs text-white/85 shadow-lg backdrop-blur transition-opacity duration-500"
            :class="resumeTipFading ? 'opacity-0' : 'opacity-100'"
            :style="playerShellW ? { left: `calc(50% - ${playerShellW / 2 - 15}px)` } : undefined"
          >
            上次看到 <span class="font-mono text-white/95">{{ clockOf(resumeTip.sec) }}</span>，已为你继续
          </div>
          </div>

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
              <div class="mt-6 flex items-center justify-between gap-4 border-t border-b border-black/5 dark:border-white/10 py-2">
                <VideoActions :stat="{ like: data.statLike, coin: data.statCoin, favorite: data.statFavorite, share: data.statShare, reply: data.statReply }" />
                <div class="relative flex shrink-0 items-center gap-2">
                  <!-- 加入合集：成员就地收纳，抽屉不够当场建一个 -->
                  <div v-if="member" class="relative">
                    <button
                      class="flex h-9 items-center gap-1.5 rounded-full border px-3 text-xs transition"
                      :class="collectionsOpen ? 'border-stationPink text-stationPink' : 'border-black/10 dark:border-white/10 text-inkSub hover:border-stationPink hover:text-stationPink'"
                      title="把这部收进我的合集"
                      @click="toggleCollections"
                    >
                      <svg viewBox="0 0 24 24" class="h-4 w-4" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round">
                        <path d="M4 6h16M4 12h10M4 18h10" />
                        <path d="M17 14v6M14 17h6" />
                      </svg>
                      合集
                    </button>
                    <div v-if="collectionsOpen" class="absolute right-0 top-11 z-30 w-64 rounded-xl border border-black/10 dark:border-white/10 bg-cardBg p-3 shadow-xl">
                      <p v-if="collBusy && !myCollections?.length" class="py-4 text-center text-[12px] text-inkFaint">正在翻你的抽屉……</p>
                      <p v-else-if="!myCollections?.length" class="py-4 text-center text-[12px] text-inkFaint">你还没有抽屉，下面建一个</p>
                      <label
                        v-for="c in myCollections"
                        :key="c.id"
                        class="flex cursor-pointer items-center gap-2 rounded-lg px-2 py-1.5 text-[12.5px] text-inkMain transition hover:bg-stationPink/5"
                      >
                        <input type="checkbox" :checked="c.contains" :disabled="collBusy" class="accent-[#fb7299]" @change="toggleCollection(c)" />
                        <span class="min-w-0 flex-1 truncate">{{ c.name }}</span>
                        <span class="font-mono text-[10px] text-inkFaint">{{ c.itemCount }}</span>
                      </label>
                      <div class="mt-2 flex items-center gap-1.5 border-t border-black/5 dark:border-white/10 pt-2">
                        <input
                          v-model="newCollName"
                          class="min-w-0 flex-1 rounded-lg border border-black/15 dark:border-white/10 px-2 py-1.5 text-[12px] outline-none focus:border-stationPink"
                          placeholder="新抽屉名字（30 字内）"
                          maxlength="30"
                          @keyup.enter="createAndAdd"
                        />
                        <button class="shrink-0 rounded-lg bg-stationPink px-2.5 py-1.5 text-[12px] text-white transition hover:opacity-90 disabled:opacity-50" :disabled="collBusy" @click="createAndAdd">建</button>
                      </div>
                      <p v-if="collNotice" :class="collNotice.kind === 'ok' ? 'text-emerald-600' : 'text-rose-500'" class="mt-1.5 text-[11px]">{{ collNotice.text }}</p>
                    </div>
                  </div>
                  <!-- 投稿人自管两钮（下载胶囊左侧）：白底描边，hover 才上色，不抢下载胶囊的风头 -->
                  <template v-if="data.canManage">
                    <button
                      class="flex h-9 w-9 shrink-0 items-center justify-center rounded-full border border-black/10 dark:border-white/10 text-inkSub transition hover:border-stationBlue hover:text-stationBlue disabled:cursor-not-allowed disabled:opacity-40"
                      :disabled="manageBusy"
                      title="重新采集评论区快照与 B 站弹幕（分站朋友发的站内弹幕不受影响）"
                      @click="refreshSnapshots"
                    >
                      <svg v-if="!manageBusy" viewBox="0 0 24 24" class="h-4 w-4" fill="none" stroke="currentColor" stroke-width="2">
                        <path d="M21 12a8 8 0 0 1-11.6 7.1L4 21l1.9-5.4A8 8 0 1 1 21 12z" stroke-linecap="round" stroke-linejoin="round" />
                      </svg>
                      <svg v-else viewBox="0 0 24 24" class="h-4 w-4 animate-spin" fill="none" stroke="currentColor" stroke-width="2">
                        <path d="M12 3a9 9 0 1 0 9 9" stroke-linecap="round" />
                      </svg>
                    </button>
                    <button
                      class="flex h-9 w-9 shrink-0 items-center justify-center rounded-full border border-black/10 dark:border-white/10 text-inkSub transition hover:border-rose-500 hover:text-rose-500 disabled:cursor-not-allowed disabled:opacity-40"
                      :disabled="removing"
                      title="删掉这个馆藏（含本地文件），仅投稿人本人与站长可删"
                      @click="removeSubmission"
                    >
                      <svg viewBox="0 0 24 24" class="h-4 w-4" fill="none" stroke="currentColor" stroke-width="2">
                        <path d="M4 7h16M9 7V5a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2m3 0-1 13a1 1 0 0 1-1 1H8a1 1 0 0 1-1-1L6 7" stroke-linecap="round" stroke-linejoin="round" />
                      </svg>
                    </button>
                  </template>
                  <button
                    class="flex shrink-0 items-center gap-1.5 rounded-full border border-stationPink px-4 py-1.5 text-sm text-stationPink transition hover:bg-stationPink hover:text-white disabled:cursor-not-allowed disabled:border-black/10 dark:disabled:border-white/10 disabled:text-inkFaint disabled:hover:bg-transparent"
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
              </div>

              <p
                v-if="data.description"
                class="mt-5 whitespace-pre-line text-sm leading-7 text-inkSub"
              >
                <span v-html="mentionHtml(data.description)"></span>
              </p>

              <CommentSection :key="commentsRev" class="mt-5 border-t border-black/5 dark:border-white/10 pt-5" :src="data.commentsUrl" :video-id="data.id" :up-mid="data.upMid" />
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
      <p
        v-if="notice"
        class="fixed bottom-6 left-1/2 z-50 max-w-[80vw] -translate-x-1/2 truncate rounded-full px-4 py-2 text-xs text-white shadow-lg"
        :class="notice.kind === 'err' ? 'bg-rose-500/90' : notice.kind === 'warn' ? 'bg-amber-500/90' : 'bg-inkMain/85'"
        :title="notice.text"
      >
        {{ notice.text }}
      </p>
    </main>
  </div>
</template>
