#!/usr/bin/env python3
# part2b：wide/web/full 三枚 + part3 占位（subtitle 太小直接内联）。追加进 bicons.js
import re, json, sys

RAW = {}

# [已移除：原始 B 站图标 SVG 源文，避免素材随仓库分发]

# [已移除：原始 B 站图标 SVG 源文，避免素材随仓库分发]

# [已移除：原始 B 站图标 SVG 源文，避免素材随仓库分发]

def clean(svg):
    m = re.search(r'viewBox="([^"]+)"', svg)
    vb = m.group(1)
    inner = re.sub(r'^.*?viewBox="[^"]*"', '', svg, count=1)
    inner = re.sub(r'></svg>\s*$', '', inner)
    inner = re.sub(r'<defs>.*?</defs>', '', inner, flags=re.S)
    inner = re.sub(r'\s*clip-path="url\([^)]*\)"', '', inner)
    inner = re.sub(r'<g style="display: ?none;">.*?</g></g>', '', inner, flags=re.S)
    inner = re.sub(r'<g style="display: ?none;">.*?</g>', '', inner, flags=re.S)
    inner = re.sub(r'\s*(data-v-[a-z0-9]+|data-pointer|style)="[^"]*"', '', inner)
    inner = re.sub(r'\s+xmlns="[^"]*"', '', inner)
    inner = re.sub(r'\s*width="[^"]*"|height="[^"]*"', '', inner)
    inner = re.sub(r'\s+', ' ', inner).strip()
    return vb, inner

def main():
    lines = []
    for key, jsname in [('wide','biWide'),('web','biWeb'),('full','biFull')]:
        vb, inner = clean(RAW[key])
        lines.append('export const %s = { vb: %s, inner: %s }' % (jsname, json.dumps(vb), json.dumps(inner)))
    # [已移除：原始 B 站图标 SVG 源文，避免素材随仓库分发]
    with open(sys.argv[1], 'a', encoding='utf-8') as f:
        f.write('\n'.join(lines) + '\n')
    print('part2b appended:', len(lines))

main()
