<script setup>
import { computed, onMounted, ref } from 'vue'
import TopBar from '../components/TopBar.vue'
import LoginDialog from '../components/LoginDialog.vue'
import CrownBadge from '../components/CrownBadge.vue'
import { member, loaded, loginDialogOpen, loginDialogMode, load as loadMember, logout as logoutMember } from '../memberStore'
import { friendlyTime } from '../utils/time'

const busy = ref(false)
const notice = ref(null) // { kind, text }

const displayName = computed(() => {
  const m = member.value
  if (!m) return ''
  return m.nickname || (m.username ? `@${m.username}` : '旅人')
})

// 改密表单：首设（扫码客从未设过）免旧密码，验旧与否后端说了算，前端只递参数
const pwdForm = ref({ oldPassword: '', newPassword: '', confirm: '' })
const pwdBusy = ref(false)
const pwdNotice = ref(null)

async function submitPasswordChange() {
  if (pwdBusy.value) return
  if (!member.value) return
  if (!pwdForm.value.newPassword) {
    pwdNotice.value = { kind: 'err', text: '新密码总得填吧' }
    return
  }
  if (pwdForm.value.newPassword !== pwdForm.value.confirm) {
    pwdNotice.value = { kind: 'err', text: '两次新密码没对上' }
    return
  }
  pwdBusy.value = true
  pwdNotice.value = null
  try {
    const res = await fetch('/api/member/password/change', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        oldPassword: pwdForm.value.oldPassword || null,
        newPassword: pwdForm.value.newPassword,
      }),
    })
    const data = await res.json().catch(() => null)
    if (!res.ok) {
      pwdNotice.value = { kind: 'err', text: data?.message ?? '分站后端没有应答' }
      return
    }
    pwdNotice.value = { kind: 'ok', text: data?.message ?? '钥匙换过了' }
    pwdForm.value = { oldPassword: '', newPassword: '', confirm: '' }
    await loadMe()
  } catch {
    pwdNotice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    pwdBusy.value = false
  }
}

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
  const who = acc.accountName || `B站账号 ${acc.accountMid}`
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

      <!-- 访客引导：登录与平台绑定分离（M5.4）——进门靠站籍，扫码只是进站后的可选绑定 -->
      <section v-else-if="!member" class="rounded-2xl border border-lineSoft bg-cardBg px-6 py-16 text-center shadow-[0_1px_2px_rgba(0,0,0,0.03)]">
        <span class="mx-auto flex h-16 w-16 items-center justify-center rounded-full bg-stationPink/10">
          <svg viewBox="0 0 24 24" class="h-8 w-8 text-stationPink" fill="none" stroke="currentColor" stroke-width="1.6">
            <circle cx="8.5" cy="8.5" r="4" />
            <path d="M11.5 11.5 20 20M16.5 16.5l2-2M13.5 13.5l2.5-2.5" stroke-linecap="round" />
          </svg>
        </span>
        <h2 class="mt-5 text-lg font-semibold text-inkMain">分站还不认识你</h2>
        <p class="mx-auto mt-2 max-w-[360px] text-sm leading-6 text-inkSub">
          注册个站籍就能进门：逛分站、看片、发私信都认这个身份；B 站号只是可选挂件，进站后在控制室扫码绑上。
        </p>
        <RouterLink
          to="/register"
          class="mt-7 inline-block rounded-full bg-gradient-to-r from-stationPink to-[#f994b6] px-9 py-2.5 text-sm font-medium text-white shadow-md shadow-stationPink/30 transition hover:-translate-y-px hover:shadow-lg hover:shadow-stationPink/40 active:translate-y-0"
        >
          去注册
        </RouterLink>
        <p class="mt-4 text-xs text-inkFaint">
          已有站籍？
          <button class="text-stationPink transition hover:underline" @click="openLogin('admit')">直接登录</button>
        </p>
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
                {{ (member.nickname || member.username || '?').slice(0, 1).toUpperCase() }}
              </span>
              <CrownBadge
                v-if="member"
                :tier="member.role"
                class="absolute -right-1.5 -top-1.5 h-5 w-5"
              />
            </span>

            <div class="min-w-0 flex-1">
              <div class="flex flex-wrap items-center gap-2">
                <h2 class="text-lg font-semibold text-inkMain">{{ displayName }}</h2>
                <span
                  v-if="member.role === 'owner'"
                  class="rounded-full border border-amber-400/60 bg-amber-50 px-2 py-0.5 text-[11px] font-medium text-amber-600"
                >
                  ♛ 站长
                </span>
                <span v-else class="flex items-center gap-1 rounded-full border border-slate-300/70 bg-slate-50 px-2 py-0.5 text-[11px] text-slate-500">
                  <CrownBadge tier="member" class="h-3 w-3" />
                  成员
                </span>
              </div>
              <p class="mt-1.5 font-mono text-xs text-inkFaint">
                UID #{{ member.memberId }}<template v-if="member.username"> · @{{ member.username }}</template>
                <template v-else>站内席位 #{{ member.memberId }}</template>
                · 上次登录 {{ friendlyTime(member.lastLoginAtUtc) }}
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

        <!-- 登录方式：分站自己的门钥匙（M5.4）——站名、密码照面；防锁死警示条钉在卡顶 -->
        <section class="mt-4 rounded-2xl border border-lineSoft bg-cardBg p-6 shadow-[0_1px_2px_rgba(0,0,0,0.03)]">
          <p
            v-if="member.hasPassword === false"
            class="mb-4 rounded-xl border border-stationPink/40 bg-stationPink/10 px-3.5 py-2.5 text-xs font-medium leading-5 text-stationPink"
          >
            还没设密码——cookie 一丢就进不来门了，现在就设一个
          </p>
          <h3 class="mb-4 text-sm font-semibold text-inkMain">登录方式</h3>

          <dl class="grid gap-3 sm:grid-cols-2">
            <div class="rounded-xl border border-lineSoft bg-[#f6f7f8] px-4 py-3">
              <dt class="text-[11px] text-inkFaint">站名（账密登录用）</dt>
              <dd class="mt-1 truncate text-sm text-inkMain">
                <template v-if="member.username">@{{ member.username }}</template>
                <template v-else>
                  <span class="text-inkFaint">未设——这是个纯扫码席位</span>
                  <p class="mt-1 text-[11px] leading-4 text-inkFaint">在下方补设密码，就能用密码开门（需先登录着）。</p>
                </template>
              </dd>
            </div>
            <div class="rounded-xl border border-lineSoft bg-[#f6f7f8] px-4 py-3">
              <dt class="text-[11px] text-inkFaint">B 站绑定</dt>
              <dd class="mt-1 flex flex-wrap items-center gap-2 text-sm text-inkMain">
                <template v-if="member.hasBilibili">
                  已绑 <span class="rounded-full bg-emerald-500/10 px-2 py-0.5 text-[11px] text-emerald-600">{{ member.accounts?.length ?? 0 }} 个号</span>
                </template>
                <template v-else>
                  <span class="text-inkFaint">未绑——素净席位可逛可看可私信，就是不能投稿</span>
                  <button
                    class="rounded-full border border-stationPink px-3 py-0.5 text-[11px] text-stationPink transition hover:bg-stationPink hover:text-white"
                    @click="openLogin('bind')"
                  >
                    扫码绑一个
                  </button>
                </template>
              </dd>
            </div>
          </dl>

          <div class="mt-4 rounded-xl border border-lineSoft px-4 py-4">
            <p class="text-[12px] font-medium text-inkSub">
              {{ member.hasPassword ? '换一把门钥匙' : '补设门钥匙（首次设密码，免旧密码）' }}
            </p>
            <div class="mt-3 flex flex-wrap items-center gap-2">
              <input
                v-if="member.hasPassword"
                v-model="pwdForm.oldPassword"
                type="password"
                autocomplete="current-password"
                placeholder="旧密码"
                class="w-full rounded-xl border border-lineSoft bg-[#f6f7f8] px-3.5 py-2 text-[13px] text-inkMain placeholder:text-inkFaint transition focus:border-stationPink focus:bg-white focus:outline-none focus:ring-2 focus:ring-stationPink/20 sm:w-40"
                :disabled="pwdBusy"
              />
              <input
                v-model="pwdForm.newPassword"
                type="password"
                autocomplete="new-password"
                placeholder="新密码（至少 8 位）"
                class="w-full rounded-xl border border-lineSoft bg-[#f6f7f8] px-3.5 py-2 text-[13px] text-inkMain placeholder:text-inkFaint transition focus:border-stationPink focus:bg-white focus:outline-none focus:ring-2 focus:ring-stationPink/20 sm:w-44"
                :disabled="pwdBusy"
              />
              <input
                v-model="pwdForm.confirm"
                type="password"
                autocomplete="new-password"
                placeholder="再验一遍"
                class="w-full rounded-xl border border-lineSoft bg-[#f6f7f8] px-3.5 py-2 text-[13px] text-inkMain placeholder:text-inkFaint transition focus:border-stationPink focus:bg-white focus:outline-none focus:ring-2 focus:ring-stationPink/20 sm:w-40"
                :disabled="pwdBusy"
                @keyup.enter="submitPasswordChange"
              />
              <button
                class="rounded-xl bg-stationPink px-4 py-2 text-[13px] text-white transition hover:bg-stationPink/90 disabled:cursor-not-allowed disabled:opacity-50"
                :disabled="pwdBusy"
                @click="submitPasswordChange"
              >
                {{ pwdBusy ? '提交中…' : member.hasPassword ? '换钥匙' : '设密码' }}
              </button>
            </div>
            <p
              v-if="pwdNotice"
              class="mt-2.5 text-xs leading-5"
              :class="pwdNotice.kind === 'ok' ? 'text-emerald-600' : 'text-rose-500'"
            >
              {{ pwdNotice.text }}
            </p>
            <p v-else class="mt-2.5 text-[11px] leading-4 text-inkFaint">
              密码只存在分站自家台账里（PBKDF2 加盐哈希），忘密了找站长代重置。
            </p>
          </div>
        </section>

        <!-- 外部账号绑定：登录与平台绑定分离（M5.4）——卡形不焊死任何平台，行戴平台徽 -->
        <section class="mt-4 rounded-2xl border border-lineSoft bg-cardBg p-6 shadow-[0_1px_2px_rgba(0,0,0,0.03)]">
          <div class="mb-4 flex items-center justify-between gap-3">
            <h3 class="text-sm font-semibold text-inkMain">
              外部账号绑定
              <span class="ml-1.5 rounded-full border border-stationPink/30 bg-stationPink/5 px-2 py-0.5 text-[11px] font-normal text-stationPink">
                {{ member.accounts?.length ?? 0 }} 个已绑
              </span>
            </h3>
            <button
              class="flex items-center gap-1 rounded-full border border-stationPink px-3.5 py-1.5 text-xs text-stationPink transition hover:bg-stationPink hover:text-white"
              @click="openLogin('bind')"
            >
              <svg viewBox="0 0 24 24" class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round">
                <path d="M12 5v14M5 12h14" />
              </svg>
              绑定 B 站
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
                  <span class="shrink-0 rounded-md bg-[#fb7299]/10 px-1.5 py-0.5 text-[10px] font-medium text-[#fb7299]">B 站</span>
                  <span class="truncate">{{ acc.accountName || `B站账号 ${acc.accountMid}` }}</span>
                  <span v-if="acc.isPrimary" class="shrink-0 rounded-full bg-stationPink px-1.5 py-0.5 text-[10px] leading-none text-white">主号</span>
                  <span v-if="acc.expired" class="shrink-0 rounded-full border border-rose-300 px-1.5 py-0.5 text-[10px] leading-none text-rose-500">登录已失效</span>
                </p>
                <p class="mt-1 font-mono text-xs text-inkFaint">B站号 {{ acc.accountMid }}</p>
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
            还没绑任何外部平台号——绑个 B 站，分站替你把活分摊到号上；不绑也能逛、能看、能私信（就是不能投稿）。
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
