<script setup>
import { computed, nextTick, onMounted, ref } from 'vue'
import TopBar from '../components/TopBar.vue'
import LoginDialog from '../components/LoginDialog.vue'
import CrownBadge from '../components/CrownBadge.vue'
import { member, loaded, loginDialogOpen, loginDialogMode, load as loadMember, logout as logoutMember } from '../memberStore'
import { requestConfirm } from '../confirmStore'
import { friendlyTime } from '../utils/time'
import { defaultAvatar, onAvatarError } from '../utils/avatar.js'
import WallWindow from '../components/WallWindow.vue'
import CollectionSettingsDialog from '../components/CollectionSettingsDialog.vue'

const busy = ref(false)
const notice = ref(null) // { kind, text }
const settingsOpen = ref(false) // 账号与登录设置弹层：登录方式 + 外部账号绑定两块收在这里

// ─── 「没绑 B 站号」那枚气泡：「外部账号绑定」藏在抽屉里，新人打开个人空间根本看不见，在头像右边递一句。
// 判据不拿 member.hasBilibili 当唯一依据：那个布尔出自 PickMemberAccountForDownload，SQL 带 expired = 0，
// 「绑过但号全失效」会被说成「没绑」。这里吃 /me 现成的 accounts 逐行看 expired。
const bindAccounts = computed(() => member.value?.accounts ?? [])
const bindNeed = computed(
  () => !!member.value && (bindAccounts.value.length === 0 || bindAccounts.value.every((a) => a.expired)),
)
// 两张脸分开：一个号都没绑过 / 绑过但全失效
const bindNever = computed(() => bindNeed.value && bindAccounts.value.length === 0)
// 「知道了」只收这一屏：不写本机记忆。切走再进个人空间（组件重挂）就又摆出来，直到号真绑上为止。
// 站长要的就是「一直显示」：新人漏绑的代价是稿件卡在门口出不去，多问一句不值钱。
// （原先往 localStorage 存了一笔 lbs.bindHint.v1.<席位> 「按过就不再烦人」，现已拆掉。）
const bindHintDismissed = ref(false)
const bindFlash = ref(false)
const bindSectionRef = ref(null)
let bindFlashTimer = 0

function dismissBindHint() {
  bindHintDismissed.value = true
}

