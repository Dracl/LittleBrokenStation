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

const historyAll = computed(() => history.value ?? [])

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

      <!-- 观看历史全量页：席位制一人一片一行，看到一半走了它记得你的位子 -->
      <section v-else class="rounded-2xl border border-lineSoft bg-cardBg p-6 shadow-[0_1px_2px_rgba(0,0,0,0.03)]">
        <div class="mb-4 flex items-center justify-between gap-3">
          <h2 class="text-[15px] font-semibold text-inkMain">观看历史</h2>
          <span v-if="history" class="text-xs tabular-nums text-inkFaint">共 {{ history.length }} 条历史记录</span>
        </div>

        <!-- 全量列座：页面自然滚，不再挤 440px 的内滚窗口 -->
        <ul v-if="history?.length" class="space-y-2">
          <li
            v-for="item in historyAll"
            :key="item.videoId"
            :data-video-id="item.videoId"
            class="hist-row flex items-start gap-3 rounded-xl border border-lineSoft bg-[#f6f7f8] dark:bg-[#23262e] px-4 py-3"
            :class="{ 'hist-row-out': historyExiting.has(item.videoId) }"
          >
            <RouterLink :to="`/watch/${item.bvid}`" class="group flex min-w-0 flex-1 items-start gap-3 no-underline">
              <span class="relative block h-[76px] w-[124px] shrink-0 overflow-hidden rounded-lg bg-black/[0.06]">
                <img
                  v-if="historyCover(item)"
                  :src="historyCover(item)"
                  :alt="item.title || item.bvid"
                  loading="lazy"
                  decoding="async"
                  referrerpolicy="no-referrer"
                  class="hist-cover h-full w-full object-cover"
                  @load="onCoverLoad"
                />
                <span v-else class="flex h-full w-full select-none items-center justify-center font-mono text-sm text-inkFaint" aria-hidden="true">
                  {{ (item.title || item.bvid || '?').slice(0, 1) }}
                </span>
              </span>
              <span class="min-w-0 flex-1">
                <span class="block truncate text-sm text-inkMain transition group-hover:text-stationPink" :title="item.title || item.bvid">
                  {{ item.title || item.bvid }}
                </span>
                <!-- 进度条：位置/时长的百分比（看完的也留档，条满格配「已看完」戳）-->
                <span class="mt-2 block h-1 w-full overflow-hidden rounded-full bg-black/[0.08]">
                  <span class="block h-full rounded-full bg-stationPink/70" :style="{ width: `${progressPct(item)}%` }"></span>
                </span>
                <span class="mt-1.5 flex flex-wrap items-center gap-x-3 gap-y-0.5 font-mono text-[11px] text-inkFaint">
                  <span>看到 {{ historyClock(item.positionSec) }}</span>
                  <span>第 {{ item.pageIdx ?? 0 }} P</span>
                  <span>{{ friendlyTime(item.updatedAtUtc) }}</span>
                  <span v-if="progressPct(item) >= 95" class="rounded bg-emerald-500/10 px-1 text-emerald-600">已看完</span>
                </span>
              </span>
            </RouterLink>
            <button
              class="shrink-0 rounded-lg border border-lineSoft bg-cardBg px-3 py-1 text-xs text-inkSub transition hover:border-rose-300 hover:text-rose-500 disabled:opacity-40"
              :disabled="historyBusy"
              title="从历史里抹掉这部（下次从头放）"
              @click="deleteHistory(item)"
            >
              删除
            </button>
          </li>
        </ul>
        <p v-else-if="history === null" class="py-8 text-center text-sm text-inkFaint">
          {{ historyBusy ? '正在翻你的历史……' : '历史没打开：分站后端没有应答' }}
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
    </main>
  </div>
</template>

<style scoped>
/* 历史行退场：删除先演收合淡出（高度/边距/内边距一起收），网络在背后跑；回滚的行淡入归位。
   入场用无 both 的动画，演完即卸，免得 fill 状态压住后续的退场过渡 */
.hist-row {
  max-height: 140px;
  overflow: hidden;
  transition:
    opacity 0.26s ease,
    transform 0.26s ease,
    max-height 0.3s ease,
    margin-top 0.3s ease,
    margin-bottom 0.3s ease,
    padding-top 0.3s ease,
    padding-bottom 0.3s ease,
    border-top-width 0.3s ease,
    border-bottom-width 0.3s ease;
  animation: hist-in 0.55s ease-out 0.12s backwards;
}
.hist-row-out {
  opacity: 0;
  transform: translateX(16px) scale(0.98);
  max-height: 0;
  margin-top: 0;
  margin-bottom: 0;
  padding-top: 0;
  padding-bottom: 0;
  border-top-width: 0;
  border-bottom-width: 0;
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
@media (prefers-reduced-motion: reduce) {
  .hist-row {
    animation: none;
    transition: none;
  }
}
</style>
