<script setup>
import { ref } from 'vue'
import { colonTime } from '../utils/time'

// 分 P 列表：版式照抄 B 站右栏合集 pod 的实测值（面板 #f1f2f3/圆角6/内衬 0 16 8，
// 行高 31、行距 4、行内衬 10、圆角 2；标题 15/15，右侧时长 14/14 灰 #9499a0，
// 当前行白底 + 蓝字 #00aeec + 12px 跳动条）。官方那份 playing.gif 属官方素材，这里自绘等价。
defineProps({
  pages: { type: Array, required: true },
  activeIndex: { type: Number, default: 0 },
})
const emit = defineEmits(['select'])
const collapsed = ref(false)
</script>

<template>
  <div class="rounded-md bg-[#f1f2f3] px-4 pb-2 pt-[14px] dark:bg-white/[.06]">
    <div class="flex items-center justify-between">
      <div class="flex items-baseline">
        <p class="text-[16px] font-medium leading-4 text-inkMain">分 P 列表</p>
        <span class="ml-[10px] text-xs leading-3 text-inkFaint">（{{ activeIndex }}/{{ pages.length }}）</span>
      </div>
      <button
        class="flex h-4 w-4 items-center justify-center text-[#61666d] transition hover:text-inkMain dark:text-inkFaint"
        :title="collapsed ? '展开分 P 列表' : '收起分 P 列表'"
        :aria-expanded="!collapsed"
        @click="collapsed = !collapsed"
      >
        <svg viewBox="0 0 16 16" class="h-4 w-4" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">
          <path d="M4 6.2 8 3l4 3.2" />
          <path d="M4 9.8 8 13l4-3.2" />
        </svg>
      </button>
    </div>

    <!-- 行盒比面板内衬宽 10px（面板 16，行盒距边 6，行内衬 10），文字基线正好对齐标题 -->
    <ul v-show="!collapsed" class="-mx-[10px] mt-[10px] max-h-[250px] overflow-y-auto">
      <li v-for="page in pages" :key="page.index">
        <button
          class="mb-1 flex h-[31px] w-full items-center rounded-[2px] px-[10px] text-left transition last:mb-0"
          :class="page.index === activeIndex
            ? 'bg-white dark:bg-white/10'
            : 'hover:bg-white/70 dark:hover:bg-white/[.05]'"
          @click="emit('select', page.index)"
        >
          <span v-if="page.index === activeIndex" class="eq mr-[5px]" aria-hidden="true"><i /><i /><i /></span>
          <span
            class="w-5 shrink-0 text-right text-[14px] leading-[14px] tabular-nums"
            :class="page.index === activeIndex ? 'text-[#00aeec]' : 'text-inkFaint'"
          >{{ page.index }}</span>
          <span
            class="ml-2.5 min-w-0 flex-1 truncate text-[15px] leading-[15px]"
            :class="page.index === activeIndex ? 'text-[#00aeec]' : 'text-inkMain'"
            :title="page.title"
          >{{ page.title }}</span>
          <span v-if="!page.videoUrl" class="ml-[10px] shrink-0 text-[12px] text-inkFaint/70">缺片</span>
          <span v-if="page.durationText" class="ml-[10px] shrink-0 text-[14px] leading-[14px] tabular-nums text-inkFaint">{{ colonTime(page.durationText) }}</span>
        </button>
      </li>
    </ul>
  </div>
</template>

<style scoped>
/* 自绘跳动条：三根 2px 蓝条错峰起落，替官方 playing.gif（官方素材不进库） */
.eq {
  display: inline-flex;
  align-items: flex-end;
  gap: 2px;
  width: 12px;
  height: 12px;
  flex-shrink: 0;
}
.eq i {
  width: 2px;
  background: #00aeec;
  transform-origin: bottom;
  animation: lbs-eq 0.9s ease-in-out infinite;
}
.eq i:nth-child(1) { height: 60%; animation-delay: -0.3s; }
.eq i:nth-child(2) { height: 100%; animation-delay: -0.15s; }
.eq i:nth-child(3) { height: 70%; }
@keyframes lbs-eq {
  0%, 100% { transform: scaleY(0.35); }
  50% { transform: scaleY(1); }
}
</style>

