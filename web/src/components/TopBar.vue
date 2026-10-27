<script setup>
import { onMounted, onUnmounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import LoginDialog from './LoginDialog.vue'
import CrownBadge from './CrownBadge.vue'
import { submitDialogOpen } from '../submitStore'
import { member, unread, loginDialogOpen, loginDialogMode, load as loadMember, logout as logoutMember, refreshUnread } from '../memberStore'

const route = useRoute()
const router = useRouter()
const searchText = ref(typeof route.query.q === 'string' ? route.query.q : '')
const menuOpen = ref(false)

// 结果页改词/回首页清词时，搜索框跟着页面走
watch(() => route.query.q, (v) => { searchText.value = typeof v === 'string' ? v : '' })
// 换页时收起头像菜单，不给导航完还悬着一张卡
watch(() => route.fullPath, () => { menuOpen.value = false })

function doSearch() {
  const term = searchText.value.trim()
  if (!term) return
  router.push({ path: '/search', query: { q: term } })
}

function openLogin(mode) {
  loginDialogMode.value = mode
  loginDialogOpen.value = true
}

// 投稿是成员待遇：访客先过扫码门，member 照常开投稿窗
function onSubmitClick() {
  if (member.value) submitDialogOpen.value = true
  else openLogin('admit')
}

// 头像就是总门：访客点开扫码窗，成员点开 B 站式下拉
function onAvatarClick() {
  if (!member.value) {
    openLogin('admit')
    return
  }

  menuOpen.value = !menuOpen.value
}

function go(path) {
  menuOpen.value = false
  router.push(path)
}

async function doLogout() {
  menuOpen.value = false
  await logoutMember()
}

function onVisible() {
  if (document.visibilityState === 'visible') refreshUnread()
}

let unreadTimer = null

onMounted(() => {
  loadMember()
  // 未读角标三把锁：登录时 load 顺手拉一次，回到前台立刻补拉，30s 心跳兜底。
  unreadTimer = setInterval(() => { refreshUnread() }, 30000)
  document.addEventListener('visibilitychange', onVisible)
})

onUnmounted(() => {
  if (unreadTimer) clearInterval(unreadTimer)
  document.removeEventListener('visibilitychange', onVisible)
})

// 铃铛只对成员亮：访客连信箱的门都看不见，扫码按钮已经在旁边了
function goMessages() {
  router.push('/messages')
}
// 顶栏壳：版式按 B 站网页版对齐的大致轮廓，M2 对基准截图逐像素打磨
</script>

<template>
  <header class="h-10 bg-cardBg shadow-[0_1px_0_0_rgba(0,0,0,0.05)]">
    <div class="mx-auto flex h-full max-w-[1400px] items-center gap-5 px-6">
      <RouterLink to="/" class="flex shrink-0 items-center gap-2" aria-label="分站首页">
        <svg viewBox="0 0 64 64" class="h-7 w-7">
          <rect x="4" y="4" width="56" height="56" rx="14" fill="#fb7299" />
          <path d="M20 22 L28 14 M44 22 L36 14" stroke="#ffffff" stroke-width="4" stroke-linecap="round" />
          <rect x="14" y="22" width="36" height="26" rx="6" fill="#ffffff" />
          <rect x="19" y="27" width="26" height="16" rx="3" fill="#fb7299" />
        </svg>
        <span class="text-[15px] font-medium text-inkMain">小破分站</span>
      </RouterLink>

      <nav class="flex items-center gap-4 text-[13px] text-inkMain">
        <RouterLink to="/dynamics" class="transition hover:text-stationPink" :class="route.path.startsWith('/dynamics') ? 'text-stationPink' : ''">动态</RouterLink>
        <span class="cursor-not-allowed text-inkFaint" title="M2 到站">番剧</span>
        <span class="cursor-not-allowed text-inkFaint" title="M2 到站">直播</span>
        <span class="cursor-not-allowed text-inkFaint" title="M2 到站">合集</span>
      </nav>

      <div class="mx-auto flex h-8 w-full max-w-[420px] items-center gap-2 rounded-full bg-[#f1f2f3] px-3.5 transition focus-within:ring-2 focus-within:ring-stationPink/40">
        <button class="shrink-0 text-inkFaint transition hover:text-stationPink" title="搜索" @click="doSearch">
          <svg viewBox="0 0 24 24" class="h-4 w-4" fill="none" stroke="currentColor" stroke-width="2">
            <circle cx="11" cy="11" r="7" />
            <path d="m20 20-3.5-3.5" stroke-linecap="round" />
          </svg>
        </button>
        <input
          v-model="searchText"
          class="w-full bg-transparent text-[13px] outline-none placeholder:text-inkFaint"
          placeholder="搜索小破分站视频"
          @keyup.enter="doSearch"
        />
      </div>

      <div class="flex shrink-0 items-center gap-3">
        <button
          class="flex h-7 items-center rounded-full border border-stationPink px-3.5 text-xs text-stationPink transition hover:bg-stationPink hover:text-white"
          @click="onSubmitClick"
        >
          投稿
        </button>

        <!-- 站内信箱铃铛：成员可见，未读>0 亮红点角标，爆 99 封显 99+ -->
        <button
          v-if="member"
          class="relative flex h-8 w-8 items-center justify-center rounded-full text-inkSub transition hover:bg-stationPink/10 hover:text-stationPink"
          title="站内信箱"
          aria-label="站内信箱"
          @click="goMessages"
        >
          <svg viewBox="0 0 24 24" class="h-[18px] w-[18px]" fill="none" stroke="currentColor" stroke-width="1.8">
            <path d="M6 9.5a6 6 0 0 1 12 0c0 3.2.8 5.2 1.8 6.4.4.5.1 1.3-.6 1.3H4.8c-.7 0-1-.8-.6-1.3C5.2 14.7 6 12.7 6 9.5Z" stroke-linejoin="round" />
            <path d="M10 20a2.2 2.2 0 0 0 4 0" stroke-linecap="round" />
          </svg>
          <span
            v-if="unread > 0"
            class="absolute -right-0.5 -top-0.5 flex h-4 min-w-4 items-center justify-center rounded-full bg-stationPink px-1 text-[10px] font-medium leading-none text-white"
          >{{ unread > 99 ? '99+' : unread }}</span>
        </button>

        <!-- 总门小头像：不分访客/成员都是这个圆——访客点开扫码窗，成员点开下拉菜单 -->
        <div class="relative">
          <button
            class="block h-8 w-8 shrink-0 overflow-visible rounded-full transition"
            :title="member ? (member.role === 'owner' ? `站长「${member.nickname}」` : `「${member.nickname}」`) : '扫码进站'"
            @click="onAvatarClick"
          >
            <span class="relative block h-full w-full">
              <img
                v-if="member?.faceUrl"
                :src="member.faceUrl"
                :alt="member.nickname"
                referrerpolicy="no-referrer"
                class="h-full w-full rounded-full object-cover"
                :class="menuOpen ? 'ring-2 ring-stationPink/60' : 'ring-1 ring-transparent hover:ring-stationPink/40'"
              />
              <span
                v-else
                class="flex h-full w-full items-center justify-center rounded-full bg-lineSoft text-inkFaint transition hover:bg-stationPink/20"
              >
                <svg viewBox="0 0 24 24" class="h-4.5 w-4.5" fill="none" stroke="currentColor" stroke-width="1.8">
                  <circle cx="12" cy="8.5" r="3.5" />
                  <path d="M5 19.5c1.4-3.2 4-4.5 7-4.5s5.6 1.3 7 4.5" stroke-linecap="round" />
                </svg>
              </span>
              <!-- 冠冕角标：站长金冠、成员银冠，同形不同色 -->
              <CrownBadge
                v-if="member"
                :tier="member.role"
                class="absolute -right-1 -top-1 h-3.5 w-3.5"
              />
            </span>
          </button>

          <!-- B 站式个人卡：名片头 + 菜单行，退出登录垫底整条灰底 -->
          <template v-if="member && menuOpen">
            <div class="fixed inset-0 z-30" @click="menuOpen = false"></div>
            <div class="absolute right-0 top-full z-40 mt-1.5 w-52 overflow-hidden rounded-xl border border-lineSoft bg-cardBg shadow-[0_12px_32px_-8px_rgba(0,0,0,0.25)]">
              <div class="flex items-center gap-2.5 px-3.5 pb-2 pt-3">
                <span class="relative block h-10 w-10 shrink-0">
                  <img
                    v-if="member.faceUrl"
                    :src="member.faceUrl"
                    :alt="member.nickname"
                    referrerpolicy="no-referrer"
                    class="h-full w-full rounded-full object-cover"
                  />
                  <span v-else class="block h-full w-full rounded-full bg-lineSoft"></span>
                  <CrownBadge
                    v-if="member"
                    :tier="member.role"
                    class="absolute -right-1 -top-1 h-4 w-4"
                  />
                </span>
                <span class="min-w-0">
                  <span class="block truncate text-[13px] font-medium text-inkMain">{{ member.nickname }}</span>
                  <span class="block text-[11px] text-inkFaint">{{ member.role === 'owner' ? '站长' : '分站成员' }} · UID {{ member.mid }}</span>
                </span>
              </div>

              <div class="border-t border-lineSoft/70 p-1.5">
                <button
                  class="flex w-full items-center gap-2.5 rounded-lg px-2.5 py-2 text-left text-xs text-inkSub transition hover:bg-stationPink/10 hover:text-stationPink"
                  @click="go('/me')"
                >
                  <svg viewBox="0 0 24 24" class="h-4 w-4 shrink-0" fill="none" stroke="currentColor" stroke-width="1.8">
                    <circle cx="12" cy="8.5" r="3.5" />
                    <path d="M5 19.5c1.4-3.2 4-4.5 7-4.5s5.6 1.3 7 4.5" stroke-linecap="round" />
                  </svg>
                  用户控制室
                </button>
                <button
                  v-if="member.role === 'owner'"
                  class="flex w-full items-center gap-2.5 rounded-lg px-2.5 py-2 text-left text-xs text-inkSub transition hover:bg-stationPink/10 hover:text-stationPink"
                  @click="go('/admin')"
                >
                  <svg viewBox="0 0 24 24" class="h-4 w-4 shrink-0" fill="none" stroke="currentColor" stroke-width="1.8">
                    <path d="M3 17.5h18L22.5 8l-5.7 4L12 3.5 7.2 12 1.5 8 3 17.5Z" stroke-linejoin="round" />
                  </svg>
                  站长室
                </button>
              </div>

              <div class="border-t border-lineSoft/70 p-1.5">
                <button
                  class="w-full rounded-lg bg-[#f1f2f3] px-2.5 py-2 text-center text-xs text-inkSub transition hover:bg-lineSoft hover:text-inkMain"
                  @click="doLogout"
                >
                  退出登录
                </button>
              </div>
            </div>
          </template>
        </div>
      </div>
    </div>
  </header>
  <!-- admit 实例：全站顶栏页共用的扫码进站窗（bind 实例由用户控制室自己挂） -->
  <LoginDialog />
</template>
