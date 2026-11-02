#!/usr/bin/env python3
"""Builds fake browser profiles under $HOME for the importer tests: Chromium-on-Linux v10/v11 encryption,
old and new cookie layouts, Opera's flat layout, Snap/Flatpak roots, Firefox profiles.ini, unreadable/corrupt DBs."""
import os, sqlite3, subprocess, hashlib, json, sys, binascii, shutil, time

HOME = os.environ['HOME']
CFG = os.path.join(HOME, '.config')

def kdf(password: bytes) -> bytes:
    return hashlib.pbkdf2_hmac('sha1', password, b'saltysalt', 1, 16)

def enc(version: bytes, key: bytes, plain: bytes) -> bytes:
    r = subprocess.run(['openssl', 'enc', '-aes-128-cbc', '-K', key.hex(), '-iv', '20' * 16], input=plain, capture_output=True, check=True)
    return version + r.stdout

def store_safe_storage(label: str, app: str, password: str):
    """What Chromium does: libsecret item, schema chrome_libsecret_os_crypt_password_v2, attribute application."""
    import gi
    gi.require_version('Secret', '1')
    from gi.repository import Secret
    schema = Secret.Schema.new('chrome_libsecret_os_crypt_password_v2', Secret.SchemaFlags.DONT_MATCH_NAME,
                               {'application': Secret.SchemaAttributeType.STRING})
    ok = Secret.password_store_sync(schema, {'application': app}, Secret.COLLECTION_DEFAULT, label, password, None)
    assert ok, 'could not store ' + label

def mkdir(p): os.makedirs(p, exist_ok=True); return p

PEANUTS = kdf(b'peanuts')
CHROME_PW = 'Zm9vYmFyYmF6cXV4MQ=='      # what Chrome keeps in "Chrome Safe Storage"
CHROMIUM_PW = 'Y2hyb21pdW1wYXNzd29yZA=='
K_CHROME = kdf(CHROME_PW.encode())
K_CHROMIUM = kdf(CHROMIUM_PW.encode())
K_WRONG = kdf(b'someone else')
WEBKIT_EPOCH = 11644473600

def chrome_time(unix): return int((unix + WEBKIT_EPOCH) * 1_000_000)
FUTURE = chrome_time(time.time() + 86400 * 365)

COOKIE_SCHEMA = """create table cookies(creation_utc integer not null, host_key text not null, top_frame_site_key text not null default '',
 name text not null, value text not null, encrypted_value blob not null default x'', path text not null, expires_utc integer not null,
 is_secure integer not null, is_httponly integer not null, last_access_utc integer not null default 0, has_expires integer not null default 1,
 is_persistent integer not null default 1, priority integer not null default 1, samesite integer not null default -1,
 source_scheme integer not null default 0, source_port integer not null default -1, last_update_utc integer not null default 0)"""

def add_cookie(db, host, name, value, key_or_none, version=b'v10', prefix_hash=True, session=False, secure=1, httponly=0, path='/', plain_col=''):
    plain = value.encode()
    if prefix_hash: plain = hashlib.sha256(host.encode()).digest() + plain
    blob = enc(version, key_or_none, plain) if key_or_none is not None else b''
    db.execute('insert into cookies(creation_utc,host_key,name,value,encrypted_value,path,expires_utc,is_secure,is_httponly,has_expires,is_persistent) values(?,?,?,?,?,?,?,?,?,?,?)',
               (chrome_time(time.time()), host, name, plain_col, blob, path, 0 if session else FUTURE, secure, httponly, 0 if session else 1, 0 if session else 1))

def make_cookies(path, version_meta=24):
    mkdir(os.path.dirname(path))
    if os.path.exists(path): os.unlink(path)
    db = sqlite3.connect(path)
    db.execute(COOKIE_SCHEMA)
    db.execute('create table meta(key text unique not null, value text)')
    db.execute("insert into meta values('version', ?)", (str(version_meta),))
    return db

