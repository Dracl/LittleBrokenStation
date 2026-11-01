import { loginDialogMode, loginDialogOpen, member } from '../memberStore'

// ─── 访客门禁的前端这一半：这四页（动态 / 分区 / 合集名录 / 合集详情）是成员待遇。
// 真门在后端（/api/dynamics* 与 /api/collections/{id} 匿名 401），这里只是别让人白跑一趟空页。
// 判据与 TopBar 那几枚入口同一条：memberStore.member 为空 = 访客。
export const GUEST_BLOCKED_ROUTES = ['dynamics', 'zones', 'collections', 'collection']

// 首页该看却进不去的那些链接：卡片进放映厅、分区行、合集入口 —— 访客按下来不跳页，改成开登录窗。
// 周报（/weekly）不在这四页之列（站长说这次不动它），照旧放行。
const GUEST_BLOCKED_HREF = /^\/(watch|zones|collections|collection|dynamics)(?:[/?#]|$)/

// 全站只这一扇登录窗：写的就是 memberStore 那对 ref，与 TopBar 的「登录 / 注册」开的是同一扇，
// 不另造弹窗（LoginDialog 由 App.vue 统一挂着）。
export function openLoginWindow() {
  loginDialogMode.value = 'admit'
  loginDialogOpen.value = true
}

// 挂在页面根元素上（@click.capture）：捕获阶段跑在卡片那颗 <a> 自己的处理之前，
// 这里 preventDefault 之后，RouterLink 的 guardEvent 见到 defaultPrevented 就自己收手（vue-router 里那一句）。
// 只认落在「进得去但这次不给进」的链接上的点击：卡片上的悬停预览、翻页箭头、站长那几枚 hover 钮一概不碰；
// 成员则整个放行，一条都不拦。
export function guardGuestNav(event) {
  if (member.value) return
  if (event.defaultPrevented) return
  const link = event.target?.closest?.('a[href]')
  if (!link) return
  if (!GUEST_BLOCKED_HREF.test(link.getAttribute('href') || '')) return
  event.preventDefault()
  openLoginWindow()
}
