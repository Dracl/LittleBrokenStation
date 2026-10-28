// ─── 日夜开关：六位色板住在 style.css 的 CSS 变量里，这里只管把 html.dark 掰上掰下。
// 选中存 localStorage['lbs-theme']（'light'|'dark'）；没选过的客人跟系统 prefers-color-scheme。
// index.html 里另有一段同口径的前置脚本防刷新闪白，两边改口径要一起改。

const KEY = '***'

function preferred() {
  let saved = null
  try {
    saved = localStorage.getItem(KEY)
  } catch {
    /* 隐私模式存不了：跟系统走就是了 */
  }
  if (saved === 'light' || saved === 'dark') return saved
  return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'
}

export function applyTheme() {
  document.documentElement.classList.toggle('dark', preferred() === 'dark')
}

export function isDark() {
  return document.documentElement.classList.contains('dark')
}

/// 一键翻日夜：客人一旦亲手选过，就不再跟系统摇摆。
export function toggleTheme() {
  try {
    localStorage.setItem(KEY, preferred() === 'dark' ? 'light' : 'dark')
  } catch {
    /* 存不下也先翻着看，刷新回到系统值不碍事 */
  }
  applyTheme()
}

// 客人还没亲手选过时，系统的日夜变化要跟着走；选过之后系统说了不算。
try {
  window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
    let saved = null
    try {
      saved = localStorage.getItem(KEY)
    } catch {
      saved = null
    }
    if (saved !== 'light' && saved !== 'dark') applyTheme()
  })
} catch {
  /* 老掉牙的浏览器连 MediaQueryList 都不给听，认了 */
}