def make_history(path, urls):
    mkdir(os.path.dirname(path))
    db = sqlite3.connect(path)
    db.execute('create table urls(id integer primary key, url text, title text, visit_count integer, typed_count integer default 0, last_visit_time integer, hidden integer default 0)')
    for i, (u, t, v) in enumerate(urls):
        db.execute('insert into urls(url,title,visit_count,last_visit_time) values(?,?,?,?)', (u, t, v, chrome_time(time.time() - 3600 * (i + 1))))
    db.commit(); db.close()

def make_logins(path, rows):
    mkdir(os.path.dirname(path))
    db = sqlite3.connect(path)
    db.execute('create table logins(origin_url text, action_url text, username_element text, username_value text, password_element text, password_value blob, blacklisted_by_user integer not null default 0, date_created integer)')
    for r in rows:
        db.execute('insert into logins(origin_url,username_value,password_value,blacklisted_by_user) values(?,?,?,?)', r)
    db.commit(); db.close()

def make_bookmarks(path, items):
    mkdir(os.path.dirname(path))
    kids = [{'type': 'url', 'name': n, 'url': u} for n, u in items]
    json.dump({'roots': {'bookmark_bar': {'type': 'folder', 'name': 'Bar', 'children': kids}, 'other': {'type': 'folder', 'name': 'Other', 'children': []}}}, open(path, 'w'))

# ---------------- keyring items (as Chrome / Chromium would have created them) ----------------
if os.environ.get('FIX_KEYRING', '1') == '1':
    store_safe_storage('Chrome Safe Storage', 'chrome', CHROME_PW)
    store_safe_storage('Chromium Safe Storage', 'chromium', CHROMIUM_PW)

# ---------------- Google Chrome: Default (new layout, DB v24: hash-prefixed values) ----------------
d = mkdir(f'{CFG}/google-chrome/Default')
db = make_cookies(f'{d}/Network/Cookies', 24)
add_cookie(db, '.example.com', 'sid', 'v10-secret-cookie', PEANUTS, b'v10')
add_cookie(db, '.example.org', 'tok', 'v11-keyring-cookie', K_CHROME, b'v11', httponly=1)
add_cookie(db, 'plain.example.net', 'pl', 'plainval', None, plain_col='plainval')
add_cookie(db, '.example.com', 'sess', 'session-only', PEANUTS, b'v10', session=True)
add_cookie(db, '.example.com', 'uni', 'žůčková ✓ 日本語', PEANUTS, b'v10')
add_cookie(db, '.example.com', 'empty', '', PEANUTS, b'v10')            # empty value = just the host hash
add_cookie(db, '.wrongkey.test', 'bad', 'cannot-read', K_WRONG, b'v11')
add_cookie(db, 'a.example.com', 'pth', 'with-path', K_CHROME, b'v11', path='/app', secure=0)
# cookies for the local fixture site (http://127.0.0.1:8765) so an end-to-end test can see them in a real request
add_cookie(db, '127.0.0.1', 'imp10', 'from-v10', PEANUTS, b'v10', secure=0)
add_cookie(db, '127.0.0.1', 'imp11', 'from-v11', K_CHROME, b'v11', secure=0, httponly=1)
add_cookie(db, '127.0.0.1', 'impplain', 'from-plain', None, secure=0, plain_col='from-plain')
db.commit(); db.close()
make_history(f'{d}/History', [('https://example.com/a', 'Example A', 5), ('https://example.org/b', 'Example B', 2), ('chrome://settings', 'skip me', 1)])
make_bookmarks(f'{d}/Bookmarks', [('Example', 'https://example.com/'), ('Org', 'https://example.org/x')])
make_logins(f'{d}/Login Data', [
    ('https://example.com/login', 'alice', enc(b'v10', PEANUTS, 'p@ss-v10'.encode()), 0),
    ('https://example.org/signin', 'bob', enc(b'v11', K_CHROME, 'p@ss-v11 ünï'.encode()), 0),
    ('https://never.example/', 'carol', b'', 1),             # "never save" marker, no password
    ('https://blocked.example/', 'dave', enc(b'v10', PEANUTS, b'x'), 1),   # blacklisted
])
# a second profile with an old-layout cookie DB (no Network/, no hash prefix: DB v20)
d2 = mkdir(f'{CFG}/google-chrome/Profile 1')
db = make_cookies(f'{d2}/Cookies', 20)
add_cookie(db, '.old.example', 'o1', 'old-layout', PEANUTS, b'v10', prefix_hash=False)
db.commit(); db.close()
make_history(f'{d2}/History', [('https://old.example/', 'Old', 1)])
open(f'{CFG}/google-chrome/Local State', 'w').write('{}')
mkdir(f'{CFG}/google-chrome/Guest Profile'); mkdir(f'{CFG}/google-chrome/System Profile')   # must be ignored

