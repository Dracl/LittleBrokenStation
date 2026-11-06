<script>
// 模块级共享（真·单例）：多卡多页同开，本机赞的小本本只有一本；裸 Set + 版本号，
// 一次点赞只惊动渲染了赞数的卡（Set 方法不触发响应，读方先碰一下 likesTick）。
const dynLikes = (() => {
  try {
    const raw = window.localStorage.getItem('lbs-dyn-likes')
    return new Set(raw ? JSON.parse(raw) : [])
  } catch {
    return new Set()
  }
})()
const likesTick = shallowRef(0)
</script>

<script setup>
/* 动态条卡（11-06 从 DynamicsPage 抽出共享）：动态橱窗与 UP 空间「动态」tab 同一张卡，
 * 改卡面一处两边同亮。卡是自闭合的：评论抽屉、安利、灯箱、本机赞都住在卡里，
 * 宿主页面只管喂 d（/api/dynamics 的 item）和可选的 faceByMid（targets 兜底脸）。
 * 本机赞是模块级共享的（与评论区同脾气的小本本 localStorage）：多卡多页同开也同步点亮。 */
import { reactive, ref, shallowRef } from 'vue'
import DynamicVideoCard from './DynamicVideoCard.vue'
import ImageLightbox from './ImageLightbox.vue'
import AvatarImg from './AvatarImg.vue'
import LevelBadge from './LevelBadge.vue'
import ShareDialog from './ShareDialog.vue'
import BIcon from './BIcon.vue'
import { biDynForward, biDynComment, biDynLike, biDynLikeFill } from '../assets/bicons'
import { useRouter } from 'vue-router'
import { member, loginDialogOpen } from '../memberStore'
import { friendlyTime } from '../utils/time'
import CollapsibleRichText from './CollapsibleRichText.vue'
import { normUrl, inferImageBadge } from '../utils/dynpic'
import { defaultAvatar, onAvatarError } from '../utils/avatar.js'
import { faceFor } from '../utils/faceChoice.js'

const props = defineProps({
  d: { type: Object, required: true },
  // targets 名片兜底：动态行没存脸时按 mid 找（宿主有名单就递，没有就空对象，首字色块接住）
  faceByMid: { type: Object, default: () => ({}) },
  // 私信分享卡落地时粉框闪两秒（宿主页面管定位与计时，卡只负责亮）
  highlight: { type: Boolean, default: false },
})
const emit = defineEmits(['toast'])

const d = props.d

// ─── 本机赞：localStorage 小本本，模块级共享（站长定案「图个过瘾」，与评论区同脾气）───
// 不上报 B 站、不入服务器档，换设备清零；线稿/实心成对切换、点亮粉、赞数本机 +1。
function toggleDynLike() {
  const key = String(d.dynamicId)
  if (dynLikes.has(key)) dynLikes.delete(key)
  else dynLikes.add(key)
  try {
    window.localStorage.setItem('lbs-dyn-likes', JSON.stringify([...dynLikes]))
  } catch {
    /* 存不下就算了 */
  }
  likesTick.value += 1
}

function likedNow() {
  likesTick.value // 读一下版本号，点赞后各卡跟着重渲染
  return dynLikes.has(String(d.dynamicId))
}

function dynLikeCount() {
  const base = d.statLike ?? 0
  const n = base + (likedNow() ? 1 : 0)
  return n > 0 ? n : null // 零赞与采不到同口径：退「点赞」中文（官方风）
}

// ─── 卡型判定（B 站式）───
// 开播提醒（LIVE）已全站退场（站长 11-06 点单：开播短保，点开时早下播）：
// 过滤在宿主页的 keepFeed 里做，这张卡只剩 FORWARD / NORMAL 两态。
function isForward() {
  return (d.type || '').toUpperCase().includes('FORWARD')
}

