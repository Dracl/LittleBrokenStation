import { reactive } from 'vue'
import { claimPreview, previewHoverEnter, previewHoverLeave, releasePreview } from './previewHover'

// 悬停预览的一路「机位」：起播延时、续播窗口、单路令牌、出错放弃、减少动效，全站只此一份口径。
// 与 previewHover.js 的分工：那边是全站契约（单路播放令牌 + 「有人正悬停」的首页洗牌锁），
// 这边是一路预览自己的状态机。谁要预览谁 createPreviewHost()，<video> 挂在谁的封面里由调用方的模板决定。
// 两个调用方：VideoCard（网格卡阵，一卡一实例，机位固定守着自己那张卡）与
// BangumiHome 的分区横滚行（一个实例挂着二十张卡，机位在整排卡里走，鼠标停在谁身上就进谁的封面）。

const PREVIEW_ARM_MS = 350 // 鼠标停够 350ms 才起播，路过卡阵的手指不算想看
const PREVIEW_RESET_MS = 200 // 离场 200ms 内回来就原地续播，不重新从 0 拉一遍流

// 系统要求减少动效：尊重到底，悬停一律不起播，卡面停在封面原样。
const reduceMotion = !!window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches

export function createPreviewHost() {
  // key：这路机位此刻守到哪张卡。离场不清空——刚按停的那路在原地留 200ms，指针回来接着播。
  // shown：首帧到位没（到位才淡入，避免黑帧裸切）。
  // gaveUp：彻底放弃的卡（出错后连 <video> 都不再挂，绝不重试）。
  const state = reactive({ key: '', shown: false, gaveUp: new Set() })

  let getEl = null // 元素取值函数由调用方给（收 key 拿元素）：机位不认识模板，只管按停与起播
  let arm = null
  let reset = null
  let held = false // 首页洗牌锁的账：这一路占着没
  let heldKey = '' // 是哪张卡占的——迟到的 mouseleave 不许替别人还账

  function node(key) {
    return getEl ? getEl(key) : null
  }

  // 停车动作，同时充当全站单例的播放令牌：函数身份每路机位固定一个。
  function stop(resetTime) {
    const el = node(state.key)
    if (!el) return
    try {
      el.pause()
    } catch {
      /* 本来就没在播 */
    }
    if (resetTime) {
      try {
        el.currentTime = 0
      } catch {
        /* 流还没 ready，归零没意义 */
      }
    }
    state.shown = false
  }

  const stopForSingleton = () => stop(true)

  function clearTimers() {
    if (arm) {
      clearTimeout(arm)
      arm = null
    }
    if (reset) {
      clearTimeout(reset)
      reset = null
    }
  }

  // 鼠标进封面。src 为空（后端没转预览片）时也照记洗牌锁：停在一张没预览的卡上同样不许换人，与上一版口径一致。
  function enter(key, src, elGetter) {
    getEl = elGetter
    if (src && !held) {
      held = true
      heldKey = key
      previewHoverEnter()
    }
    if (!src || state.gaveUp.has(key) || reduceMotion) return
    clearTimers()
    // 换卡先按停上一路：这是一发保险——mouseLeave 理论上不会漏，但鼠标斜着从两张卡中间扫过去时
    // 宁可多按一次停，也不许本路机位上同时响两路（全站令牌只拦得住别人家的机位，拦不住自己换卡）。
    if (state.key && state.key !== key) {
      const prev = node(state.key)
      if (prev) {
        try {
          prev.pause()
        } catch {
          /* 本来就没在播 */
        }
      }
      state.shown = false // 上一张的首帧状态不许带到这一张，否则 B 会拿着 A 的到位标志直接淡进黑帧
    }
    state.key = key
    arm = setTimeout(() => {
      arm = null
      const el = node(key)
      if (!el) return
      claimPreview(stopForSingleton) // 全站只许一路在播：先让上一路停车归零
      el.muted = true // 属性之外再钉一次，免得 autoplay policy 卡住无声起播
      const p = el.play()
      if (p && typeof p.catch === 'function') p.catch(() => fail(key))
    }, PREVIEW_ARM_MS)
  }

  function leave(key) {
    if (held && key === heldKey) {
      held = false
      heldKey = ''
      previewHoverLeave()
    }
    if (key !== state.key || state.gaveUp.has(key)) return
    if (arm) {
      clearTimeout(arm)
      arm = null
    }
    releasePreview(stopForSingleton)
    stop(false) // 立即按停（淡回封面）
    if (reset) clearTimeout(reset)
    reset = setTimeout(() => {
      reset = null
      const el = node(key)
      if (el && el.currentTime) el.currentTime = 0
    }, PREVIEW_RESET_MS)
  }

  // 只认当前这一路的首帧：横扫卡阵时上一张的 loadeddata 会迟到，
  // 让它替现在这张报「到位」会把还没出画的元素直接淡进来，闪一下黑帧。
  function markShown(key) {
    if (key !== state.key) return
    state.shown = true
  }

  function fail(key) {
    state.gaveUp.add(key)
    if (key !== state.key) return // 不是当前这一路出的错，只记账，不把现在在播的那一路按停
    clearTimers()
    releasePreview(stopForSingleton)
    stop(false)
  }

  function dispose() {
    clearTimers()
    releasePreview(stopForSingleton)
    if (held) {
      held = false
      heldKey = ''
      previewHoverLeave()
    }
  }

  // 模板侧只问三件事：这路机位现在守的是不是我（挂不挂 <video>）、我放弃过没、该不该淡进来。
  return {
    state,
    enter,
    leave,
    markShown,
    fail,
    dispose,
    isHost: (key) => state.key === key,
    isGaveUp: (key) => state.gaveUp.has(key),
    isOn: (key) => state.shown && state.key === key,
  }
}
