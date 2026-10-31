<script setup>
import { computed, nextTick, onMounted, ref } from 'vue'
import TopBar from '../components/TopBar.vue'
import LoginDialog from '../components/LoginDialog.vue'
import CrownBadge from '../components/CrownBadge.vue'
import { member, loaded, loginDialogOpen, loginDialogMode, load as loadMember, logout as logoutMember } from '../memberStore'
import { requestConfirm } from '../confirmStore'
import { friendlyTime } from '../utils/time'
import { coverThumb } from '../utils/cover'
import { defaultAvatar, onAvatarError } from '../utils/avatar.js'
import WallWindow from '../components/WallWindow.vue'

const busy = ref(false)
const notice = ref(null) // { kind, text }
const settingsOpen = ref(false) // 账号与登录设置弹层：登录方式 + 外部账号绑定两块收在这里

// ─── 观看历史：记所有不只没看完（2026-10-29 站长改口：看完的也盖戳留档），卡面滚动展最近十档 ───
const history = ref(null) // null = 没拉到（在飞或后端没应答），[] = 确实没看过东西
const historyBusy = ref(false)
const historyNotice = ref(null) // { kind, text }

async function loadHistory({ silent = false } = {}) {
  if (historyBusy.value) return
  if (!silent) historyBusy.value = true
  try {
    const res = await fetch('/api/member/history')
    if (!res.ok) {
      history.value = null // 401/500 都当「没拿到」，不拿空数组冒充「没看过」
      return
    }
    const data = await res.json().catch(() => null)
    history.value = Array.isArray(data?.items) ? data.items : []
  } catch {
    history.value = null
  } finally {
    historyBusy.value = false
  }
}

// 进度百分比：时长不可信（0 / 非数）就给 0，不拿除零的 NaN 去撑进度条。
function progressPct(item) {
  const dur = Number(item?.durationSec)
  const pos = Number(item?.positionSec)
  if (!Number.isFinite(dur) || dur <= 0 || !Number.isFinite(pos)) return 0
  return Math.max(0, Math.min(100, (pos / dur) * 100))
}

// 封面口径抄首页卡片：本地件优先，没归档才吃 B 站图床缩图。
function historyCover(item) {
  return item.localCover || (item.coverUrl ? coverThumb(item.coverUrl, 480) : '')
}

// 卡面只展最近十档（接口本就在按 updated_at 倒序）。
const HISTORY_SHOW_COUNT = 10
const historyRecent = computed(() => (history.value ?? []).slice(0, HISTORY_SHOW_COUNT))

// 片内时间点：分:秒，过小时补时。
function historyClock(sec) {
  const n = Math.max(0, Math.floor(Number(sec) || 0))
  const pad = (x) => String(x).padStart(2, '0')
  const h = Math.floor(n / 3600)
  return h > 0 ? `${h}:${pad(Math.floor((n % 3600) / 60))}:${pad(n % 60)}` : `${pad(Math.floor(n / 60))}:${pad(n % 60)}`
}

// 封面淡入：新补进来的行封面是首次加载的图，先隐身等 load，省得灰底上「啪」地糊上图闪一下
function onCoverLoad(e) {
  e.target.classList.add('is-on')
}

const historyList = ref(null) // 滚动容器：删除全程由我们按帧接管 scrollTop，浏览器只负责老实渲染

