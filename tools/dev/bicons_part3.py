# -*- coding: utf-8 -*-
"""bicons part3：官方 Hi-Res 徽记（波形 + 「Hi-Res无损」字组）。

来源：B 站播放器 npd.586.a82c83f3.js 模块 780（国内版 getHiresIcon()），
npd-ctrl-flac 控件用的正是这份 path——站长要求「直接去 b站官方扣，不要自己画」，
故原样照抄，仅经 clean() 去掉 xmlns/data-pointer 壳子；path 无 fill，
靠 currentColor 上色（官方也是这么玩的），控件条上给蓝色调。
国际版（模块 42211，viewBox 70x23）留在 OH-Works/hi-res-scratch/hires-intl.svg 备档，暂不进前端。
"""
import json
import re
import sys

RAW = {}

# [已移除：原始 B 站图标 SVG 源文，避免素材随仓库分发]


def clean(svg):
    m = re.search(r'viewBox="([^"]+)"', svg)
    vb = m.group(1)
    inner = re.sub(r'^.*?viewBox="[^"]*"', '', svg, count=1)
    inner = re.sub(r'</svg>\s*$', '', inner)  # 不连尖括号一口吃掉（库里 15 张老图都被咬缺过尾，新图不重蹈）
    inner = inner.lstrip('>')  # viewBox 后的自闭合尖括号不当图形使
    inner = re.sub(r'\s*(data-v-[a-z0-9]+|data-pointer)="[^"]*"', '', inner)
    inner = re.sub(r'\s+xmlns="[^"]*"', '', inner)
    # 官方原图外层 g 自带 opacity=".9"（B 站控件条上的淡出惯例）；站长要纯白，剥掉。
    inner = re.sub(r'^<g opacity="\.9"', '<g', inner)
    # 官方这份 path 裸得彻底（连一个 fill 属性都没有，靠页面 CSS 类 .bpx-ctrl-flac 上色）；
    # 分站没有那套 CSS，svg 默认 fill=黑——深底控件条上直接隐身。外层 g 声明 currentColor，
    # 颜色归控件条的 text-* 类说了算（其它 bicons 是 path 自带 fill="#fff"，同一条路）。
    if 'fill=' not in inner:
        # 不能用 replace('<g ')：上一行剥完 opacity 后开头已是无空格的 <g>，会静默失配。
        m = re.match(r'^<g(\s[^>]*)?>', inner)
        if m:
            inner = '<g fill="currentColor"' + (m.group(1) or '') + '>' + inner[m.end():]
    inner = re.sub(r'\s+', ' ', inner).strip()
    return vb, inner


def main():
    vb, inner = clean(RAW['hiRes'])
    line = 'export const %s = { vb: %s, inner: %s }' % ('biHiRes', json.dumps(vb), json.dumps(inner))
    with open(sys.argv[1], 'a', encoding='utf-8') as f:
        f.write('\n'.join([line]) + '\n')
    print('part3 appended: biHiRes (viewBox', vb + ')')


main()
