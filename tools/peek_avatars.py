import sqlite3
con = sqlite3.connect('data/station.db')
cur = con.cursor()
print('null-face rows:', cur.execute("SELECT COUNT(*) FROM dynamic_comments WHERE face_local IS NULL AND face IS NOT NULL AND face <> ''").fetchone()[0])
print('sample face urls:')
for (u,) in cur.execute("SELECT DISTINCT face FROM dynamic_comments WHERE face_local IS NULL LIMIT 3"):
    print(' ', u)
print('dynamic targets:', cur.execute("SELECT COUNT(*) FROM dynamic_targets").fetchone()[0] if cur.execute("SELECT name FROM sqlite_master WHERE name='dynamic_targets'").fetchone() else 'no table')
for row in cur.execute("SELECT name FROM sqlite_master WHERE type='table' AND name LIKE '%dynamic%'"):
    print('table:', row[0])
con.close()
