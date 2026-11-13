"""A fake terminal for testing `brain view`: runs a program on a pseudo-terminal, answers the questions it asks the terminal
(like kitty, iTerm2, a sixel terminal or a plain one would), rebuilds the pictures it draws (kitty / iTerm2 PNGs placed on a
cell grid) and sends keys and mouse reports the way a real terminal does.

    t = FakeTerm(['/app/BrAIn', 'view'], kind='kitty', cols=160, rows=45)
    t.wait_for(lambda: t.images)
    img = t.composite()          # cairo ImageSurface of what the terminal shows (kitty/iterm)
    t.click(cell_x, cell_y); t.send('hello'); t.key_ctrl('q')
"""
import base64, fcntl, io, os, re, select, signal, struct, subprocess, termios, threading, time

import cairo


class Shot:
    """A cairo surface plus the cell size in its pixels."""
    def __init__(self, surface, K, M):
        self.surface, self.K, self.M = surface, K, M

    def __getattr__(self, name):
        return getattr(self.surface, name)


class FakeTerm:
    def __init__(self, args, kind='kitty', cols=160, rows=45, cell=(8, 18), env=None, pixel_mouse=False, kitty_keys=False):
        self.kind, self.cols, self.rows, self.cell = kind, cols, rows, cell
        self.pixel_mouse, self.kitty_keys = pixel_mouse, kitty_keys
        self.master, slave = os.openpty()
        self._winsize(cols, rows)
        e = dict(env) if env is not None else dict(os.environ)   # env given = the whole environment (a test may want no DISPLAY)
        e.setdefault('TERM', 'xterm-256color')

        def ctty():
            os.setsid()
            fcntl.ioctl(0, termios.TIOCSCTTY, 0)

        self.p = subprocess.Popen(args, stdin=slave, stdout=slave, stderr=slave, env=e, preexec_fn=ctty, close_fds=True)
        os.close(slave)
        self.out = bytearray()          # everything the program wrote
        self.lock = threading.Lock()
        self.images = {}                # kitty: id -> dict(row, col, c, r, png)
        self.order = []                 # placement order (kitty ids / iterm sequence numbers)
        self.iterm = []                 # (row, col, c, r, png)
        self.sixels = []                # (row, col, payload)
        self.osc52 = []                 # decoded clipboard texts
        self._cur = (1, 1)
        self._parsed = 0
        self._kchunks = None
        self.closed = False
        threading.Thread(target=self._pump, daemon=True).start()

    def _winsize(self, cols, rows):
        fcntl.ioctl(self.master, termios.TIOCSWINSZ, struct.pack('HHHH', rows, cols, cols * self.cell[0], rows * self.cell[1]))

    def resize(self, cols, rows):
        self.cols, self.rows = cols, rows
        self._winsize(cols, rows)   # the kernel sends SIGWINCH to the program

    # ------------------------------------------------------------ output side

    def _pump(self):
        while True:
            try:
                r, _, _ = select.select([self.master], [], [], 0.2)
                if not r:
                    if self.p.poll() is not None:
                        break
                    continue
                b = os.read(self.master, 1 << 20)
            except OSError:
                break
            if not b:
                break
            with self.lock:
                self.out += b
                self._answer()
                self._parse()
        self.closed = True

    def _answer(self):
        """Answer the capability questions once (they all arrive together, DA1 last)."""
        if getattr(self, '_answered', False) or b'\x1b[c' not in self.out:
            return
        self._answered = True
        rep = b''
        if self.kind == 'kitty':
            rep += b'\x1b_Gi=31;OK\x1b\\' + b'\x1bP>|kitty(0.42.1)\x1b\\'
        elif self.kind == 'iterm':
            rep += b'\x1bP>|iTerm2 3.6.5\x1b\\'
        rep += b'\x1b[6;%d;%dt' % (self.cell[1], self.cell[0])
        rep += b'\x1b[?1016;%d$y' % (2 if self.pixel_mouse else 0)
        if self.kitty_keys:
            rep += b'\x1b[?0u'
        rep += b'\x1b[?62;22;52c' if self.kind != 'sixel' else b'\x1b[?62;4;22c'
        os.write(self.master, rep)

    _seq = re.compile(rb'\x1b\[(\d+);(\d+)H|\x1b_G([^;\x1b]*)(?:;([^\x1b]*))?\x1b\\|\x1b\]1337;File=([^:]*):([A-Za-z0-9+/=]*)\x07|'
                      rb'\x1bP0;1;0q([^\x1b]*)\x1b\\|\x1b\]52;c;([A-Za-z0-9+/=]*)\x07')

    def _parse(self):
        # only parse complete sequences: stop at the last ESC that might start an unfinished one
        data = bytes(self.out[self._parsed:])
        end = 0
        for m in self._seq.finditer(data):
            end = m.end()
            if m.group(1):
                self._cur = (int(m.group(1)), int(m.group(2)))
            elif m.group(3) is not None:
                ctrl = dict(kv.split(b'=', 1) for kv in m.group(3).split(b',') if b'=' in kv)
                a = ctrl.get(b'a', b'')
                if a == b'd':
                    d = ctrl.get(b'd', b'')
                    if d in (b'A', b'a'):
                        self.images.clear()
                    elif d in (b'I', b'i'):
                        self.images.pop(int(ctrl[b'i']), None)
                    continue
                if a == b'T':
                    self._kchunks = dict(id=int(ctrl[b'i']), row=self._cur[0], col=self._cur[1], c=int(ctrl[b'c']), r=int(ctrl[b'r']), data=[m.group(4) or b''])
                elif self._kchunks is not None:
                    self._kchunks['data'].append(m.group(4) or b'')
                if ctrl.get(b'm', b'0') == b'0' and self._kchunks is not None:
                    k = self._kchunks
                    k['png'] = base64.b64decode(b''.join(k.pop('data')))
                    self.images[k['id']] = k
                    self.order.append(k['id'])
                    self._kchunks = None
            elif m.group(5) is not None:
                args = dict(kv.split(b'=', 1) for kv in m.group(5).split(b';') if b'=' in kv)
                self.iterm.append(dict(row=self._cur[0], col=self._cur[1], c=int(args[b'width']), r=int(args[b'height']),
                                       png=base64.b64decode(m.group(6))))
            elif m.group(7) is not None:
                self.sixels.append((self._cur[0], self._cur[1], m.group(7)))
            elif m.group(8) is not None:
                self.osc52.append(base64.b64decode(m.group(8)).decode('utf-8', 'replace'))
        self._parsed += end

    _scr = re.compile(r'\x1b\[(\d*);?(\d*)H|\x1b\[2J|\x1b\[[0-9;?<>$ ]*[a-zA-Z~@`]|\x1b[_\]P][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b[^\[_\]P]|([^\x1b]+)')

    def screen(self):
        """The characters on the screen now (pictures not included): replays cursor moves and text, enough for the viewer."""
        rows = [[' '] * self.cols for _ in range(self.rows)]
        y = x = 0
        for m in self._scr.finditer(self.text()):
            g = m.group(0)
            if m.group(3) is not None:
                for ch in m.group(3):
                    if ch in '\r\n':
                        continue
                    if 0 <= y < self.rows and 0 <= x < self.cols:
                        rows[y][x] = ch
                    x += 2 if ord(ch) >= 0x1100 and (0x1100 <= ord(ch) <= 0x115F or 0x2E80 <= ord(ch) <= 0xA4CF or 0xAC00 <= ord(ch) <= 0xD7A3 or 0xF900 <= ord(ch) <= 0xFAFF or ord(ch) >= 0x1F300) else 1
            elif g == '\x1b[2J':
                rows = [[' '] * self.cols for _ in range(self.rows)]
            elif g.endswith('H') and g.startswith('\x1b[') and m.group(1) is not None:
                y, x = int(m.group(1) or 1) - 1, int(m.group(2) or 1) - 1
        return [''.join(r) for r in rows]

    def text(self):
        with self.lock:
            return bytes(self.out).decode('utf-8', 'replace')

    def mark(self):
        with self.lock:
            return len(self.out)

    def since(self, mark):
        with self.lock:
            return bytes(self.out[mark:])

    def wait_for(self, fn, timeout=10.0, every=0.1):
        end = time.time() + timeout
        v = fn()
        while not v and time.time() < end:
            time.sleep(every)
            v = fn()
        return v

    def composite(self):
        """What a kitty/iTerm2 terminal would show now, in frame pixels (cell = K×M), as a cairo surface (or None)."""
        with self.lock:
            if self.kind == 'kitty':
                pics = [self.images[i] for i in sorted(self.images)]
            else:
                pics = list(self.iterm)
        if not pics:
            return None
        full = max(pics, key=lambda p: p['c'] * p['r'])
        s0 = cairo.ImageSurface.create_from_png(io.BytesIO(full['png']))
        K, M = s0.get_width() // full['c'], s0.get_height() // full['r']
        canvas = cairo.ImageSurface(cairo.FORMAT_RGB24, self.cols * K, self.rows * M)
        cr = cairo.Context(canvas)
        for p in pics:
            s = cairo.ImageSurface.create_from_png(io.BytesIO(p['png']))
            cr.set_source_surface(s, (p['col'] - 1) * K, (p['row'] - 1) * M)
            cr.paint()
        return Shot(canvas, K, M)

    # ------------------------------------------------------------ input side

    def send(self, s):
        os.write(self.master, s.encode() if isinstance(s, str) else s)

    def key_ctrl(self, letter):
        if self.kitty_keys:
            self.send(f'\x1b[{ord(letter)};5u')
        else:
            self.send(bytes([ord(letter) & 0x1f]))

    def click(self, cx, cy, button=0):
        self.send(f'\x1b[<{button};{cx};{cy}M')
        time.sleep(0.05)
        self.send(f'\x1b[<{button};{cx};{cy}m')

    def wheel(self, cx, cy, down=True, n=1):
        for _ in range(n):
            self.send(f'\x1b[<{65 if down else 64};{cx};{cy}M')
            time.sleep(0.03)

    def paste(self, text):
        self.send('\x1b[200~' + text + '\x1b[201~')

    def close(self):
        try:
            if self.p.poll() is None:
                self.p.send_signal(signal.SIGTERM)
                self.p.wait(5)
        except Exception:
            pass
        try:
            os.close(self.master)
        except OSError:
            pass
