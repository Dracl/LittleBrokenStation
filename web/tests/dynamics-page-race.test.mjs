// DynamicsPage 首屏/筛选/翻页共池代次的回归：装载真实 <script setup> 执行，不镜像实现。
// keepFeed / tailCursor 直接从 utils/dynFeed.js 引真身：LIVE 条过滤与「原始尾巴」游标必须真参与断言。
// 覆盖：筛选逆序、翻页途中换筛选、非 2xx 清表清游标带 toast、首屏期间挡翻页、卸载作废与 toast 定时器清理。
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { fileURLToPath } from 'node:url'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'
import { loadVueScriptSetup, makeNet } from './helpers/load-script.mjs'
import { hooks, resetHooks } from './helpers/vue-hoist.mjs'

const ROOT = fileURLToPath(new URL('.', import.meta.url))
const PAGE = join(ROOT, '..', 'src', 'pages', 'DynamicsPage.vue')
const DYNFEED = join(ROOT, '..', 'src', 'utils', 'dynFeed.js')

const STUB_NAMES = ['useRoute', 'member', 'defaultAvatar', 'onAvatarError', 'faceFor', 'keepFeed', 'tailCursor']

const EXPORTS = ['loadFeed', 'loadMore', 'pickFilter', 'showToast', 'items', 'hasMore', 'loading', 'loadingMore', 'toast', 'filterMid', 'targets']
const PEEK = 'feedCursor, feedReq, feedEpoch'

function stubsSource() {
  return `
import { ref } from 'vue'
export { keepFeed, tailCursor } from '${pathToFileURL(DYNFEED).href}'
export function useRoute() { return { params: {}, query: {} } }
export const member = ref(null)
export const defaultAvatar = ''
export const onAvatarError = () => {}
export const faceFor = () => ''
`
}

async function boot() {
  resetHooks()
  const mod = await loadVueScriptSetup(PAGE, {
    stubsSource: stubsSource(),
    stubImports: STUB_NAMES,
    exportNames: EXPORTS,
    peekExprs: PEEK,
  })
  const net = makeNet()
  globalThis.fetch = net.fetchStub
  return { mod, t: mod.__t, peek: mod.__peek, ...net }
}

const D = (id, type = 'DYNAMIC') => ({ id, type, publishedAtUtc: `2026-11-10T0${id}:00:00:00Z` })
const feed = (items, hasMore = false) => ({ items, hasMore })

test('D1 筛选逆序：上一筛选的完整迟到包不得覆盖新筛选，游标取原始尾巴', async () => {
  const { t, peek, plan, deferred, flush } = await boot()
  const g1 = deferred()
  const g2 = deferred()
  plan({ match: '/api/dynamics', body: feed([D(1), D(2)], true), gate: g1.promise }) // 首屏「全部」在飞
  // B 页（含一条 LIVE 垫尾：断言走真 keepFeed/tailCursor）必须在 pickFilter 发请求前就位
  plan({ match: 'mid=', body: feed([D(9), D(10, 'LIVE')], true), gate: g2.promise })
  const p1 = t.loadFeed()
  t.pickFilter(5)
  assert.deepEqual(t.items.value, [], '切筛选立即清空旧列表')
  assert.equal(t.hasMore.value, false, '切筛选立即收 hasMore')
  assert.equal(peek().feedCursor, null, '切筛选立即清旧游标')
  g2.resolve() // 新筛选先回
  await flush()
  assert.deepEqual(t.items.value.map((x) => x.id), [9], '真 keepFeed 滤掉 LIVE 条')
  assert.equal(peek().feedCursor.id, 10, '真 tailCursor 拿原始返回的尾巴（含被滤的 LIVE 条）当游标')
  g1.resolve() // 「全部」的迟到包最后才到
  await p1
  await flush()
  assert.deepEqual(t.items.value.map((x) => x.id), [9], '迟到的旧筛选包不得覆盖列表')
  assert.equal(peek().feedCursor.id, 10, '迟到的旧筛选包不得覆盖游标')
  assert.equal(t.hasMore.value, true, 'hasMore 跟最新一发走')
  assert.equal(t.filterMid.value, 5)
  assert.equal(t.loading.value, false)
})

