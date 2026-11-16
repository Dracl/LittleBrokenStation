<script setup>
import { onMounted, onUnmounted, ref } from 'vue'
import { requestConfirm } from '../confirmStore'
import { friendlyTime } from '../utils/time'

// ─── 成员回收站弹窗（11-16 站长拍板：头像菜单点开）─────────────────────
// 两族并排：自己删的视频（/api/member/recycle）与自己收回的弹幕（/api/member/danmaku-recycle，
// 从个人空间整段搬来）。归属都由后端把闸——递到手里的只可能是自己动过手的行。
// 视频的「销毁」在成员视角就是彻底没了（措辞与站长真销毁一个口径，不提任何内部机制）；
// 弹幕只留恢复一个动作——那是单条快照，没什么可销毁的账。
const emit = defineEmits(['close'])

const videos = ref(null) // null = 没拉到（与真空仓分两脸，照收回柜那套口径）
const videosLoading = ref(false)
const videoBusy = ref(null) // 正在恢复/销毁的行 id
const videoNotice = ref(null) // { kind, text }：后端那句人话原样上屏，前端不改写

const dm = ref(null)
const dmLoading = ref(false)
const dmBusy = ref(null)
const dmNotice = ref(null)

async function loadVideos() {
  if (videosLoading.value) return
  videosLoading.value = true
  try {
    const res = await fetch('/api/member/recycle')
    if (!res.ok) {
      videos.value = null // 401/500 都当「没拿到」，不拿空数组冒充「没删过」
      return
    }
    const d = await res.json().catch(() => null)
    videos.value = Array.isArray(d?.items) ? d.items : []
  } catch {
    videos.value = null
  } finally {
    videosLoading.value = false
  }
}

async function loadDm() {
  if (dmLoading.value) return
  dmLoading.value = true
  try {
    const res = await fetch('/api/member/danmaku-recycle')
    if (!res.ok) {
      dm.value = null
      return
    }
    const d = await res.json().catch(() => null)
    dm.value = Array.isArray(d?.items) ? d.items : []
  } catch {
    dm.value = null
  } finally {
    dmLoading.value = false
  }
}

onMounted(() => {
  loadVideos()
  loadDm()
})

// Esc 收场（与站内弹层同一手感）；遮罩点击也收，见模板那层 fixed 底。
function onKey(e) {
  if (e.key === 'Escape') emit('close')
}
onMounted(() => window.addEventListener('keydown', onKey))
onUnmounted(() => window.removeEventListener('keydown', onKey))

function sizeText(bytes) {
  const n = Number(bytes)
  if (!Number.isFinite(n) || n <= 0) return ''
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let v = n
  let u = 0
  while (v >= 1024 && u < units.length - 1) {
    v /= 1024
    u += 1
  }
  return ` · ${v >= 100 || u === 0 ? Math.round(v) : v.toFixed(1)}${units[u]}`
}

// 片内点位：后端递秒，摆成分:秒（与个人空间收回柜原段、站长室那把尺子同一算法）。pageIdx 是 0 基，上桌才 +1。
function dmClock(sec) {
  const n = Number(sec)
  if (!Number.isFinite(n) || n < 0) return '—'
  const pad = (x) => String(x).padStart(2, '0')
  const total = Math.floor(n)
  return `${pad(Math.floor(total / 60))}:${pad(total % 60)}`
}

async function restoreVideo(item) {
  if (videoBusy.value !== null) return
  videoBusy.value = item.id
  videoNotice.value = null
  try {
    // 不带 body 也不手加 Content-Type：纯路径参数的口，挂了 json 头反吃路由门槛（claim 那口踩过的坑）
    const res = await fetch(`/api/member/recycle/${item.id}/restore`, { method: 'POST' })
    const d = await res.json().catch(() => null)
    videoNotice.value = {
      kind: res.ok ? 'ok' : 'err',
      text: d?.message ?? (res.ok ? '已回到架上' : '恢复没成，这一件还在回收站里'),
    }
    if (res.ok) videos.value = (videos.value ?? []).filter((x) => x.id !== item.id)
  } catch {
    videoNotice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    videoBusy.value = null
  }
}

