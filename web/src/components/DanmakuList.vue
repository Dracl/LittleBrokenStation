<script setup>
import { ref, watch } from 'vue'

// 弹幕列表：直接解析馆藏 .ass 归档，折叠卡形态仿 B 站。只读展示，数据是归档那一刻的真相。
const props = defineProps({
  src: { type: String, default: null },
})

const open = ref(false)
const loading = ref(false)
const lines = ref(null) // null=未加载，[]=加载了但空
const MAX_LINES = 80

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
</script>

<template>
  <div v-if="src" class="overflow-hidden rounded-xl bg-cardBg">
    <button class="flex w-full items-center gap-2 px-4 py-3 text-sm font-medium text-inkMain transition hover:bg-white/5" @click="toggle">
      弹幕列表
      <svg viewBox="0 0 16 16" class="h-3.5 w-3.5 text-inkFaint" fill="currentColor">
        <circle cx="3.5" cy="8" r="1.2" /><circle cx="8" cy="8" r="1.2" /><circle cx="12.5" cy="8" r="1.2" />
      </svg>
      <svg
        viewBox="0 0 16 16"
        class="ml-auto h-3.5 w-3.5 text-inkFaint transition-transform"
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
    <div v-if="open" class="max-h-72 overflow-y-auto border-t border-black/5 px-4 py-2">
      <p v-if="loading" class="py-3 text-center text-xs text-inkFaint">展开弹幕中……</p>
      <p v-else-if="lines && lines.length === 0" class="py-3 text-center text-xs text-inkFaint">这份弹幕归档是空的</p>
      <ul v-else class="space-y-1">
        <li v-for="(l, i) in lines" :key="i" class="flex gap-2 text-xs leading-5">
          <span class="shrink-0 tabular-nums text-inkFaint/70">{{ l.time }}</span>
          <span class="min-w-0 break-all text-inkSub">{{ l.text }}</span>
        </li>
      </ul>
    </div>
  </div>
</template>
