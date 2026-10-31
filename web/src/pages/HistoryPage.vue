<script setup>
import { computed, onMounted, ref, watch } from 'vue'
import TopBar from '../components/TopBar.vue'
import { member, loaded, loginDialogOpen, loginDialogMode } from '../memberStore'
import { requestConfirm } from '../confirmStore'
import { friendlyTime } from '../utils/time'
import { coverThumb } from '../utils/cover'

// ─── 观看历史专页（2026-11-01 从个人空间迁出）───
// 独立成页后不再需要卡面那套内滚治理：没有 max-h 容器，滚动交还给页面与浏览器天然的锚定，
// 删除的乐观收合动画与快照回滚原样保留；全量列座，删了就是删了，不再补第 11 条。
const history = ref(null) // null = 没拉到（在飞或后端没应答），[] = 确实没看过东西
const historyBusy = ref(false)
const historyNotice = ref(null) // { kind, text }

async function loadHistory() {
  if (historyBusy.value) return
  historyBusy.value = true
  try {
    const res = await fetch('/api/member/history')
    if (!res.ok) {
      history.value = null // 401/500 都当「没拿到」，不拿空数组冒充「没看过」
      return
    }
    const data = await res.json().catch(() => null)
    history.value = Array.isArray(data?.items) ? data.items : []
  } catch {
    history.value = null
  } finally {
    historyBusy.value = false
  }
}

onMounted(() => {
  if (member.value) loadHistory()
})
// 页内登录后（顶栏那颗登录钮）补拉一次
watch(member, (m) => {
  if (m && history.value === null) loadHistory()
})

// 进度百分比：时长不可信（0 / 非数）就给 0，不拿除零的 NaN 去撑进度条。
function progressPct(item) {
  const dur = Number(item?.durationSec)
  const pos = Number(item?.positionSec)
  if (!Number.isFinite(dur) || dur <= 0 || !Number.isFinite(pos)) return 0
  return Math.max(0, Math.min(100, (pos / dur) * 100))
}

// 封面口径抄首页卡片：本地件优先，没归档才吃 B 站图床缩图。
function historyCover(item) {
  return item.localCover || (item.coverUrl ? coverThumb(item.coverUrl, 480) : '')
}

// 片内时间点：分:秒，过小时补时。
function historyClock(sec) {
  const n = Math.max(0, Math.floor(Number(sec) || 0))
  const pad = (x) => String(x).padStart(2, '0')
  const h = Math.floor(n / 3600)
  return h > 0 ? `${h}:${pad(Math.floor((n % 3600) / 60))}:${pad(n % 60)}` : `${pad(Math.floor(n / 60))}:${pad(n % 60)}`
}

// 封面淡入：首见封面先隐身等 load，省得灰底上「啪」地糊上图闪一下
function onCoverLoad(e) {
  e.target.classList.add('is-on')
}

// 退场中的行（videoId 集）：带集合让整叠清空也能逐行收合
const historyExiting = ref(new Set())
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
function markExiting(ids, on) {
  const list = Array.isArray(ids) ? ids : [ids]
  const next = new Set(historyExiting.value)
  for (const id of list) {
    if (on) next.add(id)
    else next.delete(id)
  }
  historyExiting.value = next
}

async function deleteHistory(item) {
  if (historyBusy.value || historyExiting.value.has(item.videoId)) return
  historyNotice.value = null
  // 乐观退场：点下先演收合淡出、本地直接摘行，请求在背后并行跑；抹不成再整行快照请回来（自带淡入）
  const snapshot = (history.value ?? []).slice()
  historyBusy.value = true
  markExiting(item.videoId, true)
  const req = fetch('/api/member/history/delete', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ videoId: item.videoId }),
  })
  await sleep(300) // 等退场演完再摘行，高度收起不 snaps
  history.value = (history.value ?? []).filter((x) => x.videoId !== item.videoId)
  markExiting(item.videoId, false)
  try {
    const res = await req
    const data = await res.json().catch(() => null)
    // 成功不喧宾：退场动画本身就是交代，只留失败时的错牌
    if (res.ok) {
      historyNotice.value = null
    } else {
      history.value = snapshot
      historyNotice.value = { kind: 'err', text: data?.message ?? '没抹成' }
    }
  } catch {
    history.value = snapshot
    historyNotice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    historyBusy.value = false
  }
}

