// 悬停门：全站共用一把尺子 —— 进 120ms 才开、离 260ms 才收。
// 顶栏的铃铛预览与头像菜单、放映厅的封面浮窗都吃这一份，别再各写一份延时（原来这份长在 TopBar 的
// <script setup> 里，外部拿不到；封面浮窗要用，就抽到这里来）。
// 触屏（hover:none）整个退化成只认点击，免得手机上点一下先弹面板再跳走。

const canHover = typeof window !== 'undefined' && window.matchMedia?.('(hover: hover) and (pointer: fine)').matches
const HOVER_OPEN_MS = 120
const HOVER_CLOSE_MS = 260

// openRef：面板的开关 ref；onOpen：开面板那一刻的副作用（拉预览、量尺寸…）；
// shouldClose：离鼠标时该不该收 —— 点击钉开的面板返回 false，不让鼠标把它赶跑。
export function useHoverGate(openRef, { onOpen = null, shouldClose = () => true, openMs = HOVER_OPEN_MS, closeMs = HOVER_CLOSE_MS } = {}) {
  let openTimer = null
  let closeTimer = null
  const clear = () => {
    if (openTimer) { clearTimeout(openTimer); openTimer = null }
    if (closeTimer) { clearTimeout(closeTimer); closeTimer = null }
  }
  return {
    onEnter() {
      if (!canHover) return
      // 先清掉待收的定时器：移开又马上回来（closeMs 内）不能被那一发延时给关掉了
      clear()
      if (openRef.value) return
      openTimer = setTimeout(() => {
        openRef.value = true
        onOpen?.()
      }, openMs)
    },
    onLeave() {
      if (!canHover || !openRef.value) return
      clear()
      if (!shouldClose()) return
      closeTimer = setTimeout(() => { openRef.value = false }, closeMs)
    },
    cancel: clear,
  }
}
