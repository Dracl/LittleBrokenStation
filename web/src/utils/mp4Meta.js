// 轻量 mp4 容器探测：Range 读头尾，解析 moov/trak，一次取样同时算出分轨平均码率与真实编码（stsd 的 fourcc）。
// 编码必须由容器说了算：canPlayType 问的是「浏览器放不放得了」，拿它猜编码会把 HEVC 也说成 H.264。
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

// 常见 sample entry 的人话标签；没列出的 fourcc 原样当标签用，不硬套一个像样的名字骗人。
const FOURCC_LABELS = {
  avc1: 'H.264',
  avc3: 'H.264',
  hvc1: 'HEVC',
  hev1: 'HEVC',
  av01: 'AV1',
  vp09: 'VP9',
  mp4a: 'AAC',
  'fLaC': 'FLAC',
  'ac-3': 'AC-3',
  'ec-3': 'E-AC-3',
  opus: 'Opus',
}

function labelOf(fourcc) {
  return fourcc ? FOURCC_LABELS[fourcc] ?? fourcc : null
}

function isFourcc(s) {
  return !!s && [...s].every((ch) => ch.codePointAt(0) >= 0x20 && ch.codePointAt(0) <= 0x7e)
}

// stsd 内容区：version+flags(4) + entry_count(4)，之后每个 sample entry 头 4 字节 size、4 字节 format。
// 只取首个 sample entry（一条 trak 一个编码），取样恰好把 entry 截断就当没探到，不猜。
function readStsdFormat(buf) {
  const dv = buf instanceof DataView ? buf : new DataView(buf)
  let off = 8
  while (off + 8 <= dv.byteLength) {
    const size = dv.getUint32(off)
    if (size < 8 || off + size > dv.byteLength) return null
    const format = readType(dv, off + 4)
    return isFourcc(format) ? format : null
  }
  return null
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

// 在同一份 moov 上把两件事一次办完：码率靠 stsz 累加，编码靠 stsd 的 sample entry fourcc。
// 两者各自成立 —— 缺 stsz 也能报编码，缺 stsd 也能报码率，互不牵连。
function probeTracks(moov) {
  const out = { videoKbps: null, audioKbps: null, videoFourcc: null, audioFourcc: null }
  forEachBox(moov, (type, trak) => {
    if (type !== 'trak') return
    let handler = null
    let timescale = 0
    let duration = 0
    let bytes = null
    let format = null
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
            forEachBox(stbl, (t5, body5) => {
              if (t5 === 'stsz') bytes = readStsz(body5)
              if (t5 === 'stsd' && !format) format = readStsdFormat(body5)
            })
          })
        }
      })
    })
    if (handler !== 'vide' && handler !== 'soun') return
    const kbps = timescale && duration > 0 && bytes != null ? Math.round((bytes * 8) / (duration / timescale) / 1000) : null
    if (handler === 'vide') {
      if (kbps != null) out.videoKbps = kbps
      if (format) out.videoFourcc = format
    } else {
      if (kbps != null) out.audioKbps = kbps
      if (format) out.audioFourcc = format
    }
  })
  return out
}

// 返回 { videoKbps, audioKbps, videoFourcc, videoLabel, audioFourcc, audioLabel }（拿不到的字段为 null），
// 全探不出返回 null。totalSize 由调用方从 HEAD 的 content-length 提供，没有就只取样头部。
export async function probeMp4(url, totalSize) {
  try {
    const moov = await headAndTail(url, totalSize)
    if (!moov) return null
    const t = probeTracks(moov)
    if (t.videoKbps == null && t.audioKbps == null && !t.videoFourcc && !t.audioFourcc) return null
    return { ...t, videoLabel: labelOf(t.videoFourcc), audioLabel: labelOf(t.audioFourcc) }
  } catch {
    return null
  }
}

// 老签名留着：只关心码率的调用方不必跟着改。内部转调 probeMp4，同一份取样、同样一次 Range。
export async function probeTrackBitrates(url, totalSize) {
  const r = await probeMp4(url, totalSize)
  if (!r) return null
  const out = { video: r.videoKbps, audio: r.audioKbps }
  return out.video || out.audio ? out : null
}