async function deleteHistory(item) {
  if (historyBusy.value || historyExiting.value.has(item.videoId)) return
  historyNotice.value = null
  const snapshot = (history.value ?? []).slice()
  // 滚动治理（删最后一行「来回抽」的正解）：开场先定策——
  //   · 贴底态：逐帧追新底部。收合时浏览器会把 scrollTop 硬夹紧（overflow-anchor 管不住它），
  //     补货后高度恢复又空出一截——只要每帧都贴住 max，全程就是连续滑动，没有回弹
  //   · 删的是视口上方的整行：顶下排布会把下方内容顶上去，逐帧按「已收合量」同步缩 scrollTop，视口里的行钉死不动
  //   · 其余情形：按原 top 钉位（夹到合法区间即可）
  const el = historyList.value
  let keepTop = 0
  let wasAtBottom = false
  let wasAbove = false
  let rowH = 0
  let liveRow = null
  if (el) {
    keepTop = el.scrollTop
    wasAtBottom = el.scrollTop + el.clientHeight >= el.scrollHeight - 8
    liveRow = el.querySelector(`[data-video-id="${item.videoId}"]`)
    if (liveRow) {
      const rowTop = liveRow.getBoundingClientRect().top - el.getBoundingClientRect().top + el.scrollTop
      rowH = liveRow.offsetHeight
      wasAbove = rowTop + rowH <= el.scrollTop + 2
    }
  }
  const pin = () => {
    if (!el) return
    const max = el.scrollHeight - el.clientHeight
    let target = keepTop
    if (wasAtBottom) target = max
    else if (wasAbove) {
      // 行还在 DOM 里就按当下收合了多少补多少，被摘掉后按全高补齐，过渡连续不跳档
      const shrink = liveRow ? Math.max(0, rowH - liveRow.offsetHeight) : rowH
      target = keepTop - shrink
    }
    el.scrollTop = Math.max(0, Math.min(target, max))
  }
  let rafId = 0
  let frozen = null // 摘行瞬间封盘：之后的贴底跟随到此为止，补货长底不再拖着视野跑
  if (el) {
    const loop = () => {
      if (frozen !== null) {
        const m = el.scrollHeight - el.clientHeight
        el.scrollTop = Math.max(0, Math.min(frozen, m))
      } else {
        pin()
      }
      rafId = requestAnimationFrame(loop)
    }
    loop()
  }
  historyBusy.value = true
  markExiting(item.videoId, true)
  const req = fetch('/api/member/history/delete', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ videoId: item.videoId }),
  })
  await sleep(300) // 等退场演完再摘行，高度收起不 snaps
  history.value = (history.value ?? []).filter((x) => x.videoId !== item.videoId)
  markExiting(item.videoId, false)
  await nextTick()
  if (el) frozen = el.scrollTop // 此刻收合已落定：从这里开始视野完全静止
  try {
    const res = await req
    const data = await res.json().catch(() => null)
    // 成功不喧宾：退场动画本身就是交代，只留失败时的错牌
    if (res.ok) {
      historyNotice.value = null
      await loadHistory({ silent: true }) // 静默补货：不动 busy，免得按钮集体半透明闪一下
    } else {
      history.value = snapshot
      historyNotice.value = { kind: 'err', text: data?.message ?? '没抹成' }
    }
  } catch {
    history.value = snapshot
    historyNotice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    historyBusy.value = false
    // 收尾只守封盘位，不再追底：补货行在视线下方静悄悄长出来就好
    const settle = () => {
      if (!el) return
      const m = el.scrollHeight - el.clientHeight
      el.scrollTop = Math.max(0, Math.min(frozen ?? el.scrollTop, m))
    }
    await nextTick()
    settle()
    await sleep(150)
    settle()
    if (rafId) cancelAnimationFrame(rafId)
  }
}

async function clearHistory() {
  if (historyBusy.value) return
  if (!history.value?.length) return
  if (
    !(
      await requestConfirm({
        title: '清空观看历史',
        sub: `${history.value.length} 部`,
        message: '看过的记录整叠抹掉，之后进片子都从头放起。馆藏一根毛都不会掉。',
        confirmText: '清空',
        cancelText: '再留留',
        danger: true,
        icon: 'trash',
      })
    )
  ) {
    return
  }
  historyBusy.value = true
  historyNotice.value = null
  // 整叠退场：一行行收合淡出后清空，请求背后跑；没清成就连夜请回来
  const snapshot = (history.value ?? []).slice()
  markExiting(snapshot.map((x) => x.videoId), true)
  const req = fetch('/api/member/history/clear', { method: 'POST' })
  await sleep(300)
  history.value = []
  markExiting([], false)
  try {
    const res = await req
    const data = await res.json().catch(() => null)
    // 清空同理：整叠收合的动画就是回执，只留错牌
    if (res.ok) {
      historyNotice.value = null
    } else {
      history.value = snapshot
      historyNotice.value = { kind: 'err', text: data?.message ?? '没清成' }
    }
  } catch {
    history.value = snapshot
    historyNotice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    historyBusy.value = false
  }
}

// 退场中的行（videoId 集）：带集合让整叠清空也能逐行收合
const historyExiting = ref(new Set())
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
function markExiting(ids, on) {
  const list = Array.isArray(ids) ? ids : [ids]
  const next = new Set(historyExiting.value)
  for (const id of list) {
    if (on) next.add(id)
    else next.delete(id)
  }
  historyExiting.value = next
}

