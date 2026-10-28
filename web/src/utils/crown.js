// 席位冠冕的几何：一顶「贴着头像圆弧」的冠，底边就是圆周上的一小段弧，尖沿半径长出去。
// 弹幕那颗小圆（canvas Path2D）与头像角标（SVG）共用这一套数，两处才不会长成两副冠。
// 口径来自四季在卡片上亲手拧定的：3 尖、冠高 0.16d、离边 0.03d、张角 33°、挂在右上 45°，白边 0.014d。

const TIP_SEQ = {
  3: [1, 0.45, 1.18, 0.45, 1],
  5: [1, 0.42, 0.92, 0.42, 1.16, 0.42, 0.92, 0.42, 1],
}

export const CROWN = {
  h: 0.16, // 中尖高 / 圆径
  gap: 0.03, // 冠底与圆边之间的缝 / 圆径
  span: 33, // 冠包住圆顶的张角（度）
  tips: 3,
  center: -45, // 挂在哪：屏幕角，0=右、-90=上 → 右上 45°
  sw: 0.014, // 白边宽度 / 圆径（压在冠身轮廓上，内外各占一半）
  stroke: '#ffffff',
  gold: '#f5c542',
  silver: '#b7c2cf',
  gem: '#e0275f',
}

function polar(cx, cy, r, degA) {
  const a = (degA * Math.PI) / 180
  return [cx + r * Math.cos(a), cy + r * Math.sin(a)]
}

/**
 * 生成贴弧冠。
 * @param {number} cx 头像圆心 x
 * @param {number} cy 头像圆心 y
 * @param {number} d  头像直径
 * @param {object} [opts] 覆盖 CROWN 里的口径（调参用）
 * @returns {{ path: string, gem: {cx:number,cy:number,r:number}, reach: number }}
 *          reach = 冠尖比圆边多出多少，位图按它扩上/右空间，免得被切角。
 */
export function crownPath(cx, cy, d, opts = {}) {
  const h = (opts.h ?? CROWN.h) * d
  const gap = (opts.gap ?? CROWN.gap) * d
  const span = opts.span ?? CROWN.span
  const center = opts.center ?? CROWN.center
  const seq = TIP_SEQ[opts.tips ?? CROWN.tips] ?? TIP_SEQ[3]
  const rBase = d / 2 + gap
  const start = center - span / 2
  const step = span / (seq.length - 1)
  const f = (n) => n.toFixed(2)

  const apex = seq.map((k, i) => polar(cx, cy, rBase + h * k, start + step * i))
  const bEnd = polar(cx, cy, rBase, start + step * (seq.length - 1))
  const bStart = polar(cx, cy, rBase, start)

  let path = `M ${f(apex[0][0])} ${f(apex[0][1])}`
  for (let i = 1; i < apex.length; i++) path += ` L ${f(apex[i][0])} ${f(apex[i][1])}`
  path += ` L ${f(bEnd[0])} ${f(bEnd[1])}`
  // 底边沿圆周逆着回去（屏幕坐标 y 向下，角度递减即逆时针 → sweep 0）
  path += ` A ${f(rBase)} ${f(rBase)} 0 0 0 ${f(bStart[0])} ${f(bStart[1])} Z`

  const gemAt = polar(cx, cy, rBase + h * 0.34, center)
  return {
    path,
    gem: { cx: gemAt[0], cy: gemAt[1], r: Math.max(0.6, h * 0.15) },
    reach: gap + h * Math.max(...seq),
  }
}
