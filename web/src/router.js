import { createRouter, createWebHistory } from 'vue-router'
import HomePage from './pages/HomePage.vue'
import AdminPage from './pages/AdminPage.vue'
import MemberPage from './pages/MemberPage.vue'
import MessagesPage from './pages/MessagesPage.vue'
import WatchPage from './pages/WatchPage.vue'
import SearchPage from './pages/SearchPage.vue'
import DynamicsPage from './pages/DynamicsPage.vue'
import RegisterPage from './pages/RegisterPage.vue'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', name: 'home', component: HomePage, meta: { title: '分站首页 - 小破分站' } },
    { path: '/watch/:bvid', name: 'watch', component: WatchPage, meta: { title: '在线观看 - 小破分站' } },
    { path: '/search', name: 'search', component: SearchPage, meta: { title: '站内搜索 - 小破分站' } },
    { path: '/dynamics', name: 'dynamics', component: DynamicsPage, meta: { title: 'UP 主动态 - 小破分站' } },
    { path: '/admin', name: 'admin', component: AdminPage, meta: { title: '站长室 - 小破分站' } },
    { path: '/me', name: 'me', component: MemberPage, meta: { title: '用户控制室 - 小破分站' } },
    { path: '/messages', name: 'messages', component: MessagesPage, meta: { title: '站内信箱 - 小破分站' } },
    { path: '/register', name: 'register', component: RegisterPage, meta: { title: '落籍进站 - 小破分站' } },
  ],
})

router.afterEach((to) => {
  document.title = to.meta.title ?? '小破分站'
})

export default router
