// 粉丝数折算：名片与 UP 空间页头共用一把尺子（形状只一份，别在两处各写一遍万/亿）。
export function fmtFans(n) {
  if (n == null) return '—'
  if (n >= 10000) {
    const wan = n / 10000
    return (wan >= 100 ? Math.round(wan) : Math.round(wan * 10) / 10).toString().replace(/\.0$/, '') + '万'
  }
  return n.toString()
}