// 转发条正文是「评语\n\n转发自 XX：\n\n原文」三段式（解析器定格），卡头归转发人、灰块装原文。
// 拆成三块：评语 / 灰块头的原作者名 / 原文本体（「转发自」行升格成带头像的行，就不再塞在正文里）。
function splitForward(text) {
  if (!text) return { comment: '', fromName: '', orig: '' }
  const i = text.indexOf('\n\n转发自')
  if (i < 0) return { comment: text, fromName: '', orig: '' }
  const rest = text.slice(i + 2)
  const j = rest.indexOf('：')
  if (j < 0) return { comment: text.slice(0, i), fromName: '', orig: rest }
  return {
    comment: text.slice(0, i),
    fromName: rest.slice(0, j).replace(/^转发自\s*/, ''),
    orig: rest.slice(j + 1).trim(),
  }
}

// ─── 评论抽屉：每条评价现拉现展，收起来不拆数据（二次展开不重拉）───
const st = reactive({ open: false, loading: false, loaded: false, fetched: false, fetchedAtUtc: null, refreshing: false, total: 0, items: [], expanded: {}, emotes: {}, emoteMeta: {} })

async function loadComments() {
  if (st.loaded || st.loading) return
  st.loading = true
  try {
    const res = await fetch(`/api/dynamics/${encodeURIComponent(d.dynamicId)}/comments`)
    if (res.ok) {
      const data = await res.json()
      st.items = data.items ?? []
      st.total = data.total ?? 0
      st.fetched = !!data.fetched
      st.fetchedAtUtc = data.fetchedAtUtc ?? null
      st.emotes = data.emotes ?? {}
      st.emoteMeta = data.emoteMeta ?? {}
      st.loaded = true
    }
  } catch {
    /* 评论拉砸不阁主卡，收起重开可重试 */
  } finally {
    st.loading = false
  }
}

// 抽屉刷新钮：催后端向 B 站补采这条动态的评论（rpid 增量 upsert，新楼插旧楼留），成事重拉回显。
// 补采要登录（与安利同权）；未登录不白跑，直接弹登录窗。
async function refreshComments() {
  if (!member.value) {
    loginDialogOpen.value = true
    return
  }
  if (st.refreshing) return
  st.refreshing = true
  try {
    const res = await fetch(`/api/dynamics/${encodeURIComponent(d.dynamicId)}/comments/refresh`, { method: 'POST' })
    const data = await res.json().catch(() => null)
    if (res.ok) {
      st.loaded = false
      await loadComments()
      emit('toast', '评论补采到货，已刷新')
    } else {
      emit('toast', data?.message ?? '补采没成，等巡捕下轮再试')
    }
  } catch {
    emit('toast', '分站后端没有应答')
  } finally {
    st.refreshing = false
  }
}

function toggleComments() {
  st.open = !st.open
  if (st.open && !st.loaded) loadComments()
}

function cmtCount() {
  return d.statComment ?? (st.loaded ? st.total : null)
}

// ─── 安利分享：登录门槛在按钮上就把关，未登录弹登录窗 ───
const shareFor = ref(null)
function openShare() {
  if (!member.value) {
    loginDialogOpen.value = true
    return
  }
  shareFor.value = { type: 'dynamic', dynamicId: d.dynamicId, authorName: d.authorName, authorFaceUrl: authorFace(), text: d.text }
}
function onShared(message) {
  shareFor.value = null
  emit('toast', message || '已塞进对方信箱')
}

// B 站图格规矩：多图才进格（四图 2×2、其余三列），格子一律正方形 1:1（B 站首页动态同款）；
// 单图不裁——直接按原比例翻出来（B 站时间线同款），格宽上限 340px，w-fit 让角标贴着图而不是格子。
function picsLayout(count) {
  if (count === 1) return 'w-fit'
  if (count === 4) return 'grid grid-cols-2 gap-1 max-w-[340px]'
  return 'grid grid-cols-3 gap-1 max-w-[500px]'
}

