<script setup>
import { computed, onMounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import TopBar from '../components/TopBar.vue'
import VideoCard from '../components/VideoCard.vue'
import { member } from '../memberStore'

// ─── 分区：稿件的「户口」地图。上半顶级瓷砖（计数倒序），下半该区的卡阵。
// 有效分区 = 站长改派优先、B 站归档 zone 兜底，口径全在服务端（/api/zones），这里只管摆。
// 站长身份（member.role==='owner'）在每张卡角上有一支小笔：改派「父>子」或清空回继承。
const route = useRoute()

const zones = ref([])
const activeZone = ref('')
const items = ref([])
const loading = ref(false)
const itemsLoading = ref(false)
const notice = ref(null) // { kind: 'ok' | 'err', text }

const editing = ref(null) // { id, value } 同一时刻只改一张，改派是站长手笔不是流水线
const editBusy = ref(false)

const isOwner = computed(() => member.value?.role === 'owner')

// 首进随机开一个有货的区：头名恒开会让每次进站都撞见同一面墙，
// 随机才是「逛分区」的本意——当然只在有货的区里随机，空区开局等于没开。
function pickRandomZone() {
  if (!zones.value.length) return
  const stocked = zones.value.filter((z) => z.count > 0)
  const pool = stocked.length ? stocked : zones.value
  pickZone(pool[Math.floor(Math.random() * pool.length)].name)
}

async function loadZones() {
  loading.value = true
  notice.value = null
  try {
    const res = await fetch('/api/zones')
    const data = res.ok ? await res.json().catch(() => null) : null
    zones.value = Array.isArray(data?.zones) ? data.zones : []
    // 带着 ?zone= 来的（首页分区行「查看全部」）就开那个区，空手来的才随机落一个有货的区
    if (!activeZone.value && zones.value.length > 0) {
      const want = typeof route.query.zone === 'string' ? route.query.zone : ''
      if (want && zones.value.some((z) => z.name === want)) pickZone(want)
      else pickRandomZone()
    }
  } catch {
    notice.value = { kind: 'err', text: '分区清单没拉回来，后端在打盹' }
  } finally {
    loading.value = false
  }
}

async function pickZone(name) {
  if (itemsLoading.value) return
  activeZone.value = name
  editing.value = null
  items.value = []
  itemsLoading.value = true
  try {
    const res = await fetch(`/api/zones/videos?name=${encodeURIComponent(name)}`)
    const data = res.ok ? await res.json().catch(() => null) : null
    items.value = Array.isArray(data?.items) ? data.items : []
  } catch {
    notice.value = { kind: 'err', text: `「${name}」的货没拉回来` }
  } finally {
    itemsLoading.value = false
  }
}

function startEdit(video) {
  editing.value = { id: video.id, value: video.zone || '' }
}

async function applyEdit() {
  if (!editing.value || editBusy.value) return
  editBusy.value = true
  notice.value = null
  try {
    // 只一张嘴：改派走投稿人自管那同一口 PATCH /api/videos/{id}（里面同样走 TryNormalizeZone → SetVideoZoneOverride）。
    // 以前这页另走 POST /api/admin/zones/set，两条嘴写同一列 site_zone、各自校验，日子久了必然漂（盘点扫出来的）；
    // 那一口已退休，站长身份在 CanManageVideo 里天然短路，能力一分没削。
    const res = await fetch(`/api/videos/${editing.value.id}`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ zone: editing.value.value.trim() || null }),
    })
    const data = await res.json().catch(() => null)
    notice.value = res.ok
      ? { kind: 'ok', text: data?.message || '改派落定' }
      : { kind: 'err', text: data?.message || '改派没递上去' }
    if (res.ok) {
      editing.value = null
      await Promise.all([pickZone(activeZone.value), loadZones()]) // 户口挪了区：卡阵与瓷砖计数一起重拉才诚实
    }
  } catch {
    notice.value = { kind: 'err', text: '改派没递上去，网络不配合' }
  } finally {
    editBusy.value = false
  }
}

function clearEdit() {
  editing.value = null
}

onMounted(loadZones)
</script>

