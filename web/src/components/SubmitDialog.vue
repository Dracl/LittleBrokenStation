<script setup>
import { onUnmounted, ref, watch } from 'vue'
import { submitDialogOpen } from '../submitStore'

const TABS = [
  { key: 'video', label: '视频链接' },
  { key: 'favurl', label: '收藏夹链接' },
  { key: 'favfile', label: '收藏夹文件' },
]

const activeTab = ref('video')
const videoUrl = ref('')
const favUrl = ref('')
const busy = ref(false)
const notice = ref(null) // { kind, text }

function close() {
  submitDialogOpen.value = false
}

function onKeydown(event) {
  if (event.key === 'Escape') close()
}

watch(submitDialogOpen, (open) => {
  if (open) {
    notice.value = null
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
  const result = settle(await postJson('/api/videos', { url }))
  busy.value = false
  notice.value = result
  if (result.kind === 'ok') videoUrl.value = ''
}

async function submitFavUrl() {
  const url = favUrl.value.trim()
  if (!url || busy.value) return
  busy.value = true
  const result = settle(await postJson('/api/favorites/import-url', { url }))
  busy.value = false
  notice.value = result
  if (result.kind === 'ok') favUrl.value = ''
}

async function submitFavFile(event) {
  const file = event.target.files?.[0]
  event.target.value = ''
  if (!file || busy.value) return
  busy.value = true
  try {
    const content = await file.text()
    const result = settle(await postJson('/api/favorites/import', { content }))
    notice.value = result
  } catch {
    notice.value = { kind: 'err', text: '读不动这个文件' }
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <Teleport to="body">
    <div
      v-if="submitDialogOpen"
      class="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4"
      @click.self="close"
    >
      <div class="w-full max-w-[520px] rounded-xl bg-cardBg shadow-2xl">
        <div class="flex items-center justify-between border-b border-white/5 px-5 py-4">
          <div>
            <h3 class="text-base font-semibold text-inkMain">投稿进站</h3>
            <p class="mt-0.5 text-xs text-inkFaint">丢进来，分站替你存正片、弹幕、字幕和封面</p>
          </div>
          <button class="rounded p-1 text-inkFaint transition hover:text-inkMain" aria-label="关闭" @click="close">
            <svg viewBox="0 0 24 24" class="h-5 w-5" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M6 6l12 12M18 6L6 18" stroke-linecap="round" />
            </svg>
          </button>
        </div>

        <div class="flex gap-1 px-5 pt-4">
          <button
            v-for="tab in TABS"
            :key="tab.key"
            class="rounded-md px-3 py-1.5 text-sm transition"
            :class="activeTab === tab.key ? 'bg-stationPink/15 text-stationPink' : 'text-inkSub hover:text-inkMain'"
            @click="activeTab = tab.key"
          >
            {{ tab.label }}
          </button>
        </div>

        <div class="px-5 py-4">
          <template v-if="activeTab === 'video'">
            <input
              v-model="videoUrl"
              type="text"
              placeholder="https://www.bilibili.com/video/BV…"
              class="w-full rounded-lg border border-white/10 bg-bgPage px-4 py-2.5 text-sm text-inkMain placeholder:text-inkFaint focus:border-stationPink focus:outline-none"
              :disabled="busy"
              @keyup.enter="submitVideo"
            />
          </template>

          <template v-else-if="activeTab === 'favurl'">
            <input
              v-model="favUrl"
              type="text"
              placeholder="space.bilibili.com/…/favlist?fid=… 或直接填收藏夹 ID"
              class="w-full rounded-lg border border-white/10 bg-bgPage px-4 py-2.5 text-sm text-inkMain placeholder:text-inkFaint focus:border-stationPink focus:outline-none"
              :disabled="busy"
              @keyup.enter="submitFavUrl"
            />
            <p class="mt-2 text-xs text-inkFaint">整架搬回：按 BV 号去重，公开收藏夹免登录即可搬，加密的要等 cookie 到站。</p>
          </template>

          <template v-else>
            <label class="flex cursor-pointer flex-col items-center justify-center gap-2 rounded-lg border border-dashed border-white/15 py-8 transition hover:border-stationPink/50">
              <svg viewBox="0 0 24 24" class="h-7 w-7 text-inkFaint" fill="none" stroke="currentColor" stroke-width="1.6">
                <path d="M12 16V4m0 0 4 4m-4-4-4 4" stroke-linecap="round" stroke-linejoin="round" />
                <path d="M4 16v2a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-2" stroke-linecap="round" />
              </svg>
              <span class="text-sm text-inkSub">{{ busy ? '正在签收……' : '选择浏览器导出的收藏夹 HTML 文件' }}</span>
              <input type="file" accept=".html,.htm,text/html" class="hidden" :disabled="busy" @change="submitFavFile" />
            </label>
          </template>

          <p
            v-if="notice"
            class="mt-3 text-xs leading-5"
            :class="{
              'text-emerald-300': notice.kind === 'ok',
              'text-amber-200': notice.kind === 'warn',
              'text-rose-300': notice.kind === 'err',
            }"
          >
            {{ notice.text }}
          </p>
        </div>

        <div v-if="activeTab !== 'favfile'" class="flex justify-end gap-2 border-t border-white/5 px-5 py-3.5">
          <button class="rounded-lg px-4 py-2 text-sm text-inkSub transition hover:text-inkMain" @click="close">
            先不投了
          </button>
          <button
            class="rounded-lg bg-stationPink px-5 py-2 text-sm font-medium text-white transition hover:opacity-90 disabled:opacity-50"
            :disabled="busy || !(activeTab === 'video' ? videoUrl.trim() : favUrl.trim())"
            @click="activeTab === 'video' ? submitVideo() : submitFavUrl()"
          >
            {{ busy ? '签收中…' : '丢它进站' }}
          </button>
        </div>
      </div>
    </div>
  </Teleport>
</template>