// 销毁是毁件动作：走全站统一确认窗，danger 玫红。措辞只说「抹掉、恢复不回来」，不招供任何内部机制。
async function discardVideo(item) {
  if (videoBusy.value !== null) return
  const yes = await requestConfirm({
    title: '彻底销毁这一件？',
    message: `「${item.title || item.bvid}」的文件与台账会一并抹掉，之后恢复不回来。`,
    confirmText: '销毁',
    danger: true,
  })
  if (!yes) return
  videoBusy.value = item.id
  videoNotice.value = null
  try {
    const res = await fetch(`/api/member/recycle/${item.id}/discard`, { method: 'POST' })
    const d = await res.json().catch(() => null)
    videoNotice.value = {
      kind: res.ok ? 'ok' : 'err',
      text: d?.message ?? (res.ok ? '这一件已彻底销毁' : '销毁没成，稍后再试'),
    }
    if (res.ok) videos.value = (videos.value ?? []).filter((x) => x.id !== item.id)
  } catch {
    videoNotice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    videoBusy.value = null
  }
}

async function restoreDm(item) {
  if (dmBusy.value !== null) return
  dmBusy.value = item.id
  dmNotice.value = null
  try {
    const res = await fetch(`/api/member/danmaku-recycle/${item.id}/restore`, { method: 'POST' })
    const d = await res.json().catch(() => null)
    dmNotice.value = {
      kind: res.ok ? 'ok' : 'err',
      text: d?.message ?? (res.ok ? '已恢复，这条重新飘得起来' : '恢复没成，这条还躺在回收站里'),
    }
    if (res.ok) dm.value = (dm.value ?? []).filter((x) => x.id !== item.id)
  } catch {
    dmNotice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    dmBusy.value = null
  }
}
</script>

