// VideoPage 换 BV / 轮询 / 卸载的竞争回归：装载真实 <script setup> 执行，不镜像实现。
// 覆盖：A/B 逆序、fetch 后 json 迟到、旧前台 finally 不摸新 loading、卸载作废、
// 换 BV 停轮询且旧 tick 不得动新 interval、silent 不抢前台 loading、换 BV 窗口期 related/弹幕迟到包。
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { fileURLToPath } from 'node:url'
import { join } from 'node:path'
import { loadVueScriptSetup, makeNet } from './helpers/load-script.mjs'
import { hooks, resetHooks } from './helpers/vue-hoist.mjs'

const ROOT = fileURLToPath(new URL('.', import.meta.url))
const WATCH_PAGE = join(ROOT, '..', 'src', 'pages', 'VideoPage.vue')

const STUB_NAMES = [
  'useRoute', 'useRouter', 'refetchArchive', 'removeVideoEntry', 'mentionHtml',
  'member', 'loginDialogOpen', 'submitDialogOpen', 'HINT_DELETE_VIDEO', 'HINT_FETCH_COMMENTS',
]

const EXPORTS = [
  'loadVideo', 'startCodecPoll', 'ensureCodecPoll',
  'data', 'loading', 'gateState', 'activeIndex', 'related', 'localDanmaku', 'resumeSec', 'resumeTip',
]
const PEEK = 'codecPoll, videoEpoch, videoReqSeq, relatedSeq, danmakuSeq, resumeSeq'

function stubsSource() {
  return `
import { ref } from 'vue'
export const __box = { route: { params: {}, query: {} }, pushes: [] }
export function useRoute() { return __box.route }
export function useRouter() { return { push: (p) => { __box.pushes.push(p) } } }
export const member = ref(null)
export const loginDialogOpen = ref(false)
export const submitDialogOpen = ref(false)
export const HINT_DELETE_VIDEO = 'hint-del'
export const HINT_FETCH_COMMENTS = 'hint-fetch'
export const mentionHtml = (s) => String(s)
export async function refetchArchive() { return { kind: 'ok', text: '', commentsOk: false, danmakuOk: false } }
export async function removeVideoEntry() { return { outcome: 'noop' } }
`
}

async function boot(bvid = 'BV_A') {
  resetHooks()
  // 组件外全局的最小代身：createElement 是给 @vue/runtime-dom 装载时占位用的（它只存引用，不真渲染）
  globalThis.localStorage = { getItem: () => null, setItem() {}, removeItem() {} }
  globalThis.document = { addEventListener() {}, removeEventListener() {}, querySelector() { return null }, createElement: () => ({}) }
  const mod = await loadVueScriptSetup(WATCH_PAGE, {
    stubsSource: stubsSource(),
    stubImports: STUB_NAMES,
    exportNames: EXPORTS,
    peekExprs: PEEK,
  })
  const net = makeNet()
  globalThis.fetch = net.fetchStub
  const route = mod.__stubs.__box.route
  route.params.bvid = bvid
  // 引爆「route.params.bvid 那支 watcher」：source() 返回值等于当前 bvid 的才响
  function fireBvidWatchers() {
    let fired = 0
    for (const w of hooks.watchers) {
      try {
        if (typeof w.source === 'function' && w.source() === route.params.bvid) {
          w.cb()
          fired += 1
        }
      } catch { /* autoRelated 那支的 source 是个 ref，不可调用，跳过 */ }
    }
    return fired
  }
  return { mod, t: mod.__t, peek: mod.__peek, route, fireBvidWatchers, ...net }
}

const videoPayload = (id, bvid, extraPages = {}) => ({
  id,
  bvid,
  pages: [{ index: 1, videoUrl: `/media/${bvid}.mp4`, codec: 'avc', codecVersions: [], ...extraPages }],
})

