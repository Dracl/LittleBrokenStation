// 墙上本地封面的小图缓存（站长 11-03「馆藏壁加载掉帧」的第二刀）。
//
// 病灶是字节与解码量：data/videos 下 168 张本地封面合计 35.7 MB，中位 201 KB，最大 625 KB，
// 而墙上的卡片只有 176×110 CSS 像素，每张原图的解码面积是显示所需的上百倍。
// 远程图床那路有 hdslb 的 @<宽>w.webp 兜着（见 utils/cover.js），本地这路一直裸奔。
//
// 做法：首访时把原图交给 createImageBitmap 缩放（解码与缩放都跑在浏览器后台线程，不占主线程），
// 编成 352×220（卡片 16:10 的 2 倍）的 webp 存进 IndexedDB；之后再开墙，卡片读的是每张几 KB 的小图，解码量只剩零头。
// 只缓存同源 /media 下的本地图；任何一步失败都原样回退原图地址——墙不能因为缓存不出图就留空洞。
//
// 并发限 6：一次开墙几百张同时 fetch + 解码，等于自己把自己打回掉帧。
export const WALL_THUMB_W = 352 // 卡片 176 × DPR 2
export const WALL_THUMB_H = 220 // 卡片 110 × DPR 2（16:10，跟 CanvasWall 的 CARD_W/CARD_H 同比例）

const DB_NAME = 'lbs-wall-thumbs'
const STORE = 'thumbs'
// 库版本 3：卡片比例两度改过（16:10 → 16:9 → 又回 16:10），上一代 352×198 的小图对新卡形没意义，整仓作废。
// 存储键同时带上尺幅（见 cacheKey），今后再改卡形自然另成一个世代，旧图不会被读到。
const DB_VERSION = 3
const MAX_ENTRIES = 4000 // 个人站点的量级远用不到，纯防空转膨胀
const CONCURRENCY = 6

function cacheKey(url) {
  return `${WALL_THUMB_W}x${WALL_THUMB_H}|${url}`
}

const objUrls = new Map() // 原图地址 -> objectURL（本页内命中，省一次 IndexedDB 读）
const failed = new Set() // 做不出小图的地址：peek 直接回原图，免得重渲染把元素上已写的 src 抹成空
const inflight = new Map() // 原图地址 -> Promise，同一张不重复解码
let queue = [] // 待做的活
let running = 0
let dbPromise = null
let overflowed = false

