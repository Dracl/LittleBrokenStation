<script setup>
import { ref, watch } from 'vue'

// 弹幕列表：直接解析馆藏 .ass 归档，只读展示，数据是归档那一刻的真相。
// 折叠条版式照抄 B 站右栏实测：条高 44、圆角 6、底 #f1f2f3（夜 white/6），
// 标题 15px #18191c 系，右侧 ⋮（14px）与折叠箭头（16px）分居两端。
const props = defineProps({
  src: { type: String, default: null },
  // 全片弹幕数（可选）：只为把加载中占位撑到接近最终高，免得数据落地那一瞬高度突变。
  count: { type: Number, default: null },
})

const open = ref(false)
const loading = ref(false)
const lines = ref(null) // null=未加载，[]=加载了但空
const MAX_LINES = 80
// 行高 30 + 上下衬 16：占位按这个公式算，和真行完全同构，交换时零跳变。
const placeholderHeight = () => Math.min(props.count ?? MAX_LINES, MAX_LINES) * 30 + 16

async function load() {
  if (!props.src || lines.value !== null) return
  loading.value = true
  try {
    const res = await fetch(props.src)
    if (!res.ok) {
      lines.value = []
      return
    }
    const text = await res.text()
    const out = []
    for (const raw of text.split(/\r?\n/)) {
      if (!raw.startsWith('Dialogue: ')) continue
      const parts = raw.slice(10).split(',')
      if (parts.length < 10) continue
      const m = parts[1].match(/(\d+):(\d{1,2}):(\d{1,2})/)
      const time = m ? `${Number(m[1]) * 60 + Number(m[2])}:${m[3]}` : ''
      const body = parts
        .slice(9)
        .join(',')
        .replace(/\{[^}]*\}/g, '')
        .replace(/\\N/g, ' ')
        .trim()
      if (body) out.push({ time, text: body })
      if (out.length >= MAX_LINES) break
    }
    lines.value = out
  } catch {
    lines.value = []
  } finally {
    loading.value = false
  }
}

function toggle() {
  open.value = !open.value
  if (open.value) load()
}

watch(() => props.src, () => (lines.value = null))

// 折叠动画走 B 站同款「容器 height 过渡」：动的是外层高度，列表保持固有高度只被裁剪，
// 浏览器每帧只重算裁剪矩形、不重排 80 行——这比 grid 0fr→1fr 逐帧重算 flex 便宜得多，
// 也就没有展开时的粘连掉帧。300ms ease 与官方折叠一致。
const EASE = 'cubic-bezier(0.25, 0.1, 0.25, 1)'
function enter(el) {
  el.style.overflow = 'hidden'
  el.style.height = '0px'
  el.style.transition = ''
  void el.offsetHeight // 强制回流，让起点高度落地
  el.style.transition = `height 300ms ${EASE}`
  el.style.height = `${el.scrollHeight}px`
}
function afterEnter(el) {
  el.style.height = ''
  el.style.overflow = ''
  el.style.transition = ''
}
function enterCancelled(el) {
  el.style.height = ''
  el.style.overflow = ''
  el.style.transition = ''
}
function leave(el) {
  el.style.overflow = 'hidden'
  el.style.transition = ''
  el.style.height = `${el.scrollHeight}px`
  void el.offsetHeight
  el.style.transition = `height 300ms ${EASE}`
  el.style.height = '0px'
}
function afterLeave(el) {
  el.style.height = ''
  el.style.overflow = ''
  el.style.transition = ''
}
function leaveCancelled(el) {
  el.style.height = ''
  el.style.overflow = ''
  el.style.transition = ''
}
</script>

<template>
  <div v-if="src" class="overflow-hidden rounded-md bg-[#f1f2f3] dark:bg-white/[.06]">
    <button
      class="flex h-11 w-full items-center px-5 text-left transition hover:bg-black/[.03] dark:hover:bg-white/5"
      :aria-expanded="open"
      @click="toggle"
    >
      <span class="text-[15px] leading-[17px] text-inkMain">弹幕列表</span>
      <svg viewBox="0 0 14 14" class="ml-1.5 h-3.5 w-3.5 text-inkMain" fill="currentColor">
        <circle cx="7" cy="2.6" r="1.25" /><circle cx="7" cy="7" r="1.25" /><circle cx="7" cy="11.4" r="1.25" />
      </svg>
      <svg
        viewBox="0 0 16 16"
        class="ml-auto h-4 w-4 text-inkMain transition-transform duration-300"
        :class="open ? 'rotate-180' : ''"
        fill="none"
        stroke="currentColor"
        stroke-width="1.6"
        stroke-linecap="round"
        stroke-linejoin="round"
      >
        <path d="m3.5 6 4.5 4.5L12.5 6" />
      </svg>
    </button>
    <Transition @enter="enter" @after-enter="afterEnter" @enter-cancelled="enterCancelled" @leave="leave" @after-leave="afterLeave" @leave-cancelled="leaveCancelled">
      <div v-if="open" class="bg-cardBg">
        <!-- 加载中用 count 估高占位，数据落地换成真行时高度几乎不变，折叠体不因异步而二次抽动 -->
        <div
          class="max-h-72 overflow-y-auto px-5 py-2"
          :style="loading ? { height: placeholderHeight() + 'px' } : null"
        >
          <p
            v-if="loading"
            class="text-center text-xs text-inkFaint"
            :class="lines === null ? 'flex h-full items-center justify-center' : 'py-3'"
          >展开弹幕中……</p>
          <p v-else-if="lines && lines.length === 0" class="py-3 text-center text-xs text-inkFaint">这份弹幕归档是空的</p>
          <ul v-else>
            <li v-for="(l, i) in lines" :key="i" class="flex h-[30px] items-center gap-2.5 text-[13px]">
              <span class="shrink-0 text-xs leading-3 tabular-nums text-inkFaint">{{ l.time }}</span>
              <span class="min-w-0 flex-1 truncate text-inkSub" :title="l.text">{{ l.text }}</span>
            </li>
          </ul>
        </div>
      </div>
    </Transition>
  </div>
</template>
