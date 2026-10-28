/** @type {import('tailwindcss').Config} */
export default {
  content: ['./index.html', './src/**/*.{vue,js}'],
  darkMode: 'class',
  theme: {
    extend: {
      colors: {
        // 对 B 站网页版的自绘配色基线（M2 开工时钉死基准截图后微调）
        stationBlue: '#00a1d6',
        stationPink: '#fb7299',
        // 六位色板走 CSS 变量：日夜切换只改 style.css 的 :root/html.dark 两处，
        // 全站用 bg-pageBg/text-inkMain 的地方自动跟夜；花青两色与站长室冷光不受夜权。
        pageBg: 'rgb(var(--page-bg) / <alpha-value>)',
        cardBg: 'rgb(var(--card-bg) / <alpha-value>)',
        inkMain: 'rgb(var(--ink-main) / <alpha-value>)',
        inkSub: 'rgb(var(--ink-sub) / <alpha-value>)',
        inkFaint: 'rgb(var(--ink-faint) / <alpha-value>)',
        lineSoft: 'rgb(var(--line-soft) / <alpha-value>)',
        // 站长室冷光极简，不受复刻约束（也永不入夜）
        adminBg: '#0f1115',
        adminPanel: '#171a21',
        adminLine: 'rgba(255, 255, 255, 0.08)',
        adminAccent: '#22d3ee',
      },
      fontFamily: {
        sans: [
          '-apple-system',
          'BlinkMacSystemFont',
          'PingFang SC',
          'Microsoft YaHei',
          'Noto Sans CJK SC',
          'Helvetica Neue',
          'Arial',
          'sans-serif',
        ],
      },
    },
  },
  plugins: [],
}
