<script setup>
import { nextTick, onUnmounted, ref, watch } from 'vue'
import { answerConfirm, confirmState } from '../confirmStore'

// 全站共用的确认弹窗：装裱照抄投稿窗（渐变衬底 + 圆底图标 + 底部双钮），
// 开关与文案住在 confirmStore.js，调用方 await requestConfirm({...}) 拿 true/false。
const okBtn = ref(null)

function onKeydown(event) {
  if (event.key === 'Escape') answerConfirm(false)
}

watch(
  () => confirmState.value.open,
  (open) => {
    if (open) {
      window.addEventListener('keydown', onKeydown)
      // 焦点落在主钮上，回车即确认，Esc 即取消，手不用离开键盘
      nextTick(() => okBtn.value?.focus())
    } else {
      window.removeEventListener('keydown', onKeydown)
    }
  },
)

onUnmounted(() => window.removeEventListener('keydown', onKeydown))
</script>

<template>
  <Teleport to="body">
    <div
      v-if="confirmState.open"
      class="lbs-fade fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4 backdrop-blur-sm"
      @click.self="answerConfirm(false)"
    >
      <div
        class="lbs-pop w-full max-w-[440px] overflow-hidden rounded-2xl bg-cardBg shadow-[0_24px_80px_-12px_rgba(0,0,0,0.45)]"
        role="dialog"
        aria-modal="true"
      >
        <!-- 页眉：投稿窗同款渐变衬底，破坏性的活换玫红调 -->
        <div
          class="flex items-start gap-3 px-6 pb-4 pt-5"
          :class="confirmState.danger ? 'bg-gradient-to-r from-rose-500/10 via-rose-500/5 to-transparent' : 'bg-gradient-to-r from-stationPink/10 via-stationPink/5 to-transparent'"
        >
          <span
            class="flex h-10 w-10 shrink-0 items-center justify-center rounded-full"
            :class="confirmState.danger ? 'bg-rose-500/10' : 'bg-stationPink/10'"
          >
            <svg
              v-if="confirmState.icon === 'out'"
              viewBox="0 0 24 24"
              class="h-5 w-5"
              :class="confirmState.danger ? 'text-rose-500' : 'text-stationPink'"
              fill="none"
              stroke="currentColor"
              stroke-width="1.8"
              stroke-linecap="round"
              stroke-linejoin="round"
            >
              <path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4" />
              <path d="M16 17l5-5-5-5M21 12H9" />
            </svg>
            <svg
              v-else-if="confirmState.icon === 'trash'"
              viewBox="0 0 24 24"
              class="h-5 w-5"
              :class="confirmState.danger ? 'text-rose-500' : 'text-stationPink'"
              fill="none"
              stroke="currentColor"
              stroke-width="1.8"
              stroke-linecap="round"
              stroke-linejoin="round"
            >
              <path d="M4 7h16M9 7V5a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2m3 0-1 13a1 1 0 0 1-1 1H8a1 1 0 0 1-1-1L6 7" />
            </svg>
            <svg
              v-else
              viewBox="0 0 24 24"
              class="h-5 w-5"
              :class="confirmState.danger ? 'text-rose-500' : 'text-stationPink'"
              fill="none"
              stroke="currentColor"
              stroke-width="1.8"
              stroke-linecap="round"
              stroke-linejoin="round"
            >
              <circle cx="12" cy="12" r="9" />
              <path d="M12 7.5v5.5M12 16.2h.01" />
            </svg>
          </span>
          <div class="min-w-0 pt-1">
            <h3 class="text-[17px] font-semibold leading-6 text-inkMain">{{ confirmState.title }}</h3>
            <p v-if="confirmState.sub" class="mt-0.5 text-xs text-inkFaint">{{ confirmState.sub }}</p>
          </div>
          <button
            class="-mr-1 flex h-8 w-8 shrink-0 items-center justify-center rounded-full text-inkFaint transition hover:bg-black/5 hover:text-inkMain"
            aria-label="关闭"
            @click="answerConfirm(false)"
          >
            <svg viewBox="0 0 24 24" class="h-5 w-5" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M6 6l12 12M18 6L6 18" stroke-linecap="round" />
            </svg>
          </button>
        </div>

        <div class="px-6 pb-5 pt-1">
          <p class="whitespace-pre-line text-sm leading-7 text-inkSub">{{ confirmState.message }}</p>
        </div>

        <div class="flex justify-end gap-2 border-t border-lineSoft/70 bg-[#fafbfc] px-6 py-4 dark:bg-white/5">
          <button class="rounded-lg px-4 py-2 text-sm text-inkSub transition hover:bg-black/5 hover:text-inkMain" @click="answerConfirm(false)">
            {{ confirmState.cancelText }}
          </button>
          <button
            ref="okBtn"
            class="rounded-lg px-5 py-2 text-sm font-medium text-white shadow-md outline-none transition hover:-translate-y-px hover:shadow-lg active:translate-y-0"
            :class="
              confirmState.danger
                ? 'bg-gradient-to-r from-rose-500 to-[#f87171] shadow-rose-500/30 hover:shadow-rose-500/40'
                : 'bg-gradient-to-r from-stationPink to-[#f994b6] shadow-stationPink/30 hover:shadow-stationPink/40'
            "
            @click="answerConfirm(true)"
          >
            {{ confirmState.confirmText }}
          </button>
        </div>
      </div>
    </div>
  </Teleport>
</template>

<style scoped>
/* 动效与投稿窗、扫码门同一套（那两个各自 scoped 里也各留了一份，此处照此惯例自持） */
.lbs-fade {
  animation: lbs-fade-in 0.18s ease-out both;
}
.lbs-pop {
  animation: lbs-pop-in 0.22s cubic-bezier(0.34, 1.4, 0.64, 1) both;
}
@keyframes lbs-fade-in {
  from {
    opacity: 0;
  }
  to {
    opacity: 1;
  }
}
@keyframes lbs-pop-in {
  from {
    opacity: 0;
    transform: translateY(10px) scale(0.97);
  }
  to {
    opacity: 1;
    transform: translateY(0) scale(1);
  }
}
</style>
