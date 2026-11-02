<script setup>
// 安利给分站朋友（走私信）：一个弹窗两副形态，target 驱动——
//   动态橱窗：{ type:'dynamic', dynamicId, authorName, authorFaceUrl, text }
//   站内放映厅：{ type:'video', bvid, title, cover, upName }
// 安利语随信落库，定格卡面由后端定格（dyn_json 同族），对方点卡回橱窗/放映厅。SubmitDialog 同款语言。
import { computed, ref } from 'vue'
import PeerPicker from './PeerPicker.vue'
import { defaultAvatar, onAvatarError } from '../utils/avatar.js'
import { coverSrcOf } from '../utils/coverSrc'

const props = defineProps({
  target: { type: Object, required: true },
})
const emit = defineEmits(['close', 'done'])

const isVideo = computed(() => props.target.type === 'video')

const picked = ref(null) // memberId
const note = ref('')
const busy = ref(false)
const error = ref('')

const summary = computed(() => {
  if (isVideo.value) return props.target.title || props.target.bvid || '（一个片子）'
  const raw = (props.target.text || '').split('\n\n转发自')[0]
  return raw.length > 60 ? `${raw.slice(0, 60)}…` : raw || '（一条动态）'
})

async function submit() {
  if (!picked.value || busy.value) return
  busy.value = true
  error.value = ''
  const path = isVideo.value
    ? `/api/videos/${encodeURIComponent(props.target.bvid)}/share`
    : `/api/dynamics/${encodeURIComponent(props.target.dynamicId)}/share`
  try {
    const res = await fetch(path, {
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
          {{ isVideo ? '安利这个片子' : '安利这条动态' }}
        </span>
        <!-- after 只撑热区（28→44）：盒子和 hover 圆底不变，外拓落在 header px-4/py-3 留白里 -->
        <button class="relative flex h-7 w-7 items-center justify-center rounded-full text-inkFaint transition hover:bg-black/5 hover:text-inkMain after:absolute after:-inset-[9px] after:content-['']" @click="emit('close')">✕</button>
      </div>

      <!-- 要安利的内容缩略：动态看作者+摘要，片子看馆藏封面+标题 -->
      <div v-if="isVideo" class="mx-4 mt-3 flex items-center gap-2.5 rounded-xl bg-[#f6f7f8] dark:bg-[#23262e] px-3 py-2.5">
        <img
          v-if="target.cover"
          :src="coverSrcOf({ coverUrl: target.cover }, 68)"
          referrerpolicy="no-referrer"
          class="h-10 w-[68px] shrink-0 rounded-md object-cover"
          alt=""
          @error="$event.target.style.display = 'none'"
        />
        <span v-else class="flex h-10 w-[68px] shrink-0 items-center justify-center rounded-md bg-stationPink/15 text-[11px] text-stationPink">{{ (target.bvid || '?').slice(0, 6) }}</span>
        <span class="min-w-0 flex-1">
          <span class="block truncate text-[12px] font-medium text-inkMain">{{ target.title || target.bvid }}</span>
          <span class="block truncate text-[12px] text-inkSub">{{ target.upName || '站内放映厅' }} · /watch/{{ target.bvid }}</span>
        </span>
      </div>
      <div v-else class="mx-4 mt-3 flex items-center gap-2.5 rounded-xl bg-[#f6f7f8] dark:bg-[#23262e] px-3 py-2.5">
        <img
          :src="target.authorFaceUrl || defaultAvatar"
          referrerpolicy="no-referrer"
          class="h-8 w-8 shrink-0 rounded-full object-cover"
          alt=""
          @error="onAvatarError"
        />
        <span class="min-w-0 flex-1">
          <span class="block text-[12px] font-medium text-inkMain">{{ target.authorName || 'B站作者' }}</span>
          <span class="block truncate text-[12px] text-inkSub">{{ summary }}</span>
        </span>
      </div>

      <!-- 选伙伴：常聊打底、输词才搜（与发起私信共用 PeerPicker） -->
      <div class="px-4 pt-3">
        <p class="mb-1.5 text-[12px] text-inkFaint">送给谁</p>
        <PeerPicker v-model="picked" mode="select" />
      </div>

      <div class="px-4 pb-4 pt-3">
        <textarea
          v-model="note"
          rows="2"
          maxlength="500"
          class="w-full resize-none rounded-xl border border-lineSoft bg-[#f6f7f8] dark:bg-[#23262e] dark:focus:bg-[#23262e] px-3.5 py-2 text-[13px] text-inkMain outline-none transition placeholder:text-inkFaint focus:border-stationPink focus:bg-white focus:ring-2 focus:ring-stationPink/20"
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