# ---------------- Chromium: its own keyring item ("Chromium Safe Storage", application chromium) ----------------
d = mkdir(f'{CFG}/chromium/Default')
db = make_cookies(f'{d}/Network/Cookies', 24)
add_cookie(db, '.chromium.example', 'cs', 'chromium-v11', K_CHROMIUM, b'v11')
db.commit(); db.close()
make_history(f'{d}/History', [('https://chromium.example/', 'Chromium', 3)])

# ---------------- Opera: the profile IS the user-data folder ----------------
o = mkdir(f'{CFG}/opera')
make_history(f'{o}/History', [('https://opera.example/', 'Opera', 1)])
make_bookmarks(f'{o}/Bookmarks', [('Opera bm', 'https://opera.example/bm')])

# ---------------- Snap / Flatpak roots ----------------
s = mkdir(f'{HOME}/snap/chromium/common/chromium/Default')
make_history(f'{s}/History', [('https://snapchromium.example/', 'Snap', 1)])
f = mkdir(f'{HOME}/.var/app/com.brave.Browser/config/BraveSoftware/Brave-Browser/Default')
make_history(f'{f}/History', [('https://flatbrave.example/', 'Flat', 1)])

# ---------------- Firefox ----------------
ff = mkdir(f'{HOME}/.mozilla/firefox')
open(f'{ff}/profiles.ini', 'w').write("""[Install4F96D1932A9F858E]
Default=abcd1234.default-release
Locked=1

[Profile1]
Name=default
IsRelative=1
Path=wxyz9876.default

[Profile0]
Name=default-release
IsRelative=1
Path=abcd1234.default-release
Default=1

[General]
StartWithLastProfile=1
Version=2
""")
p = mkdir(f'{ff}/abcd1234.default-release')
db = sqlite3.connect(f'{p}/places.sqlite')
db.execute('create table moz_places(id integer primary key, url text, title text, visit_count integer, hidden integer default 0, last_visit_date integer)')
db.execute('create table moz_bookmarks(id integer primary key, type integer, fk integer, parent integer, title text)')
db.execute("insert into moz_places(url,title,visit_count,last_visit_date) values('https://firefox.example/a','FF A',4,?)", (int(time.time() * 1e6),))
db.execute("insert into moz_places(url,title,visit_count,last_visit_date) values('https://firefox.example/b','FF B',1,?)", (int(time.time() * 1e6),))
db.execute("insert into moz_bookmarks(type,fk,parent,title) values(1,1,1,'FF bookmark')")
db.commit(); db.close()
db = sqlite3.connect(f'{p}/cookies.sqlite')
db.execute('create table moz_cookies(id integer primary key, name text, value text, host text, path text, expiry integer, isSecure integer, isHttpOnly integer)')
db.execute("insert into moz_cookies(name,value,host,path,expiry,isSecure,isHttpOnly) values('ffc','ffval','.firefox.example','/',?,1,1)", (int(time.time() * 1000 + 86400000),))
db.commit(); db.close()
p2 = mkdir(f'{ff}/wxyz9876.default')
db = sqlite3.connect(f'{p2}/places.sqlite')
db.execute('create table moz_places(id integer primary key, url text, title text, visit_count integer, hidden integer default 0, last_visit_date integer)')
db.execute('create table moz_bookmarks(id integer primary key, type integer, fk integer, parent integer, title text)')
db.execute("insert into moz_places(url,title,visit_count,last_visit_date) values('https://ffold.example/','FF old',1,?)", (int(time.time() * 1e6),))
db.commit(); db.close()

print('fixtures ready')
