import { onBeforeUnmount, onMounted, ref } from 'vue'

/// 影院渐隐（11-17 站长令·二修：只有「没人理她们」才退场）——
/// 判定只看右下角那一片：鼠标（或手指）伸进感应区才算动静、才唤回钮群；
/// 在页面别处晃鼠标、敲键盘，都不算——她们该沉就沉，不打扰看电影。
/// zone() 每次现取（窗口会 resize）：返回 { x, y, w, h } 视口坐标；不传等于永醒。
/// awake：聊天卡开着、指针正停在钮上等「正被人用着」的凭证——到点也不沉，2 秒后再探。
export function useIdleFade(awake, zone, timeoutMs = 15000) {
  const idle = ref(false)
  let timer = 0

  function schedule(delay) {
    clearTimeout(timer)
    timer = setTimeout(() => {
      if (awake && awake()) {
        schedule(2000) // 正被人用着：不沉，稍后再探一眼
        return
      }
      idle.value = true
    }, delay)
  }

  function poke() {
    idle.value = false
    schedule(timeoutMs)
  }

  function inZone(x, y) {
    const z = zone && zone()
    return !!z && x >= z.x && x <= z.x + z.w && y >= z.y && y <= z.y + z.h
  }

  // 只认落点在角落里的动静；别处的 mousemove 连计时都不重跑——静默是她们应得的安静
  function onPointer(e) {
    const t = e.touches && e.touches[0] ? e.touches[0] : e
    if (inZone(t.clientX, t.clientY)) poke()
  }

  const events = ['mousemove', 'touchstart']
  onMounted(() => {
    events.forEach((ev) => window.addEventListener(ev, onPointer, { passive: true }))
    poke() // 刚进页给 15 秒亮相期
  })
  onBeforeUnmount(() => {
    clearTimeout(timer)
    events.forEach((ev) => window.removeEventListener(ev, onPointer))
  })
  return idle
}
