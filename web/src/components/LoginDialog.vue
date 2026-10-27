<script setup>
import { computed, nextTick, onUnmounted, ref, watch } from 'vue'
import { loginDialogOpen, loginDialogMode, load as loadMember } from '../memberStore'
// qrcode.min.js 是 davidshimjs qrcodejs 原版经典脚本（顶层 var QRCode，没有模块导出）。
// 直接 ESM import 会被模块作用域关住、挂不上 window，所以按资源 URL 注入 classic script，
// 让它在经典脚本作用域里把 QRCode 落到 window 上（dev 与 build 都成立的唯一稳妥路）。
import qrcodeScriptUrl from '../libs/qrcode.min.js?url'

const props = defineProps({
  // 本实例只认领「全局模式与自己一致」的那次唤起：
  // 同页可能挂两个实例（顶栏默认登录窗 + 控制室 bind），各亮各的，不叠罗汉。
  // 登录与平台绑定彻底分离（M5.4 修正）：admit 实例 = 账密登录单屏，不再亮扫码；
  // 扫码链路只留给 bind 实例（/me 绑外部平台号），扫码代码整段留在文件里。
  instanceMode: { type: String, default: 'admit' },
})
const emit = defineEmits(['bind-success'])

const visible = computed(() =>
  loginDialogOpen.value
  && (props.instanceMode === 'admit'
    ? loginDialogMode.value === 'admit' || loginDialogMode.value === 'login'
    : loginDialogMode.value === props.instanceMode),
)

const activeMode = ref('login') // login=账密登录（默认实例唯一形态）| bind=往账号池加号；admit 扫码形态已退役，相关代码休眠备用
const hasOwner = ref(true)
const invite = ref('')
const step = ref('idle') // idle | loading | qr | expired | done
const pollPhase = ref('waiting') // waiting | scanned（success 直接进 done 分支）
const notice = ref(null) // { kind, text }
const qrBox = ref(null)
const qrKey = ref('')
// 账密登录表单
const loginForm = ref({ username: '', password: '' })
const loggingIn = ref(false)
let pollTimer = null
let closeTimer = null

function stopPoll() {
  if (pollTimer) {
    clearTimeout(pollTimer)
    pollTimer = null
  }
}

let qrScript = null
function ensureQRLib() {
  if (window.QRCode) return Promise.resolve()
  if (!qrScript) {
    qrScript = new Promise((resolve, reject) => {
      const el = document.createElement('script')
      el.src = qrcodeScriptUrl
      el.onload = resolve
      el.onerror = () => {
        qrScript = null
        reject(new Error('二维码组件没到位'))
      }
      document.head.appendChild(el)
    })
  }
  return qrScript
}

function renderQr(text) {
  const el = qrBox.value
  if (!el || !window.QRCode) return
  el.innerHTML = ''
  new window.QRCode(el, {
    text,
    width: 224,
    height: 224,
    colorDark: '#1f1f1f',
    colorLight: '#ffffff',
    correctLevel: window.QRCode.CorrectLevel.M,
  })
}

async function loadStatus() {
  hasOwner.value = true
  try {
    const res = await fetch('/api/member/status')
    if (res.ok) {
      const data = await res.json().catch(() => null)
      hasOwner.value = data?.hasOwner !== false
    }
  } catch {
    /* 问不动就当有主：邀请码框照常摆着，真无主时出码那步后端自会放行 */
  }
}

function onKeydown(event) {
  if (event.key === 'Escape') close()
}

const modeTitle = computed(() => ({
  bind: '绑定 B 站账号',
  login: '登录分站',
  admit: '扫码进站',
}[activeMode.value]))
const modeSubtitle = computed(() => ({
  bind: '再绑一个号进账号池，分站替两号轮流出力',
  login: '分站自己的门钥匙；外部平台号进站后在控制室绑定',
  admit: '手机 B 站扫一扫，分站就认识你了',
}[activeMode.value]))

function close() {
  loginDialogOpen.value = false
}

