// 全站分享一张嘴 siteLink 的回归（utils/siteLink.js）：
// 门面地址来自站长室「分享链接设置」（GET /api/settings 的 shareBaseUrl，11-15 站长点单）。
// 钉死：配置值原样带尾斜杠剃净；没配 / 空串 / 读失败 / 非 200 一律回退访问地址；
// 在途去重与页面级缓存（二次调用不二次拉）；forgetSiteBase 后新值立刻生效。
import { test } from 'node:test'
import assert from 'node:assert/strict'

globalThis.window = { location: { origin: 'http://localhost:8090' } }

const { siteBase, forgetSiteBase } = await import('../src/utils/siteLink.js')

function stubFetch(impl) {
  globalThis.fetch = impl
  return () => { globalThis.fetch = undefined }
}

test('T1 配置了门面：原样吃，尾斜杠剃净', async () => {
  forgetSiteBase()
  const un = stubFetch(async () => ({ ok: true, json: async () => ({ shareBaseUrl: 'https://lbs.sijii.space/' }) }))
  try {
    assert.equal(await siteBase(), 'https://lbs.sijii.space')
  } finally { un() }
})

test('T2 没配置 / 空串 / 只有空白：回退访问地址', async () => {
  for (const value of [null, '', '   ']) {
    forgetSiteBase()
    const un = stubFetch(async () => ({ ok: true, json: async () => ({ shareBaseUrl: value }) }))
    try {
      assert.equal(await siteBase(), 'http://localhost:8090')
    } finally { un() }
  }
})

test('T3 读崩（网络炸 / 非 200 / 响应没这个字段）：照样回退访问地址', async () => {
  const cases = [
    async () => { throw new Error('network down') },
    async () => ({ ok: false, json: async () => ({}) }),
    async () => ({ ok: true, json: async () => ({}) }),
  ]
  for (const impl of cases) {
    forgetSiteBase()
    const un = stubFetch(impl)
    try {
      assert.equal(await siteBase(), 'http://localhost:8090')
    } finally { un() }
  }
})

test('T4 在途去重 + 页面级缓存：连续四次分享只拉一发', async () => {
  forgetSiteBase()
  let pulls = 0
  const un = stubFetch(async () => {
    pulls += 1
    return { ok: true, json: async () => ({ shareBaseUrl: 'https://lbs.sijii.space' }) }
  })
  try {
    const got = await Promise.all([siteBase(), siteBase(), siteBase(), siteBase()])
    assert.deepEqual(got, Array(4).fill('https://lbs.sijii.space'))
    assert.equal(pulls, 1)
    // 再调仍是缓存，不发第二发
    assert.equal(await siteBase(), 'https://lbs.sijii.space')
    assert.equal(pulls, 1)
  } finally { un() }
})

test('T5 站长改完forget立刻换头', async () => {
  forgetSiteBase()
  const un = stubFetch(async () => ({ ok: true, json: async () => ({ shareBaseUrl: 'http://192.168.1.8:9000' }) }))
  try {
    assert.equal(await siteBase(), 'http://192.168.1.8:9000')
  } finally { un() }
})
