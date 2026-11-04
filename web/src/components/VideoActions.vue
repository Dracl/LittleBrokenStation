<script setup>
// B 站式互动栏：官方图标（BIcon + bicons.js）+ 数字。站长定案：点赞/投币/收藏三枚纯展示（不发请求不落库）；
// 分享一枚已激活（站内安利走私信，弹 ShareDialog，hover 蓝 #00a1d6 是本轮站长亲定的 B 站分享蓝）。
// 点击反馈：四枚各有 CSS keyframes 小动画（弹跳/硬币下落/星星 pop/轻缩放）；like/coin/fav 点击顺带点亮成
// 站内装饰色（粉/金黄）、再点复位——纯前端装饰态，不上报 B 站；点亮按视频记入 localStorage（与动态/评论区同脾气），刷新不丢。
// 数字全部来自 B 站 view 接口真采快照（到站时入库），采不到显示「—」，绝不编数；本机点亮时赞数 +1（同评论区口径）。
import { onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import BIcon from './BIcon.vue'
import { biLike, biCoin, biFav, biShare } from '../assets/bicons'
import { useHoverGate } from '../utils/useHoverGate.js'

const props = defineProps({
  stat: { type: Object, default: null }, // { like, coin, favorite, share, reply }
  storeKey: { type: String, default: '' }, // 本机点亮存档的每支视频键（传 bvid）；不给则退回刷新即失
})
const emit = defineEmits(['share', 'sharePoster'])

// 动画触发：先落再点，同名 class 连点也能重放；animationend 后自动卸架。
const anim = reactive({ like: false, coin: false, fav: false, share: false })
// 装饰性点亮（点赞/投币/收藏）：只改颜色不上报，点击切换；有 storeKey 时落 localStorage 本机小本本。
const lit = reactive({ like: false, coin: false, fav: false })

const LIT_KEY = 'lbs-action-lit'

function litMap() {
  try {
    return JSON.parse(window.localStorage.getItem(LIT_KEY) || '{}')
  } catch {
    return {}
  }
}

function hydrate() {
  const m = litMap()
  for (const k of ['like', 'coin', 'fav']) lit[k] = !!(props.storeKey && m[`${props.storeKey}:${k}`])
}
hydrate()
// 同一路由组件切集复用（/watch/:bvid 换视频不重新挂载），换视频时重新对齐本机存档
watch(() => props.storeKey, hydrate)

function tap(key) {
  anim[key] = false
  requestAnimationFrame(() => {
    anim[key] = true
  })
  if (key in lit) {
    lit[key] = !lit[key]
    if (props.storeKey) {
      try {
        const m = litMap()
        if (lit[key]) m[`${props.storeKey}:${key}`] = true
        else delete m[`${props.storeKey}:${key}`]
        window.localStorage.setItem(LIT_KEY, JSON.stringify(m))
      } catch {
        /* 存不下就算了 */
      }
    }
  }
  if (key === 'share') {
    // 点击本体仍是「转发私信」主路（键盘 Enter / 触屏没有 hover，都从这一走）
    closeShare()
    emit('share')
  }
}

// ─── 分享钮的悬浮窗（站长 11-04 定稿）───
// hover 弹两颗：「✉ 转发私信」= 点本体同一件事，「🖼 分享海报」= 开海报调参台。
// 延时吃全站那一把尺子（utils/useHoverGate：进 120ms / 出 260ms，触屏整个退化成只认点击），
// 面板自己吃 mouseenter/mouseleave：鼠标从钮滑进面板那 8px 缝隙由 260ms 收尾补上，不掉。
// 手感照顶栏铃铛预览卡：Teleport 到 body、白底细边阴影、贴钮下沿 8dp。
const SHARE_PANEL_W = 216
const shareOpen = ref(false)
const shareBtn = ref(null)
const sharePanelStyle = ref({})
const shareHover = useHoverGate(shareOpen, { onOpen: () => placeSharePanel() })

function placeSharePanel() {
  const el = shareBtn.value
  if (!el) return
  const r = el.getBoundingClientRect()
  const left = Math.max(8, Math.min(r.left, window.innerWidth - SHARE_PANEL_W - 8))
  const top = Math.min(r.bottom + 8, window.innerHeight - 108)
  sharePanelStyle.value = { left: `${Math.round(left)}px`, top: `${Math.round(top)}px` }
}

function closeShare() {
  shareOpen.value = false
  shareHover.cancel()
}

function pickShare(kind) {
  closeShare()
  if (kind === 'poster') emit('sharePoster')
  else emit('share')
}

// 页面一滚面板就与钮脱钩（fixed 坐标是开那一刻量的），不如当场收了
function onViewportChange() {
  if (shareOpen.value) closeShare()
}
onMounted(() => {
  window.addEventListener('scroll', onViewportChange, true)
  window.addEventListener('resize', onViewportChange)
})
onBeforeUnmount(() => {
  window.removeEventListener('scroll', onViewportChange, true)
  window.removeEventListener('resize', onViewportChange)
})

function likeShown() {
  if (props.stat?.like == null) return null
  return props.stat.like + (lit.like ? 1 : 0)
}

function fmt(n) {
  if (n == null) return '—'
  if (n >= 10000) return (n / 10000).toFixed(1).replace(/\.0$/, '') + '万'
  return n.toLocaleString('zh-CN')
}
</script>

<template>
  <div class="flex items-center gap-10 py-2 text-base text-inkSub">
    <span
      class="group flex cursor-pointer items-center gap-2"
      @click="tap('like')"
    >
      <BIcon
        :meta="biLike"
        class="h-[25px] w-[25px] shrink-0 transition duration-200"
        :class="[lit.like ? 'text-[#fb7299]' : 'text-[#757575] group-hover:text-[#fb7299]', anim.like ? 'va-anim-like' : '']"
        @animationend="anim.like = false"
      />
      <span class="leading-none tabular-nums">{{ fmt(likeShown()) }}</span>
    </span>
    <span
      class="group flex cursor-pointer items-center gap-2"
      @click="tap('coin')"
    >
      <BIcon
        :meta="biCoin"
        class="h-[25px] w-[25px] shrink-0 transition duration-200"
        :class="[lit.coin ? 'text-[#fb7299]' : 'text-[#757575] group-hover:text-[#fb7299]', anim.coin ? 'va-anim-coin' : '']"
        @animationend="anim.coin = false"
      />
      <span class="leading-none tabular-nums">{{ fmt(stat?.coin) }}</span>
    </span>
    <span
      class="group flex cursor-pointer items-center gap-2"
      @click="tap('fav')"
    >
      <BIcon
        :meta="biFav"
        class="h-[25px] w-[25px] shrink-0 transition duration-200"
        :class="[lit.fav ? 'text-[#ffe138]' : 'text-[#757575] group-hover:text-[#fb7299]', anim.fav ? 'va-anim-fav' : '']"
        @animationend="anim.fav = false"
      />
      <span class="leading-none tabular-nums">{{ fmt(stat?.favorite) }}</span>
    </span>
    <button
      ref="shareBtn"
      type="button"
      class="group flex items-center gap-2 transition hover:text-[#00a1d6]"
      aria-label="安利给分站朋友"
      aria-haspopup="menu"
      :aria-expanded="shareOpen ? 'true' : 'false'"
      @click="tap('share')"
      @mouseenter="shareHover.onEnter()"
      @mouseleave="shareHover.onLeave()"
    >
      <BIcon
        :meta="biShare"
        class="h-[25px] w-[25px] shrink-0 transition duration-200"
        :class="['text-[#757575] group-hover:text-[#00a1d6]', anim.share ? 'va-anim-share' : '']"
        @animationend="anim.share = false"
      />
      <span class="leading-none tabular-nums">{{ fmt(stat?.share) }}</span>
    </button>

    <!-- 分享悬浮窗：Teleport 到 body（互动栏那层带 border 与 overflow 风险，浮层不跟它同层），
         白底细边阴影、圆角 8px、行 hover 浅灰 + B 站分享蓝点缀，与观看页现有浅色体系一路。 -->
    <Teleport to="body">
      <div
        v-if="shareOpen"
        class="fixed z-[65] w-[216px] overflow-hidden rounded-lg border border-lineSoft bg-cardBg py-1 shadow-[0_12px_32px_-8px_rgba(0,0,0,0.25)]"
        :style="sharePanelStyle"
        role="menu"
        aria-label="安利方式"
        @mouseenter="shareHover.onEnter()"
        @mouseleave="shareHover.onLeave()"
      >
        <button type="button" role="menuitem" class="vas-row" @click="pickShare('dm')">
          <!-- 线性信筒：与拍立得同一套手描 SVG（emoji 在 Windows 上渲染缺齿，弃用） -->
          <svg class="vas-ic" viewBox="0 0 24 24" aria-hidden="true"><rect x="3" y="5" width="18" height="14" rx="2.5" /><path d="M3.8 6.5 L12 12.5 L20.2 6.5" /></svg>
          <span>转发私信</span>
        </button>
        <button type="button" role="menuitem" class="vas-row" @click="pickShare('poster')">
          <!-- D 号「拍立得」（站长 11-04 翻牌）：相纸大底边 -->
          <svg class="vas-ic" viewBox="0 0 24 24" aria-hidden="true"><rect x="4.5" y="3.5" width="15" height="17" rx="2" /><path d="M4.5 16 H19.5" /><circle cx="9" cy="8.5" r="1.3" /><path d="M6.5 13.8 L9.8 10.6 L12 12.7 L14.2 10.2 L17.5 13.8" /></svg>
          <span>分享海报</span>
        </button>
      </div>
    </Teleport>
  </div>
</template>

<style scoped>
/* 悬浮窗两行选项（站长 11-04 加尺版）：行高 44、字 14.5；行 hover 浅灰，字色落 B 站分享蓝 */
.vas-row {
  display: flex;
  width: 100%;
  align-items: center;
  gap: 11px;
  height: 44px;
  padding: 0 14px;
  text-align: left;
  font-size: 14.5px;
  line-height: 20px;
  color: rgb(var(--ink-sub));
  transition: background-color 0.15s ease, color 0.15s ease;
}
.vas-row:hover {
  background-color: rgba(0, 0, 0, 0.045);
  color: #00a1d6;
}
html.dark .vas-row:hover {
  background-color: rgba(255, 255, 255, 0.07);
}
.vas-ic {
  width: 20px;
  height: 20px;
  flex: none;
  fill: none;
  stroke: currentColor;
  stroke-width: 1.8;
  stroke-linecap: round;
  stroke-linejoin: round;
}

/* 点击小动画：官方图标是 currentColor 的整枚 svg，直接对根 svg 做变换。 */
@keyframes va-like {
  0% { transform: scale(1); }
  30% { transform: scale(1.35); }
  60% { transform: scale(0.85); }
  100% { transform: scale(1); }
}
.va-anim-like {
  animation: va-like 0.45s ease;
}
@keyframes va-coin {
  0% { transform: translateY(-14px) rotate(-8deg); }
  55% { transform: translateY(0) rotate(4deg); }
  75% { transform: translateY(-4px) rotate(-2deg); }
  100% { transform: translateY(0) rotate(0deg); }
}
.va-anim-coin {
  animation: va-coin 0.5s cubic-bezier(0.34, 1.56, 0.64, 1);
}
@keyframes va-fav {
  0% { transform: scale(0.6); }
  60% { transform: scale(1.25); }
  100% { transform: scale(1); }
}
.va-anim-fav {
  animation: va-fav 0.4s ease;
}
@keyframes va-share {
  0% { transform: scale(1); }
  50% { transform: scale(0.86); }
  100% { transform: scale(1); }
}
.va-anim-share {
  animation: va-share 0.25s ease;
}
</style>
