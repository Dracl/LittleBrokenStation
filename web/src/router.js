import { createRouter, createWebHistory } from 'vue-router'
import HomePage from './pages/HomePage.vue'
import AdminPage from './pages/AdminPage.vue'
import MemberPage from './pages/MemberPage.vue'
import HistoryPage from './pages/HistoryPage.vue'
import MessagesPage from './pages/MessagesPage.vue'
import WatchPage from './pages/WatchPage.vue'
import SearchPage from './pages/SearchPage.vue'
import DynamicsPage from './pages/DynamicsPage.vue'
import ZonePage from './pages/ZonePage.vue'
import CollectionsPage from './pages/CollectionsPage.vue'
import CollectionDetailPage from './pages/CollectionDetailPage.vue'
import WeeklyPage from './pages/WeeklyPage.vue'
import RegisterPage from './pages/RegisterPage.vue'
import GuestGatePage from './pages/GuestGatePage.vue'
import { member, loaded, load as loadMember } from './memberStore'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', name: 'home', component: HomePage, meta: { title: '分站首页 - 小破分站' } },
    { path: '/watch/:bvid', name: 'watch', component: WatchPage, meta: { title: '在线观看 - 小破分站' } },
    { path: '/search', name: 'search', component: SearchPage, meta: { title: '站内搜索 - 小破分站' } },
    { path: '/dynamics', name: 'dynamics', component: DynamicsPage, meta: { title: 'UP 主动态 - 小破分站', guestBlocked: true } },
    { path: '/zones', name: 'zones', component: ZonePage, meta: { title: '分区 - 小破分站', guestBlocked: true } },
    { path: '/collections', name: 'collections', component: CollectionsPage, meta: { title: '合集名录 - 小破分站', guestBlocked: true } },
    { path: '/collection/:id', name: 'collection', component: CollectionDetailPage, meta: { title: '合集 - 小破分站', guestBlocked: true } },
    { path: '/weekly', name: 'weekly', component: WeeklyPage, meta: { title: '分站周报 - 小破分站' } },
    { path: '/admin', name: 'admin', component: AdminPage, meta: { title: '站长室 - 小破分站' } },
    { path: '/me', name: 'me', component: MemberPage, meta: { title: '个人空间 - 小破分站' } },
    { path: '/history', name: 'history', component: HistoryPage, meta: { title: '观看历史 - 小破分站' } },
    { path: '/messages', name: 'messages', component: MessagesPage, meta: { title: '站内信箱 - 小破分站' } },
    { path: '/register', name: 'register', component: RegisterPage, meta: { title: '落籍进站 - 小破分站' } },
    // 访客被拦下来的落脚页（守卫把 to.fullPath 放在 ?from= 里递过去）
    { path: '/gated', name: 'guest-gate', component: GuestGatePage, meta: { title: '这一页要进站 - 小破分站' } },
  ],
})

// 访客只看主页的门（前端这一半）：动态 / 分区 / 合集名录 / 合集详情这四页是成员待遇。
// 真门在后端（/api/dynamics* 与 /api/collections/{id} 进会员门，匿名 401），这道只是别让人白跑一趟空页；
// /api/zones 与 /api/collections（名录）仍旧公开，首页那些卡拿数据照旧、不因此改请求。
// memberStore 首次那发还没落（loaded 仍为 false）时先等它：把「还没查」当成「查过是访客」会误拦刚进站的客人。
router.beforeEach(async (to) => {
  if (!to.meta?.guestBlocked) return true
  if (!loaded.value) await loadMember()
  if (member.value) return true
  return { name: 'guest-gate', query: { from: to.fullPath } }
})

router.afterEach((to) => {
  document.title = to.meta.title ?? '小破分站'
})

export default router