test('T1 换BV逆序：旧BV的完整迟到包不得覆盖新页（A/B逆序）', async () => {
  const { t, route, net, plan, deferred, fireBvidWatchers, flush } = await boot()
  const gateA = deferred()
  plan({ match: '/api/video/BV_A', body: videoPayload(1, 'BV_A'), gate: gateA.promise })
  plan({ match: '/api/video/BV_B', body: videoPayload(2, 'BV_B') })
  const pA = t.loadVideo()
  assert.equal(t.loading.value, true)
  route.params.bvid = 'BV_B'
  assert.equal(fireBvidWatchers(), 1)
  await flush()
  gateA.resolve() // A 的完整回包在 B 落地之后才到
  await pA
  await flush()
  assert.equal(t.data.value?.bvid, 'BV_B', '迟到的 A 包不得整页覆盖 B')
  assert.equal(t.loading.value, false)
  assert.equal(net.filter((r) => r.url.includes('/api/videos/1/')).length, 0, '作废的 A 前台不得派生 related/弹幕/续播')
})

test('T2 fetch先到json迟到：旧BV body迟于新页落地不覆盖', async () => {
  const { t, route, plan, deferred, fireBvidWatchers, flush } = await boot()
  const jsonA = deferred()
  plan({ match: '/api/video/BV_A', body: videoPayload(1, 'BV_A'), jsonGate: jsonA.promise })
  plan({ match: '/api/video/BV_B', body: videoPayload(2, 'BV_B') })
  const pA = t.loadVideo()
  route.params.bvid = 'BV_B'
  fireBvidWatchers()
  await flush()
  assert.equal(t.data.value?.bvid, 'BV_B')
  jsonA.resolve() // fetch 早已返回，body 解析现在才完
  await pA
  await flush()
  assert.equal(t.data.value?.bvid, 'BV_B', 'await json 之后也必须过闸')
  assert.equal(t.loading.value, false)
})

test('T3 旧前台finally不摸新前台的loading', async () => {
  const { t, route, plan, deferred, fireBvidWatchers, flush } = await boot()
  const gateA = deferred()
  const gateB = deferred()
  plan({ match: '/api/video/BV_A', body: videoPayload(1, 'BV_A'), gate: gateA.promise })
  plan({ match: '/api/video/BV_B', body: videoPayload(2, 'BV_B'), gate: gateB.promise })
  const pA = t.loadVideo()
  route.params.bvid = 'BV_B'
  fireBvidWatchers()
  gateA.resolve() // 旧前台先回：finally 不许关掉 B 的 loading
  await pA
  await flush()
  assert.equal(t.loading.value, true, 'B 还在飞，loading 必须还是 true')
  assert.equal(t.data.value, null, '旧包不写 data')
  gateB.resolve()
  await flush()
  assert.equal(t.data.value?.bvid, 'BV_B')
  assert.equal(t.loading.value, false)
})

test('T4 卸载：迟到包整发作废，轮询停掉摘handle且旧tick不作操作', async () => {
  const { t, peek, net, plan, deferred, flush } = await boot()
  const dl = videoPayload(1, 'BV_A', { codecVersions: [{ codec: 'hevc', status: 'Downloading' }] })
  const silentGate = deferred()
  plan({ match: '/api/video/BV_A', body: dl, gate: silentGate.promise }) // 先注册：被 tick 消耗
  const gate = deferred()
  plan({ match: '/api/video/BV_A', body: videoPayload(1, 'BV_A'), gate: gate.promise }) // 后注册：被前台消耗
  t.startCodecPoll('hevc') // 手开一轮，让一个 silent tick 在飞
  assert.ok(peek().codecPoll, '轮询已开')
  const p = t.loadVideo()
  hooks.unmount.at(-1)() // 卸载钩子（最后注册的那支：codecPoll/heartTimer 那组）
  assert.equal(peek().codecPoll, null, '卸载必须停轮询并摘 handle')
  gate.resolve()
  silentGate.resolve()
  await p
  await flush()
  assert.equal(t.data.value, null, '卸载后的迟到包不写任何状态')
  assert.equal(net.filter((r) => r.url.includes('/api/videos/1/related')).length, 0)
  assert.equal(peek().codecPoll, null, '旧 tick 不得再碰定时器')
})

