// 全站默认头像：站长给的小人立绘（240px 自缩放，8 KB 级，圆形/方形由外层 class 决定）。
// 两处用得上：① 访客与「没绑 B 站/没抓到头像」的成员占位；② 远程热链与本地缓存都拉挂时的临时兜底。
// 只在这里 import 一次资源，全站共用同一个 URL，浏览器也只需缓存一份。
import defaultAvatar from '../assets/default-avatar.jpg'

export { defaultAvatar }

/**
 * img 加载失败的兜底：换成默认头像。
 * 只许换一次（data 标记），免得默认头像自己拉不到时来回横跳。
 */
export function onAvatarError(event) {
  const el = event.target
  if (el.dataset.avatarFallback) return
  el.dataset.avatarFallback = '1'
  el.src = defaultAvatar
}
