<script setup>
import { computed, nextTick, onMounted, reactive, ref } from 'vue'
import { useRoute } from 'vue-router'
import TopBar from '../components/TopBar.vue'
import DynamicVideoCard from '../components/DynamicVideoCard.vue'
import ShareDynamicDialog from '../components/ShareDynamicDialog.vue'
import { member, loginDialogOpen } from '../memberStore'
import { friendlyTime } from '../utils/time'
import { richHtml } from '../utils/emote'

const route = useRoute()

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

// 转发条正文是「评语\n\n转发自 XX：\n\n原文」三段式（解析器定格），卡头归转发人、灰块装原文。
// 拆成三块：评语 / 灰块头的原作者名 / 原文本体（「转发自」行升格成带头像的行，就不再塞在正文里）。
function splitForward(text) {
  if (!text) return { comment: '', fromName: '', orig: '' }
  const i = text.indexOf('\n\n转发自')
  if (i < 0) return { comment: text, fromName: '', orig: '' }
  const rest = text.slice(i + 2)
  const j = rest.indexOf('：')
  if (j < 0) return { comment: text.slice(0, i), fromName: '', orig: rest }
  return {
    comment: text.slice(0, i),
    fromName: rest.slice(0, j).replace(/^转发自\s*/, ''),
    orig: rest.slice(j + 1).trim(),
  }
}

// ─── 评论抽屉：每条评价现拉现展，收起来不拆数据（二次展开不重拉）───
const commentsMap = reactive({})
function cmtState(d) {
  return (commentsMap[d.dynamicId] ||= { open: false, loading: false, loaded: false, fetched: false, total: 0, items: [], expanded: {}, emotes: {} })
}

async function loadComments(d) {
  const st = cmtState(d)
  if (st.loaded || st.loading) return
  st.loading = true
  try {
    const res = await fetch(`/api/dynamics/${encodeURIComponent(d.dynamicId)}/comments`)
    if (res.ok) {
      const data = await res.json()
      st.items = data.items ?? []
      st.total = data.total ?? 0
      st.fetched = !!data.fetched
      st.emotes = data.emotes ?? {}
      st.loaded = true
    }
  } catch {
    /* 评论拉砸不阁主卡，收起重开可重试 */
  } finally {
    st.loading = false
  }
}

function toggleComments(d) {
  const st = cmtState(d)
  st.open = !st.open
  if (st.open && !st.loaded) loadComments(d)
}

function cmtCount(d) {
  return d.statComment ?? commentsMap[d.dynamicId]?.total ?? null
}