test('T5 换BV停轮询：在飞旧tick不得清掉新任interval', async () => {
  const { t, peek: pk, route, plan, deferred, fireBvidWatchers, flush } = await boot()
  const dlBody = videoPayload(1, 'BV_A', { codecVersions: [{ codec: 'hevc', status: 'Downloading' }] })
  plan({ match: '/api/video/BV_A', body: dlBody }) // 前台 A：立刻回
  const silentA = deferred()
  plan({ match: '/api/video/BV_A', body: dlBody, gate: silentA.promise }) // 轮询 tick 1：在飞
  await t.loadVideo()
  assert.ok(pk().codecPoll, '前台发现有 Downloading 加档行，ensureCodecPoll 自动开轮')
  // 前台期间跳过静默请求；前台完成后启动一发真正等待响应的 tick。
  t.startCodecPoll('hevc')
  route.params.bvid = 'BV_B'
  const gateB = deferred()
  plan({ match: '/api/video/BV_B', body: videoPayload(2, 'BV_B', { codecVersions: [{ codec: 'hevc', status: 'Downloading' }] }), gate: gateB.promise })
  fireBvidWatchers()
  await flush()
  assert.equal(pk().codecPoll, null, '换 BV watcher 停旧轮询并摘 handle')
  gateB.resolve() // 新前台落地 → ensureCodecPoll 开新任
  await flush()
  assert.ok(pk().codecPoll, '新片仍有在途加档，由 ensureCodecPoll 续上')
  const newPoll = pk().codecPoll
  silentA.resolve() // 这时才让旧 tick 的 await loadVideo(true) 返回
  await flush()
  await flush()
  assert.equal(pk().codecPoll, newPoll, '旧 tick 的 settled 判断不得 clearInterval 新任')
  hooks.unmount.at(-1)()
  assert.equal(pk().codecPoll, null)
})

test('T6 前台加载期间跳过silent轮询，related与续播正常初始化', async () => {
  const { t, net, plan, deferred, flush } = await boot()
  const gateFg = deferred()
  plan({ match: '/api/video/BV_A', body: videoPayload(1, 'BV_A'), gate: gateFg.promise }) // 前台
  plan({ match: '/api/video/BV_A', body: videoPayload(1, 'BV_A', { codecVersions: [] }) }) // 轮询位
  const pFg = t.loadVideo()
  const pSilent = t.loadVideo(true)
  await pSilent
  await flush()
  assert.equal(t.loading.value, true, '跳过 silent 不得关前台的 loading')
  assert.equal(net.length, 1, '前台未完成时不发送静默请求')
  assert.equal(net.filter((r) => r.url.includes('/related')).length, 0, 'silent 不派生 related')
  gateFg.resolve()
  await pFg
  await flush()
  assert.equal(t.loading.value, false)
  assert.equal(net.filter((r) => r.url.includes('/related')).length, 1, 'related 只由前台派生一发')
})

test('T7 换BV空窗期：旧related/本地弹幕迟到包不写且旧显示已清', async () => {
  const { t, route, plan, deferred, fireBvidWatchers, flush } = await boot()
  plan({ match: '/api/video/BV_A', body: videoPayload(1, 'BV_A') })
  const relA = deferred()
  const dmA = deferred()
  plan({ match: '/api/videos/1/related', body: [{ bvid: 'REL_OLD' }], gate: relA.promise })
  plan({ match: '/api/videos/1/danmaku', body: [{ id: 9, text: '旧弹幕' }], gate: dmA.promise })
  await t.loadVideo() // A 前台落地 → related/弹幕各一发在飞
  route.params.bvid = 'BV_B'
  const gateB = deferred()
  plan({ match: '/api/video/BV_B', body: videoPayload(2, 'BV_B'), gate: gateB.promise })
  plan({ match: '/api/videos/2/related', body: [{ bvid: 'REL_NEW' }] })
  plan({ match: '/api/videos/2/danmaku', body: [{ id: 8, text: '新弹幕' }] })
  fireBvidWatchers()
  await flush()
  assert.deepEqual(t.related.value, [], '换 BV 当场清旧 related 显示')
  assert.deepEqual(t.localDanmaku.value, [], '换 BV 当场清旧本地弹幕显示')
  // 主线程复核点：新 watch 未回的窗口里，旧两发恰好迟到
  relA.resolve()
  dmA.resolve()
  await flush()
  assert.deepEqual(t.related.value, [], '旧 related 迟到包不得写入')
  assert.deepEqual(t.localDanmaku.value, [], '旧弹幕迟到包不得写入')
  gateB.resolve()
  await flush()
  await flush()
  assert.deepEqual(t.related.value.map((x) => x.bvid), ['REL_NEW'], '新片派生照常落地')
  assert.equal(t.localDanmaku.value[0]?.text, '新弹幕')
})