async function clearHistory() {
  if (historyBusy.value) return
  if (!history.value?.length) return
  if (
    !(
      await requestConfirm({
        title: '清空观看历史',
        sub: `${history.value.length} 部`,
        message: '看过的记录整叠抹掉，之后进片子都从头放起。馆藏一根毛都不会掉。',
        confirmText: '清空',
        cancelText: '再留留',
        danger: true,
        icon: 'trash',
      })
    )
  ) {
    return
  }
  historyBusy.value = true
  historyNotice.value = null
  // 整叠退场：一行行收合淡出后清空，请求背后跑；没清成就连夜请回来
  const snapshot = (history.value ?? []).slice()
  markExiting(snapshot.map((x) => x.videoId), true)
  const req = fetch('/api/member/history/clear', { method: 'POST' })
  await sleep(300)
  history.value = []
  markExiting([], false)
  try {
    const res = await req
    const data = await res.json().catch(() => null)
    // 清空同理：整叠收合的动画就是回执，只留错牌
    if (res.ok) {
      historyNotice.value = null
    } else {
      history.value = snapshot
      historyNotice.value = { kind: 'err', text: data?.message ?? '没清成' }
    }
  } catch {
    history.value = snapshot
    historyNotice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    historyBusy.value = false
  }
}

// ─── 左侧时间轨：按自然日切段筛片（今天/昨天/前天/近一周/近一月/更早），段间互斥不重影；
// 选段记进 localStorage，下次推门还站在上次那段上
const RAIL_BUCKETS = [
  { key: 'all', label: '全部', test: () => true },
  { key: 'today', label: '今天', test: (d) => d === 0 },
  { key: 'yesterday', label: '昨天', test: (d) => d === 1 },
  { key: 'dayBefore', label: '前天', test: (d) => d === 2 },
  { key: 'week', label: '近一周', test: (d) => d >= 3 && d <= 7 },
  { key: 'month', label: '近一月', test: (d) => d >= 8 && d <= 30 },
  { key: 'older', label: '更早', test: (d) => d > 30 },
]

// 自然日距：拿本地历法日相减，不拿 24h 窗口硬除——晚上十一点看的片不该被午夜踢出「今天」
function daysAgo(iso) {
  const t = new Date(iso)
  if (!Number.isFinite(t.getTime())) return null
  const now = new Date()
  const today0 = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime()
  const that0 = new Date(t.getFullYear(), t.getMonth(), t.getDate()).getTime()
  return Math.round((today0 - that0) / 86400000)
}

const histFilter = ref('all')
try {
  const saved = localStorage.getItem('lbs.history.filter')
  if (saved && RAIL_BUCKETS.some((b) => b.key === saved)) histFilter.value = saved
} catch {
  /* 隐私模式读不到就算了，用默认档 */
}
function setHistFilter(key) {
  histFilter.value = key
  try {
    localStorage.setItem('lbs.history.filter', key)
  } catch {
    /* 存不上不拦筛片 */
  }
}

function inBucket(item, bucket) {
  const d = daysAgo(item.updatedAtUtc)
  if (d === null) return bucket.key === 'all' // 时间戳读不懂的只认全部，不乱塞进哪段
  return bucket.test(d)
}

const activeBucket = computed(() => RAIL_BUCKETS.find((b) => b.key === histFilter.value) ?? RAIL_BUCKETS[0])
const filteredHistory = computed(() => {
  const all = history.value ?? []
  const bucket = activeBucket.value
  return bucket.key === 'all' ? all : all.filter((x) => inBucket(x, bucket))
})
// 每段计数：空段淡显不可点，一眼看清哪几天真看过东西
const railCounts = computed(() => {
  const map = {}
  const all = history.value ?? []
  for (const b of RAIL_BUCKETS) {
    map[b.key] = b.key === 'all' ? all.length : all.filter((x) => inBucket(x, b)).length
  }
  return map
})

function openLogin(mode) {
  loginDialogMode.value = mode
  loginDialogOpen.value = true
}
</script>