function reset() {
  stopPoll()
  if (closeTimer) {
    clearTimeout(closeTimer)
    closeTimer = null
  }
  activeMode.value = props.instanceMode === 'bind' ? 'bind' : 'login'
  invite.value = ''
  step.value = 'idle'
  pollPhase.value = 'waiting'
  notice.value = null
  qrKey.value = ''
  loginForm.value = { username: '', password: '' }
  loggingIn.value = false
}

watch(visible, (open) => {
  if (open) {
    reset()
    window.addEventListener('keydown', onKeydown)
  } else {
    stopPoll()
    if (closeTimer) {
      clearTimeout(closeTimer)
      closeTimer = null
    }
    window.removeEventListener('keydown', onKeydown)
  }
})

async function pollOnce() {
  if (!visible.value) return
  try {
    const res = await fetch(`/api/member/qr/poll?key=${encodeURIComponent(qrKey.value)}`)
    const data = await res.json().catch(() => null)
    const phase = data?.phase
    if (phase === 'success') {
      stopPoll()
      step.value = 'done'
      if (activeMode.value === 'bind') {
        // bind 成功不动登录态：喊父组件去重拉账号池，这里报个喜自己收摊
        notice.value = { kind: 'ok', text: `账号「${data?.account?.nickname ?? ''}」已绑进池子` }
        emit('bind-success')
        closeTimer = setTimeout(close, 1000)
      } else {
        notice.value = { kind: 'ok', text: `欢迎进站，${data?.member?.nickname ?? ''}` }
        await loadMember()
        close()
      }
      return
    }
    if (phase === 'waiting' || phase === 'scanned') {
      pollPhase.value = phase
      pollTimer = setTimeout(pollOnce, 1500)
      return
    }
    // expired / unknown / key 不存在（410）：终止轮询，message 原样递到脸上，给刷新入口
    stopPoll()
    step.value = 'expired'
    if (data?.message) notice.value = { kind: 'err', text: data.message }
  } catch {
    stopPoll()
    step.value = 'expired'
    notice.value = { kind: 'err', text: '分站后端没有应答' }
  }
}

async function getQr() {
  if (step.value === 'loading') return
  stopPoll()
  notice.value = null
  step.value = 'loading'
  const body = { mode: activeMode.value }
  const code = invite.value.trim()
  if (code && hasOwner.value && activeMode.value === 'admit') body.invite = code
  try {
    const res = await fetch('/api/member/qr/new', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    })
    const data = await res.json().catch(() => null)
    if (!res.ok || !data?.url || !data?.key) {
      // 403（邀请制拒绝 / bind 未登录）429 502：后端 message 原样展示，不自己编
      notice.value = { kind: 'err', text: data?.message ?? '二维码没发出来，稍后再试' }
      step.value = 'idle'
      return
    }
    qrKey.value = data.key
    pollPhase.value = 'waiting'
    step.value = 'qr'
    try {
      await ensureQRLib()
    } catch {
      notice.value = { kind: 'err', text: '二维码组件没到位，刷新页面再试' }
      step.value = 'idle'
      return
    }
    await nextTick()
    renderQr(data.url)
    pollTimer = setTimeout(pollOnce, 1500)
  } catch {
    notice.value = { kind: 'err', text: '分站后端没有应答' }
    step.value = 'idle'
  }
}

onUnmounted(() => {
  stopPoll()
  if (closeTimer) clearTimeout(closeTimer)
  window.removeEventListener('keydown', onKeydown)
})

// 账密登录：后端 401/403/429 的 message 原样递到脸上，不自编拒词
async function submitPasswordLogin() {
  if (loggingIn.value) return
  const username = loginForm.value.username.trim()
  const password = loginForm.value.password
  if (!username || !password) {
    notice.value = { kind: 'err', text: '站名和密码都得填上' }
    return
  }
  loggingIn.value = true
  notice.value = null
  try {
    const res = await fetch('/api/member/login/password', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username, password }),
    })
    const data = await res.json().catch(() => null)
    if (!res.ok) {
      notice.value = { kind: 'err', text: data?.message ?? '分站后端没有应答' }
      return
    }
    notice.value = { kind: 'ok', text: data?.message ?? `欢迎回来，${username}` }
    await loadMember()
    close()
  } catch {
    notice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    loggingIn.value = false
  }
}