const displayName = computed(() => {
  const m = member.value
  if (!m) return ''
  return m.nickname || m.username || '旅人'
})

// ─── 我的合集：成员自管的抽屉。这里能建、能拆、能跳去正面看货；
// 往抽屉里装片去放映厅（WatchPage）的「合集」钮，那才是装卸口。
const myCollections = ref(null) // null = 没拉到；[] = 确实没建过
const collBusy = ref(false)
const collNotice = ref(null)
const newColl = ref({ name: '', intro: '' })

async function loadMyCollections() {
  collBusy.value = true
  try {
    const res = await fetch('/api/member/collections')
    const d = res.ok ? await res.json().catch(() => null) : null
    myCollections.value = Array.isArray(d?.collections) ? d.collections : []
  } catch {
    myCollections.value = null // 401/500 都当「没拿到」，不拿空数组冒充「没建过」
  } finally {
    collBusy.value = false
  }
}

async function createCollection() {
  const name = newColl.value.name.trim()
  if (!name || collBusy.value) return
  collBusy.value = true
  collNotice.value = null
  try {
    const res = await fetch('/api/member/collections', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name, intro: newColl.value.intro.trim() || null }),
    })
    const d = await res.json().catch(() => null)
    collNotice.value = { kind: res.ok ? 'ok' : 'err', text: d?.message ?? (res.ok ? '抽屉建好，去片里装货吧' : '建不起来') }
    if (res.ok) {
      newColl.value = { name: '', intro: '' }
      await loadMyCollections()
    }
  } catch {
    collNotice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    collBusy.value = false
  }
}

