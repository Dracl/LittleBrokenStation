import { mentionHtml } from './mention'

// 评论正文渲染公共管线：转义 → @高亮 → [表情] 换图。视频评论区与动态评论区共用一份，别复制第二份。
// emoteMap 是后端定格好的「转义符 → 图片地址」（本地 /emotes/ 优先，下载失败为远程热链）。
export function richHtml(text, emoteMap) {
  let html = mentionHtml(text)
  for (const [seq, loc] of Object.entries(emoteMap ?? {})) {
    if (!seq || !loc) continue
    const safeSrc = String(loc).replace(/"/g, '&quot;')
    const alt = String(seq).replace(/"/g, '&quot;')
    html = html.split(seq).join(`<img src="${safeSrc}" alt="${alt}" referrerpolicy="no-referrer" class="mx-[1px] inline h-[18px] align-text-bottom" />`)
  }
  return html
}
