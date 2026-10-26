// B 站图床（hdslb）自带缩放参数：@<宽>w.webp 等比缩到指定宽并转 webp，
// 体积通常只剩原图的几十分之一。首页两百多张卡绝不能再拉原图。
// 已带 @ 参数、非 hdslb 域或空串原样返回，交给浏览器自己消化。
export function coverThumb(url, width = 480) {
  if (!url || !/^https?:\/\/[^/]*hdslb\.com\//.test(url) || url.includes('@')) return url
  return `${url}@${width}w.webp`
}
