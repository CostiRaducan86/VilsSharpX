"""Scratch analysis of Saleae Logic 2 .sal captures (Osram 20 Mbaud LVDS UART)."""
import zipfile, struct, sys, collections, bisect

SR = 250_000_000
BIT = SR / 20_000_000  # 12.5 samples


def load_raw(path, ch):
    return zipfile.ZipFile(path).read('digital-%d.bin' % ch)


def chunk_list(b):
    hdr = 51
    out = []
    while hdr + 26 <= len(b):
        a, e, st, nb = struct.unpack_from('<QQHQ', b, hdr)
        out.append((a, e, st, nb, hdr + 26))
        hdr = hdr + 26 + nb
    return out


def decode_channel(path, ch, max_samples=None):
    """Return (initial_level, edges) where edges = sorted list of transition sample times."""
    b = load_raw(path, ch)
    cs = chunk_list(b)
    edges = []
    level = None
    init = None
    unknown = 0
    for (a, e, st, nb, d0) in cs:
        if max_samples and a > max_samples:
            break
        if level is None:
            level = st
            init = st
        elif st != level:
            edges.append(a)
            level = st
        # parse runs; a long record (>=0x80) extends the current run (no toggle)
        runs = []
        ext = []
        i = d0
        end = d0 + nb
        while i < end:
            x = b[i]
            if x < 0x40:
                runs.append(x + 1); i += 1
            elif x < 0x80:
                runs.append((((x & 0x3f) << 8) | b[i + 1]) + 1); i += 2
            else:
                ext.append(len(runs) - 1); i += 2
        if len(ext) == 1:
            known = sum(runs)
            k = max(ext[0], 0)
            if ext[0] < 0:
                runs.insert(0, 0)
                k = 0
            runs[k] += (e - a - known)
        elif len(ext) > 1:
            unknown += 1
        if True:
            t = a
            for r in runs[:-1]:
                t += r
                edges.append(t)
            level = level ^ ((len(runs) - 1) & 1)
            continue
        runs = [None]
        nunk = 0
        if nunk > 1:
            unknown += 1
            # forward until first unknown, backward after last unknown; drop middle
            fi = runs.index(None)
            li = len(runs) - 1 - runs[::-1].index(None)
            t = a
            lv = level
            for r in runs[:fi]:
                t += r
                edges.append(t); lv ^= 1
            tail = runs[li + 1:]
            t2 = e - sum(tail)
            # level at t2: parity of number of runs from fi..li inclusive
            lv2 = level ^ ((li + 1) & 1)
            edges.append(t2 if lv2 != lv else t2)  # approximate: mark a transition
            t = t2
            for r in tail[:-1]:
                t += r
                edges.append(t)
            level = level ^ ((len(runs) - 1) & 1)
            continue
        t = a
        for r in runs[:-1]:
            t += r
            edges.append(t)
        level = level ^ ((len(runs) - 1) & 1)
    return init, edges, unknown


class Trace:
    def __init__(self, init, edges):
        self.init = init
        self.edges = edges

    def level_at(self, t):
        k = bisect.bisect_right(self.edges, t)
        return self.init ^ (k & 1)

    def next_fall(self, t):
        k = bisect.bisect_right(self.edges, t)
        while k < len(self.edges):
            if (self.init ^ ((k + 1) & 1)) == 0:
                return self.edges[k]
            k += 1
        return None


def glitch_filter(init, edges, min_width):
    """Remove pulses narrower than min_width samples (pairs of close edges)."""
    out = []
    for t in edges:
        if out and t - out[-1] < min_width:
            out.pop()
        else:
            out.append(t)
    return out


def run_stats(init, edges, lim=6):
    widths = collections.Counter()
    for k in range(1, len(edges)):
        w = edges[k] - edges[k - 1]
        if w <= lim:
            widths[w] += 1
    return widths


def osram_crc32(data):
    crc = 0xDEADAFFE
    for byte in data:
        crc ^= byte << 24
        for _ in range(8):
            crc = ((crc << 1) ^ 0x04C11DB7) & 0xFFFFFFFF if (crc & 0x80000000) else (crc << 1) & 0xFFFFFFFF
    return int.from_bytes(crc.to_bytes(4, 'big'), 'little')


def uart_decode(tr, t0, t1, sample_offsets, nbits_frame=11):
    """Decode bytes between t0 and t1. sample_offsets(bit_index) -> list of sample times rel. to start edge.

    Returns list of (t_start, value, parity_ok, stop_ok)."""
    out = []
    t = t0
    while True:
        s = tr.next_fall(t)
        if s is None or s > t1:
            break
        bits = []
        for bi in range(nbits_frame):
            votes = [tr.level_at(s + o) for o in sample_offsets(bi)]
            bits.append(1 if sum(votes) * 2 > len(votes) else 0)
        # bits[0] start, 1..8 data, 9 parity, 10 stop
        v = 0
        for k in range(8):
            v |= bits[1 + k] << k
        par_ok = ((bin(v).count('1') + bits[9]) & 1) == 1
        stop_ok = bits[10] == 1 and bits[0] == 0
        out.append((s, v, par_ok, stop_ok))
        # ASCLIN re-arms after the stop-bit sample point
        t = s + max(sample_offsets(nbits_frame - 1))
    return out


def ideal_offsets(bi):
    return [(bi + 0.5) * BIT]


def asclin_offsets_factory(ovs=8, sp=3, sm=True, lat=0.0):
    tick = BIT / ovs

    def f(bi):
        base = bi * BIT + lat
        pts = [sp - 2, sp - 1, sp] if sm else [sp]
        return [base + p * tick for p in pts]
    return f


def frames_from(tr, gap=2000):
    """Return list of (t_first_edge, t_last_edge) bursts."""
    e = tr.edges
    res = []
    if not e:
        return res
    st = e[0]
    for k in range(1, len(e)):
        if e[k] - e[k - 1] > gap:
            res.append((st, e[k - 1]))
            st = e[k]
    res.append((st, e[-1]))
    return res


HEADER = bytes([0x80, 0xA5, 0xAA, 0x55])


def check_frames(tr, offsets, max_frames=40, verbose=False):
    fr = frames_from(tr)
    stats = collections.Counter()
    details = []
    for (a, z) in fr[:max_frames]:
        bs = uart_decode(tr, a - 1, z + 1, offsets)
        data = bytes(x[1] for x in bs)
        perr = sum(1 for x in bs if not x[2])
        serr = sum(1 for x in bs if not x[3])
        stats['frames'] += 1
        stats['bytes'] += len(bs)
        stats['parity_err'] += perr
        stats['stop_err'] += serr
        if len(data) == 25608 and data[:4] == HEADER:
            ok = osram_crc32(data[4:25604]) == int.from_bytes(data[25604:25608], 'little')
            stats['crc_ok' if ok else 'crc_bad'] += 1
        else:
            stats['len_bad'] += 1
        first_err = next((k for k, x in enumerate(bs) if not (x[2] and x[3])), None)
        details.append((len(bs), perr, serr, first_err))
    return stats, details


if __name__ == '__main__':
    path = sys.argv[1]
    max_s = int(float(sys.argv[2]) * SR) if len(sys.argv) > 2 else None
    for ch in (7, 0, 6):
        init, edges, unk = decode_channel(path, ch, max_s)
        print('ch', ch, 'init', init, 'edges', len(edges), 'multi-unknown chunks', unk)
        print('   narrow run widths (samples of 4ns):', sorted(run_stats(init, edges).items()))