// ─── 安利分享：登录门槛在按钮上就把关，未登录弹登录窗 ───
const shareFor = ref(null)
const focusHit = ref(null)
function openShare(d) {
  if (!member.value) {
    loginDialogOpen.value = true
    return
  }
  shareFor.value = d
}
function onShared(message) {
  shareFor.value = null
  toast.value = message || '已塞进对方信箱'
  setTimeout(() => { toast.value = null }, 2600)
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
    const q = new URLSearchParams({ limit: String(PAGE_SIZE), beforeId: String(items.value[items.value.length - 1].id ?? 0), beforeTime: String(items.value[items.value.length - 1].publishedAtUtc ?? '') })
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
  // 从私信分享卡进来：定位到那条动态并粉框闪两秒（不在当前页就不硬造位置）。
  const focus = typeof route.query.focus === 'string' ? route.query.focus : null
  if (focus) {
    await nextTick()
    const el = document.querySelector(`[data-dyn="${CSS.escape(focus)}"]`)
    if (el) {
      focusHit.value = focus
      el.scrollIntoView({ behavior: 'smooth', block: 'center' })
      setTimeout(() => { focusHit.value = null }, 2400)
    }
  }
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
          :data-dyn="d.dynamicId"
          class="mb-2.5 rounded-lg border bg-cardBg p-4 transition-shadow duration-500"
          :class="[isLive(d) ? 'border-stationPink/50' : 'border-lineSoft', focusHit === d.dynamicId ? 'ring-2 ring-stationPink/60' : '']"
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

          <!-- 转发条：评语 + 灰块内嵌原文（B 站同款）；灰块头挂原作者的小脸 -->
          <template v-else-if="isForward(d)">
            <p v-if="splitForward(d.text).comment" class="mb-2 whitespace-pre-wrap break-words text-[15px] leading-relaxed text-inkMain">{{ splitForward(d.text).comment }}</p>
            <div class="rounded-md bg-[#f6f7f8] p-3">
              <div v-if="d.origAuthorName || splitForward(d.text).fromName" class="mb-2 flex items-center gap-1.5">
                <img
                  v-if="d.origAuthorFace"
                  :src="d.origAuthorFace"
                  referrerpolicy="no-referrer"
                  loading="lazy"
                  class="h-[18px] w-[18px] shrink-0 rounded-full object-cover"
                  alt=""
                  @error="$event.target.style.display = 'none'"
                />
                <span v-else class="flex h-[18px] w-[18px] shrink-0 items-center justify-center rounded-full bg-stationPink/15 text-[10px] text-stationPink">{{ (d.origAuthorName || splitForward(d.text).fromName || '?').slice(0, 1) }}</span>
                <span class="text-xs text-inkSub">转发自 {{ d.origAuthorName || splitForward(d.text).fromName }}</span>
              </div>
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

          <!-- 底栏三段（B 站同款）：转发走私信安利，评论开抽屉，赞数只读 -->
          <footer v-if="!isLive(d)" class="mt-2 flex items-stretch border-t border-lineSoft/70 pt-1">
            <button class="flex flex-1 items-center justify-center gap-1.5 rounded-md py-1.5 text-[13px] text-inkSub transition hover:text-stationPink" title="安利给分站朋友" @click="openShare(d)">
              <svg viewBox="0 0 24 24" class="h-4 w-4" fill="none" stroke="currentColor" stroke-width="1.8">
                <path d="M13 5l5 5-5 5" stroke-linecap="round" stroke-linejoin="round" />
                <path d="M18 10H9a5 5 0 0 0-5 5v3" stroke-linecap="round" />
              </svg>
              转发<span v-if="d.statForward" class="text-[12px] text-inkFaint">{{ d.statForward }}</span>
            </button>
            <button class="flex flex-1 items-center justify-center gap-1.5 rounded-md py-1.5 text-[13px] text-inkSub transition hover:text-stationPink" @click="toggleComments(d)">
              <svg viewBox="0 0 24 24" class="h-4 w-4" fill="none" stroke="currentColor" stroke-width="1.8">
                <path d="M21 12a8 8 0 0 1-8 8c-1.2 0-2.3-.2-3.3-.6L5 21l1.2-3.4A8 8 0 1 1 21 12Z" stroke-linecap="round" stroke-linejoin="round" />
              </svg>
              <span v-if="cmtCount(d) !== null">{{ cmtCount(d) }}</span>
            </button>
            <span class="flex flex-1 items-center justify-center gap-1.5 py-1.5 text-[13px] text-inkSub" :title="d.statLike !== null ? 'B 站赞数（抓取定格）' : ''">
              <svg viewBox="0 0 24 24" class="h-4 w-4" fill="none" stroke="currentColor" stroke-width="1.8">
                <path d="M7 10v10H4.5a1 1 0 0 1-1-1v-8a1 1 0 0 1 1-1H7Zm0 0l4.2-7a2 2 0 0 1 2 1v3.5h5.2a2 2 0 0 1 2 2.4l-1.3 6.2A2 2 0 0 1 17.1 19H7" stroke-linecap="round" stroke-linejoin="round" />
              </svg>
              <span v-if="d.statLike !== null">{{ d.statLike }}</span>
            </span>
          </footer>

          <!-- 评论抽屉：热评与子回复都吃分站留档，名字粉红、时间淡灰，B 站同款排布 -->
          <section v-if="cmtState(d).open" class="mt-1 border-t border-lineSoft/70 pt-3">
            <p v-if="cmtState(d).loading" class="py-3 text-center text-xs text-inkFaint">评论拉取中…</p>
            <p v-else-if="!cmtState(d).items.length" class="py-3 text-center text-xs text-inkFaint">
              {{ cmtState(d).fetched ? '这条动态还没人开口' : '评论还没巡到货，等巡捕下轮补' }}
            </p>
            <template v-else>
              <p class="mb-2 text-[11px] text-inkFaint">评论同步自 B 站，分站只读留档 · 共 {{ cmtState(d).total }} 条</p>
              <div v-for="top in cmtState(d).items" :key="top.rpid" class="mb-3 flex gap-2.5">
                <img
                  v-if="top.faceLocal || top.face"
                  :src="top.faceLocal || top.face"
                  referrerpolicy="no-referrer"
                  loading="lazy"
                  class="h-8 w-8 shrink-0 rounded-full object-cover"
                  alt=""
                  @error="$event.target.style.display = 'none'"
                />
                <span v-else class="flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-stationPink/10 text-xs text-stationPink">{{ (top.name || '?').slice(0, 1) }}</span>
                <div class="min-w-0 flex-1">
                  <div class="flex items-baseline gap-2">
                    <span class="text-[12.5px] font-medium text-stationPink">{{ top.name || '无名网友' }}</span>
                    <span class="text-[11px] text-inkFaint">{{ friendlyTime(top.ctimeUtc) }}</span>
                  </div>
                  <p class="mt-0.5 whitespace-pre-wrap break-words text-[13.5px] leading-relaxed text-inkMain" v-html="richHtml(top.message, cmtState(d).emotes)"></p>
                  <div class="mt-0.5 flex items-center gap-3 text-[11.5px] text-inkFaint">
                    <span v-if="top.like">👍 {{ top.like }}</span>
                  </div>
                  <!-- 子回复也随抓取定格，缩进挂在楼下 -->
                  <div v-if="top.replies?.length" class="mt-2 space-y-2.5 border-l-2 border-lineSoft/80 pl-3">
                    <template v-for="(sub, si) in top.replies" :key="sub.rpid">
                      <div v-if="top.replies.length <= 3 || cmtState(d).expanded[top.rpid] || si < 3" class="flex gap-2">
                        <img
                          v-if="sub.faceLocal || sub.face"
                          :src="sub.faceLocal || sub.face"
                          referrerpolicy="no-referrer"
                          loading="lazy"
                          class="h-5 w-5 shrink-0 rounded-full object-cover"
                          alt=""
                          @error="$event.target.style.display = 'none'"
                        />
                        <div class="min-w-0 flex-1">
                          <span class="text-[12px] font-medium text-stationPink">{{ sub.name || '无名网友' }}</span>
                          <span class="ml-1.5 text-[10.5px] text-inkFaint">{{ friendlyTime(sub.ctimeUtc) }}</span>
                          <p class="whitespace-pre-wrap break-words text-[13px] leading-relaxed text-inkMain" v-html="richHtml(sub.message, cmtState(d).emotes)"></p>
                        </div>
                      </div>
                    </template>
                    <button
                      v-if="top.replies.length > 3 && !cmtState(d).expanded[top.rpid]"
                      class="text-[12px] text-stationPink transition hover:underline"
                      @click="cmtState(d).expanded[top.rpid] = true"
                    >
                      展开查看全部 {{ top.replies.length }} 条子回复
                    </button>
                  </div>
                </div>
              </div>
            </template>
          </section>
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

      <ShareDynamicDialog v-if="shareFor" :d="shareFor" @close="shareFor = null" @done="onShared" />
    </main>
  </div>
</template>
