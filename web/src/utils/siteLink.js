/**
 * 全站分享链接一张嘴：门面地址由站长室「分享链接设置」配置（settings 表 share-base-url，
 * 经 GET /api/settings 读出）；没配置、读失败、或读来的值不成形，一律回退跟随访问地址——
 * 从哪个门分享出去的链接都得点得开。
 *
 * 页面级缓存：一次 /api/settings 拉取全员共用（在途去重），分享口都在点击瞬间，多这一发网络不心疼。
 * 站长改完设置可调 forgetSiteBase() 让本页立刻换头，不必刷页。
 */
let cachedBase = null
let inflight = null

/** 取门面地址（不带尾斜杠）。 */
export function siteBase() {
  if (cachedBase) return Promise.resolve(cachedBase)
  if (!inflight) {
    inflight = fetch('/api/settings')
      .then((res) => (res.ok ? res.json() : null))
      .then((data) => pickBase(data))
      .catch(() => window.location.origin)
      .then((base) => {
        inflight = null
        cachedBase = base
        return base
      })
  }
  return inflight
}

/** 忘掉缓存（站长保存新门面地址后调用，本页后续分享立刻吃新值）。 */
export function forgetSiteBase() {
  cachedBase = null
}

function pickBase(data) {
  const configured = typeof data?.shareBaseUrl === 'string' ? data.shareBaseUrl.trim().replace(/\/+$/, '') : ''
  return configured || window.location.origin
}
