import { ref } from 'vue'

/// 全站共享的成员登录态：TopBar、用户控制室、站长室都读这份，LoginDialog 统一唤起。
/// 参照 submitStore 的写法：无 Pinia，模块级 ref 即 store。
/// member 为 /api/member/me 的回包；null = 访客。

export const member = ref(null)
export const loaded = ref(false) // 首次 load 是否落定（区分「还没查」与「查过是访客」）
export const loginDialogOpen = ref(false)
export const loginDialogMode = ref('admit') // admit=扫码进站 | bind=往账号池加号

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
}

export async function logout() {
  try {
    await fetch('/api/member/logout', { method: 'POST' })
  } catch {
    /* 后端没应答也照样把本地身份摘掉 */
  }
  member.value = null
}
