import sqlite3
con = sqlite3.connect('data/station.db')
cur = con.cursor()
rows = cur.execute("SELECT dynamic_id, author_name FROM dynamics WHERE dynamic_id IS NOT NULL ORDER BY cached_at_utc DESC LIMIT 5").fetchall()
for r in rows:
    print(r[0], r[1])
con.close()
