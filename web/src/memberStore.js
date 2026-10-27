import { ref } from 'vue'

/// 全站共享的成员登录态：TopBar、用户控制室、站长室都读这份，LoginDialog 统一唤起。
/// 参照 submitStore 的写法：无 Pinia，模块级 ref 即 store。
/// member 为 /api/member/me 的回包；null = 访客。

export const member = ref(null)
export const loaded = ref(false) // 首次 load 是否落定（区分「还没查」与「查过是访客」）
export const loginDialogOpen = ref(false)
export const loginDialogMode = ref('admit') // admit=扫码进站 | bind=往账号池加号
export const unread = ref(0) // 顶栏铃铛角标：信箱未读 + 私信未读的合流数
export const dmUnread = ref(0) // 私信未读单值（私信页签小红点用）

export async function load() {
  try {
    const res = await fetch('/api/member/me')
    // 401 及其他非 2xx 一律当访客，不自作主张编身份
    member.value = res.ok ? await res.json() : null
  } catch {
    member.value = null
  } finally {
    loaded.value = true
  }
  // 确认是成员才去拉未读；访客不敲信箱的门
  refreshUnread()
}

/// 未读合流：信箱与私信两本账并行拉，铃铛总数 = 两者之和；失败不叫（角标小差不差人命）。
export async function refreshUnread() {
  if (!member.value) {
    unread.value = 0
    dmUnread.value = 0
    return
  }
  try {
    const [inboxRes, dmRes] = await Promise.all([
      fetch('/api/member/messages?unreadOnly=true&limit=1'),
      fetch('/api/dm/unread'),
    ])
    const inbox = inboxRes.ok ? Number((await inboxRes.json())?.unread) || 0 : 0
    const dm = dmRes.ok ? Number((await dmRes.json())?.unread) || 0 : 0
    unread.value = inbox + dm
    dmUnread.value = dm
  } catch {
    /* 断网时角标维持原样 */
  }
}

export async function logout() {
  try {
    await fetch('/api/member/logout', { method: 'POST' })
  } catch {
    /* 后端没应答也照样把本地身份摘掉 */
  }
  member.value = null
  unread.value = 0 // 登出即刻摘铃铛，不等下一轮心跳
  dmUnread.value = 0
}