<template>
  <div class="min-h-screen bg-pageBg">
    <TopBar />

    <main class="mx-auto max-w-[1000px] px-6 py-8">
      <p v-if="!member && !loaded" class="py-20 text-center text-sm text-inkSub">正在核对身份……</p>

      <!-- 访客引导：这页认站籍，没进门先看片去 -->
      <section v-else-if="!member" class="rounded-2xl border border-lineSoft bg-cardBg px-6 py-16 text-center shadow-[0_1px_2px_rgba(0,0,0,0.03)]">
        <span class="mx-auto flex h-16 w-16 items-center justify-center rounded-full bg-stationPink/10">
          <svg viewBox="0 0 24 24" class="h-8 w-8 text-stationPink" fill="none" stroke="currentColor" stroke-width="1.6">
            <circle cx="12" cy="12" r="8.5" />
            <path d="M12 7.5V12l3 2" stroke-linecap="round" />
          </svg>
        </span>
        <h2 class="mt-5 text-lg font-semibold text-inkMain">历史只记给自己人</h2>
        <p class="mx-auto mt-2 max-w-[360px] text-sm leading-6 text-inkSub">
          登录后看过的片子都会在这里留档，回来接着放；没进门的话，先去大厅抽一部？
        </p>
        <RouterLink
          to="/register"
          class="mt-7 inline-block rounded-full bg-gradient-to-r from-stationPink to-[#f994b6] px-9 py-2.5 text-sm font-medium text-white shadow-md shadow-stationPink/30 transition hover:-translate-y-px hover:shadow-lg hover:shadow-stationPink/40 active:translate-y-0"
        >
          去注册
        </RouterLink>
        <p class="mt-4 text-xs text-inkFaint">
          已有站籍？
          <button class="text-stationPink transition hover:underline" @click="openLogin('admit')">直接登录</button>
        </p>
      </section>

      <!-- 观看历史全量页：左侧时间轨选段，右侧席位制一人一片一行，看到一半走了它记得你的位子 -->
      <div v-else class="flex items-start gap-5">
        <aside class="w-[104px] shrink-0">
          <div class="hist-rail sticky top-[76px]">
            <button
              v-for="b in RAIL_BUCKETS"
              :key="b.key"
              class="hist-rail-item group"
              :class="{ 'is-active': histFilter === b.key, 'is-empty': !railCounts[b.key] }"
              :disabled="!railCounts[b.key] && histFilter !== b.key"
              :title="railCounts[b.key] ? `${b.label}：${railCounts[b.key]} 条` : `${b.label}：没有记录`"
              @click="setHistFilter(b.key)"
            >
              <span class="hist-rail-label text-xs leading-4 text-inkSub transition group-hover:text-stationPink">{{ b.label }}</span>
              <span class="hist-rail-dot"></span>
            </button>
          </div>
        </aside>

        <section class="min-w-0 flex-1 rounded-2xl border border-lineSoft bg-cardBg p-6 shadow-[0_1px_2px_rgba(0,0,0,0.03)]">
          <div class="mb-4 flex items-center justify-between gap-3">
            <h2 class="text-[15px] font-semibold text-inkMain">观看历史</h2>
            <span v-if="history" class="text-xs tabular-nums text-inkFaint">
              <template v-if="histFilter !== 'all'">{{ activeBucket.label }} {{ filteredHistory.length }} 条 · 共 {{ history.length }} 条</template>
              <template v-else>共 {{ history.length }} 条历史记录</template>
            </span>
          </div>

          <!-- 格子阵列（选中时段内）：封面大头卡自适应列宽，一屏装得下的比长条多得多 -->
          <div v-if="filteredHistory.length" class="grid grid-cols-[repeat(auto-fill,minmax(190px,1fr))] gap-3">
            <div
              v-for="item in filteredHistory"
              :key="item.videoId"
              :data-video-id="item.videoId"
              class="hist-cell group relative overflow-hidden rounded-xl border border-lineSoft bg-[#f6f7f8] dark:bg-[#23262e]"
              :class="{ 'hist-cell-out': historyExiting.has(item.videoId) }"
            >
              <RouterLink :to="`/watch/${item.bvid}`" class="block no-underline">
                <span class="relative block aspect-video w-full overflow-hidden bg-black/[0.06]">
                  <img
                    v-if="historyCover(item)"
                    :src="historyCover(item)"
                    :alt="item.title || item.bvid"
                    loading="lazy"
                    decoding="async"
                    referrerpolicy="no-referrer"
                    class="hist-cover h-full w-full object-cover transition duration-300 group-hover:scale-[1.04]"
                    @load="onCoverLoad"
                  />
                  <span v-else class="flex h-full w-full select-none items-center justify-center font-mono text-sm text-inkFaint" aria-hidden="true">
                    {{ (item.title || item.bvid || '?').slice(0, 1) }}
                  </span>
                  <!-- 进度条贴在封面底：满格配「已看完」戳 -->
                  <span class="absolute inset-x-0 bottom-0 h-[3px] bg-black/25">
                    <span class="block h-full bg-stationPink" :style="{ width: `${progressPct(item)}%` }"></span>
                  </span>
                  <span v-if="progressPct(item) >= 95" class="absolute left-1.5 top-1.5 rounded bg-emerald-500/90 px-1 py-px text-[10px] leading-4 text-white">已看完</span>
                </span>
                <span class="block px-2 pb-2 pt-1.5">
                  <span class="line-clamp-2 block text-[13px] leading-[19px] text-inkMain transition group-hover:text-stationPink" :title="item.title || item.bvid">
                    {{ item.title || item.bvid }}
                  </span>
                  <span class="mt-1 block truncate font-mono text-[10.5px] text-inkFaint">
                    看到 {{ historyClock(item.positionSec) }} · 第 {{ item.pageIdx ?? 0 }} P · {{ friendlyTime(item.updatedAtUtc) }}
                  </span>
                </span>
              </RouterLink>
              <button
                class="absolute right-1.5 top-1.5 z-10 rounded-md bg-black/50 px-1.5 py-0.5 text-[11px] leading-4 text-white transition hover:bg-rose-500 focus-visible:opacity-100 disabled:opacity-0 md:opacity-0 md:group-hover:opacity-100"
                :disabled="historyBusy"
                title="从历史里抹掉这部（下次从头放）"
                @click="deleteHistory(item)"
              >
                删除
              </button>
            </div>
          </div>
        <p v-else-if="history === null" class="py-8 text-center text-sm text-inkFaint">
          {{ historyBusy ? '正在翻你的历史……' : '历史没打开：分站后端没有应答' }}
        </p>
        <p v-else-if="histFilter !== 'all'" class="py-8 text-center text-sm text-inkFaint">
          {{ activeBucket.label }}没有看过的片子——换个时段看看，或点左轨「全部」观所有留档。
        </p>
        <p v-else class="py-8 text-center text-sm text-inkFaint">
          还没有观看记录——去大厅抽一部，看过的都会在这里留档；看到一半走了它也会记得你的位子。
        </p>

        <div class="mt-3 flex items-end justify-between gap-4">
          <p
            v-if="historyNotice"
            class="flex-1 text-xs leading-5"
            :class="historyNotice.kind === 'ok' ? 'text-emerald-600' : 'text-rose-500'"
          >
            {{ historyNotice.text }}
          </p>
          <p v-else class="flex-1 text-[11px] leading-4 text-inkFaint">
            历史按席位存，一人一片只占一行，看完的也留档；这里全量列座，退场只认手动删除与清空。
          </p>
          <button
            class="shrink-0 rounded-full border border-lineSoft px-3.5 py-1.5 text-xs text-inkSub transition hover:border-rose-300 hover:text-rose-500 disabled:cursor-not-allowed disabled:opacity-40"
            :disabled="historyBusy || !history?.length"
            @click="clearHistory"
          >
            清空
          </button>
        </div>
      </section>
      </div>
    </main>
  </div>