function isLocalMedia(url) {
  if (!url) return false
  if (/^(https?:)?\/\//i.test(url)) return false // 远程图床交给 coverThumb
  if (/^(data|blob):/i.test(url)) return false
  return url.startsWith('/media/') || !url.startsWith('/') // 同源相对路径或 /media 绝对路径
}

function toBlob(canvas, type, quality) {
  return new Promise((resolve) => {
    try {
      canvas.toBlob((b) => resolve(b || null), type, quality)
    } catch {
      resolve(null)
    }
  })
}

function openDbSafe() {
  if (dbPromise) return dbPromise
  dbPromise = new Promise((resolve) => {
    if (typeof indexedDB === 'undefined') return resolve(null)
    let req
    try {
      req = indexedDB.open(DB_NAME, DB_VERSION)
    } catch {
      return resolve(null)
    }
    req.onupgradeneeded = () => {
      const db = req.result
      // 升级就是换世代：旧尺幅的小图一律捣翻重做
      if (db.objectStoreNames.contains(STORE)) db.deleteObjectStore(STORE)
      db.createObjectStore(STORE)
    }
    req.onsuccess = () => {
      const db = req.result
      // 条目数顶到上限就整仓倒空重来（个人站点的量级走不到，防的是长久堆积）
      try {
        const c = db.transaction(STORE, 'readonly').objectStore(STORE).count()
        c.onsuccess = () => {
          if (c.result > MAX_ENTRIES) db.transaction(STORE, 'readwrite').objectStore(STORE).clear()
        }
      } catch {
        /* 数不出来就算了 */
      }
      resolve(db)
    }
    req.onerror = () => resolve(null) // 隐私模式/被禁：退回纯内存
  })
  return dbPromise
}

function idbGet(db, key) {
  return new Promise((resolve) => {
    if (!db) return resolve(null)
    try {
      const tx = db.transaction(STORE, 'readonly')
      const req = tx.objectStore(STORE).get(key)
      req.onsuccess = () => resolve(req.result || null)
      req.onerror = () => resolve(null)
    } catch {
      resolve(null)
    }
  })
}

function idbPut(db, key, blob) {
  if (!db || overflowed) return
  try {
    const tx = db.transaction(STORE, 'readwrite')
    tx.objectStore(STORE).put(blob, key)
    tx.onabort = () => {
      // 配额顶到天花板就别再写了，往后一律走原图
      if (tx.error && tx.error.name === 'QuotaExceededError') overflowed = true
    }
  } catch {
    /* 同上 */
  }
}

async function makeThumb(url) {
  const res = await fetch(url, { credentials: 'same-origin', referrerPolicy: 'no-referrer' })
  if (!res.ok) return null
  const blob = await res.blob()
  // 按原尺寸解码（解码在浏览器后台线程），再自己算居中裁剪：createImageBitmap 的 resizeWidth/Height
  // 是拉伸不保比例，真用上会把竖封面压成扁的。卡片那边是 object-cover，小图就得先替它把同一刀裁好。
  const bmp = await createImageBitmap(blob)
  const target = WALL_THUMB_W / WALL_THUMB_H
  const src = bmp.width / bmp.height
  let sx = 0
  let sy = 0
  let sw = bmp.width
  let sh = bmp.height
  if (src > target) {
    sw = bmp.height * target
    sx = (bmp.width - sw) / 2
  } else if (src < target) {
    sh = bmp.width / target
    sy = (bmp.height - sh) / 2
  }
  const canvas = document.createElement('canvas')
  canvas.width = WALL_THUMB_W
  canvas.height = WALL_THUMB_H
  const ctx = canvas.getContext('2d')
  ctx.imageSmoothingEnabled = true
  ctx.imageSmoothingQuality = 'high'
  ctx.drawImage(bmp, sx, sy, sw, sh, 0, 0, WALL_THUMB_W, WALL_THUMB_H)
  bmp.close?.()
  let out = await toBlob(canvas, 'image/webp', 0.82)
  if (!out) out = await toBlob(canvas, 'image/jpeg', 0.82) // Safari 编不出 webp
  if (!out) return null
  const db = await openDbSafe()
  idbPut(db, cacheKey(url), out)
  return out
}

async function work(url) {
  const db = await openDbSafe()
  const hit = await idbGet(db, cacheKey(url))
  if (hit && hit.size) {
    const u = URL.createObjectURL(hit)
    objUrls.set(url, u)
    return u
  }
  const small = await makeThumb(url)
  if (!small) return null
  const u = URL.createObjectURL(small)
  objUrls.set(url, u)
  return u
}

function pump() {
  while (running < CONCURRENCY && queue.length) {
    const job = queue.shift()
    running += 1
    job()
      .catch(() => null)
      .then((u) => job.done(u || null))
      .finally(() => {
        running -= 1
        pump()
      })
  }
}

/**
 * 取一张卡该显示的图：命中缓存立刻同步返回，没命中就排队去做，做好后用 peekWallThumb 拿。
 * 返回 Promise<string>——失败时回退成原图地址，绝不让卡片空着。
 */
export function wallThumb(url) {
  if (!url) return Promise.resolve('')
  if (!isLocalMedia(url)) return Promise.resolve(url)
  const hit = objUrls.get(url)
  if (hit) return Promise.resolve(hit)
  if (failed.has(url)) return Promise.resolve(url) // 做过的不重试：一次开墙别把同一张坏图反复拉七遍
  const fly = inflight.get(url)
  if (fly) return fly
  const p = new Promise((resolve) => {
    const job = () => work(url)
    job.done = resolve
    queue.push(job)
    pump()
  }).then((u) => {
    if (!u) failed.add(url) // 记账：模板重渲染时 peek 照样给原图，不会把已经显影的卡打回空白
    return u || url
  })
  inflight.set(url, p)
  p.finally(() => inflight.delete(url))
  return p
}

/** 同步看一眼：小图到位了就给地址，没到位给空串（模板据此先不挂 src，避免整张原图去解码）。 */
export function peekWallThumb(url) {
  if (!url) return ''
  const hit = objUrls.get(url)
  if (hit) return hit
  return failed.has(url) ? url : ''
}

/** 是否本地件：模板要据此区分「等小图」与「远程图床直接给」。 */
export function isLocalCoverUrl(url) {
  return isLocalMedia(url)
}

/** 站长重拉封面后清缓存用（调用方：utils/videoActions.js 里重拉媒体且 mediaReplaced 为真那一发；不再需要临时脚本）。 */
export async function clearWallThumbs() {
  objUrls.clear()
  failed.clear()
  inflight.clear()
  queue = []
  const db = await openDbSafe()
  if (!db) return
  try {
    const tx = db.transaction(STORE, 'readwrite')
    tx.objectStore(STORE).clear()
  } catch {
    /* 清不掉就算了 */
  }
}