// 格子里的取景：普通图居中，长图（高>宽×2，与 picBadge 那条判据同源）从顶上取一段引子。
// 不写 object-position 的话浏览器默认居中裁：那条 1000×8000 的特典长图会露出正中间的问号特典，
// 而不是像 B 站那样露出最上面的「5 周年新衣回」大字（站长 11-02 比对两张图抓到的）。
function gridPicStyle(pic) {
  const w = pic?.width ?? pic?.Width ?? 0
  const h = pic?.height ?? pic?.Height ?? 0
  return w > 0 && h > w * 2 ? { objectPosition: 'top center' } : undefined
}

// 单图样式：有宽高就按比例预留盒子（防跳动）；超长图（高>宽×2，即「长图」角标货）
// 定高 450px 顶部对齐裁个引子，点开灯箱看全图；宽高缺失的裸串回退 contain 装，不硬裁。
function singlePicStyle(pic) {
  const w = pic?.width ?? pic?.Width ?? 0
  const h = pic?.height ?? pic?.Height ?? 0
  if (w > 0 && h > 0 && h > w * 2) {
    return { width: 'min(340px, 100%)', height: '450px', objectFit: 'cover', objectPosition: 'top' }
  }
  if (w > 0 && h > 0) {
    return { width: `${Math.min(340, w)}px`, maxWidth: '100%', aspectRatio: `${w} / ${h}` }
  }
  return { maxWidth: '340px', maxHeight: '450px', objectFit: 'contain', background: 'rgb(var(--line-soft))' }
}

// 九宫格取图：端点吐 pics 对象数组 [{url,width,height}] + picsLocal 同序本地数组（单图失败为 null）。
// 本地优先、远程兑底走 normUrl（协议相对链补 https）；角标照 dynamic-bot：.gif → 动图，高>宽×2 → 长图。
// 库里曾有 PascalCase 脏行（首轮 bug），读时两形都认，不让老货空图。
function picUrl(pic) {
  if (typeof pic === 'string') return pic
  return pic?.url || pic?.Url || ''
}
function picSrc(i) {
  return d.picsLocal?.[i] || normUrl(picUrl(d.pics?.[i])) || ''
}
function picBadge(pic) {
  return inferImageBadge(picUrl(pic), pic?.width ?? pic?.Width, pic?.height ?? pic?.Height)
}

// 图片灯箱：九宫格（正常卡+转发灰块）与评论配图共用公共 ImageLightbox，同组多图左右翻。
// 点开设本地原图（picsLocal/pictures 已本地优先，不走 coverThumb 缩图）；失败空位剔除但点击下标同步修正。
const lightbox = ref(null) // { images: string[], index: number } | null
function openZoom(rawList, rawIndex) {
  const images = []
  let click = 0
  ;(rawList ?? []).forEach((u, idx) => {
    if (!u) return
    if (idx < rawIndex) click++
    images.push(u)
  })
  if (!images.length) return
  lightbox.value = { images, index: Math.min(click, images.length - 1) }
}
function openPicZoom(i) {
  openZoom((d.pics ?? []).map((_, pi) => picSrc(pi)), i)
}

const avatarPalette = ['bg-stationPink/70', 'bg-emerald-500/70', 'bg-sky-500/70', 'bg-amber-500/70', 'bg-violet-500/70']
function avatarClass(mid) {
  return avatarPalette[Math.abs(Number(mid ?? 0)) % avatarPalette.length]
}

function authorFace() {
  // 本机钦选压过服务器脸（小本本住 utils/faceChoice，全员可换只改本机）
  return faceFor(d.authorMid, d.authorFaceUrl || props.faceByMid[d.authorMid] || '')
}

function origAuthorFace() {
  return faceFor(d.origAuthorMid, d.origAuthorFace) || defaultAvatar
}

// 脸与名字是进 UP 空间的门（站长 11-06 点名：动态页点头像/名字该落到 TA 的站内空间）；
// 没 mid 的人（转发原主偶尔查无此号）点了不跳，不送进空页吃「找不到人」。
const router = useRouter()
function goUpSpace(mid) {
  if (mid != null && Number(mid) > 0) router.push(`/up/${mid}`)
}
</script>

