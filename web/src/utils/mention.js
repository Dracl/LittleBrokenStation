// B 站风 @提及高亮：先把 HTML 全部转义，再把 @昵称包成 stationBlue 的 span，注入安全。
export function mentionHtml(text) {
  const esc = String(text ?? '')
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    // 有些接口把 @ 存成 [@user:mid_昵称] 转义符，先还原成 @昵称
    .replace(/\[@user:\d+_([^\]]+)\]/g, '@$1')
  // @昵称：前面不能紧跟字母数字或点（避开邮箱），遇空白/标点后截断，最长 32 字符
  return esc.replace(/(?<![\w.])@[^@\s，。！？、；：:;,.!?()（）【】[\]"'「」《》\-]{1,32}/g, '<span class="text-stationBlue">$&</span>')
}
