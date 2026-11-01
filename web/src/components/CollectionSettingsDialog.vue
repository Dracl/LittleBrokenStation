<script setup>
import { computed, onUnmounted, ref, watch } from 'vue'

// ─── 抽屉设置：改名 / 改简介 / 改公开还是私有。
// 骨架照抄 MemberPage 的「账号与登录设置」弹层（fixed 遮罩 + role=dialog + 点遮罩关 +
// 内容限高内滚 + sticky 页眉），站内那个先例没搬 Teleport，这里也不搬。
// 落库走 PATCH /api/collections/{id}：只把真改过的字段递上去；后端回更新后的 brief，
// 这里不自己改本地对象——emit('saved') 让父页重拉，省得两处状态各说各话。
const props = defineProps({
  // 要改的那座抽屉（合集 brief：id / name / intro / visibility …）。
  // 显隐由父页的 v-if 管：组件只在开着的时候存在，所以底稿在挂载时按它铺一次。
  collection: { type: Object, required: true },
})
const emit = defineEmits(['saved', 'close'])

const VISIBILITIES = [
  { key: 'public', label: '公开' },
  { key: 'private', label: '私有' },
]

const name = ref('')
const intro = ref('')
const visibility = ref('public')
const busy = ref(false)
const notice = ref(null) // { kind, text }

// 缺字段一律当公开（与后端契约同口径）
function normalize(v) {
  return v === 'private' ? 'private' : 'public'
}

function fillFrom(c) {
  name.value = c?.name ?? ''
  intro.value = c?.intro ?? ''
  visibility.value = normalize(c?.visibility)
  notice.value = null
}

fillFrom(props.collection)

// 父页复用同一实例换抽屉时（一般不会有，防一手），底稿跟着换
watch(() => props.collection, fillFrom)

function onKeydown(event) {
  if (event.key === 'Escape') close()
}
window.addEventListener('keydown', onKeydown)
onUnmounted(() => window.removeEventListener('keydown', onKeydown))

function close() {
  emit('close')
}

const trimmedName = computed(() => name.value.trim())
const trimmedIntro = computed(() => intro.value.trim())

// 只递改动的字段：没改的不进 body，免得把后端没打算动的列覆盖回去
function payload() {
  const c = props.collection ?? {}
  const body = {}
  if (trimmedName.value !== (c.name ?? '')) body.name = trimmedName.value
  if (trimmedIntro.value !== (c.intro ?? '')) body.intro = trimmedIntro.value || null
  if (visibility.value !== normalize(c.visibility)) body.visibility = visibility.value
  return body
}

function settle({ ok, status, data }) {
  if (ok) return { kind: 'ok', text: data?.message ?? '抽屉改好了' }
  if (status === 400) return { kind: 'err', text: data?.message ?? '集名不合规矩，换一个再存' }
  if (status === 401) return { kind: 'err', text: '先登录，再回来改抽屉' }
  if (status === 403) return { kind: 'err', text: '这座抽屉不归你改' }
  if (status === 404) return { kind: 'err', text: '这座抽屉好像已经拆了' }
  return { kind: 'err', text: data?.message ?? '分站收不下这次改动，稍后再试' }
}

async function save() {
  if (busy.value || !trimmedName.value) return
  const body = payload()
  if (!Object.keys(body).length) {
    close()
    return
  }
  busy.value = true
  notice.value = null
  try {
    const res = await fetch(`/api/collections/${props.collection.id}`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    })
    const data = await res.json().catch(() => null)
    if (res.ok) {
      emit('saved', data ?? null)
      return
    }
    notice.value = settle({ ok: false, status: res.status, data })
  } catch {
    notice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div
    class="fixed inset-0 z-50 flex items-center justify-center bg-black/45 p-4"
    role="dialog"
    aria-modal="true"
    aria-label="抽屉设置"
    @click.self="close"
  >
    <div class="no-scrollbar max-h-[85vh] w-full max-w-[520px] overflow-y-auto rounded-2xl border border-lineSoft bg-cardBg shadow-2xl">
      <div class="sticky top-0 z-10 flex items-center justify-between border-b border-lineSoft/70 bg-cardBg px-6 py-4">
        <h3 class="text-sm font-semibold text-inkMain">抽屉设置</h3>
        <button
          class="grid h-7 w-7 place-items-center rounded-full border border-lineSoft text-inkSub transition hover:border-stationPink/60 hover:text-stationPink"
          aria-label="收起设置"
          @click="close"
        >
          ✕
        </button>
      </div>

      <div class="px-6 pb-6">
        <section class="mt-4 rounded-2xl border border-lineSoft bg-cardBg p-6 shadow-[0_1px_2px_rgba(0,0,0,0.03)]">
          <label class="block">
            <span class="text-[11px] text-inkFaint">集名（30 字内）</span>
            <input
              v-model="name"
              type="text"
              maxlength="30"
              placeholder="这座抽屉叫什么"
              class="mt-1 w-full rounded-xl border border-lineSoft bg-[#f6f7f8] px-4 py-2.5 text-sm text-inkMain placeholder:text-inkFaint transition focus:border-stationPink focus:outline-none focus:ring-2 focus:ring-stationPink/20 dark:bg-[#23262e]"
              :disabled="busy"
            />
          </label>

          <label class="mt-4 block">
            <span class="text-[11px] text-inkFaint">简介（可留空）</span>
            <textarea
              v-model="intro"
              rows="3"
              maxlength="60"
              placeholder="一句话说清这抽屉装的是什么"
              class="mt-1 w-full resize-none rounded-xl border border-lineSoft bg-[#f6f7f8] px-4 py-2.5 text-sm leading-6 text-inkMain placeholder:text-inkFaint transition focus:border-stationPink focus:outline-none focus:ring-2 focus:ring-stationPink/20 dark:bg-[#23262e]"
              :disabled="busy"
            ></textarea>
          </label>

          <!-- 可见性：分段钮形状照 WallWindow 的喂料三档，白卡上直接摆，日夜同一套颜色 -->
          <div class="mt-4 flex flex-wrap items-center gap-2">
            <span class="text-[11.5px] text-inkFaint">谁能看见</span>
            <div class="flex items-center rounded-full border border-lineSoft p-0.5" role="group" aria-label="这座抽屉谁能看见">
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
            <span v-if="visibility === 'private'" class="text-[11.5px] text-inkFaint">只有你自己看得见</span>
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

          <div class="mt-5 flex items-center justify-end gap-2 border-t border-lineSoft/70 pt-4">
            <button class="rounded-lg px-4 py-2 text-sm text-inkSub transition hover:bg-black/5 hover:text-inkMain" :disabled="busy" @click="close">
              算了
            </button>
            <button
              class="rounded-lg bg-stationPink px-5 py-2 text-sm font-medium text-white transition hover:opacity-90 disabled:opacity-50"
              :disabled="busy || !trimmedName"
              @click="save"
            >
              {{ busy ? '存好中…' : '存好' }}
            </button>
          </div>
        </section>
      </div>
    </div>
  </div>
</template>
