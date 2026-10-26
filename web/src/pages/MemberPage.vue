<script setup>
import { onMounted, ref } from 'vue'
import TopBar from '../components/TopBar.vue'
import LoginDialog from '../components/LoginDialog.vue'
import { member, loaded, loginDialogOpen, loginDialogMode, load as loadMember, logout as logoutMember } from '../memberStore'
import { friendlyTime } from '../utils/time'

const busy = ref(false)
const notice = ref(null) // { kind, text }

async function loadMe() {
  await loadMember()
}

onMounted(() => {
  loadMe()
})

function openLogin(mode) {
  loginDialogMode.value = mode
  loginDialogOpen.value = true
}

// bind 成功：账号池变了，重拉 /api/member/me
async function onBindSuccess() {
  await loadMe()
}

function fmtBytes(b) {
  if (b === null || b === undefined) return '—'
  if (b === 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let v = b
  let i = 0
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024
    i++
  }
  return `${v >= 100 || i === 0 ? Math.round(v) : v.toFixed(1)} ${units[i]}`
}

// 账号池操作：id 传 accountMid（/me 的 accounts 行没给别的 id）；后端 message 原样展示
async function accountAction(action, acc) {
  if (busy.value) return
  const who = acc.accountName || `UID ${acc.accountMid}`
  if (action === 'delete' && !window.confirm(`确定解绑「${who}」？解绑后这个号不再替分站出力。`)) return
  busy.value = true
  notice.value = null
  try {
    const res = await fetch(`/api/member/accounts/${action}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ id: acc.id }),
    })
    const data = await res.json().catch(() => null)
    if (res.ok) {
      await loadMe()
    } else {
      notice.value = { kind: 'err', text: data?.message ?? '操作没成' }
    }
  } catch {
    notice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    busy.value = false
  }
}

async function doLogout() {
  await logoutMember()
}
</script>

<template>
  <div class="min-h-screen bg-pageBg">
    <TopBar />

    <main class="mx-auto max-w-[1000px] px-6 py-8">
      <p v-if="!member && !loaded" class="py-20 text-center text-sm text-inkSub">正在核对身份……</p>

      <!-- 访客引导：一扇大大的扫码门 -->
      <section v-else-if="!member" class="rounded-2xl border border-lineSoft bg-cardBg px-6 py-16 text-center shadow-[0_1px_2px_rgba(0,0,0,0.03)]">
        <span class="mx-auto flex h-16 w-16 items-center justify-center rounded-full bg-stationPink/10">
          <svg viewBox="0 0 24 24" class="h-8 w-8 text-stationPink" fill="none" stroke="currentColor" stroke-width="1.6">
            <rect x="3.5" y="3.5" width="7" height="7" rx="1.5" />
            <rect x="13.5" y="3.5" width="7" height="7" rx="1.5" />
            <rect x="3.5" y="13.5" width="7" height="7" rx="1.5" />
            <path d="M13.5 13.5h3v3h-3zM18.5 18.5h2v2h-2z" fill="currentColor" stroke="none" />
          </svg>
        </span>
        <h2 class="mt-5 text-lg font-semibold text-inkMain">分站还不认识你</h2>
        <p class="mx-auto mt-2 max-w-[360px] text-sm leading-6 text-inkSub">
          手机 B 站扫个码进门：投稿记在你名下，B 站账号池、出力记录都在这间控制室里管。
        </p>
        <button
          class="mt-7 rounded-full bg-gradient-to-r from-stationPink to-[#f994b6] px-9 py-2.5 text-sm font-medium text-white shadow-md shadow-stationPink/30 transition hover:-translate-y-px hover:shadow-lg hover:shadow-stationPink/40 active:translate-y-0"
          @click="openLogin('admit')"
        >
          扫码进站
        </button>
      </section>

      <template v-else>
        <!-- 身份卡 -->
        <section class="rounded-2xl border border-lineSoft bg-cardBg p-6 shadow-[0_1px_2px_rgba(0,0,0,0.03)]">
          <div class="flex flex-wrap items-center gap-5">
            <span class="relative block h-16 w-16 shrink-0">
              <img
                v-if="member.faceUrl"
                :src="member.faceUrl"
                :alt="member.nickname"
                referrerpolicy="no-referrer"
                class="h-full w-full rounded-full object-cover ring-2 ring-stationPink/20"
              />
              <span v-else class="flex h-full w-full items-center justify-center rounded-full bg-lineSoft text-xl text-inkFaint">
                {{ (member.nickname || '?').slice(0, 1) }}
              </span>
              <svg
                v-if="member.role === 'owner'"
                viewBox="0 0 24 24"
                class="absolute -right-1.5 -top-1.5 h-5 w-5 drop-shadow-[0_1px_2px_rgba(0,0,0,0.3)]"
                aria-label="站长"
              >
                <path
                  d="M3 17.5h18L22.5 8l-5.7 4L12 3.5 7.2 12 1.5 8 3 17.5Z"
                  fill="#f5c542"
                  stroke="#ffffff"
                  stroke-width="1.4"
                  stroke-linejoin="round"
                />
              </svg>
            </span>

            <div class="min-w-0 flex-1">
              <div class="flex flex-wrap items-center gap-2">
                <h2 class="text-lg font-semibold text-inkMain">{{ member.nickname }}</h2>
                <span
                  v-if="member.role === 'owner'"
                  class="rounded-full border border-amber-400/60 bg-amber-50 px-2 py-0.5 text-[11px] font-medium text-amber-600"
                >
                  ♛ 站长
                </span>
                <span v-else class="rounded-full border border-stationPink/40 bg-stationPink/5 px-2 py-0.5 text-[11px] text-stationPink">成员</span>
              </div>
              <p class="mt-1.5 font-mono text-xs text-inkFaint">
                UID {{ member.mid }} · 上次登录 {{ friendlyTime(member.lastLoginAtUtc) }}
                <template v-if="member.note"> · 站长备注：{{ member.note }}</template>
              </p>
            </div>

            <div class="flex shrink-0 items-center gap-2">
              <RouterLink
                v-if="member.role === 'owner'"
                to="/admin"
                class="rounded-full border border-amber-400/60 bg-amber-50 px-4 py-1.5 text-xs text-amber-700 transition hover:bg-amber-100"
              >
                ♛ 进站长室
              </RouterLink>
              <button
                class="rounded-full border border-lineSoft px-4 py-1.5 text-xs text-inkSub transition hover:border-stationPink/40 hover:text-stationPink"
                @click="doLogout"
              >
                退出登录
              </button>
            </div>
          </div>

          <!-- 入站统计 -->
          <div class="mt-6 grid grid-cols-2 gap-x-6 gap-y-4 border-t border-lineSoft/70 pt-5 sm:max-w-[420px]">
            <div class="flex flex-col gap-1">
              <b class="font-mono text-[26px] font-medium leading-none text-inkMain">{{ member.submitted ?? 0 }}</b>
              <span class="text-xs text-inkFaint">投稿进站（条）</span>
            </div>
            <div class="flex flex-col gap-1">
              <b class="font-mono text-[26px] font-medium leading-none text-inkMain">{{ fmtBytes(member.bytes) }}</b>
              <span class="text-xs text-inkFaint">替你占的库存</span>
            </div>
          </div>
        </section>

        <!-- 账号池 -->
        <section class="mt-4 rounded-2xl border border-lineSoft bg-cardBg p-6 shadow-[0_1px_2px_rgba(0,0,0,0.03)]">
          <div class="mb-4 flex items-center justify-between gap-3">
            <h3 class="text-sm font-semibold text-inkMain">
              B 站账号池
              <span class="ml-1.5 rounded-full border border-stationPink/30 bg-stationPink/5 px-2 py-0.5 text-[11px] font-normal text-stationPink">
                {{ member.accounts?.length ?? 0 }} 个号
              </span>
            </h3>
            <button
              class="flex items-center gap-1 rounded-full border border-stationPink px-3.5 py-1.5 text-xs text-stationPink transition hover:bg-stationPink hover:text-white"
              @click="openLogin('bind')"
            >
              <svg viewBox="0 0 24 24" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round">
                <path d="M12 5v14M5 12h14" />
              </svg>
              添加 B 站账号
            </button>
          </div>

          <ul v-if="member.accounts?.length" class="space-y-2">
            <li
              v-for="acc in member.accounts"
              :key="acc.accountMid"
              class="flex flex-wrap items-center gap-3 rounded-xl border border-lineSoft bg-[#f6f7f8] px-4 py-3"
            >
              <div class="min-w-0 flex-1">
                <p class="flex items-center gap-2 text-sm text-inkMain">
                  <span class="truncate">{{ acc.accountName || `UID ${acc.accountMid}` }}</span>
                  <span v-if="acc.isPrimary" class="shrink-0 rounded-full bg-stationPink px-1.5 py-0.5 text-[10px] leading-none text-white">主号</span>
                  <span v-if="acc.expired" class="shrink-0 rounded-full border border-rose-300 px-1.5 py-0.5 text-[10px] leading-none text-rose-500">登录已失效</span>
                </p>
                <p class="mt-1 font-mono text-xs text-inkFaint">UID {{ acc.accountMid }}</p>
              </div>
              <div class="flex shrink-0 items-center gap-2">
                <button
                  v-if="!acc.isPrimary"
                  class="rounded-lg border border-lineSoft bg-cardBg px-3 py-1 text-xs text-inkSub transition hover:border-stationPink/50 hover:text-stationPink disabled:opacity-40"
                  :disabled="busy"
                  @click="accountAction('primary', acc)"
                >
                  设为主号
                </button>
                <button
                  class="rounded-lg border border-lineSoft bg-cardBg px-3 py-1 text-xs text-inkSub transition hover:border-rose-300 hover:text-rose-500 disabled:opacity-40"
                  :disabled="busy"
                  @click="accountAction('delete', acc)"
                >
                  解绑
                </button>
              </div>
            </li>
          </ul>
          <p v-else class="py-8 text-center text-sm text-inkFaint">
            账号池还空着——添个 B 站号，分站替你把活分摊到号上。
          </p>

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
        </section>
      </template>
    </main>

    <!-- bind 实例：只管「绑定 B 站账号」的弹层，成功后喊这里重拉账号池 -->
    <LoginDialog instance-mode="bind" @bind-success="onBindSuccess" />
  </div>
</template>
