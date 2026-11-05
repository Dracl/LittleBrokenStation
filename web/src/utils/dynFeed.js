// ─── 动态列表的展示口径（DynamicsPage 与 UP 空间动态 tab 共用，防两处漂移）───

// 开播提醒（LIVE）全站退场（站长 11-06 点单）：开播是短保商品，点开动态页时早下播了，
// 本地存的开播条没有观赏价值。本地档照采不误，只是这里不摆。
export function isLiveDyn(x) {
  return String(x?.type || '').toUpperCase().includes('LIVE')
}

export function keepFeed(list) {
  return (list ?? []).filter((x) => !isLiveDyn(x))
}

// 翻页游标一律取「原始返回」的尾巴（含被滤掉的开播条）：拿过滤后的尾巴当游标，
// 整页都是开播条时游标不前进，「看更多」会原地反复重拉同一段。
export function tailCursor(list) {
  const arr = list ?? []
  const tail = arr[arr.length - 1]
  return tail ? { id: tail.id ?? 0, time: tail.publishedAtUtc ?? '' } : null
}
