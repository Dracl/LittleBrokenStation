<script setup>
import { computed, onMounted, ref } from 'vue'
import VideoCard from '../components/VideoCard.vue'
import { member } from '../memberStore'

// ─── 分区：稿件的「户口」地图。上半顶级瓷砖（计数倒序），下半该区的卡阵。
// 有效分区 = 站长改派优先、B 站归档 zone 兜底，口径全在服务端（/api/zones），这里只管摆。
// 站长身份（member.role==='owner'）在每张卡角上有一支小笔：改派「父>子」或清空回继承。
const zones = ref([])
const activeZone = ref('')
const items = ref([])
const loading = ref(false)
const notice = ref(null) // { kind: 'ok' | 'err', text }

const editing = ref(null) // { id, value } 同一时刻只改一张，改派是站长手笔不是流水线
const editBusy = ref(false)

const isOwner = computed(() => member.value?.role === 'owner')

async function loadZones() {
  loading.value = true
  notice.value = null
  try {
    const res = await fetch('/api/zones')
    const data = res.ok ? await res.json().catch(() => null) : null
    zones.value = Array.isArray(data?.zones) ? data.zones : []
    // 首次进来自动选中头名（最旺的区），之后保持站长的选择
    if (!activeZone.value && zones.value.length > 0) pickZone(zones.value[0].name)
  } catch {
    notice.value = { kind: 'err', text: '分区清单没拉回来，后端在打盹' }
  } finally {
    loading.value = false
  }
}

async function pickZone(name) {
  if (loading.value) return
  activeZone.value = name
  editing.value = null
  items.value = []
  try {
    const res = await fetch(`/api/zones/videos?name=${encodeURIComponent(name)}`)
    const data = res.ok ? await res.json().catch(() => null) : null
    items.value = Array.isArray(data?.items) ? data.items : []
  } catch {
    notice.value = { kind: 'err', text: `「${name}」的货没拉回来` }
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
    const res = await fetch('/api/admin/zones/set', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ videoId: editing.value.id, zone: editing.value.value.trim() || null }),
    })
    const data = await res.json().catch(() => null)
    notice.value = res.ok
      ? { kind: 'ok', text: data?.message || '改派落定' }
      : { kind: 'err', text: data?.message || '改派没递上去' }
    if (res.ok) {
      editing.value = null
      await pickZone(activeZone.value) // 户口挪了区，这张卡该从眼前退场，重拉本区最诚实
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
  <div class="mx-auto max-w-[1400px] px-6 py-6">
    <div class="mb-4 flex items-end justify-between gap-4">
      <h1 class="text-[20px] font-semibold text-inkMain">分区</h1>
      <p class="text-[12px] text-inkFaint">
        户口继承自 B 站档案，站长可改派「父&gt;子」
        <button class="ml-2 text-stationPink transition hover:opacity-80" :disabled="loading" @click="loadZones">重扫一遍</button>
      </p>
    </div>

    <p v-if="notice" :class="notice.kind === 'ok' ? 'text-emerald-400' : 'text-rose-400'" class="mb-3 text-[12px]">{{ notice.text }}</p>

    <!-- 上半：顶级瓷砖，计数多者在前 -->
    <div class="mb-6 flex flex-wrap gap-2.5">
      <button
        v-for="z in zones"
        :key="z.name"
        class="rounded-lg border px-3.5 py-2 text-[13px] transition"
        :class="activeZone === z.name
          ? 'border-stationPink/60 bg-stationPink/10 text-stationPink'
          : 'border-black/10 bg-white text-inkMain hover:border-stationPink/40'"
        @click="pickZone(z.name)"
      >
        {{ z.name }}
        <span class="ml-1.5 font-mono text-[11px] opacity-60">{{ z.count }}</span>
      </button>
      <p v-if="!loading && zones.length === 0" class="text-[13px] text-inkFaint">还没有一户落了户的货，瓷砖先空着。</p>
    </div>

    <!-- 下半：该区卡阵（首页同款 VideoCard），站长在卡角有支笔 -->
    <div v-if="activeZone" class="grid grid-cols-2 gap-x-4 gap-y-[38px] md:grid-cols-3 xl:grid-cols-5">
      <div v-for="video in items" :key="video.bvid" class="relative">
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
        <div v-if="editing?.id === video.id" class="absolute inset-x-0 top-9 z-20 rounded-lg border border-black/10 bg-white p-2.5 shadow-xl">
          <input
            v-model="editing.value"
            class="w-full rounded border border-black/15 px-2 py-1.5 text-[12px] text-inkMain outline-none focus:border-stationPink"
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
      <p v-if="items.length === 0" class="col-span-full py-10 text-center text-[13px] text-inkFaint">「{{ activeZone }}」名下暂时没有货。</p>
    </div>
  </div>
</template>