async function removeCollection(c) {
  if (collBusy.value) return
  // 措辞与抽屉正面页（CollectionDetailPage）的「拆除整个抽屉」一字不差：同一个活两个入口，不能两种说法。
  if (
    !(
      await requestConfirm({
        title: '拆除整个抽屉',
        sub: '货回货架不受损伤，但抽屉本身连同名字、简介一起消失',
        message: `拆掉抽屉「${c.name}」？${c.itemCount ?? 0} 件货会回到货架，想收回来得重建一座抽屉。`,
        confirmText: '拆掉它',
        cancelText: '再想想',
        danger: true,
        icon: 'trash',
      })
    )
  ) {
    return
  }
  collBusy.value = true
  collNotice.value = null
  try {
    const res = await fetch(`/api/member/collections/${c.id}`, { method: 'DELETE' })
    const d = await res.json().catch(() => null)
    collNotice.value = { kind: res.ok ? 'ok' : 'err', text: d?.message ?? (res.ok ? '已拆除' : '拆不掉') }
    if (res.ok) await loadMyCollections()
  } catch {
    collNotice.value = { kind: 'err', text: '分站后端没有应答' }
  } finally {
    collBusy.value = false
  }
}

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
  // 只有认得人才问历史：访客那一发必吃 401，白敲一下后端。
  if (member.value) await Promise.all([loadHistory(), loadMyCollections()])
  else {
    history.value = null
    myCollections.value = null
  }
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
  if (
    action === 'delete' &&
    !(
      await requestConfirm({
        title: '解绑这个账号',
        sub: who,
        message: '这个 B 站号从账号池里销掉，存的 Cookie 也跟着没，之后不再替分站出力；要再出力得重新粘一次。',
        confirmText: '解绑',
        cancelText: '再想想',
        danger: true,
        icon: 'out',
      })
    )
  ) {
    return
  }
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
          注册个站籍就能进门：逛分站、看片、发私信都认这个身份；B 站号只是可选挂件，进站后在个人空间扫码绑上。
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
        <!-- 馆藏壁：个人空间的门面——投稿与观看历史铺成一面可拖的画布墙，喂料三档记在本地 -->
        <WallWindow class="mb-4" />

        <!-- 身份卡 -->
        <section class="rounded-2xl border border-lineSoft bg-cardBg p-6 shadow-[0_1px_2px_rgba(0,0,0,0.03)]">
          <div class="flex flex-wrap items-center gap-5">
            <span class="relative block h-16 w-16 shrink-0">
              <img
                :src="member.faceUrl || defaultAvatar"
                :alt="member.nickname"
                referrerpolicy="no-referrer"
                class="h-full w-full rounded-full object-cover ring-2 ring-stationPink/20"
                @error="onAvatarError"
              />
              <CrownBadge
                v-if="member"
                :tier="member.role"
                mode="fit"
                class="pointer-events-none absolute inset-0 h-full w-full"
              />
            </span>

            <div class="min-w-0 flex-1">
              <div class="flex flex-wrap items-center gap-2">
                <h2 class="text-lg font-semibold text-inkMain">{{ displayName }}</h2>
                <span
                  v-if="member.role === 'owner'"
                  class="rounded-full border border-amber-400/60 bg-amber-50 px-2 py-0.5 text-[11px] font-medium text-amber-600 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-300"
                >
                  ♛ 站长
                </span>
                <span v-else class="flex items-center gap-1 rounded-full border border-slate-300/70 bg-slate-50 dark:border-white/10 dark:bg-white/5 dark:text-slate-400 px-2 py-0.5 text-[11px] text-slate-500">
                  <CrownBadge tier="member" class="h-3 w-3" />
                  成员
                </span>
              </div>
              <p class="mt-1.5 font-mono text-xs text-inkFaint">
                UID #{{ member.memberId }}
                · 上次登录 {{ friendlyTime(member.lastLoginAtUtc) }}
                <template v-if="member.note"> · 站长备注：{{ member.note }}</template>
              </p>
            </div>

            <div class="flex shrink-0 items-center gap-2">
              <RouterLink
                v-if="member.role === 'owner'"
                to="/admin"
                class="rounded-full border border-amber-400/60 bg-amber-50 px-4 py-1.5 text-xs text-amber-700 transition hover:bg-amber-100 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-300 dark:hover:bg-amber-500/20"
              >
                ♛ 进站长室
              </RouterLink>
              <button
                class="flex items-center gap-1.5 rounded-full border border-lineSoft px-4 py-1.5 text-xs text-inkSub transition hover:border-stationPink/40 hover:text-stationPink"
                aria-haspopup="dialog"
                @click="settingsOpen = true"
              >
                设置
                <span v-if="member.hasPassword === false" class="h-1.5 w-1.5 rounded-full bg-stationPink" title="还没设密码，点进来补一把门钥匙"></span>
              </button>
              <button
                class="rounded-full border border-lineSoft px-4 py-1.5 text-xs text-inkSub transition hover:border-stationPink/40 hover:text-stationPink"
                @click="doLogout"
              >
                退出登录
              </button>
            </div>
          </div>

          <!-- 入站统计 -->
          <div class="mt-6 grid grid-cols-3 gap-x-6 gap-y-4 border-t border-lineSoft/70 pt-5 sm:max-w-[560px]">
            <div class="flex flex-col gap-1">
              <b class="font-mono text-[26px] font-medium leading-none text-inkMain">{{ member.submitted ?? 0 }}</b>
              <span class="text-xs text-inkFaint">投稿进站</span>
            </div>
            <div class="flex flex-col gap-1">
              <b class="font-mono text-[26px] font-medium leading-none text-inkMain">{{ member.danmaku ?? 0 }}</b>
              <span class="text-xs text-inkFaint">发过的弹幕</span>
            </div>
            <div class="flex flex-col gap-1">
              <b class="font-mono text-[26px] font-medium leading-none text-inkMain">{{ fmtBytes(member.bytes) }}</b>
              <span class="text-xs text-inkFaint">替你占的库存</span>
            </div>
          </div>
        </section>

        <!-- 观看历史：记所有不只没看完（看完的也盖戳留档），卡面滚动展最近十档，点行进放映厅接着放 -->
        <section class="mt-4 rounded-2xl border border-lineSoft bg-cardBg p-6 shadow-[0_1px_2px_rgba(0,0,0,0.03)]">
          <div class="mb-4 flex items-center justify-between gap-3">
            <h3 class="text-sm font-semibold text-inkMain">观看历史</h3>
            <button
              class="rounded-full border border-lineSoft px-3.5 py-1.5 text-xs text-inkSub transition hover:border-rose-300 hover:text-rose-500 disabled:cursor-not-allowed disabled:opacity-40"
              :disabled="historyBusy || !history?.length"
              @click="clearHistory"
            >
              清空
            </button>
          </div>

          <!-- 固定高滚动区：十档叠起来比半屏还高，让列表自己内部滚，不把个人空间顶成无限长 -->
          <ul v-if="history?.length" ref="historyList" class="hist-list max-h-[440px] space-y-2 overflow-y-auto overflow-x-hidden pr-1">
            <li
              v-for="item in historyRecent"
              :key="item.videoId"
              :data-video-id="item.videoId"
              class="hist-row flex items-start gap-3 rounded-xl border border-lineSoft bg-[#f6f7f8] dark:bg-[#23262e] px-4 py-3"
              :class="{ 'hist-row-out': historyExiting.has(item.videoId) }"
            >
              <RouterLink :to="`/watch/${item.bvid}`" class="group flex min-w-0 flex-1 items-start gap-3 no-underline">
                <span class="relative block h-[76px] w-[124px] shrink-0 overflow-hidden rounded-lg bg-black/[0.06]">
                  <img
                    v-if="historyCover(item)"
                    :src="historyCover(item)"
                    :alt="item.title || item.bvid"
                    loading="lazy"
                    decoding="async"
                    referrerpolicy="no-referrer"
                    class="hist-cover h-full w-full object-cover"
                    @load="onCoverLoad"
                  />
                  <span v-else class="flex h-full w-full select-none items-center justify-center font-mono text-sm text-inkFaint" aria-hidden="true">
                    {{ (item.title || item.bvid || '?').slice(0, 1) }}
                  </span>
                </span>
                <span class="min-w-0 flex-1">
                  <span class="block truncate text-sm text-inkMain transition group-hover:text-stationPink" :title="item.title || item.bvid">
                    {{ item.title || item.bvid }}
                  </span>
                  <!-- 进度条：位置/时长的百分比（看完的也留档，条满格配「已看完」戳）-->
                  <span class="mt-2 block h-1 w-full overflow-hidden rounded-full bg-black/[0.08]">
                    <span class="block h-full rounded-full bg-stationPink/70" :style="{ width: `${progressPct(item)}%` }"></span>
                  </span>
                  <span class="mt-1.5 flex flex-wrap items-center gap-x-3 gap-y-0.5 font-mono text-[11px] text-inkFaint">
                    <span>看到 {{ historyClock(item.positionSec) }}</span>
                    <span>第 {{ item.pageIdx ?? 0 }} P</span>
                    <span>{{ friendlyTime(item.updatedAtUtc) }}</span>
                    <span v-if="progressPct(item) >= 95" class="rounded bg-emerald-500/10 px-1 text-emerald-600">已看完</span>
                  </span>
                </span>
              </RouterLink>
              <button
                class="shrink-0 rounded-lg border border-lineSoft bg-cardBg px-3 py-1 text-xs text-inkSub transition hover:border-rose-300 hover:text-rose-500 disabled:opacity-40"
                :disabled="historyBusy"
                title="从历史里抹掉这部（下次从头放）"
                @click="deleteHistory(item)"
              >
                删除
              </button>
            </li>
          </ul>
          <p v-else-if="history === null" class="py-8 text-center text-sm text-inkFaint">
            {{ historyBusy ? '正在翻你的历史……' : '历史没打开：分站后端没有应答' }}
          </p>
          <p v-else class="py-8 text-center text-sm text-inkFaint">
            还没有观看记录——去大厅抽一部，看过的都会在这里留档；看到一半走了它也会记得你的位子。
          </p>

          <p
            v-if="historyNotice"
            class="mt-3 text-xs leading-5"
            :class="historyNotice.kind === 'ok' ? 'text-emerald-600' : 'text-rose-500'"
          >
            {{ historyNotice.text }}
          </p>
          <p v-else class="mt-3 text-[11px] leading-4 text-inkFaint">
            历史按席位存，一人一片只占一行，看完的也留档；卡面滚动展最近 {{ HISTORY_SHOW_COUNT }} 条，退场只认手动删除与清空。
          </p>
        </section>

        <!-- 我的合集：成员自管的抽屉（建/拆在这，装货去放映厅的「合集」钮） -->
        <section class="mt-4 rounded-2xl border border-lineSoft bg-cardBg p-6 shadow-[0_1px_2px_rgba(0,0,0,0.03)]">
          <div class="mb-3 flex items-center justify-between gap-4">
            <h2 class="text-[15px] font-semibold text-inkMain">我的合集</h2>
            <RouterLink to="/collections" class="text-[12px] text-stationPink transition hover:opacity-80">逛全站的抽屉</RouterLink>
          </div>
          <ul v-if="myCollections?.length" class="space-y-2">
            <li v-for="c in myCollections" :key="c.id" class="flex items-center gap-3 rounded-xl border border-lineSoft px-3.5 py-2.5">
              <RouterLink :to="`/collection/${c.id}`" class="min-w-0 flex-1">
                <p class="truncate text-[13.5px] font-medium text-inkMain transition hover:text-stationPink">{{ c.name }}</p>
                <p class="mt-0.5 truncate text-[11.5px] text-inkFaint">
                  {{ c.itemCount }} 件 · {{ friendlyTime(c.createdAtUtc) }}建{{ c.intro ? ` · ${c.intro}` : '' }}
                </p>
              </RouterLink>
              <button class="shrink-0 text-[12px] text-inkFaint transition hover:text-rose-500 disabled:opacity-40" :disabled="collBusy" @click="removeCollection(c)">拆除</button>
            </li>
          </ul>
          <p v-else-if="myCollections === null" class="py-6 text-center text-sm text-inkFaint">{{ collBusy ? '正在翻你的抽屉……' : '抽屉清单没拉回来' }}</p>
          <p v-else class="py-6 text-center text-sm text-inkFaint">还没建过抽屉，下面起个头？</p>
          <div class="mt-3 flex items-center gap-2 border-t border-black/5 dark:border-white/10 pt-3">
            <input v-model="newColl.name" class="w-40 min-w-0 rounded-lg border border-black/15 dark:border-white/10 px-2.5 py-1.5 text-[12.5px] outline-none focus:border-stationPink" placeholder="抽屉名（30 字内）" maxlength="30" @keyup.enter="createCollection" />
            <input v-model="newColl.intro" class="min-w-0 flex-1 rounded-lg border border-black/15 dark:border-white/10 px-2.5 py-1.5 text-[12.5px] outline-none focus:border-stationPink" placeholder="简介，可留空" maxlength="60" @keyup.enter="createCollection" />
            <button class="shrink-0 rounded-lg bg-stationPink px-3 py-1.5 text-[12.5px] text-white transition hover:opacity-90 disabled:opacity-50" :disabled="collBusy || !newColl.name.trim()" @click="createCollection">建一个</button>
          </div>
          <p v-if="collNotice" class="mt-2 text-[12px]" :class="collNotice.kind === 'ok' ? 'text-emerald-600' : 'text-rose-500'">{{ collNotice.text }}</p>
        </section>

        <!-- 账号与登录设置弹层：登录方式与外部账号绑定两块收在这里，门口那枚「设置」钮开合；
             弹层就长在两块料原来的位置，没搬 DOM，展开时浮在全页之上 -->
        <div
          v-if="settingsOpen"
          class="fixed inset-0 z-50 flex items-center justify-center bg-black/45 p-4"
          role="dialog"
          aria-modal="true"
          aria-label="账号与登录设置"
          @click.self="settingsOpen = false"
        >
          <div class="max-h-[85vh] w-full max-w-[640px] overflow-y-auto rounded-2xl border border-lineSoft bg-cardBg shadow-2xl">
            <div class="sticky top-0 z-10 flex items-center justify-between border-b border-lineSoft/70 bg-cardBg px-6 py-4">
              <h3 class="text-sm font-semibold text-inkMain">账号与登录设置</h3>
              <button
                class="grid h-7 w-7 place-items-center rounded-full border border-lineSoft text-inkSub transition hover:border-stationPink/60 hover:text-stationPink"
                aria-label="收起设置"
                @click="settingsOpen = false"
              >
                ✕
              </button>
            </div>
            <div class="px-6 pb-6">

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
            <div class="rounded-xl border border-lineSoft bg-[#f6f7f8] dark:bg-[#23262e] px-4 py-3">
              <dt class="text-[11px] text-inkFaint">站名（账密登录用）</dt>
              <dd class="mt-1 truncate text-sm text-inkMain">
                <template v-if="member.username">{{ member.username }}</template>
                <template v-else>
                  <span class="text-inkFaint">未设——这是个纯扫码席位</span>
                  <p class="mt-1 text-[11px] leading-4 text-inkFaint">在下方补设密码，就能用密码开门（需先登录着）。</p>
                </template>
              </dd>
            </div>
            <div class="rounded-xl border border-lineSoft bg-[#f6f7f8] dark:bg-[#23262e] px-4 py-3">
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
                class="w-full rounded-xl border border-lineSoft bg-[#f6f7f8] dark:bg-[#23262e] dark:focus:bg-[#23262e] px-3.5 py-2 text-[13px] text-inkMain placeholder:text-inkFaint transition focus:border-stationPink focus:bg-white focus:outline-none focus:ring-2 focus:ring-stationPink/20 sm:w-40"
                :disabled="pwdBusy"
              />
              <input
                v-model="pwdForm.newPassword"
                type="password"
                autocomplete="new-password"
                placeholder="新密码（至少 8 位）"
                class="w-full rounded-xl border border-lineSoft bg-[#f6f7f8] dark:bg-[#23262e] dark:focus:bg-[#23262e] px-3.5 py-2 text-[13px] text-inkMain placeholder:text-inkFaint transition focus:border-stationPink focus:bg-white focus:outline-none focus:ring-2 focus:ring-stationPink/20 sm:w-44"
                :disabled="pwdBusy"
              />
              <input
                v-model="pwdForm.confirm"
                type="password"
                autocomplete="new-password"
                placeholder="再验一遍"
                class="w-full rounded-xl border border-lineSoft bg-[#f6f7f8] dark:bg-[#23262e] dark:focus:bg-[#23262e] px-3.5 py-2 text-[13px] text-inkMain placeholder:text-inkFaint transition focus:border-stationPink focus:bg-white focus:outline-none focus:ring-2 focus:ring-stationPink/20 sm:w-40"
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
              class="flex flex-wrap items-center gap-3 rounded-xl border border-lineSoft bg-[#f6f7f8] dark:bg-[#23262e] px-4 py-3"
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

          <p class="mt-3 text-[11px] leading-4 text-inkFaint">
            投稿、采集这些活得用你自己的 B 站号出力；哪个号哪天显示「登录已失效」，就点右上角「绑定 B 站」重新扫一次码补回来。
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
            </div>
          </div>
        </div>
      </template>
    </main>

    <!-- bind 实例：只管「绑定 B 站账号」的弹层，成功后喊这里重拉账号池 -->
    <LoginDialog instance-mode="bind" @bind-success="onBindSuccess" />
  </div>
