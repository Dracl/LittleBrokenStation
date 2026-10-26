// 轻量 mp4 容器探测：Range 读头尾，解析 moov/trak，算出视频/音频分轨平均码率。
// 不引任何依赖；moov 恰好跨在取样区外解析不出时返回 null，调用方如实降级「—」。

async function getRange(url, from, to) {
  const res = await fetch(url, { headers: { Range: `bytes=${from}-${to}` }, cache: 'no-store' })
  if (!res.ok && res.status !== 206) return null
  const buf = await res.arrayBuffer()
  return buf.byteLength ? new DataView(buf) : null
}

// 总大小由调用方从 HEAD 的 content-length 提供，这里不重复探。
async function headAndTail(url, size) {
  const HEAD = 2 * 1024 * 1024
  const head = await getRange(url, 0, HEAD - 1)
  let moov = findMoov(head)
  if (moov) return moov
  if (size && size > HEAD) {
    const tail = await getRange(url, Math.max(0, size - 2 * HEAD), size - 1)
    moov = findMoov(tail)
    if (moov) return moov
  }
  return null
}

// 顶层扫 box 找 moov，返回其内容区的 DataView（含子 box）。
function findMoov(dv) {
  if (!dv) return null
  let off = 0
  while (off + 8 <= dv.byteLength) {
    let size = dv.getUint32(off)
    const type = readType(dv, off + 4)
    let header = 8
    if (size === 1) {
      // 64 位 largesize
      size = Number(dv.getBigUint64(off + 8))
      header = 16
    } else if (size === 0) {
      size = dv.byteLength - off
    }
    if (size < header || off + size > dv.byteLength) return null
    if (type === 'moov') return dv.buffer.slice(off + header, off + size)
    off += size
  }
  return null
}

function readType(dv, at) {
  return String.fromCharCode(dv.getUint8(at), dv.getUint8(at + 1), dv.getUint8(at + 2), dv.getUint8(at + 3))
}

// 遍历一个 box 内容区里的直接子 box。
function forEachBox(buf, cb) {
  const dv = buf instanceof DataView ? buf : new DataView(buf)
  let off = 0
  while (off + 8 <= dv.byteLength) {
    let size = dv.getUint32(off)
    const type = readType(dv, off + 4)
    let header = 8
    if (size === 1) {
      size = Number(dv.getBigUint64(off + 8))
      header = 16
    } else if (size === 0) {
      size = dv.byteLength - off
    }
    if (size < header || off + size > dv.byteLength) return
    cb(type, dv.buffer.slice(off + header, off + size))
    off += size
  }
}

function readHandler(buf) {
  const dv = new DataView(buf)
  // hdlr：ver+flags(4) + predefined(4) + handler(4)
  if (dv.byteLength < 12) return null
  return readType(dv, 8)
}

function readMdhd(buf) {
  const dv = new DataView(buf)
  const ver = dv.getUint8(0)
  if (ver === 1) {
    return { timescale: dv.getUint32(20), duration: Number(dv.getBigUint64(24)) }
  }
  if (dv.byteLength < 20) return null
  return { timescale: dv.getUint32(12), duration: dv.getUint32(16) }
}

function readStsz(buf) {
  const dv = new DataView(buf)
  const uniform = dv.getUint32(4)
  const count = dv.getUint32(8)
  if (uniform > 0) return uniform * count
  let sum = 0
  for (let i = 0; i < count && 12 + i * 4 + 4 <= dv.byteLength; i++) {
    sum += dv.getUint32(12 + i * 4)
  }
  return sum
}

// 返回 { video: Kbps|null, audio: Kbps|null }，全空返回 null。
export async function probeTrackBitrates(url, totalSize) {
  try {
    const moov = await headAndTail(url, totalSize)
    if (!moov) return null
    const out = { video: null, audio: null }
    forEachBox(moov, (type, trak) => {
      if (type !== 'trak') return
      let handler = null
      let timescale = 0
      let duration = 0
      let bytes = null
      forEachBox(trak, (t2, mdia) => {
        if (t2 !== 'mdia') return
        forEachBox(mdia, (t3, body) => {
          if (t3 === 'hdlr') handler = readHandler(body)
          if (t3 === 'mdhd') {
            const m = readMdhd(body)
            if (m) {
              timescale = m.timescale
              duration = m.duration
            }
          }
          if (t3 === 'minf') {
            forEachBox(body, (t4, stbl) => {
              if (t4 !== 'stbl') return
              forEachBox(stbl, (t5, stsz) => {
                if (t5 === 'stsz') bytes = readStsz(stsz)
              })
            })
          }
        })
      })
      if (!handler || !timescale || duration <= 0 || bytes == null) return
      const kbps = Math.round((bytes * 8) / (duration / timescale) / 1000)
      if (handler === 'vide') out.video = kbps
      else if (handler === 'soun') out.audio = kbps
    })
    return out.video || out.audio ? out : null
  } catch {
    return null
  }
}
