import sqlite3, glob, os
dbs = glob.glob('data/*.db') + glob.glob('data/*.sqlite*')
print('dbs:', dbs)
for db in dbs[:1]:
    con = sqlite3.connect(db)
    cur = con.cursor()
    n = cur.execute("SELECT COUNT(*) FROM dynamic_comments WHERE face_local IS NOT NULL").fetchone()[0]
    print('stamped before:', n)
    cur.execute('UPDATE dynamic_comments SET face_local=NULL')
    con.commit()
    n2 = cur.execute("SELECT COUNT(*) FROM dynamic_comments WHERE face_local IS NOT NULL").fetchone()[0]
    print('stamped after:', n2)
    tot = cur.execute("SELECT COUNT(*) FROM dynamic_comments").fetchone()[0]
    print('total comments:', tot)
    con.close()
print('avatars dir files:', len(os.listdir('data/avatars')) if os.path.isdir('data/avatars') else 'MISSING')