<template>
  <Teleport to="body">
    <div class="fixed inset-0 z-50 flex items-center justify-center p-4" role="dialog" aria-modal="true" aria-label="回收站">
      <div class="absolute inset-0 bg-black/45" @click="emit('close')"></div>
      <div class="relative flex max-h-[80vh] w-[min(640px,94vw)] flex-col overflow-hidden rounded-2xl border border-lineSoft bg-cardBg shadow-[0_20px_60px_-16px_rgba(0,0,0,0.4)]">
        <!-- 卡头 -->
        <div class="flex items-center justify-between border-b border-lineSoft px-5 py-3.5">
          <h3 class="flex items-center gap-2 text-[15px] font-semibold text-inkMain">
            <svg viewBox="0 0 24 24" class="h-4 w-4 shrink-0 text-inkSub" fill="none" stroke="currentColor" stroke-width="1.8">
              <path d="M4 7h16" stroke-linecap="round" />
              <path d="M9 7V5a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2" />
              <path d="M6 7l1 12a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2l1-12" />
              <path d="M10 11v6M14 11v6" stroke-linecap="round" />
            </svg>
            回收站
          </h3>
          <button
            class="flex h-7 w-7 items-center justify-center rounded-full text-inkFaint transition hover:bg-black/5 hover:text-inkMain dark:hover:bg-white/10"
            aria-label="关闭"
            @click="emit('close')"
          >
            <svg viewBox="0 0 24 24" class="h-4 w-4" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="m6 6 12 12M18 6 6 18" /></svg>
          </button>
        </div>

        <div class="min-h-0 flex-1 overflow-y-auto px-5 py-4">
          <!-- ═══ 我删的视频 ═══ -->
          <div class="mb-2 flex items-center justify-between">
            <h4 class="text-[13px] font-semibold text-inkMain">我删的视频</h4>
            <button class="text-[12px] text-stationPink transition hover:opacity-80 disabled:opacity-50" :disabled="videosLoading" @click="loadVideos">
              {{ videosLoading ? '翻箱中…' : '重拉' }}
            </button>
          </div>
          <ul v-if="videos?.length" class="space-y-2">
            <li
              v-for="item in videos"
              :key="item.id"
              class="flex items-center gap-3 rounded-xl border border-lineSoft bg-[#f6f7f8] px-3 py-2.5 dark:bg-[#23262e]"
            >
              <img
                v-if="!item._coverFailed && item.coverUrl"
                :src="item.coverUrl"
                alt=""
                loading="lazy"
                class="h-10 w-[60px] shrink-0 rounded-md object-cover"
                @error="item._coverFailed = true"
              />
              <span
                v-else
                class="flex h-10 w-[60px] shrink-0 items-center justify-center rounded-md bg-black/5 text-[16px] text-inkFaint dark:bg-white/10"
                aria-hidden="true"
              >🎬</span>
              <div class="min-w-0 flex-1">
                <p class="truncate text-[13.5px] font-medium text-inkMain" :title="item.title || item.bvid">{{ item.title || item.bvid }}</p>
                <p class="mt-0.5 flex flex-wrap items-center gap-x-1.5 text-[11.5px] text-inkFaint">
                  <span class="truncate font-mono">{{ item.bvid || '—' }}</span>
                  <span>· {{ item.pageCount ?? 1 }} P · {{ item.fileCount ?? 0 }} 件{{ sizeText(item.bytes) }}</span>
                  <span>· {{ friendlyTime(item.deletedAtUtc) }}删除</span>
                </p>
              </div>
              <div class="flex shrink-0 items-center gap-1.5">
                <button
                  class="rounded-lg border border-lineSoft bg-cardBg px-3 py-1 text-xs text-inkSub transition hover:border-stationPink/50 hover:text-stationPink disabled:cursor-not-allowed disabled:opacity-40 disabled:hover:border-lineSoft disabled:hover:text-inkSub"
                  :disabled="videoBusy !== null"
                  title="把这一件请回架上"
                  @click="restoreVideo(item)"
                >
                  {{ videoBusy === item.id ? '忙…' : '恢复' }}
                </button>
                <button
                  class="rounded-lg border border-lineSoft bg-cardBg px-3 py-1 text-xs text-inkSub transition hover:border-rose-300 hover:text-rose-500 disabled:cursor-not-allowed disabled:opacity-40 disabled:hover:border-lineSoft disabled:hover:text-inkSub"
                  :disabled="videoBusy !== null"
                  title="彻底销毁这一件"
                  @click="discardVideo(item)"
                >
                  销毁
                </button>
              </div>
            </li>
          </ul>
          <p v-else-if="videos === null" class="py-4 text-center text-[12.5px] text-inkFaint">{{ videosLoading ? '正在翻你的回收站……' : '回收站没拉回来' }}</p>
          <p v-else class="py-4 text-center text-[12.5px] text-inkFaint">没有删过视频</p>
          <p v-if="videoNotice" class="mt-2 text-[12px]" :class="videoNotice.kind === 'ok' ? 'text-emerald-600' : 'text-rose-500'">{{ videoNotice.text }}</p>

          <!-- ═══ 我收回的弹幕（11-09 那口收回柜，11-16 从个人空间搬进来）═══ -->
          <div class="mb-2 mt-6 flex items-center justify-between">
            <h4 class="text-[13px] font-semibold text-inkMain">我收回的弹幕</h4>
            <button class="text-[12px] text-stationPink transition hover:opacity-80 disabled:opacity-50" :disabled="dmLoading" @click="loadDm">
              {{ dmLoading ? '翻箱中…' : '重拉' }}
            </button>
          </div>
          <ul v-if="dm?.length" class="space-y-2">
            <li
              v-for="item in dm"
              :key="item.id"
              class="flex flex-wrap items-center gap-3 rounded-xl border border-lineSoft bg-[#f6f7f8] px-4 py-3 dark:bg-[#23262e]"
            >
              <div class="min-w-0 flex-1">
                <p class="text-sm leading-6 text-inkMain" :title="item.text">{{ item.text }}</p>
                <p class="mt-1 flex flex-wrap items-center gap-x-1.5 text-[11.5px] text-inkFaint">
                  <span class="truncate font-mono">{{ item.bvid || '—' }} · 第 {{ (item.pageIdx ?? 0) + 1 }} P · {{ dmClock(item.tSec) }}</span>
                  <span>· {{ friendlyTime(item.deletedAtUtc) }}收回</span>
                  <span
                    v-if="item.inStation === false"
                    class="shrink-0 rounded-full border border-rose-300 px-1.5 py-0.5 text-[10px] leading-none text-rose-500"
                  >
                    片子已不在架，恢复不了
                  </span>
                </p>
              </div>
              <button
                class="shrink-0 rounded-lg border border-lineSoft bg-cardBg px-3 py-1 text-xs text-inkSub transition hover:border-stationPink/50 hover:text-stationPink disabled:cursor-not-allowed disabled:opacity-40 disabled:hover:border-lineSoft disabled:hover:text-inkSub"
                :disabled="dmBusy !== null || item.inStation === false"
                :title="item.inStation === false ? '片子已不在架，恢复不了' : '把这条恢复回片里重新飘'"
                @click="restoreDm(item)"
              >
                {{ dmBusy === item.id ? '恢复中…' : '恢复' }}
              </button>
            </li>
          </ul>
          <p v-else-if="dm === null" class="py-4 text-center text-[12.5px] text-inkFaint">{{ dmLoading ? '正在翻你的收回柜……' : '收回柜没拉回来' }}</p>
          <p v-else class="py-4 text-center text-[12.5px] text-inkFaint">没有在放映厅里收回过弹幕</p>
          <p v-if="dmNotice" class="mt-2 text-[12px]" :class="dmNotice.kind === 'ok' ? 'text-emerald-600' : 'text-rose-500'">{{ dmNotice.text }}</p>
        </div>
      </div>
    </div>
  </Teleport>
</template>
