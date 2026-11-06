/* ─────────────────────────────────────────────────────────────────────────
   小破分站 · 周报海报画笔（分派口）
   ---------------------------------------------------------------------------
   WeeklyPage.vue 只认这里放出去的四个口：posterTheme / paintPoster / posterBackdrop /
   posterFooter，外加几张常量表。真正画画的是各套皮肤，一律平铺在 SKINS 表里：

     · weeklyPosterEditorial.js  —— 默认风格（网格 + 单强调色，界面上叫「默认风格」）
     · weeklyPosterConstruct.js  —— 构成主义（红黑纸 · 斜切与几何，11-03 新画）
     · weeklyPosterKit.js        —— 大家共用的字体、量宽裁字、千分位、封面裁切、浅模糊

   再加一套皮肤只要三步，不用碰这里的分派逻辑：
     1. 新写 weeklyPoster<名字>.js，export theme / paint / backdrop / footer 四个函数
     2. 在下面的 SKINS 里添一行，把这四个函数递进来
     3. 到 weeklyPosterKit.js 的 POSTER_STYLES 里添一条 { key, label, hint }
   皮肤之间不许互相 import，各自只认 kit；这样一张塌了不牵连另一张。

   分派只看 opts.style，认不住一律回默认风格：不显式选风格的调用（含旧代码）画出来跟以前一样。
   posterFooter 没有 opts 参数，风格记在 theme 对象上（posterTheme 会盖上 style 字段），
   所以调用方照旧把 posterTheme(opts) 的结果递进来就行。
   ───────────────────────────────────────────────────────────────────────── */

import { paintEditorial, editorialBackdrop, editorialFooter, editorialTheme } from './weeklyPosterEditorial.js'
import { paintConstruct, constructBackdrop, constructFooter, constructTheme } from './weeklyPosterConstruct.js'

export {
  POSTER_WIDTH,
  POSTER_ACCENTS,
  POSTER_THEMES,
  POSTER_STYLES,
  POSTER_CONSTRUCT_ACCENTS,
  POSTER_QR,
  posterQr,
} from './weeklyPosterKit.js'

/** 认不住就回这一套，宁可画成老样子也不画成空白。 */
const DEFAULT_STYLE = 'editorial'

/** 皮肤登记处：一套皮肤 = 四个口。新皮肤往这里添一行就够了。 */
const SKINS = {
  editorial: {
    theme: editorialTheme,
    paint: paintEditorial,
    backdrop: editorialBackdrop,
    footer: editorialFooter,
  },
  construct: {
    theme: constructTheme,
    paint: paintConstruct,
    backdrop: constructBackdrop,
    footer: constructFooter,
  },
}

/** 按 style 取皮肤，没登记的一律回默认那套。 */
function skinOf(style) {
  return SKINS[style] ?? SKINS[DEFAULT_STYLE]
}

/** 解出版面色。各套皮肤自己配色，这里只负责分派并盖上 style 印记。 */
export function posterTheme(opts) {
  const style = SKINS[opts?.style] ? opts.style : DEFAULT_STYLE
  return { ...skinOf(style).theme(opts), style }
}

/** 画完海报正文，返回落笔后的底部 y。预览与下载都调它，所以它必须不碰状态。 */
export function paintPoster(g, M, W, imgs, opts) {
  return skinOf(opts?.style).paint(g, M, W, imgs, opts)
}

/** 纸底：画在所有内容底下。默认风格是渐变加光晕，构成主义是纸色加大斜切。 */
export function posterBackdrop(g, W, H, opts) {
  return skinOf(opts?.style).backdrop(g, W, H, opts)
}

/** 落款。风格跟着 theme 走，调用方不必再递 opts。 */
export function posterFooter(g, M, T, W, H) {
  return skinOf(T?.style).footer(g, M, T, W, H)
}
