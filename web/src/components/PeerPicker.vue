<script setup>
// 私信 / 安利共用的选人控件：默认只列「最近聊过」的人（全站花名册不再整本露面），
// 输入关键词才搜人。门槛与后端 DirectorySearchAllowed 对齐——纯数字（UID/mid）1 位即搜，
// 文字关键词至少 2 字，防单字广撒网。mode='open' 点人即 emit pick 开聊；
// mode='select' 单选（配 v-model 传 memberId），递给 TA 由父层驱动。
import { computed, onMounted, ref, watch } from 'vue'
import { defaultAvatar, onAvatarError } from '../utils/avatar.js'

const props = defineProps({
  mode: { type: String, default: 'open' }, // 'open' | 'select'
  modelValue: { type: Number, default: null },
})
const emit = defineEmits(['pick', 'update:modelValue'])

const recent = ref([])
const recentLoaded = ref(false)
const keyword = ref('')
const results = ref(null) // null=搜索框空（走 recent 态）；[]=搜了没人
const searching = ref(false)
const error = ref('')

// 前端门槛：与后端同规。空→不搜；纯数字≥1；文字≥2。
function allowed(q) {
  const t = q.trim()
  if (!t) return false
  return /^\d+$/.test(t) ? t.length >= 1 : t.length >= 2
}

async function loadRecent() {
  try {
    const res = await fetch('/api/dm/directory')
    if (res.status === 401) {
      error.value = '登录后才能找人'
      recent.value = []
    } else {
      const data = await res.json()
      recent.value = data.recent ?? []
    }
  } catch {
    error.value = '点名失败，检查分站后端'
    recent.value = []
  } finally {
    recentLoaded.value = true
  }
}

let timer = null
watch(keyword, (val) => {
  if (timer) clearTimeout(timer)
  const q = val.trim()
  if (!allowed(q)) {
    results.value = null
    searching.value = false
    return
  }
  searching.value = true
  timer = setTimeout(async () => {
    try {
      const res = await fetch(`/api/dm/directory?q=${encodeURIComponent(q)}`)
      if (res.status === 401) {
        error.value = '登录后才能找人'
        results.value = []
      } else {
        error.value = ''
        const data = await res.json()
        results.value = data.people ?? []
      }
    } catch {
      error.value = '分站后端没有应答'
      results.value = []
    } finally {
      searching.value = false
    }
  }, 300)
})

onMounted(loadRecent)

const list = computed(() => (results.value === null ? recent.value : results.value))
const picked = computed(() => props.modelValue)

function label(p) {
  return p.displayName || p.nickname || p.username || `席位 #${p.memberId}`
}
function sub(p) {
  // 副行只补主行没有的信息：昵称与用户名同名字时不重复报，直接落 UID。
  const main = label(p)
  const bits = []
  const extra = p.username || p.nickname
  if (extra && extra !== main) bits.push(extra)
  bits.push(`UID #${p.memberId}`)
  return bits.join(' · ')
}
function choose(p) {
  if (props.mode === 'select') emit('update:modelValue', p.memberId)
  emit('pick', p)
}
// 单边销账：只销自己这边的会话记录，对方不受影响；重新发信自动复活。
async function forget(p) {
  try {
    const res = await fetch(`/api/dm/thread/${p.memberId}`, { method: 'DELETE' })
    if (res.ok) recent.value = recent.value.filter(x => x.memberId !== p.memberId)
  } catch {
    /* 销不掉也不堵路，列表将就用 */
  }
}
function isPicked(p) {
  return props.mode === 'select' && picked.value === p.memberId
}
</script>

<template>
  <div class="flex items-center gap-2 rounded-xl border border-lineSoft bg-[#f6f7f8] px-3 py-2 focus-within:border-stationPink">
    <svg viewBox="0 0 24 24" class="h-3.5 w-3.5 shrink-0 text-inkFaint" fill="none" stroke="currentColor" stroke-width="1.8">
      <circle cx="11" cy="11" r="6.5" />
      <path d="m16 16 4.5 4.5" stroke-linecap="round" />
    </svg>
    <input
      v-model="keyword"
      class="min-w-0 flex-1 bg-transparent text-[13px] text-inkMain outline-none placeholder:text-inkFaint"
      placeholder="输入昵称、用户名或 UID 找人"
      autocomplete="off"
    />
  </div>

  <p v-if="error" class="mt-2 text-[12px] text-stationPink">{{ error }}</p>

  <p v-else-if="results === null && !recentLoaded" class="py-5 text-center text-[12px] text-inkFaint">在点名…</p>

  <p v-if="list.length" class="mb-1 mt-3 text-[12px] text-inkFaint">
    {{ results === null ? '最近聊过' : `找到 ${results.length} 人` }}
  </p>

  <div
    v-if="list.length"
    class="max-h-[168px] space-y-1 overflow-y-auto pr-0.5"
  >
    <button
      v-for="p in list"
      :key="p.memberId"
      class="flex w-full items-center gap-2.5 rounded-xl px-2.5 py-2 text-left transition"
      :class="isPicked(p) ? 'bg-stationPink/10 ring-1 ring-stationPink/40' : 'hover:bg-stationPink/5'"
      @click="choose(p)"
    >
      <img :src="p.faceUrl || defaultAvatar" referrerpolicy="no-referrer" class="h-7 w-7 rounded-full object-cover" alt="" @error="onAvatarError" />
      <span class="min-w-0 flex-1">
        <span class="block truncate text-[13px] text-inkMain">{{ label(p) }}</span>
        <span class="block truncate text-[11px] text-inkFaint">{{ sub(p) }}</span>
      </span>
      <span v-if="mode === 'open'" class="shrink-0 rounded-full bg-stationPink/10 px-2.5 py-1 text-[11px] text-stationPink">开聊</span>
      <span
        v-else-if="isPicked(p)"
        class="flex h-[18px] w-[18px] shrink-0 items-center justify-center rounded-full bg-stationPink text-[10px] font-bold leading-none text-white shadow-sm shadow-stationPink/40"
        aria-hidden="true"
      >✓</span>
      <button
        v-if="results === null"
        class="flex h-6 w-6 shrink-0 items-center justify-center rounded-full text-[11px] text-inkFaint transition hover:bg-black/5 hover:text-inkMain"
        title="删掉这段记录（只销你这边的）"
        @click.stop="forget(p)"
      >✕</button>
    </button>
  </div>

  <!-- 空态：搜索框有字但没命中，或压根没聊过人 -->
  <div
    v-else-if="!searching"
    class="rounded-xl border border-dashed border-lineSoft px-3 py-5 text-center text-[12px] text-inkFaint"
  >
    <template v-if="results !== null">没匹配到人，换个词或直接试 UID</template>
    <template v-else>还没有聊过天的人，输入昵称 / 用户名 / UID 找一位</template>
  </div>
  <div v-else class="py-5 text-center text-[12px] text-inkFaint">搜索中…</div>
</template>
