import { ref } from 'vue'

// 悬停预览的两条全站协作契约：履约由 utils/hoverPreview 那一路机位担着（VideoCard 与 BangumiHome 分区行共用），
// HomePage 只消费结果，调用方（AdminVideoCell 等）不必知情。

/// 有卡正被悬停（含 350ms 起播延时窗口）：首页据此冻结洗牌，鼠标原地不动时卡片不许换人。
export const previewHovering = ref(false)
let hoverHold = 0

export function previewHoverEnter() {
  hoverHold += 1
  previewHovering.value = true
}

export function previewHoverLeave() {
  hoverHold = Math.max(0, hoverHold - 1)
  if (!hoverHold) previewHovering.value = false
}

// 单路播放令牌：新卡起播前先把上一路按停归零，鼠标横扫卡阵也不会两路同时动。
// 令牌就是「停车」这个函数本身——谁在播谁交停车票，收回时认票不认人。
let currentToken = null

export function claimPreview(token) {
  if (currentToken && currentToken !== token) currentToken()
  currentToken = token
}

export function releasePreview(token) {
  if (currentToken === token) currentToken = null
}