<template>
  <div class="min-h-screen">
    <TopBar />

    <main class="mx-auto max-w-[1400px] px-6 py-6">
      <!-- 页头：区徽 + 标题 + 说明，右侧留重扫 -->
      <div class="mb-5 flex items-end justify-between gap-4">
        <div>
          <h1 class="flex items-center gap-2 text-[20px] font-semibold text-inkMain">
            <svg viewBox="0 0 24 24" class="h-[18px] w-[18px] text-stationPink" fill="currentColor">
              <rect x="3" y="3" width="8" height="8" rx="2.5" />
              <rect x="13" y="3" width="8" height="8" rx="2.5" />
              <rect x="3" y="13" width="8" height="8" rx="2.5" />
              <rect x="13" y="13" width="8" height="8" rx="2.5" />
            </svg>
            分区
          </h1>
          <p class="mt-1 text-[12px] text-inkFaint">户口继承自 B 站档案，站长可改派「父&gt;子」</p>
        </div>
        <button class="shrink-0 text-[12px] text-stationPink transition hover:opacity-80" :disabled="loading" @click="loadZones">重扫一遍</button>
      </div>

      <p v-if="notice" :class="notice.kind === 'ok' ? 'text-emerald-600 dark:text-emerald-400' : 'text-rose-500 dark:text-rose-400'" class="mb-3 text-[12px]">{{ notice.text }}</p>

      <!-- 上半：顶级瓷砖，计数多者在前 -->
      <div v-if="loading && zones.length === 0" class="mb-6 flex flex-wrap gap-2.5">
        <span v-for="i in 8" :key="i" class="h-[38px] w-24 animate-pulse rounded-xl bg-black/[0.05] dark:bg-white/[0.06]"></span>
      </div>
      <div v-else class="mb-6 flex flex-wrap gap-2.5">
        <button
          v-for="z in zones"
          :key="z.name"
          class="flex items-center gap-2 rounded-xl border px-3.5 py-2 text-[13px] transition-all duration-150"
          :class="activeZone === z.name
            ? 'border-stationPink/50 bg-gradient-to-br from-stationPink/15 to-stationPink/5 text-stationPink shadow-sm'
            : 'border-black/10 dark:border-white/10 bg-cardBg text-inkMain hover:-translate-y-0.5 hover:border-stationPink/40 hover:shadow-md'"
          @click="pickZone(z.name)"
        >
          {{ z.name }}
          <span
            class="rounded-full px-1.5 py-0.5 font-mono text-[10.5px] leading-none"
            :class="activeZone === z.name ? 'bg-stationPink/15' : 'bg-black/[0.06] dark:bg-white/10 opacity-70'"
          >{{ z.count }}</span>
        </button>
        <p v-if="!loading && zones.length === 0" class="text-[13px] text-inkFaint">还没有一户落了户的货，瓷砖先空着。</p>
      </div>

      <!-- 当前区标题条：区名 + 件数，右手一颗骰子随区 -->
      <div v-if="activeZone" class="mb-4 flex items-center gap-2.5">
        <h2 class="text-[16px] font-semibold text-inkMain">{{ activeZone }}</h2>
        <span v-if="!itemsLoading" class="rounded-full bg-stationPink/10 px-2 py-0.5 text-[11.5px] text-stationPink">{{ items.length }} 件</span>
        <span v-if="zones.length > 1" class="ml-auto"></span>
        <button
          v-if="zones.length > 1"
          class="flex items-center gap-1.5 rounded-full border border-black/10 dark:border-white/10 px-2.5 py-1 text-[12px] text-inkSub transition hover:border-stationPink/50 hover:text-stationPink disabled:opacity-40"
          :disabled="itemsLoading"
          title="随机换一个分区逛逛"
          @click="pickRandomZone"
        >
          <svg viewBox="0 0 24 24" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="1.7">
            <rect x="3.5" y="3.5" width="17" height="17" rx="4" />
            <circle cx="8.5" cy="8.5" r="1.3" fill="currentColor" stroke="none" />
            <circle cx="15.5" cy="8.5" r="1.3" fill="currentColor" stroke="none" />
            <circle cx="12" cy="12" r="1.3" fill="currentColor" stroke="none" />
            <circle cx="8.5" cy="15.5" r="1.3" fill="currentColor" stroke="none" />
            <circle cx="15.5" cy="15.5" r="1.3" fill="currentColor" stroke="none" />
          </svg>
          随机换区
        </button>
      </div>

      <!-- 拉货中的骨架阵：卡阵位先立灰模，免白屏等后端 -->
      <div v-if="itemsLoading" class="grid grid-cols-2 gap-x-4 gap-y-[38px] md:grid-cols-3 xl:grid-cols-5">
        <div v-for="i in 10" :key="i" class="animate-pulse">
          <div class="aspect-[16/10] rounded-xl bg-black/[0.05] dark:bg-white/[0.06]"></div>
          <div class="mt-2 h-3.5 w-11/12 rounded bg-black/[0.05] dark:bg-white/[0.06]"></div>
          <div class="mt-2 h-3 w-1/2 rounded bg-black/[0.05] dark:bg-white/[0.06]"></div>
        </div>
      </div>

      <!-- 下半：该区卡阵（首页同款 VideoCard），站长在卡角有支笔 -->
      <div v-else-if="activeZone && items.length" class="grid grid-cols-2 gap-x-4 gap-y-[38px] md:grid-cols-3 xl:grid-cols-5">
        <div v-for="video in items" :key="video.bvid" class="relative">
          <!-- 命中区收口（站长 11-02 定的 coverTitleText）：外层包整卡的 RouterLink 撤了，可点的是 VideoCard 内部的封面与标题字面。
               本层 relative 留着：左上角区角标（pointer-events-none）、右上改派钮与下方的改派抽屉都以它为定位参照 -->
          <VideoCard :video="video" />
          <span
            class="pointer-events-none absolute left-1.5 top-1.5 rounded bg-black/55 px-1.5 py-0.5 text-[10px] text-white/85 backdrop-blur"
            :title="video.zone"
          >{{ video.zone }}</span>
          <button
            v-if="isOwner"
            class="absolute right-1.5 top-1.5 z-10 flex h-6 w-6 items-center justify-center rounded-full bg-black/55 text-white/75 backdrop-blur transition hover:bg-stationPink hover:text-white"
            title="改派分区"
            @click="startEdit(video)"
          >
            <svg viewBox="0 0 24 24" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round">
              <path d="M12 20h9M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4Z" />
            </svg>
          </button>

          <!-- 改派小抽屉：贴在卡上就地办，不弹窗打断扫卡 -->
          <div v-if="editing?.id === video.id" class="absolute inset-x-0 top-9 z-20 rounded-lg border border-black/10 dark:border-white/10 bg-cardBg p-2.5 shadow-xl">
            <input
              v-model="editing.value"
              class="w-full rounded border border-black/15 dark:border-white/10 px-2 py-1.5 text-[12px] text-inkMain outline-none focus:border-stationPink"
              placeholder="父>子，如 音乐>翻唱；留空回继承"
              @keyup.enter="applyEdit"
              @keyup.esc="clearEdit"
            />
            <div class="mt-2 flex justify-end gap-2">
              <button class="text-[12px] text-inkFaint transition hover:text-inkMain" @click="clearEdit">算了</button>
              <button
                class="rounded bg-stationPink px-2.5 py-1 text-[12px] text-white transition hover:opacity-90 disabled:opacity-50"
                :disabled="editBusy"
                @click="applyEdit"
              >{{ editBusy ? '递单中…' : '落定' }}</button>
            </div>
          </div>
        </div>
      </div>

      <!-- 空区与开局前的空态 -->
      <div v-else-if="activeZone && !itemsLoading" class="rounded-xl border border-dashed border-black/10 dark:border-white/10 py-16 text-center">
        <p class="text-[13px] text-inkSub">「{{ activeZone }}」名下暂时没有货</p>
        <button
          v-if="zones.length > 1"
          class="mt-3 rounded-full border border-stationPink px-5 py-1.5 text-[13px] text-stationPink transition hover:bg-stationPink hover:text-white"
          @click="pickRandomZone"
        >
          随机换个区逛逛
        </button>
      </div>
      <div v-else-if="!loading && zones.length === 0" class="rounded-xl border border-dashed border-black/10 dark:border-white/10 py-16 text-center">
        <p class="text-[13px] text-inkSub">还没有落了户口的稿件</p>
      </div>
    </main>
  </div>
</template>
