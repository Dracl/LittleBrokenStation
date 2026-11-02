<script setup>
import { onUnmounted, ref, watch } from 'vue'
import { submitDialogOpen } from '../submitStore'

const TABS = [
  { key: 'video', label: '视频链接' },
  { key: 'favurl', label: '收藏夹链接' },
  { key: 'upurl', label: 'UP 主主页' },
]

const VISIBILITIES = [
  { key: 'public', label: '公开' },
  { key: 'private', label: '私有' },
]

const activeTab = ref('video')
const videoUrl = ref('')
const favUrl = ref('')
const upUrl = ref('')
const busy = ref(false)
const notice = ref(null) // { kind, text }
// 可见性三档共用一档（批量档也是整批同一个），默认公开；
// 切 tab 不动它，关窗重开才复位。
const visibility = ref('public')

function close() {
  submitDialogOpen.value = false
}

function onKeydown(event) {
  if (event.key === 'Escape') close()
}

watch(submitDialogOpen, (open) => {
  if (open) {
    notice.value = null
    visibility.value = 'public'
    window.addEventListener('keydown', onKeydown)
  } else {
    window.removeEventListener('keydown', onKeydown)
  }
})

onUnmounted(() => window.removeEventListener('keydown', onKeydown))

async function postJson(url, body) {
  const res = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
  const data = await res.json().catch(() => null)
  return { ok: res.ok, status: res.status, data }
}

function settle({ ok, status, data }) {
  if (ok) return { kind: 'ok', text: data?.message ?? '已加入进站队列' }
  if (status === 409) return { kind: 'warn', text: data?.message ?? '这个视频已经到过了' }
  return { kind: 'err', text: data?.message ?? '分站收不下这条投稿，检查一下内容' }
}

async function submitVideo() {
  const url = videoUrl.value.trim()
  if (!url || busy.value) return
  busy.value = true
  const result = settle(await postJson('/api/videos', { url, visibility: visibility.value }))
  busy.value = false
  notice.value = result
  if (result.kind === 'ok') videoUrl.value = ''
}

async function submitFavUrl() {
  const url = favUrl.value.trim()
  if (!url || busy.value) return
  busy.value = true
  const result = settle(await postJson('/api/favorites/import-url', { url, visibility: visibility.value }))
  busy.value = false
  notice.value = result
  if (result.kind === 'ok') favUrl.value = ''
}

async function submitUpUrl() {
  const url = upUrl.value.trim()
  if (!url || busy.value) return
  busy.value = true
  const result = settle(await postJson('/api/up/import', { url, visibility: visibility.value }))
  busy.value = false
  notice.value = result
  if (result.kind === 'ok') upUrl.value = ''
}

function inputReady() {
  if (activeTab.value === 'video') return videoUrl.value.trim()
  if (activeTab.value === 'upurl') return upUrl.value.trim()
  return favUrl.value.trim()
}

function submitCurrent() {
  if (activeTab.value === 'video') return submitVideo()
  if (activeTab.value === 'upurl') return submitUpUrl()
  return submitFavUrl()
}
</script>

