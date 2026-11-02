// 全站封面取数的唯一出口（站长 11-03「缩略图要本地原图裁切，不许用 B 站 CDN 缩放」）。
//
// 三级兜底，顺序不许倒过来：
//   1. 服务端预切的档位件 /thumbs/<bvid>_<档>-<版本>.webp —— 由本站从本地原图裁出，是唯一的「缩略」来源；
//   2. 本地原图 /media/…Cover.jpg —— 存量老货还没切过档，只能拉原图（站长定：老视频不强行回填之外的补丁）；
//   3. 远程热链 coverUrl —— 原样给，一个缩放参数都不贴。
//
// 过去这里贴的是 B 站图床的 @<宽>w.webp（见 utils/cover.js 的旧规矩）。那是把缩略服务押在别人的
// CDN 上：哪天 hdslb 收了缩放参数或加了防盗链，全站卡片就集体退化成原图。现在 B 站只当图片来源。
//
// 档位表与后端 CoverThumbTiers 一字对齐，改一处必须同步改另一处（后端按这张表切，前端按这张表挑）。
export const COVER_TIERS = [
  { token: '160x100', w: 160, h: 100 }, // 安利弹窗、站长室下载队列
  { token: '352x220', w: 352, h: 220 }, // 馆藏壁卡、接下来播放、历史页、热榜、大幕下方 tab
  { token: '512x320', w: 512, h: 320 }, // 首页卡阵、分区横滚行、抽屉
  { token: '768x480', w: 768, h: 480 }, // 动态视频卡、入口卡
  { token: '1280x800', w: 1280, h: 800 }, // 首页大幕、周报头图
]

// 按「显示宽度 × DPR」挑最不浪费的一档：取第一个宽够的；都不够就给了最大档，让浏览器自己缩。
function pickTier(needW) {
  for (const tier of COVER_TIERS) {
    if (tier.w >= needW) return tier.token
  }
  return COVER_TIERS[COVER_TIERS.length - 1].token
}

// 后端递来的 coverThumbs 是逗号相接的文件名清单（形如 BV1xx411c7mD_352x220-6389012-3f2a1.webp）。
// 文件名里带版本段，所以原图重拉过一次 URL 自然改变，长缓存 immutable 才不会把旧图钉死。
function thumbFileOf(manifest, token) {
  if (!manifest) return ''
  const files = manifest.split(',')
  // 先要档位对得上，再要前缀是个像样的 BV 号：脏数据（缺 bvid、只落了一半档）宁可不显缩略。
  for (const file of files) {
    if (file.includes(`_${token}-`) && file.endsWith('.webp')) return file
  }
  for (const file of files) {
    if (file.includes(`_${token}-`)) return file // 降级成 jpg 的那批（容器里 ffmpeg 没编 libwebp 时）
  }
  return ''
}

/**
 * 取某条稿件的封面地址。
 * @param item 带 bvid / localCover / coverUrl / coverThumbs 的稿件对象（缺哪个都能活）
 * @param needW 显示宽度（CSS 像素），内部按 DPR 2 折算去挑档位；也可直接传档位 token 字符串
 * @returns {string} 可以直接给 <img :src> 的地址，一条封面都没有时回 ''
 */
export function coverSrcOf(item, needW = 512) {
  if (!item) return ''
  const token = typeof needW === 'string' ? needW : pickTier(needW * 2)
  const file = token ? thumbFileOf(item.coverThumbs, token) : '' // token 为空 = 五档都窄于需求，不拿小档充数
  if (file) return `/thumbs/${encodeURIComponent(file)}`
  if (item.localCover) return item.localCover // 存量或挑不到够宽的档：拉盘上原图（它就是这支稿的天然上限）
  return item.coverUrl || '' // 远程：原样热链，不贴任何缩放参数
}

/**
 * 大幕（英雄位）专用：尽可能用原图（站长 11-03「首页大幕尽可能用原图吧，毕竟这么大，压缩了就难看了」）。
 *
 * 大幕铺满 ~1400 CSS 像素，DPR 2 下要 2800 物理像素，而最高档只有 1280 —— 摆上去就是 2.2 倍放大，
 * 档位件的压缩块在这么大面积上直接被看见。卡片那 176×110 才需要省字节：一屏只有一张大幕，
 * 原图那几百 KB（中位 201 KB）花得起，也不进懒加载那批卡阵的预算。
 * 顺序：本地原图 → 远程热链原样（一样不贴 B 站缩放参数）；一条都没有回 ''。
 */
export function heroCoverSrc(item) {
  if (!item) return ''
  return item.localCover || item.coverUrl || ''
}

/** 只要缩略、不要原图（悬停预览这类「有就显、没有就整块不摆」的场合）。挑不到够宽的档也回 ''。 */
export function coverThumbOnly(item, needW = 352) {
  if (!item) return ''
  const token = typeof needW === 'string' ? needW : pickTier(needW * 2)
  if (!token) return '' // 同上：宁缺不放大
  const file = thumbFileOf(item.coverThumbs, token)
  return file ? `/thumbs/${encodeURIComponent(file)}` : ''
}

// 站长室下载队列专用的「外站缩放」（站长 11-03 点名的例外）：那些条目还没到站、盘上根本就没封面文件，
// 无料可切；而它显示的本来就是 B 站图、本来也在连 B 站服务器，本地切片在这既无料也无意义。
// 全站其余封面一律不贴缩放参数（那是为了「哪天 hdslb 收了参数不会全站没图」），这一处靠下面的兑底兼顶。
const HDSLB = /^https?:\/\/[^/]*hdslb\.com\//i

/** 给 B 站图床的链贴一个宽度（只认 hdslb 域；别人家的 URL 与已带 @ 的原样给）。 */
export function remoteCoverThumb(url, w) {
  if (typeof url !== 'string' || !url) return ''
  if (!HDSLB.test(url) || url.includes('@')) return url
  return `${url}@${Math.round(w)}w.webp`
}

/** 哪天缩放被收了就把 @ 段摘掉退回原图（所以调用方只需重试一次）。 */
export function remoteCoverFallback(url) {
  return typeof url === 'string' ? url.replace(/@[0-9]+[wh][^/]*$/i, '') : ''
}

/** 同源本地件判定：只有以 / 开头的站内绝对路径才算（旧写法把「不带斜杠的相对串」也算本地，判得过宽）。 */
export function isLocalMediaPath(url) {
  return typeof url === 'string' && url.startsWith('/') && !url.startsWith('//')
}