</template>

<style scoped>
/* 格子卡退场：删前先演淡出缩身，网络在背后跑；回滚的卡淡入归位。
   格子阵没有高度收合可演，摘卡时后排顺位补上，浏览器锚定自会稳视野。
   入场用无 both 的动画，演完即卸，免得 fill 状态压住后续的退场过渡 */
.hist-cell {
  animation: hist-in 0.55s ease-out 0.12s backwards;
  transition:
    opacity 0.26s ease,
    transform 0.26s ease;
}
.hist-cell-out {
  opacity: 0;
  transform: scale(0.92);
}
@keyframes hist-in {
  from {
    opacity: 0;
    transform: translateY(4px);
  }
  to {
    opacity: 1;
    transform: none;
  }
}
/* 封面图先透着（底下的灰垫子顶位），load 到了再淡进来接管，杜绝图到的那一下白闪 */
.hist-cover {
  opacity: 0;
  transition: opacity 0.35s ease;
}
.hist-cover.is-on {
  opacity: 1;
}
/* 左轨时间筛：一条竖线穿珠——选中段换空心粉环（#fb7299 即 tailwind 的 stationPink），空段淡到不可点 */
.hist-rail {
  display: flex;
  flex-direction: column;
}
.hist-rail::before {
  content: '';
  position: absolute;
  top: 12px;
  bottom: 12px;
  right: 7px;
  width: 2px;
  border-radius: 1px;
  background: rgb(var(--line-soft) / 1);
}
.hist-rail-item {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 10px;
  min-height: 36px;
  cursor: pointer;
}
.hist-rail-item:disabled {
  cursor: default;
}
.hist-rail-item.is-empty {
  opacity: 0.35;
}
.hist-rail-dot {
  position: relative;
  flex: none;
  width: 16px;
  height: 16px;
  box-sizing: border-box;
  border-radius: 9999px;
  border: 2px solid transparent;
  transition:
    border-color 0.15s ease,
    transform 0.15s ease;
}
.hist-rail-dot::after {
  content: '';
  position: absolute;
  inset: 0;
  margin: auto;
  width: 6px;
  height: 6px;
  border-radius: 9999px;
  background: #c3c8d1;
  transition: opacity 0.15s ease;
}
.dark .hist-rail-dot::after {
  background: #565b66;
}
.hist-rail-item.is-active .hist-rail-dot {
  border-color: #fb7299;
  transform: scale(1.08);
}
.hist-rail-item.is-active .hist-rail-dot::after {
  opacity: 0;
}
.hist-rail-item.is-active .hist-rail-label {
  color: #fb7299;
  font-weight: 600;
}
@media (prefers-reduced-motion: reduce) {
  .hist-cell {
    animation: none;
    transition: none;
  }
}
</style>