<template>
  <Teleport to="body">
    <div
      v-if="submitDialogOpen"
      class="lbs-fade fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4 backdrop-blur-sm"
      @click.self="close"
    >
      <div class="lbs-pop w-full max-w-[520px] overflow-hidden rounded-2xl bg-cardBg shadow-[0_24px_80px_-12px_rgba(0,0,0,0.45)]">
        <div class="flex items-center justify-between bg-gradient-to-r from-stationPink/10 via-stationPink/5 to-transparent px-6 pb-4 pt-5">
          <div class="flex items-center gap-3">
            <span class="flex h-10 w-10 shrink-0 items-center justify-center rounded-full bg-stationPink/10">
              <svg viewBox="0 0 24 24" class="h-5 w-5 text-stationPink" fill="none" stroke="currentColor" stroke-width="1.8">
                <path d="M22 2 11 13" stroke-linecap="round" stroke-linejoin="round" />
                <path d="m22 2-7 20-4-9-9-4 20-7Z" stroke-linecap="round" stroke-linejoin="round" />
              </svg>
            </span>
            <div>
              <h3 class="text-[17px] font-semibold text-inkMain">投稿进站</h3>
              <p class="mt-0.5 text-xs text-inkFaint">丢进来，分站替你存正片、弹幕、字幕和封面</p>
            </div>
          </div>
          <button
            class="flex h-8 w-8 shrink-0 items-center justify-center rounded-full text-inkFaint transition hover:bg-black/5 hover:text-inkMain"
            aria-label="关闭"
            @click="close"
          >
            <svg viewBox="0 0 24 24" class="h-5 w-5" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M6 6l12 12M18 6L6 18" stroke-linecap="round" />
            </svg>
          </button>
        </div>

        <div class="flex gap-2 px-6 pt-4">
          <button
            v-for="tab in TABS"
            :key="tab.key"
            class="rounded-full border px-3.5 py-1.5 text-sm transition"
            :class="
              activeTab === tab.key
                ? 'border-stationPink bg-stationPink text-white shadow-sm shadow-stationPink/30'
                : 'border-lineSoft text-inkSub hover:border-stationPink/40 hover:text-inkMain'
            "
            @click="activeTab = tab.key"
          >
            {{ tab.label }}
          </button>
        </div>

        <div class="px-6 py-5">
          <template v-if="activeTab === 'video'">
            <input
              v-model="videoUrl"
              type="text"
              placeholder="https://www.bilibili.com/video/BV…"
              class="w-full rounded-xl border border-lineSoft bg-[#f6f7f8] dark:bg-[#23262e] dark:focus:bg-[#23262e] px-4 py-3 text-sm text-inkMain placeholder:text-inkFaint transition focus:border-stationPink focus:bg-white focus:outline-none focus:ring-2 focus:ring-stationPink/20"
              :disabled="busy"
              @keyup.enter="submitVideo"
            />
          </template>

          <template v-else-if="activeTab === 'favurl'">
            <input
              v-model="favUrl"
              type="text"
              placeholder="space.bilibili.com/…/favlist?fid=… 或直接填收藏夹 ID"
              class="w-full rounded-xl border border-lineSoft bg-[#f6f7f8] dark:bg-[#23262e] dark:focus:bg-[#23262e] px-4 py-3 text-sm text-inkMain placeholder:text-inkFaint transition focus:border-stationPink focus:bg-white focus:outline-none focus:ring-2 focus:ring-stationPink/20"
              :disabled="busy"
              @keyup.enter="submitFavUrl"
            />
            <p class="mt-2 text-xs leading-5 text-inkFaint">整架搬回：按 BV 号去重，公开收藏夹谁都能搬；加密收藏夹得等成员号池里有能读它的 B 站号（个人空间扫码绑一个）。</p>
          </template>

          <template v-else-if="activeTab === 'upurl'">
            <input
              v-model="upUrl"
              type="text"
              placeholder="space.bilibili.com/… 空间链接，或直接填 UP 主 MID"
              class="w-full rounded-xl border border-lineSoft bg-[#f6f7f8] dark:bg-[#23262e] dark:focus:bg-[#23262e] px-4 py-3 text-sm text-inkMain placeholder:text-inkFaint transition focus:border-stationPink focus:bg-white focus:outline-none focus:ring-2 focus:ring-stationPink/20"
              :disabled="busy"
              @keyup.enter="submitUpUrl"
            />
            <p class="mt-2 text-xs leading-5 text-inkFaint">整架搬回投稿：按 BV 去重，公开空间免登录即可搬；单次封顶 500 条，翻页节奏由分站节流慢慢收，大 UP 主会收得安静又持久。</p>
          </template>

          <!-- 谁能看见：分段钮形状照抄墙上「喂料三档」（WallWindow），白卡上直接摆，日夜同一套颜色。
               三档共用这一枚：摆在输入区之下、回执之上。 -->
          <div class="mt-4 flex flex-wrap items-center gap-2">
            <span class="text-[11.5px] text-inkFaint">谁能看见</span>
            <div class="flex items-center rounded-full border border-lineSoft p-0.5" role="group" aria-label="这条投稿谁能看见">
              <button
                v-for="v in VISIBILITIES"
                :key="v.key"
                type="button"
                class="rounded-full px-2.5 py-0.5 text-[11.5px] transition"
                :class="visibility === v.key ? 'bg-stationPink text-white' : 'text-inkSub hover:text-stationPink'"
                :aria-pressed="visibility === v.key"
                :disabled="busy"
                @click="visibility = v.key"
              >
                {{ v.label }}
              </button>
            </div>
            <span v-if="visibility === 'private'" class="text-[11.5px] text-inkFaint">只有你自己看得见，别处搜不到</span>
          </div>

          <p
            v-if="notice"
            class="mt-3 text-xs leading-5"
            :class="{
              'text-emerald-600': notice.kind === 'ok',
              'text-amber-600': notice.kind === 'warn',
              'text-rose-500': notice.kind === 'err',
            }"
          >
            {{ notice.text }}
          </p>
        </div>

        <div class="flex justify-end gap-2 border-t border-lineSoft/70 bg-[#fafbfc] dark:bg-white/5 px-6 py-4">
          <button class="rounded-lg px-4 py-2 text-sm text-inkSub transition hover:bg-black/5 hover:text-inkMain" @click="close">
            先不投了
          </button>
          <button
            class="rounded-lg bg-gradient-to-r from-stationPink to-[#f994b6] px-5 py-2 text-sm font-medium text-white shadow-md shadow-stationPink/30 transition hover:-translate-y-px hover:shadow-lg hover:shadow-stationPink/40 active:translate-y-0 disabled:translate-y-0 disabled:opacity-50 disabled:shadow-none"
            :disabled="busy || !inputReady()"
            @click="submitCurrent()"
          >
            {{ busy ? '签收中…' : '丢它进站' }}
          </button>
        </div>
      </div>
    </div>
  </Teleport>
</template>

<style scoped>
.lbs-fade {
  animation: lbs-fade-in 0.18s ease-out both;
}
.lbs-pop {
  animation: lbs-pop-in 0.22s cubic-bezier(0.34, 1.4, 0.64, 1) both;
}
@keyframes lbs-fade-in {
  from {
    opacity: 0;
  }
  to {
    opacity: 1;
  }
}
@keyframes lbs-pop-in {
  from {
    opacity: 0;
    transform: translateY(10px) scale(0.97);
  }
  to {
    opacity: 1;
    transform: translateY(0) scale(1);
  }
}
</style>