<template>
  <article
    :data-dyn="d.dynamicId"
    class="relative mb-4 rounded-lg border bg-cardBg pb-4 pr-4 transition-shadow duration-500"
    :class="['border-lineSoft', highlight ? 'ring-2 ring-stationPink/60' : '']"
    style="padding-left: 88px"
  >
    <!-- 头像独柱（官方 bili-dyn-item-draw 布局）：88px 左栏居中挂脸，正文/底栏都从 88px 起对齐；点头脸进 TA 的站内空间 -->
    <div class="absolute left-0 top-0 flex h-[88px] w-[88px] items-center justify-center">
      <AvatarImg
        :face="authorFace()"
        :name="d.authorName || String(d.authorMid ?? '?')"
        :class="['h-12 w-12 shrink-0 cursor-pointer rounded-full object-cover text-base text-white transition hover:brightness-105', avatarClass(d.authorMid)]"
        :title="d.authorName ? `去 ${d.authorName} 的站内空间` : ''"
        @click="goUpSpace(d.authorMid)"
      />
    </div>
    <!-- 头行：昵称 + 时间（官高 62px）；视频卡右上挂内外签 -->
    <header class="flex min-h-[62px] items-start justify-between pt-4">
      <span class="min-w-0 flex-1">
        <span
          class="block cursor-pointer truncate text-[15px] font-semibold leading-5 text-inkMain transition-colors hover:text-stationPink"
          :title="d.authorName ? `去 ${d.authorName} 的站内空间` : ''"
          @click="goUpSpace(d.authorMid)"
        >{{ d.authorName || `B站号 ${d.authorMid}` }}</span>
        <span class="mt-[3px] block text-[13px] leading-[18px] text-inkFaint">{{ friendlyTime(d.publishedAtUtc) }}</span>
      </span>
      <span
        v-if="d.bvid"
        class="shrink-0 rounded-md px-2 py-0.5 text-xs"
        :class="d.inStation
          ? 'border border-stationPink/50 bg-stationPink/5 text-stationPink'
          : 'border border-lineSoft bg-[#f6f7f8] dark:bg-[#23262e] text-inkSub'"
      >{{ d.inStation ? '站内可看' : '去 B 站看 ↗' }}</span>
    </header>

    <!-- 转发条：评语 + 灰块内嵌原文（B 站同款）；灰块头挂原作者的小脸 -->
    <template v-if="isForward()">
      <CollapsibleRichText v-if="splitForward(d.text).comment" :text="splitForward(d.text).comment" :emotes="d.bodyEmotes" content-class="mb-[8px] whitespace-pre-wrap break-words text-[15px] leading-relaxed text-inkMain" />
      <div class="rounded-md bg-[#f6f7f8] dark:bg-[#23262e] p-3">
        <div v-if="d.origAuthorName || splitForward(d.text).fromName" class="mb-2 flex items-center gap-1.5">
          <img
            :src="origAuthorFace()"
            referrerpolicy="no-referrer"
            loading="lazy"
            class="h-[18px] w-[18px] shrink-0 cursor-pointer rounded-full object-cover transition hover:brightness-105"
            alt=""
            :title="d.origAuthorName ? `去 ${d.origAuthorName} 的站内空间` : ''"
            @click="goUpSpace(d.origAuthorMid)"
            @error="onAvatarError"
          />
          <span
            class="cursor-pointer text-xs text-inkSub transition-colors hover:text-stationPink"
            @click="goUpSpace(d.origAuthorMid)"
          >转发自 {{ d.origAuthorName || splitForward(d.text).fromName }}</span>
        </div>
        <!-- 原文无文字时不再咬死「已被删」：有视频/图体就只留卡（删了文字不等于删了货），
             连体都没有才是真删了，给个不带审讯味道的提示。 -->
        <CollapsibleRichText
          v-if="splitForward(d.text).orig || !(d.bvid || d.pics?.length)"
          :text="splitForward(d.text).orig || '原文没留文字，只转发了内容本身'"
          :emotes="d.bodyEmotes"
          content-class="whitespace-pre-wrap break-words text-sm leading-relaxed text-inkSub"
        />
        <DynamicVideoCard v-if="d.bvid" :d="d" class="mt-2" />
        <div v-else-if="d.pics?.length" :class="['mt-2', picsLayout(d.pics.length)]">
          <div v-for="(pic, i) in d.pics" :key="i" class="relative">
            <img
              :src="picSrc(i)"
              referrerpolicy="no-referrer"
              loading="lazy"
              :class="d.pics.length === 1 ? 'block cursor-zoom-in rounded' : 'aspect-square w-full cursor-zoom-in rounded object-cover'"
              :style="d.pics.length === 1 ? singlePicStyle(pic) : gridPicStyle(pic)"
              :alt="`原动态配图 ${i + 1}`"
              @error="$event.target.style.display = 'none'"
              @click="openPicZoom(i)"
            />
            <span v-if="picBadge(pic)" class="absolute bottom-1 right-1 rounded bg-black/50 px-1 py-px text-[10px] leading-none text-white">{{ picBadge(pic) }}</span>
          </div>
        </div>
      </div>
    </template>

    <!-- 平常条：正文 → 图格 → 视频卡 -->
    <template v-else>
      <CollapsibleRichText v-if="d.text" :text="d.text" :emotes="d.bodyEmotes" content-class="mb-[8px] whitespace-pre-wrap break-words text-[15px] leading-relaxed text-inkMain" />
      <div v-if="d.pics?.length" :class="['mb-[8px]', picsLayout(d.pics.length)]">
        <div v-for="(pic, i) in d.pics" :key="i" class="relative">
          <img
            :src="picSrc(i)"
            referrerpolicy="no-referrer"
            loading="lazy"
            :class="d.pics.length === 1 ? 'block cursor-zoom-in rounded' : 'aspect-square w-full cursor-zoom-in rounded object-cover'"
            :style="d.pics.length === 1 ? singlePicStyle(pic) : gridPicStyle(pic)"
            :alt="`动态配图 ${i + 1}`"
            @error="$event.target.style.display = 'none'"
            @click="openPicZoom(i)"
          />
          <span v-if="picBadge(pic)" class="absolute bottom-1 right-1 rounded bg-black/50 px-1 py-px text-[10px] leading-none text-white">{{ picBadge(pic) }}</span>
        </div>
      </div>
      <DynamicVideoCard v-if="d.bvid" :d="d" class="mt-1" />
    </template>

    <!-- 底栏：三项均分全宽，内容统一从各自 slot 左缘起笔（站长选定的 B 钉法，兼取官方左向款灵魂），
         图标 18px，hover 官蓝；图标直采 SVGIconNext BDC 细线系（axes stroke=1）。转发走私信安利，评论开抽屉，赞本机点亮（与评论区同脾气，刷新不丢）。 -->
    <footer class="mt-3 flex items-center">
      <button class="flex flex-1 items-center rounded-md py-1 text-[13px] text-inkSub transition hover:text-[#00a1d6]" aria-label="安利给分站朋友" @click="openShare()">
        <BIcon :meta="biDynForward" class="mr-1 h-[18px] w-[18px] shrink-0" />
        <span>{{ d.statForward > 0 ? d.statForward : '转发' }}</span>
      </button>
      <button class="flex flex-1 items-center rounded-md py-1 text-[13px] text-inkSub transition hover:text-[#00a1d6]" @click="toggleComments()">
        <BIcon :meta="biDynComment" class="mr-1 h-[18px] w-[18px] shrink-0" />
        <span>{{ cmtCount() > 0 ? cmtCount() : '评论' }}</span>
      </button>
      <button
        class="flex flex-1 items-center rounded-md py-1 text-[13px] transition"
        :class="likedNow() ? 'text-stationPink' : 'text-inkSub hover:text-[#00a1d6]'"
        aria-label="点赞"
        @click="toggleDynLike()"
      >
        <BIcon :meta="likedNow() ? biDynLikeFill : biDynLike" class="mr-1 h-[18px] w-[18px] shrink-0" />
        <span>{{ dynLikeCount() ?? '点赞' }}</span>
      </button>
    </footer>

    <!-- 评论抽屉：热评与子回复都吃分站留档，名字粉红、时间淡灰，B 站同款排布；
         站长定案：抵掉卡的 88px 左栏，评论整组（头像+文字）左对齐卡缘，不要竖线 -->
    <section v-if="st.open" class="mt-1 -ml-[88px] border-t border-lineSoft/70 pt-3">
      <div class="ml-[16px] pl-[14px]">
      <p v-if="st.loading" class="py-3 text-center text-xs text-inkFaint">评论拉取中…</p>
      <p v-else-if="!st.items.length" class="py-3 text-center text-xs text-inkFaint">
        {{ st.fetched ? '这条动态还没人开口' : '评论还没巡到货，等巡捕下轮补' }}
      </p>
      <template v-else>
        <p class="mb-2 flex flex-wrap items-center gap-x-2 text-[11px] text-inkFaint">
          <span>评论同步自 B 站，分站只读留档 · 共 {{ st.total }} 条</span>
          <span v-if="st.fetchedAtUtc">· 留档 {{ friendlyTime(st.fetchedAtUtc) }}</span>
          <button
            class="inline-flex items-center gap-0.5 rounded px-1 py-0.5 text-[11px] text-stationBlue transition hover:opacity-80 disabled:opacity-40"
            :disabled="st.refreshing"
            :title="member ? '催分站向 B 站补采这条的评论（新楼增量入库）' : '登录后才能催补采'"
            @click="refreshComments()"
          >
            <svg viewBox="0 0 24 24" class="h-3 w-3 shrink-0" :class="st.refreshing ? 'animate-spin' : ''" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M20 11.5A8 8 0 1 0 18.4 16M20 5.5V11.5h-6" stroke-linecap="round" stroke-linejoin="round" />
            </svg>
            {{ st.refreshing ? '补采中…' : '刷新' }}
          </button>
        </p>
        <div
          v-for="(top, ti) in st.items"
          :key="top.rpid"
          class="flex items-start gap-2.5"
          :class="[ti > 0 ? 'border-t border-lineSoft/60 pt-4' : '', 'mb-4']"
        >
          <AvatarImg :face="top.face" :face-local="top.faceLocal" :name="top.name" class="mt-px h-8 w-8 shrink-0 rounded-full object-cover bg-stationPink/10 text-xs text-stationPink" />
          <div class="min-w-0 flex-1">
            <!-- 主楼与楼中楼同骨架（站长定案）：名字行不带时间，时间与赞数同站行动行 -->
            <div class="flex items-center gap-1.5">
              <span class="truncate text-[12.5px] font-medium text-stationPink">{{ top.name || '无名网友' }}</span>
              <LevelBadge :level="top.level" :size="10" class="shrink-0" />
            </div>
            <CollapsibleRichText :text="top.message" :emotes="st.emotes" :emote-meta="st.emoteMeta" content-class="mt-2.5 whitespace-pre-wrap break-words text-[13.5px] leading-relaxed text-inkMain" />
            <!-- 评论配图：端点已本地优先合并；单图 200px 宽、多图小方格（照视频侧 CommentSection 观感） -->
            <div v-if="top.pictures?.length" class="mt-1 flex flex-wrap gap-1">
              <img
                v-for="(pic, pi) in top.pictures"
                :key="pi"
                :src="pic"
                referrerpolicy="no-referrer"
                loading="lazy"
                class="cursor-zoom-in rounded object-cover"
                :class="top.pictures.length === 1 ? 'h-auto w-[200px] max-w-full' : 'h-20 w-20'"
                alt=""
                @error="$event.target.style.display = 'none'"
                @click="openZoom(top.pictures, pi)"
              />
            </div>
            <div class="mt-2.5 flex items-center gap-3 text-[11.5px] text-inkFaint">
              <span>{{ friendlyTime(top.ctimeUtc) }}</span>
              <span v-if="top.ipLocation">IP属地：{{ top.ipLocation }}</span>
              <span v-if="top.like" class="flex items-center gap-1"><BIcon :meta="biDynLike" class="h-3.5 w-3.5 shrink-0" />{{ top.like }}</span>
            </div>
            <!-- 子回复随抓取定格，缩进挂在楼下；pl 补回子楼头像的自身宽（h-5），名字与主楼正文同轴 -->
            <div v-if="top.replies?.length" class="mt-3 space-y-3.5 pl-5">
              <template v-for="(sub, si) in top.replies" :key="sub.rpid">
                <div
                  v-if="top.replies.length <= 3 || st.expanded[top.rpid] || si < 3"
                  class="flex items-start gap-2"
                >
                  <AvatarImg :face="sub.face" :face-local="sub.faceLocal" :name="sub.name" class="mt-px h-5 w-5 shrink-0 rounded-full object-cover bg-[#f1f2f3] dark:bg-white/10 text-[9px] text-inkFaint" />
                  <div class="min-w-0 flex-1">
                    <!-- 官方楼中楼骨架（站长照 B 站图定案）：名字行不带时间，时间落到正文下的行动行，楼层间拉浅分隔线 -->
                    <div class="flex items-center gap-1.5">
                      <span class="truncate text-[12px] font-medium text-stationPink">{{ sub.name || '无名网友' }}</span>
                      <LevelBadge :level="sub.level" :size="9.5" class="shrink-0" />
                    </div>
                    <CollapsibleRichText :text="sub.message" :emotes="st.emotes" :emote-meta="st.emoteMeta" content-class="mt-2.5 whitespace-pre-wrap break-words text-[13px] leading-relaxed text-inkMain" />
                    <div v-if="sub.pictures?.length" class="mt-1 flex flex-wrap gap-1">
                      <img
                        v-for="(pic, pi) in sub.pictures"
                        :key="pi"
                        :src="pic"
                        referrerpolicy="no-referrer"
                        loading="lazy"
                        class="cursor-zoom-in rounded object-cover"
                        :class="sub.pictures.length === 1 ? 'h-auto w-[200px] max-w-full' : 'h-16 w-16'"
                        alt=""
                        @error="$event.target.style.display = 'none'"
                        @click="openZoom(sub.pictures, pi)"
                      />
                    </div>
                    <div class="mt-2.5 flex items-center gap-3 text-[10.5px] text-inkFaint">
                      <span>{{ friendlyTime(sub.ctimeUtc) }}</span>
                      <span v-if="sub.ipLocation">IP属地：{{ sub.ipLocation }}</span>
                      <span v-if="sub.like" class="flex items-center gap-1"><BIcon :meta="biDynLike" class="h-3 w-3 shrink-0" />{{ sub.like }}</span>
                    </div>
                  </div>
                </div>
              </template>
              <button
                v-if="top.replies.length > 3 && !st.expanded[top.rpid]"
                class="text-[12px] text-inkSub transition hover:text-stationPink"
                @click="st.expanded[top.rpid] = true"
              >
                共 {{ top.replies.length }} 条回复，点击查看
              </button>
            </div>
          </div>
        </div>
      </template>
      </div>
    </section>

    <ImageLightbox v-if="lightbox" :images="lightbox.images" :index="lightbox.index" @close="lightbox = null" />
    <ShareDialog v-if="shareFor" :target="shareFor" @close="shareFor = null" @done="onShared" />
  </article>
</template>
