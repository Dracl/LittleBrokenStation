<script setup>
// 分享动态到私信：选一位分站伙伴 + 一句安利语，定格快照随信落库（SubmitDialog 同款语言）。
import { computed, onMounted, ref } from 'vue'

const props = defineProps({
  d: { type: Object, required: true }, // 橱窗行：{ dynamicId, authorName, authorFaceUrl, text }
})
const emit = defineEmits(['close', 'done'])

const people = ref(null) // null 加载中 / [] 没人
const picked = ref(null) // memberId
const note = ref('')
const busy = ref(false)
const error = ref('')

const summary = computed(() => {
  const raw = (props.d.text || '').split('\n\n转发自')[0]
  return raw.length > 60 ? `${raw.slice(0, 60)}…` : raw || '（一条动态）'
})

async function loadPeople() {
  try {
    const res = await fetch('/api/dm/directory')
    if (res.status === 401) {
      error.value = '登录后才能给朋友安利'
      people.value = []
      return
    }
    const data = await res.json()
    people.value = data.people ?? []
  } catch {
    error.value = '点名失败，检查分站后端'
    people.value = []
  }
}

async function submit() {
  if (!picked.value || busy.value) return
  busy.value = true
  error.value = ''
  try {
    const res = await fetch(`/api/dynamics/${encodeURIComponent(props.d.dynamicId)}/share`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ toMemberId: picked.value, note: note.value.trim() || null }),
    })
    const data = await res.json().catch(() => null)
    if (res.ok) {
      emit('done', data?.message ?? '已塞进对方信箱')
    } else {
      error.value = data?.message ?? '分享没递出去'
    }
  } catch {
    error.value = '分站后端没有应答'
  } finally {
    busy.value = false
  }
}

onMounted(loadPeople)
</script>

<template>
  <div class="fixed inset-0 z-40 flex items-center justify-center px-4">
    <div class="absolute inset-0 bg-black/35 backdrop-blur-[2px]" @click="emit('close')"></div>
    <div class="relative w-full max-w-[380px] overflow-hidden rounded-2xl border border-lineSoft bg-cardBg shadow-[0_20px_60px_-12px_rgba(0,0,0,0.35)]">
      <div class="flex items-center justify-between border-b border-lineSoft bg-gradient-to-r from-stationPink/10 via-stationPink/5 to-transparent px-4 py-3">
        <span class="flex items-center gap-2.5 text-sm font-medium text-inkMain">
          <span class="flex h-8 w-8 items-center justify-center rounded-full bg-stationPink/10">
            <svg viewBox="0 0 24 24" class="h-4 w-4 text-stationPink" fill="none" stroke="currentColor" stroke-width="1.8">
              <path d="M4 12v7a1 1 0 0 0 1 1h14a1 1 0 0 0 1-1v-7" stroke-linecap="round" />
              <path d="M12 15V3M8 7l4-4 4 4" stroke-linecap="round" stroke-linejoin="round" />
            </svg>
          </span>
          安利这条动态
        </span>
        <button class="flex h-7 w-7 items-center justify-center rounded-full text-inkFaint transition hover:bg-black/5 hover:text-inkMain" @click="emit('close')">✕</button>
      </div>

      <!-- 要安利的内容缩略 -->
      <div class="mx-4 mt-3 flex items-center gap-2.5 rounded-xl bg-[#f6f7f8] px-3 py-2.5">
        <img
          v-if="d.authorFaceUrl"
          :src="d.authorFaceUrl"
          referrerpolicy="no-referrer"
          class="h-8 w-8 shrink-0 rounded-full object-cover"
          alt=""
          @error="$event.target.style.display = 'none'"
        />
        <span v-else class="flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-stationPink/15 text-xs text-stationPink">{{ (d.authorName || '?').slice(0, 1) }}</span>
        <span class="min-w-0 flex-1">
          <span class="block text-[12px] font-medium text-inkMain">{{ d.authorName || 'B站作者' }}</span>
          <span class="block truncate text-[12px] text-inkSub">{{ summary }}</span>
        </span>
      </div>

      <!-- 选伙伴 -->
      <div class="px-4 pt-3">
        <p class="mb-1.5 text-[12px] text-inkFaint">送给谁</p>
        <p v-if="people === null" class="py-4 text-center text-[12px] text-inkFaint">在点名…</p>
        <div v-else-if="!people.length" class="rounded-xl border border-dashed border-lineSoft px-3 py-4 text-center text-[12px] text-inkFaint">
          站上还没有别的伙伴可安利
        </div>
        <div v-else class="max-h-[168px] space-y-1 overflow-y-auto pr-0.5">
          <button
            v-for="p in people"
            :key="p.memberId"
            class="flex w-full items-center gap-2.5 rounded-xl px-2.5 py-2 text-left transition"
            :class="picked === p.memberId ? 'bg-stationPink/10 ring-1 ring-stationPink/50' : 'hover:bg-stationPink/5'"
            @click="picked = p.memberId"
          >
            <img v-if="p.faceUrl" :src="p.faceUrl" referrerpolicy="no-referrer" class="h-7 w-7 rounded-full object-cover" alt="" />
            <span v-else class="flex h-7 w-7 items-center justify-center rounded-full bg-lineSoft text-[11px] text-inkFaint">{{ (p.displayName || '?').slice(0, 1) }}</span>
            <span class="min-w-0 flex-1 truncate text-[13px] text-inkMain">{{ p.displayName }}</span>
            <span v-if="picked === p.memberId" class="text-[11px] text-stationPink">✓</span>
          </button>
        </div>
      </div>

      <div class="px-4 pb-4 pt-3">
        <textarea
          v-model="note"
          rows="2"
          maxlength="500"
          class="w-full resize-none rounded-xl border border-lineSoft bg-[#f6f7f8] px-3.5 py-2 text-[13px] text-inkMain outline-none transition placeholder:text-inkFaint focus:border-stationPink focus:bg-white focus:ring-2 focus:ring-stationPink/20"
          placeholder="附一句安利语（可不写）"
        ></textarea>
        <p v-if="error" class="mt-2 text-[12px] text-stationPink">{{ error }}</p>
        <div class="mt-3 flex justify-end gap-2">
          <button class="rounded-full border border-lineSoft px-4 py-1.5 text-[13px] text-inkSub transition hover:border-inkSub" @click="emit('close')">算了</button>
          <button
            class="rounded-full bg-gradient-to-r from-stationPink to-[#f994b6] px-5 py-1.5 text-[13px] font-medium text-white shadow-md shadow-stationPink/30 transition hover:-translate-y-px hover:shadow-lg hover:shadow-stationPink/40 active:translate-y-0 disabled:cursor-not-allowed disabled:opacity-50 disabled:shadow-none"
            :disabled="!picked || busy"
            @click="submit"
          >
            {{ busy ? '投递中…' : '递给 TA' }}
          </button>
        </div>
      </div>
    </div>
  </div>
</template>
