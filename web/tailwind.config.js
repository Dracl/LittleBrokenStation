/** @type {import('tailwindcss').Config} */
export default {
  content: ['./index.html', './src/**/*.{vue,js}'],
  theme: {
    extend: {
      colors: {
        // 对 B 站网页版的自绘配色基线（M2 开工时钉死基准截图后微调）
        stationBlue: '#00a1d6',
        stationPink: '#fb7299',
        pageBg: '#ffffff',
        cardBg: '#ffffff',
        inkMain: '#18191c',
        inkSub: '#61666d',
        inkFaint: '#9499a0',
        lineSoft: '#e3e5e7',
        // 站长室冷光极简，不受复刻约束
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
