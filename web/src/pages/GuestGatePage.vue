<script setup>
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import TopBar from '../components/TopBar.vue'
import EmptyTv from '../components/EmptyTv.vue'
import { openLoginWindow } from '../utils/guestGate'

// ─── 访客门禁的门面：router 的 beforeEach 把没进站的客人送到这儿（/gated，name: guest-gate）。
// 形象用站内现成那台小电视（放映厅的访客门与查无此片共用它），不自立一套装裱，也不自己写登录表单 ——
// 「登录」那颗开的就是 memberStore 那对 ref 唤起的现成 LoginDialog（App.vue 统一挂着的那扇）。
const route = useRoute()
// 被挡的那串路径原样摆出来（?from=），让人知道是哪一页没让过；直接敲 /gated 进来就没这一串
const from = computed(() => (typeof route.query.from === 'string' ? route.query.from : ''))
</script>

<template>
  <div class="min-h-screen">
    <TopBar />

    <main class="mx-auto max-w-[1400px] px-6 py-5">
      <EmptyTv
        title="这一页要进站才能看"
        subtitle="动态、分区与合集这些是分站成员的待遇 —— 花两秒领个席位，或直接把门钥匙输进来"
      >
        <p v-if="from" class="w-full text-center text-xs text-inkFaint">
          被挡在这里：<span class="font-mono text-inkSub">{{ from }}</span>
        </p>
        <button
          class="rounded-lg bg-gradient-to-r from-stationPink to-[#f994b6] px-6 py-2 text-sm font-medium text-white shadow-md shadow-stationPink/30 transition hover:-translate-y-px hover:shadow-lg hover:shadow-stationPink/40 active:translate-y-0"
          @click="openLoginWindow"
        >
          登录
        </button>
        <RouterLink
          to="/"
          class="rounded-lg border border-lineSoft px-6 py-2 text-sm text-inkSub transition hover:border-stationPink hover:text-stationPink"
        >
          回首页
        </RouterLink>
      </EmptyTv>
    </main>
  </div>
</template>
