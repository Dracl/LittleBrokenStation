// ─── UP 脸的本地钦选（站长 11-06 定案：全员可换、只改本机）───
// 与动态的本机赞同一条脾气：localStorage 小本本，不上报、不入服务器档、换设备清零。
// 服务器只管攒档案（up_faces 表 + 周巡检），切换全在浏览器 —— 「点谁是谁」，按 mid 全站认：
// 空间页名片、放映厅 UpCard、动态卡、评论条……凡显示这位 UP 脸的地方都跟着换。
// 只认 /up_faces/ 开头的值：小本本被手改脏了也只当没钦选，不给塞野 URL 的机会。
import { shallowRef } from 'vue'

const STORE_KEY = 'lbs-up-face-choices'

function loadChoices() {
  try {
    const raw = window.localStorage.getItem(STORE_KEY)
    const obj = raw ? JSON.parse(raw) : null
    return obj && typeof obj === 'object' ? obj : {}
  } catch {
    return {}
  }
}

const choices = loadChoices()
// 裸对象 + 版本号（与动态赞的 dynLikes 同族）：换脸只惊动正在渲染脸的组件（读方先碰一下 faceTick）
const faceTick = shallowRef(0)

function sane(mid) {
  const picked = mid == null ? null : choices[String(mid)]
  return typeof picked === 'string' && picked.startsWith('/up_faces/') ? picked : null
}

// 显示口的总闸：本机钦选（件在不在盘由调用侧 @error 兜底）＞ 服务器给的脸
export function faceFor(mid, serverFace) {
  faceTick.value // 读一下版本号，换脸后各处跟着重渲染
  return sane(mid) || serverFace
}

// 相册弹层用：本机当前钦选的那张（没有回 null = 跟服务器）
export function chosenFace(mid) {
  faceTick.value
  return sane(mid)
}

// 点谁是谁；传 null 收回钦选、回到跟服务器
export function chooseFace(mid, faceRel) {
  if (faceRel && !String(faceRel).startsWith('/up_faces/')) return // 只收档案口的件
  const key = String(mid)
  if (faceRel) choices[key] = faceRel
  else delete choices[key]
  try {
    window.localStorage.setItem(STORE_KEY, JSON.stringify(choices))
  } catch {
    /* 存不下就算了，本地小本本本就是图个顺手 */
  }
  faceTick.value += 1
}