</template>

<style scoped>
/* 历史行退场：删除先演收合淡出（高度/边距/内边距一起收），网络在背后跑；新行淡入不抢戏。
   入场用无 both 的动画，演完即卸，免得 fill 状态压住后续的退场过渡 */
.hist-row {
  max-height: 140px;
  overflow: hidden;
  transition:
    opacity 0.26s ease,
    transform 0.26s ease,
    max-height 0.3s ease,
    margin-top 0.3s ease,
    margin-bottom 0.3s ease,
    padding-top 0.3s ease,
    padding-bottom 0.3s ease,
    border-top-width 0.3s ease,
    border-bottom-width 0.3s ease;
  animation: hist-in 0.55s ease-out 0.12s backwards;
}
.hist-row-out {
  opacity: 0;
  transform: translateX(16px) scale(0.98);
  max-height: 0;
  margin-top: 0;
  margin-bottom: 0;
  padding-top: 0;
  padding-bottom: 0;
  border-top-width: 0;
  border-bottom-width: 0;
}
@keyframes hist-in {
  from {
    opacity: 0;
    transform: translateY(4px);
  }
  to {
    opacity: 1;
    transform: none;
  }
}
/* 关掉浏览器滚动锚定：内容变矮变高时它会自动改 scrollTop，与我们的手动钉位叠在一起就是「来回抽」 */
.hist-list {
  overflow-anchor: none;
}
/* 封面图先透着（底下的灰垫子顶位），load 到了再淡进来接管，杜绝图到的那一下白闪 */
.hist-cover {
  opacity: 0;
  transition: opacity 0.35s ease;
}
.hist-cover.is-on {
  opacity: 1;
}
@media (prefers-reduced-motion: reduce) {
  .hist-row {
    animation: none;
    transition: none;
  }
}
</style>
