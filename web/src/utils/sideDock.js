// ─── 放映厅左缘那「半颗贴边钮」的尺子，全站只这一把。
// SideHandle（钮本体：看封面 / 稿件设置两颗共用它）按它把圆心压在视口 x=0，
// CoverPeek 的封面卡按它摆在钮的右侧。两处各算一遍迟早分叉，数字就抄在这里一次。
//
// 为什么 left 是个负数：把手挂在包 VideoPlayer 的那层 relative 里，它的 left 是容器坐标；
// 要贴视口左缘，得先把「容器左缘到视口左缘」那一段折出去（main 居中 + max-w-[1600px] + px-6）。

export const FAB = 36 // 圆钮直径（SideHandle 里那颗 h-9 w-9）
export const PAD = 24 // main 容器的左右内边距（px-6）
export const MAX_W = 1600 // main 的 max-w

// 容器左缘到视口左缘的距离（px）。窗口比 max-w 窄时容器贴边，这段自己归零。
export function wrapperLeftOf(winW) {
  return (winW - (Math.min(winW, MAX_W) - PAD * 2)) / 2
}

export function dockOf(winW) {
  const wrapperLeft = wrapperLeftOf(winW)
  return {
    wrapperLeft,
    fabLeft: -(wrapperLeft + FAB / 2), // 中心压在视口 x=0 → 左半颗被视口裁掉，只剩右半边露着
    panelLeft: -wrapperLeft + FAB + 10, // 弹层开在钮的右侧（贴着视口左缘那一竖条，与 B 站那两枚同位置）
  }
}