// 「去绑定」：掀开抽屉、滚到那块、闪它一下。这一发也不记账（绑成后 onBindSuccess 重拉 accounts，气泡自己就消失）。
async function goBindFromHint() {
  settingsOpen.value = true
  await nextTick()
  bindSectionRef.value?.scrollIntoView({ behavior: 'smooth', block: 'center' })
  bindFlash.value = true
  if (bindFlashTimer) clearTimeout(bindFlashTimer)
  bindFlashTimer = setTimeout(() => (bindFlash.value = false), 1400)
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
const settingsFor = ref(null) // 非空 = 这座抽屉的「抽屉设置」弹层开着

function openCollectionSettings(c) {
  if (collBusy.value) return
  settingsFor.value = c
}

// 存完不自己接 brief，重拉一次清单：名、件数、私有角标一并跟着后端的事实归位
async function onCollectionSettingsSaved() {
  settingsFor.value = null
  await loadMyCollections()
}

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
  // 只有认得人才问抽屉：访客那一发必吃 401，白敲一下后端。
  if (member.value) await loadMyCollections()
  else {
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

// 账号池操作：传的是那一行的行 id（/me 的 accounts 每行都带 id；不是 accountMid —— 后端拿它与自己池里的 a.Id 核，
// 对不上就 404「这个号不在你的池里」，拿 mid 传进去只会打空，伤不到别人的行）。后端 message 原样展示。
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
              <div class="relative">
                <!-- 提醒挂在「设置」这颗钮上方：它指的就是你要点的那里，比在左边空地里开一块屏实在。
                     两张脸（一个号都没绑 / 绑了但全失效）；按「知道了」只收这一屏（切走再进来就又摆出来），按「去绑定」只掀抽屉。 -->
                <div
                  v-if="bindNeed && !bindHintDismissed"
                  class="absolute bottom-full right-0 z-20 mb-2.5 w-[272px] max-w-[72vw] rounded-2xl border border-stationPink/40 bg-cardBg p-3 text-left shadow-[0_2px_10px_rgba(0,0,0,0.06)]"
                >
                  <span class="absolute -bottom-[5px] right-7 h-2.5 w-2.5 rotate-45 border-b border-r border-stationPink/40 bg-cardBg"></span>
                  <p class="text-[13px] font-medium text-inkMain">{{ bindNever ? '还没绑 B 站号' : 'B 站号都登录失效了' }}</p>
                  <p class="mt-1 text-[11.5px] leading-5 text-inkSub">
                    {{
                      bindNever
                        ? '投稿、采集这些活得用你自己的号出力。绑一个，分站才能替你把它拉回来。'
                        : '补一次扫码，分站才拉得动投稿、采集这些活。'
                    }}
                  </p>
                  <div class="mt-2 flex items-center gap-2">
                    <button
                      class="rounded-full border border-stationPink px-3 py-1 text-xs text-stationPink transition hover:bg-stationPink hover:text-white"
                      @click="goBindFromHint"
                    >
                      去绑定
                    </button>
                    <button
                      class="rounded-full border border-lineSoft px-3 py-1 text-xs text-inkSub transition hover:border-stationPink/40 hover:text-stationPink"
                      @click="dismissBindHint"
                    >
                      知道了
                    </button>
                  </div>
                </div>
                <button
                  class="flex items-center gap-1.5 rounded-full border border-lineSoft px-4 py-1.5 text-xs text-inkSub transition hover:border-stationPink/40 hover:text-stationPink"
                  aria-haspopup="dialog"
                  @click="settingsOpen = true"
                >
                  设置
                  <span v-if="member.hasPassword === false" class="h-1.5 w-1.5 rounded-full bg-stationPink" title="还没设密码，点进来补一把门钥匙"></span>
                  <!-- 提醒被「知道了」关掉后退成一颗小点接着指路。颜色与上面那颗密码点拉开（琥珀 vs 粉），
                       两枚同色就分不清谁是谁了 -->
                  <span
                    v-if="bindNeed && bindHintDismissed"
                    class="h-1.5 w-1.5 rounded-full bg-amber-400"
                    title="B 站号还没绑 / 都失效了，点进来补一个"
                    aria-label="B 站号还没绑 / 都失效了，点进来补一个"
                  ></span>
                </button>
              </div>
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

        <!-- 我的合集：成员自管的抽屉（建/拆在这，装货去放映厅的「合集」钮） -->
        <section class="mt-4 rounded-2xl border border-lineSoft bg-cardBg p-6 shadow-[0_1px_2px_rgba(0,0,0,0.03)]">
          <div class="mb-3 flex items-center justify-between gap-4">
            <h2 class="text-[15px] font-semibold text-inkMain">我的合集</h2>
            <RouterLink to="/collections" class="text-[12px] text-stationPink transition hover:opacity-80">逛全站的抽屉</RouterLink>
          </div>
          <!-- 抽屉做成 B 站收藏夹那种「一叠照片」：封面底下垫两张白卡，从左下右上探出一点边、微微八字，
               鼠标扫过来才张开一点。装裱照逗站页 CollectionsPage，只是 16/10 改成 16/9（照站长给的基准图）。
               名字与副行仍旧在块下方，正方形里挤两行字不好看。 -->
          <ul v-if="myCollections?.length" class="grid grid-cols-2 gap-x-5 gap-y-8 sm:grid-cols-3 lg:grid-cols-4 xl:grid-cols-5">
            <li v-for="c in myCollections" :key="c.id" class="group/coll relative">
              <RouterLink :to="`/collection/${c.id}`" class="block">
                <div class="relative pt-2.5">
                  <!-- 垫卡：只露上缘一条，转角走八字；z 序靠 DOM 顺序，不需要 z-index -->
                  <span
                    class="pointer-events-none absolute inset-x-1 top-0 aspect-[16/9] -rotate-2 rounded-xl bg-white shadow-[0_3px_10px_-4px_rgba(0,0,0,0.22)] ring-1 ring-black/5 transition duration-300 group-hover/coll:-translate-y-0.5 group-hover/coll:-rotate-3 dark:bg-[#2a2d34] dark:ring-white/10"
                    aria-hidden="true"
                  ></span>
                  <span
                    class="pointer-events-none absolute inset-x-2 top-1 aspect-[16/9] rotate-1 rounded-xl bg-white shadow-[0_2px_8px_-4px_rgba(0,0,0,0.18)] ring-1 ring-black/5 transition duration-300 group-hover/coll:rotate-2 dark:bg-[#262a31] dark:ring-white/10"
                    aria-hidden="true"
                  ></span>
                  <div class="relative aspect-[16/9] overflow-hidden rounded-xl bg-[#f1f2f3] shadow-[0_2px_10px_-4px_rgba(0,0,0,0.25)] ring-1 ring-black/5 dark:bg-[#23262e] dark:ring-white/10">
                    <img
                      v-if="c.cover"
                      :src="c.cover"
                      :alt="c.name"
                      loading="lazy"
                      referrerpolicy="no-referrer"
                      class="h-full w-full object-cover transition duration-300 group-hover/coll:scale-105"
                    />
                    <!-- 集里还没到货（或货没到站）：占一块纯色，不摆破图 -->
                    <span v-else class="flex h-full flex-col items-center justify-center gap-1.5 text-inkFaint">
                      <svg viewBox="0 0 24 24" class="h-6 w-6" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round">
                        <path d="M4 7.5A2.5 2.5 0 0 1 6.5 5h3l1.7 2h6.3A2.5 2.5 0 0 1 20 9.5v8A2.5 2.5 0 0 1 17.5 20h-11A2.5 2.5 0 0 1 4 17.5v-10Z" />
                      </svg>
                      <span class="text-[11.5px]">先占着，等货入集</span>
                    </span>
                    <span class="absolute bottom-1.5 right-1.5 flex items-center gap-1 rounded bg-black/55 px-1.5 py-0.5 font-mono text-[10px] text-white/90 backdrop-blur">
                      <svg viewBox="0 0 24 24" class="h-2.5 w-2.5" fill="currentColor">
                        <path d="M4 6h4v14H4zM10 6h4v14h-4zM16.5 6.6l3.8 1-3.5 13.2-3.8-1z" />
                      </svg>
                      {{ c.itemCount }} 件
                    </span>
                    <span
                      v-if="c.visibility === 'private'"
                      class="absolute bottom-1.5 left-1.5 flex items-center gap-1 rounded bg-black/55 px-1.5 py-0.5 text-[10px] text-white/90 backdrop-blur"
                      title="只有你自己看得见这座抽屉"
                    >
                      <svg viewBox="0 0 24 24" class="h-2.5 w-2.5" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round">
                        <rect x="5" y="10.5" width="14" height="9.5" rx="2" />
                        <path d="M8 10.5V7.8a4 4 0 0 1 8 0v2.7" />
                      </svg>
                      私有
                    </span>
                    <span class="pointer-events-none absolute inset-0 bg-gradient-to-t from-black/40 via-transparent to-transparent opacity-0 transition duration-200 group-hover/coll:opacity-100"></span>
                  </div>
                </div>
              </RouterLink>
              <!-- 设置 / 拆除：共用同一套 hover 才浮右上角的语法并排站着（与后台卡阵那两同一脾气），
                   不 hover 时仍旧只剩一叠图 -->
              <div class="absolute right-1.5 top-3.5 z-10 flex items-center gap-1">
                <button
                  class="rounded-full border border-white/15 bg-black/60 px-2 py-0.5 text-[10.5px] text-white/80 opacity-0 backdrop-blur transition hover:border-stationPink/60 hover:text-stationPink focus-visible:opacity-100 group-hover/coll:opacity-100 disabled:opacity-40"
                  :disabled="collBusy"
                  title="改名字、简介与可见性"
                  aria-haspopup="dialog"
                  @click="openCollectionSettings(c)"
                >
                  设置
                </button>
                <button
                  class="rounded-full border border-white/15 bg-black/60 px-2 py-0.5 text-[10.5px] text-white/80 opacity-0 backdrop-blur transition hover:border-rose-400/60 hover:text-rose-300 focus-visible:opacity-100 group-hover/coll:opacity-100 disabled:opacity-40"
                  :disabled="collBusy"
                  title="拆除这个抽屉"
                  @click="removeCollection(c)"
                >
                  拆除
                </button>
              </div>
              <p class="mt-2.5 truncate text-[14px] font-medium text-inkMain transition group-hover/coll:text-stationPink" :title="c.name">{{ c.name }}</p>
              <p class="mt-1 truncate text-[11.5px] text-inkFaint" :title="c.intro || ''">
                {{ c.itemCount }} 件 · {{ friendlyTime(c.createdAtUtc) }}建{{ c.intro ? ` · ${c.intro}` : '' }}
              </p>
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

          <!-- 原先这里还有第二栏「B 站号（只出力）」，与下方「外部账号绑定」那块重复，已拆；
               剩下站名一栏独占整宽，不必再撑双列。 -->
          <dl class="grid gap-3">
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
        <section
          ref="bindSectionRef"
          class="mt-4 rounded-2xl border border-lineSoft bg-cardBg p-6 shadow-[0_1px_2px_rgba(0,0,0,0.03)]"
          :class="bindFlash ? 'ring-2 ring-stationPink/50 transition' : ''"
        >
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
              <!-- 这枚脸以前画不出来：/me 的 accounts 交付把 faceUrl 漏在投影里（数据早就在库里），现已补上。
                   B 站图床挡外链，no-referrer 不能省；早期粘 cookie 进来的号可能没脸，退回默认图。 -->
              <span class="block h-7 w-7 shrink-0">
                <img
                  :src="acc.faceUrl || defaultAvatar"
                  :alt="acc.accountName || `B站账号 ${acc.accountMid}`"
                  referrerpolicy="no-referrer"
                  class="h-full w-full rounded-full object-cover ring-1 ring-stationPink/20"
                  @error="onAvatarError"
                />
              </span>
              <div class="min-w-0 flex-1">
                <p class="flex items-center gap-2 text-sm text-inkMain">
                  <span class="shrink-0 rounded-md bg-[#fb7299]/10 px-1.5 py-0.5 text-[10px] font-medium text-[#fb7299]">B 站</span>
                  <span class="truncate">{{ acc.accountName || `B站账号 ${acc.accountMid}` }}</span>
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

    <!-- 抽屉设置：东家自己改名字 / 简介 / 公私，存完重拉我的合集 -->
    <CollectionSettingsDialog
      v-if="settingsFor"
      :collection="settingsFor"
      @saved="onCollectionSettingsSaved"
      @close="settingsFor = null"
    />
  </div>
</template>

