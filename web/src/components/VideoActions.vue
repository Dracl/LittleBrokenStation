<script setup>
// B 站式互动栏：官方图标（BIcon + bicons.js）+ 数字。站长定案：点赞/投币/收藏三枚纯展示（不发请求不落库）；
// 分享一枚已激活（站内安利走私信，弹 ShareDialog，hover 蓝 #00a1d6 是本轮站长亲定的 B 站分享蓝）。
// 点击反馈：四枚各有 CSS keyframes 小动画（弹跳/硬币下落/星星 pop/轻缩放）；like/fav 点击顺带点亮成
// 站内装饰色（粉/金黄）、再点复位——纯前端装饰态，不上报 B 站，刷新即失。
// 数字全部来自 B 站 view 接口真采快照（到站时入库），采不到显示「—」，绝不编数。
import { reactive } from 'vue'
import BIcon from './BIcon.vue'
import { biLike, biCoin, biFav, biShare } from '../assets/bicons'

defineProps({
  stat: { type: Object, default: null }, // { like, coin, favorite, share, reply }
})
const emit = defineEmits(['share'])

// 动画触发：先落再点，同名 class 连点也能重放；animationend 后自动卸架。
const anim = reactive({ like: false, coin: false, fav: false, share: false })
// 装饰性点亮（点赞/投币/收藏）：只改颜色不上报，点击切换。
const lit = reactive({ like: false, coin: false, fav: false })

function tap(key) {
  anim[key] = false
  requestAnimationFrame(() => {
    anim[key] = true
  })
  if (key in lit) lit[key] = !lit[key]
  if (key === 'share') emit('share')
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
      title="B 站点赞（采自 view 接口）· 站内的点亮只是装饰"
      @click="tap('like')"
    >
      <BIcon
        :meta="biLike"
        class="h-[25px] w-[25px] shrink-0 transition duration-200"
        :class="[lit.like ? 'text-[#fb7299]' : 'text-[#757575] group-hover:text-[#fb7299]', anim.like ? 'va-anim-like' : '']"
        @animationend="anim.like = false"
      />
      <span class="leading-none tabular-nums">{{ fmt(stat?.like) }}</span>
    </span>
    <span
      class="group flex cursor-pointer items-center gap-2"
      title="B 站投币（采自 view 接口）· 站内的点亮只是装饰"
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
      title="B 站收藏（采自 view 接口）· 站内的点亮只是装饰"
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
      type="button"
      class="group flex items-center gap-2 transition hover:text-[#00a1d6]"
      title="安利给分站朋友（走私信）"
      @click="tap('share')"
    >
      <BIcon
        :meta="biShare"
        class="h-[25px] w-[25px] shrink-0 transition duration-200"
        :class="['text-[#757575] group-hover:text-[#00a1d6]', anim.share ? 'va-anim-share' : '']"
        @animationend="anim.share = false"
      />
      <span class="leading-none tabular-nums">{{ fmt(stat?.share) }}</span>
    </button>
  </div>
</template>

<style scoped>
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