function goRegister() {
  close()
}
</script>

<template>
  <Teleport to="body">
    <div
      v-if="visible"
      class="lbs-fade fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4 backdrop-blur-sm"
      @click.self="close"
    >
      <div class="lbs-pop w-full max-w-[400px] overflow-hidden rounded-2xl bg-cardBg shadow-[0_24px_80px_-12px_rgba(0,0,0,0.45)]">
        <div class="flex items-center justify-between bg-gradient-to-r from-stationPink/10 via-stationPink/5 to-transparent px-6 pb-4 pt-5">
          <div class="flex items-center gap-3">
            <span class="flex h-10 w-10 shrink-0 items-center justify-center rounded-full bg-stationPink/10">
              <svg v-if="activeMode === 'bind'" viewBox="0 0 24 24" class="h-5 w-5 text-stationPink" fill="none" stroke="currentColor" stroke-width="1.8">
                <rect x="3.5" y="3.5" width="7" height="7" rx="1.5" />
                <rect x="13.5" y="3.5" width="7" height="7" rx="1.5" />
                <rect x="3.5" y="13.5" width="7" height="7" rx="1.5" />
                <path d="M13.5 13.5h3v3h-3zM18.5 18.5h2v2h-2z" fill="currentColor" stroke="none" />
              </svg>
              <svg v-else viewBox="0 0 24 24" class="h-5 w-5 text-stationPink" fill="none" stroke="currentColor" stroke-width="1.8">
                <circle cx="8.5" cy="8.5" r="4" />
                <path d="M11.5 11.5 20 20M16.5 16.5l2-2M13.5 13.5l2.5-2.5" stroke-linecap="round" />
              </svg>
            </span>
            <div>
              <h3 class="text-[17px] font-semibold text-inkMain">{{ modeTitle }}</h3>
              <p class="mt-0.5 text-xs text-inkFaint">{{ modeSubtitle }}</p>
            </div>
          </div>
          <button
            class="flex h-8 w-8 shrink-0 items-center justify-center rounded-full text-inkFaint transition hover:bg-black/5 hover:text-inkMain"
            aria-label="关闭"
            @click="close"
          >
            <svg viewBox="0 0 24 24" class="h-5 w-5" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M6 6l12 12M18 6L6 18" stroke-linecap="round" />
            </svg>
          </button>
        </div>

        <!-- 默认态 = 账密登录单屏（登录与平台绑定分离）；扫码流程只在 bind 实例里渲染 -->
        <div class="px-6 py-5">
          <template v-if="activeMode === 'login'">
            <input
              v-model="loginForm.username"
              type="text"
              autocomplete="username"
              placeholder="站名（汉字/字母/数字/下划线）"
              class="w-full rounded-xl border border-lineSoft bg-[#f6f7f8] px-4 py-3 text-sm text-inkMain placeholder:text-inkFaint transition focus:border-stationPink focus:bg-white focus:outline-none focus:ring-2 focus:ring-stationPink/20"
              :disabled="loggingIn"
              @keyup.enter="submitPasswordLogin"
            />
            <input
              v-model="loginForm.password"
              type="password"
              autocomplete="current-password"
              placeholder="密码"
              class="mt-3 w-full rounded-xl border border-lineSoft bg-[#f6f7f8] px-4 py-3 text-sm text-inkMain placeholder:text-inkFaint transition focus:border-stationPink focus:bg-white focus:outline-none focus:ring-2 focus:ring-stationPink/20"
              :disabled="loggingIn"
              @keyup.enter="submitPasswordLogin"
            />
            <p class="mt-3 text-xs leading-5">
              还没有站籍？
              <RouterLink to="/register" class="font-medium text-stationPink transition hover:underline" @click="goRegister">注册去 →</RouterLink>
            </p>
            <p class="mt-1 text-[11px] leading-4 text-inkFaint">忘记密码？找站长代重置；扫码绑外部平台号在控制室的「外部账号绑定」卡里。</p>
          </template>

          <template v-else>
          <p
            v-if="activeMode === 'admit' && hasOwner === false"
            class="mb-4 rounded-xl border border-amber-300/70 bg-amber-50 px-3.5 py-2.5 text-xs font-medium leading-5 text-amber-700"
          >
            本站尚无主：第一位扫码者继位站长，免邀请码
          </p>

          <template v-if="step === 'idle' || step === 'loading'">
            <template v-if="activeMode === 'admit' && hasOwner">
              <input
                v-model="invite"
                type="text"
                placeholder="一次性邀请码（老席位留空直接扫）"
                class="w-full rounded-xl border border-lineSoft bg-[#f6f7f8] px-4 py-3 text-sm text-inkMain placeholder:text-inkFaint transition focus:border-stationPink focus:bg-white focus:outline-none focus:ring-2 focus:ring-stationPink/20"
                :disabled="step === 'loading'"
                @keyup.enter="getQr"
              />
              <p class="mt-2 text-xs leading-5 text-inkFaint">首次进站需要站长发的邀请码；老席位直接扫。</p>
            </template>
            <p v-else-if="activeMode === 'bind'" class="text-xs leading-5 text-inkFaint">
              扫码绑定的新号会进你的账号池；主号仍是默认出力位，可随时改设。
            </p>
          </template>

          <div v-else-if="step === 'qr'" class="flex flex-col items-center gap-3">
            <div ref="qrBox" class="rounded-xl border border-lineSoft bg-white p-3 shadow-sm"></div>
            <p class="text-sm" :class="pollPhase === 'scanned' ? 'font-medium text-stationPink' : 'text-inkSub'">
              {{ pollPhase === 'scanned' ? '已扫码，请在手机上确认' : '请用手机 B 站扫码' }}
            </p>
          </div>

          <div v-else-if="step === 'expired'" class="flex flex-col items-center gap-2 py-3">
            <p class="text-sm text-inkSub">这张码作废了，右下角换一张再来。</p>
          </div>
          </template>

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
        </div>

        <div class="flex items-center justify-end gap-2 border-t border-lineSoft/70 bg-[#fafbfc] px-6 py-4">
          <div class="flex gap-2">
            <button class="rounded-lg px-4 py-2 text-sm text-inkSub transition hover:bg-black/5 hover:text-inkMain" @click="close">
              {{ activeMode === 'bind' ? '先不绑了' : '先不登了' }}
            </button>
            <button
              v-if="activeMode === 'login'"
              class="rounded-lg bg-gradient-to-r from-stationPink to-[#f994b6] px-5 py-2 text-sm font-medium text-white shadow-md shadow-stationPink/30 transition hover:-translate-y-px hover:shadow-lg hover:shadow-stationPink/40 active:translate-y-0 disabled:translate-y-0 disabled:opacity-50 disabled:shadow-none"
              :disabled="loggingIn"
              @click="submitPasswordLogin"
            >
              {{ loggingIn ? '验钥中…' : '登录' }}
            </button>
            <button
              v-else
              class="rounded-lg bg-gradient-to-r from-stationPink to-[#f994b6] px-5 py-2 text-sm font-medium text-white shadow-md shadow-stationPink/30 transition hover:-translate-y-px hover:shadow-lg hover:shadow-stationPink/40 active:translate-y-0 disabled:translate-y-0 disabled:opacity-50 disabled:shadow-none"
              :disabled="step === 'loading' || step === 'qr' || step === 'done'"
              @click="getQr"
            >
              {{ step === 'loading' ? '出码中…' : step === 'expired' ? '换一张二维码' : '获取二维码' }}
            </button>
          </div>
        </div>
      </div>
    </div>
  </Teleport>
</template>

<style scoped>
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
