import { ref } from 'vue'

/// ─── 看板娘坞的共享闸门（11-17 站长令：三颗钮同时亮，慢一点也没关系）─────
/// 病根：看板娘钮等 /api/agent/status 回票才亮，日夜与切换模式挂载即画——两颗抢跑、一颗迟到。
/// （cookie 快路径是死路：lbs_member 是 HttpOnly，JS 读不到，不许再试。）
/// 如今三颗钮共吃这一道 dockReveal：status 落定（成或败都算落定）那一刻一起揭幕；
/// 2.5 秒保险丝兜底——上游打摆也不能让日夜钮永远罚站。
/// status 也存这里（模块级一份）：站厅⇄放映厅翻面不重敲接口，按钮不再二次迟到。

export const dockReveal = ref(false)
export const status = ref(null)
// 放映厅渐隐的两位证人（11-17 站长令）：聊天卡开着、或指针正停在钮/卡上，都算「正被人用着」，
// useIdleFade 到点也不许沉；两钮与聊天卡自己挂 mouseenter/mouseleave 登记。
export const panelOpen = ref(false)
export const dockHover = ref(false)

let inflight = null

export function ensureStatus() {
  if (inflight) return inflight
  inflight = (async () => {
    try {
      const res = await fetch('/api/agent/status')
      status.value = res.ok ? await res.json().catch(() => null) : null
    } catch {
      status.value = null
    } finally {
      dockReveal.value = true
    }
  })()
  // 保险丝：fetch 悬挂（弱网/半死的服务）也让钮揭幕——AI 钮自己等它的票，别的钮不陪葬
  setTimeout(() => { dockReveal.value = true }, 2500)
  return inflight
}

/// 强制重敲一发 status 换新票（11-17 站长报障根治：存完接线回首页，点钮仍被旧「未接线」票带去 /me）。
/// 刷新失败保留旧票不降级——偶发网络打摆不该把钮打回待接线脸。
export function refreshStatus() {
  inflight = (async () => {
    try {
      const res = await fetch('/api/agent/status')
      if (res.ok) status.value = await res.json().catch(() => null)
    } catch {
      /* 旧票坐岗，等新票 */
    } finally {
      dockReveal.value = true
    }
  })()
  return inflight
}
