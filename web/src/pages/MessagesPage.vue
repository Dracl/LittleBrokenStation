<script setup>
import { onMounted, ref } from 'vue'
import TopBar from '../components/TopBar.vue'
import { member, loaded, unread, loginDialogOpen, loginDialogMode, load as loadMember } from '../memberStore'
import { friendlyTime } from '../utils/time'

const items = ref([])
const loading = ref(true)
const expandedId = ref(null)
const notice = ref(null)

// kind 徽标：welcome 粉 / complete 绿 / fail 红 / announce 金，未知 kind 落灰底保底
const kindMeta = {
  welcome: { label: '欢迎', cls: 'bg-stationPink/10 text-stationPink' },
  complete: { label: '入档', cls: 'bg-emerald-500/10 text-emerald-600' },
  fail: { label: '趔趄', cls: 'bg-red-500/10 text-red-500' },
  announce: { label: '公告', cls: 'bg-amber-400/15 text-amber-600' },
}

function meta(kind) {
  return kindMeta[kind] ?? { label: kind || '信', cls: 'bg-lineSoft text-inkSub' }
}

async function loadInbox() {
  if (!member.value) {
    items.value = []
    loading.value = false
    return
  }
  loading.value = true
  try {
    const res = await fetch('/api/member/messages')
    if (res.ok) {
      const data = await res.json()
      items.value = data.items ?? []
      unread.value = Number(data.unread) || 0
    } else if (res.status === 401) {
      member.value = null
    }
  } catch {
    notice.value = '信箱没拉开，刷新再试'
  } finally {
    loading.value = false
  }
}

onMounted(async () => {
  if (!loaded.value) await loadMember()
  await loadInbox()
})

function openLogin() {
  loginDialogMode.value = 'admit'
  loginDialogOpen.value = true
}

// 点开即读：展开正文的顺手替主人把信拆了、标上已读
async function toggle(item) {
  expandedId.value = expandedId.value === item.id ? null : item.id
  if (expandedId.value !== item.id || item.readAtUtc) return
  try {
    const res = await fetch(`/api/member/messages/${item.id}/read`, { method: 'POST' })
    if (res.ok) {
      item.readAtUtc = new Date().toISOString()
      if (unread.value > 0) unread.value -= 1
    }
  } catch {
    /* 标读没回音，下次进页再补 */
  }
}

async function markAll() {
  if (!member.value) return
  try {
    const res = await fetch('/api/member/messages/read-all', { method: 'POST' })
    if (res.ok) {
      const now = new Date().toISOString()
      items.value.forEach((m) => { if (!m.readAtUtc) m.readAtUtc = now })
      unread.value = 0
      const data = await res.json().catch(() => null)
      notice.value = data?.message ?? '都标已读了'
      setTimeout(() => { notice.value = null }, 2500)
    }
  } catch {
    notice.value = '全部已读没递上去'
    setTimeout(() => { notice.value = null }, 2500)
  }
}
</script>

<template>
  <div class="min-h-screen bg-pageBg">
    <TopBar />
    <main class="mx-auto max-w-[860px] px-6 py-6">
      <h1 class="mb-4 text-lg font-medium text-inkMain">站内信箱</h1>

      <!-- 访客：信箱是成员待遇，指路扫码 -->
      <section v-if="loaded && !member" class="rounded-2xl border border-lineSoft bg-cardBg px-6 py-14 text-center">
        <svg viewBox="0 0 64 48" class="mx-auto mb-4 h-14 w-16 text-lineSoft" fill="none" stroke="currentColor" stroke-width="2.4">
          <rect x="3" y="5" width="58" height="38" rx="6" />
          <path d="M5 9 32 29 59 9" stroke-linejoin="round" />
        </svg>
        <p class="text-sm text-inkSub">信箱是分站成员的待遇——扫码进站，第一封欢迎信就在等你。</p>
        <button
          class="mt-5 rounded-full bg-stationPink px-6 py-2 text-sm text-white transition hover:bg-stationPink/90"
          @click="openLogin"
        >
          扫码进站
        </button>
      </section>

      <template v-else>
        <section class="overflow-hidden rounded-2xl border border-lineSoft bg-cardBg">
          <div class="flex items-center justify-between border-b border-lineSoft px-5 py-3">
            <span class="text-[13px] text-inkSub">
              {{ items.length ? `${items.length} 封信 · ${unread} 封未读` : '信笺都在这' }}
            </span>
            <button
              v-if="unread > 0"
              class="rounded-full border border-lineSoft px-3 py-1 text-xs text-inkSub transition hover:border-stationPink hover:text-stationPink"
              @click="markAll"
            >
              全部已读
            </button>
          </div>

          <p v-if="loading" class="px-5 py-10 text-center text-sm text-inkFaint">正在翻信箱…</p>

          <!-- 空态：邮箱空空，岁月静好 -->
          <div v-else-if="!items.length" class="px-6 py-14 text-center">
            <svg viewBox="0 0 64 48" class="mx-auto mb-4 h-14 w-16 text-lineSoft" fill="none" stroke="currentColor" stroke-width="2.4">
              <rect x="3" y="5" width="58" height="38" rx="6" />
              <path d="M5 9 32 29 59 9" stroke-linejoin="round" />
              <path d="M44 34c2.5-3 6-3.4 9-1" stroke-linecap="round" opacity="0.55" />
            </svg>
            <p class="text-sm text-inkFaint">邮箱空空，岁月静好。投的稿子入档时，这里会亮起来。</p>
          </div>

          <ul v-else>
            <li v-for="item in items" :key="item.id" class="border-b border-lineSoft/60 last:border-b-0">
              <button class="flex w-full items-center gap-3 px-5 py-3.5 text-left transition hover:bg-stationPink/5" @click="toggle(item)">
                <span class="shrink-0 rounded-md px-1.5 py-0.5 text-[11px]" :class="meta(item.kind).cls">{{ meta(item.kind).label }}</span>
                <span class="min-w-0 flex-1">
                  <span class="block truncate text-[13px]" :class="item.readAtUtc ? 'text-inkSub' : 'font-medium text-inkMain'">
                    {{ item.title }}
                  </span>
                  <span v-if="item.fromName" class="block truncate text-[11px] text-inkFaint">来自 {{ item.fromName }}</span>
                </span>
                <span class="shrink-0 text-[11px] text-inkFaint">{{ friendlyTime(item.createdAtUtc) }}</span>
                <span v-if="!item.readAtUtc" class="h-2 w-2 shrink-0 rounded-full bg-stationPink" title="未读"></span>
              </button>

              <div v-if="expandedId === item.id" class="px-5 pb-4">
                <div class="rounded-xl bg-[#f6f7f8] px-4 py-3">
                  <p class="whitespace-pre-wrap text-[13px] leading-relaxed text-inkMain">{{ item.body }}</p>
                  <RouterLink
                    v-if="item.link"
                    :to="item.link"
                    class="mt-2.5 inline-block text-xs text-stationPink transition hover:underline"
                  >去看 →</RouterLink>
                </div>
              </div>
            </li>
          </ul>
        </section>

        <p v-if="notice" class="mt-3 text-center text-xs text-inkFaint">{{ notice }}</p>
      </template>
    </main>
  </div>
</template>