test('D2 筛选吃非2xx：清表清游标带toast，无旧游标可翻', async () => {
  const { t, peek, net, plan, flush } = await boot()
  plan({ match: '/api/dynamics', body: feed([D(1), D(2)], true) }) // 首屏 ok
  await t.loadFeed()
  assert.equal(t.hasMore.value, true)
  const before = net.length
  plan({ match: 'mid=', ok: false, status: 500 }) // 筛选轮吃 500
  t.pickFilter(7)
  await flush()
  assert.equal(net.length, before + 1, '筛选轮已发出')
  assert.ok(t.toast.value, '非 2xx 有 toast')
  assert.deepEqual(t.items.value, [], '非 2xx 清空列表，不拿上一个筛选冒充')
  assert.equal(t.hasMore.value, false)
  assert.equal(peek().feedCursor, null, '不留旧筛选游标')
  assert.equal(t.loading.value, false)
  await t.loadMore()
  assert.equal(net.length, before + 1, '无 hasMore/游标时翻页不发任何请求')
})

test('D6 同一筛选失败后可再次点击重试', async () => {
  const { t, net, plan, flush } = await boot()
  plan({ match: 'mid=7', ok: false, status: 500 })
  t.pickFilter(7)
  await flush()
  assert.deepEqual(t.items.value, [])
  assert.equal(t.loading.value, false)

  plan({ match: 'mid=7', body: feed([D(9)], false) })
  t.pickFilter(7)
  await flush()
  assert.equal(net.length, 2, '重选当前 UP 仍会重新请求')
  assert.deepEqual(t.items.value.map((x) => x.id), [9])
  assert.equal(t.loading.value, false)
})

test('D3 翻页途中换筛选：旧页回包一个字段都不许写', async () => {
  const { t, peek, plan, deferred, flush } = await boot()
  plan({ match: 'limit=20', body: feed([D(1), D(2)], true) }) // 首屏（无 mid= 无 beforeId=）
  await t.loadFeed()
  assert.equal(t.hasMore.value, true)
  const gMore = deferred()
  plan({ match: 'beforeId=', body: feed([D(3), D(4)], true), gate: gMore.promise })
  const pM = t.loadMore()
  assert.equal(t.loadingMore.value, true, '翻页在飞')
  const gFront = deferred()
  plan({ match: 'mid=', body: feed([D(9)], false), gate: gFront.promise })
  t.pickFilter(5)
  assert.deepEqual(t.items.value, [], '切筛选立即清空')
  assert.equal(t.loadingMore.value, false, '切筛选重置 loadingMore')
  gMore.resolve() // 旧筛选的第二页迟到
  await pM
  await flush()
  assert.deepEqual(t.items.value, [], '旧筛选的翻页回包不得合入')
  assert.equal(peek().feedCursor, null, '旧翻页不得写游标')
  assert.equal(t.hasMore.value, false, '旧翻页不得写 hasMore')
  assert.equal(t.loadingMore.value, false, '作废的翻页不得动 loadingMore 之外的状态')
  gFront.resolve()
  await flush()
  assert.deepEqual(t.items.value.map((x) => x.id), [9], '新筛选正常落地')
  assert.equal(t.loading.value, false)
})

test('D4 首屏在飞时挡翻页提交，落地后翻页带捕获的游标', async () => {
  const { t, net, plan, deferred, flush } = await boot()
  const g = deferred()
  plan({ match: 'limit=20', body: feed([D(1), D(2)], true), gate: g.promise })
  plan({ match: 'beforeId=', body: feed([D(3)], false) })
  const p = t.loadFeed()
  await flush()
  await t.loadMore()
  assert.equal(net.length, 1, '首屏在飞时不许提交第二发请求')
  assert.equal(t.loadingMore.value, false, '被挡下不留 loadingMore')
  g.resolve()
  await p
  await flush()
  const pM = t.loadMore()
  await pM
  await flush()
  assert.equal(net.length, 2)
  assert.ok(net[1].url.includes('beforeId=2'), '翻页带上出发时捕获的游标')
  assert.deepEqual(t.items.value.map((x) => x.id), [1, 2, 3], '翻页追加')
  assert.equal(t.hasMore.value, false)
  assert.equal(t.loadingMore.value, false)
})

test('D5 卸载：在飞请求作废不写状态，toast定时器一并清掉', async () => {
  const { t, net, plan, deferred, flush } = await boot()
  const g = deferred()
  plan({ match: 'limit=20', body: feed([D(1)], false), gate: g.promise })
  const p = t.loadFeed()
  await flush()
  t.showToast('临别一句')
  const cleared = []
  const origClear = globalThis.clearTimeout
  globalThis.clearTimeout = (id) => {
    cleared.push(id)
    return origClear(id)
  }
  hooks.unmount.at(-1)()
  globalThis.clearTimeout = origClear
  assert.ok(cleared.length >= 1, '卸载清 toast 定时器')
  g.resolve()
  await p
  await flush()
  assert.deepEqual(t.items.value, [], '卸载后的迟到包不写数据')
  assert.equal(t.loading.value, true, '迟到包不再有权动 loading')
  assert.equal(net.length, 1, '未再派生任何请求')
})
