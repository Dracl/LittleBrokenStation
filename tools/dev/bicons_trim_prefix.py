#!/usr/bin/env python3
# 修 bicons.js：inner 首部裸属性串（class=.../preserveAspectRatio=.../>等）去掉，只留从 < 起的正规 SVG 内容。
import re, json

path = 'web/src/assets/bicons.js'
src = open(path, encoding='utf-8').read()
JS_STR = r'"(?:[^"\\]|\\.)*"'

out_lines = []
n_fixed = 0
for line in src.splitlines():
    m = re.match(r'^(export const \w+ = \{ vb: ' + JS_STR + r', inner: )(' + JS_STR + r')( \})$', line)
    if m:
        inner = json.loads(m.group(2))
        stripped = re.sub(r'^[^<]*', '', inner)
        if stripped != inner:
            n_fixed += 1
        line = m.group(1) + json.dumps(stripped, ensure_ascii=False) + m.group(3)
    out_lines.append(line)
open(path, 'w', encoding='utf-8').write('\n'.join(out_lines) + '\n')
print('lines:', len(out_lines), 'fixed:', n_fixed)
