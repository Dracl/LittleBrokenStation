<script setup>
import { onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import TopBar from '../components/TopBar.vue'
import { load as loadMember } from '../memberStore'

// 落籍页（M5.4）：站名 + 密码开分站自己的门。有主之站凭邀请码落席，
// 无主之站首位免码继位站长（与扫码首扫同一套裁决，后端 ResolveSeatForRegister 说了算）。
const router = useRouter()

const hasOwner = ref(true)
const form = ref({ username: '', password: '', confirm: '', code: '' })
const submitting = ref(false)
const notice = ref(null) // { kind: 'ok' | 'err', text }

onMounted(async () => {
  try {
    const res = await fetch('/api/member/status')
    if (res.ok) {
      const data = await res.json().catch(() => null)
      hasOwner.value = data?.hasOwner !== false
    }
  } catch {
    /* 问不动就当有主：邀请码框照常摆着 */
  }
})

async function submit() {
  if (submitting.value) return
  const username = form.value.username.trim()
  if (!username || !form.value.password) {
    notice.value = { kind: 'err', text: '站名和密码都得填上' }
    return
  }
  if (form.value.password !== form.value.confirm) {
    notice.value = { kind: 'err', text: '两次密码没对上' }
    return
  }
  submitting.value = true
  notice.value = null
  try {
    const res = await fetch('/api/member/register', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        username,
        password: form.value.password,
        code: form.value.code.trim() || null,
      }),
    })
    const data = await res.json().catch(() => null)
    if (!res.ok) {
      // 400 格式/弱码、403 邀请制、409 重名：后端 message 原样展示
      notice.value = { kind: 'err', text: data?.message ?? '分站后端没有应答' }
      return
    }
    // 注册即已登录（后端发了同款会话 cookie）：拉齐身份直奔个人空间
    await loadMember()
    notice.value = { kind: 'ok', text: data?.message ?? '站籍落定' }
    setTimeout(() => router.push('/me'), 800)
  } catch {
    notice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <div class="min-h-screen bg-pageBg">
    <TopBar />
    <main class="mx-auto max-w-[440px] px-6 py-10">
      <section class="overflow-hidden rounded-2xl border border-lineSoft bg-cardBg shadow-sm">
        <div class="bg-gradient-to-r from-stationPink/10 via-stationPink/5 to-transparent px-6 pb-5 pt-6">
          <h1 class="text-lg font-semibold text-inkMain">落个站籍</h1>
          <p class="mt-1 text-xs leading-5 text-inkFaint">
            站名密码是分站自己的门钥匙；B 站号是可选挂件，进站后随时能在个人空间扫码挂上。
          </p>
        </div>

        <div class="px-6 py-5">
          <p
            v-if="hasOwner === false"
            class="mb-4 rounded-xl border border-amber-300/70 bg-amber-50 px-3.5 py-2.5 text-xs font-medium leading-5 text-amber-700"
          >
            本站尚无主：第一位注册者继位站长，免邀请码
          </p>

          <label class="block text-[12px] font-medium text-inkSub">站名</label>
          <input
            v-model="form.username"
            type="text"
            autocomplete="username"
            placeholder="2–24 个汉字、字母、数字或下划线"
            class="mt-1.5 w-full rounded-xl border border-lineSoft bg-[#f6f7f8] px-4 py-3 text-sm text-inkMain placeholder:text-inkFaint transition focus:border-stationPink focus:bg-white focus:outline-none focus:ring-2 focus:ring-stationPink/20"
            :disabled="submitting"
            @keyup.enter="submit"
          />

          <label class="mt-4 block text-[12px] font-medium text-inkSub">密码</label>
          <input
            v-model="form.password"
            type="password"
            autocomplete="new-password"
            placeholder="至少 8 位"
            class="mt-1.5 w-full rounded-xl border border-lineSoft bg-[#f6f7f8] px-4 py-3 text-sm text-inkMain placeholder:text-inkFaint transition focus:border-stationPink focus:bg-white focus:outline-none focus:ring-2 focus:ring-stationPink/20"
            :disabled="submitting"
            @keyup.enter="submit"
          />

          <label class="mt-4 block text-[12px] font-medium text-inkSub">再验一遍密码</label>
          <input
            v-model="form.confirm"
            type="password"
            autocomplete="new-password"
            placeholder="同一个密码敲两遍"
            class="mt-1.5 w-full rounded-xl border border-lineSoft bg-[#f6f7f8] px-4 py-3 text-sm text-inkMain placeholder:text-inkFaint transition focus:border-stationPink focus:bg-white focus:outline-none focus:ring-2 focus:ring-stationPink/20"
            :disabled="submitting"
            @keyup.enter="submit"
          />

          <template v-if="hasOwner">
            <label class="mt-4 block text-[12px] font-medium text-inkSub">邀请码</label>
            <input
              v-model="form.code"
              type="text"
              placeholder="站长发的一次性邀请码"
              class="mt-1.5 w-full rounded-xl border border-lineSoft bg-[#f6f7f8] px-4 py-3 text-sm text-inkMain placeholder:text-inkFaint transition focus:border-stationPink focus:bg-white focus:outline-none focus:ring-2 focus:ring-stationPink/20"
              :disabled="submitting"
              @keyup.enter="submit"
            />
            <p class="mt-1.5 text-xs leading-5 text-inkFaint">分站是邀请制——没码就找站长要一枚。</p>
          </template>

          <p
            v-if="notice"
            class="mt-4 rounded-xl px-3.5 py-2.5 text-xs leading-5"
            :class="notice.kind === 'ok' ? 'bg-emerald-50 text-emerald-700' : 'bg-rose-50 text-rose-600'"
          >
            {{ notice.text }}
          </p>

          <button
            class="mt-5 w-full rounded-xl bg-gradient-to-r from-stationPink to-[#f994b6] py-2.5 text-sm font-medium text-white shadow-md shadow-stationPink/30 transition hover:-translate-y-px hover:shadow-lg hover:shadow-stationPink/40 active:translate-y-0 disabled:translate-y-0 disabled:opacity-50 disabled:shadow-none"
            :disabled="submitting"
            @click="submit"
          >
            {{ submitting ? '正在落籍…' : '落籍进站' }}
          </button>

          <p class="mt-3 text-center text-xs text-inkFaint">
            已经有席位的？顶栏「登录」里用站名密码直接进。
          </p>
        </div>
      </section>
    </main>
  </div>
</template>
