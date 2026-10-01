"""ASCLIN RX timing model applied to decoded Saleae edges."""
import sys, bisect, collections, random
from salan import *

FA_NS = 5.0  # fASCLINF = 200 MHz


class AsclinModel:
    def __init__(self, ovs, num, den, sp, sm=True, depth=0, name=''):
        self.ovs, self.num, self.den, self.sp, self.sm, self.depth = ovs, num, den, sp, sm, depth
        self.name = name

    def tick_times(self, t_detect_ns, phase, count):
        """fA-cycle-aligned OVS tick times after start detection (fractional divider)."""
        ticks = []
        acc = phase
        k = 0
        while len(ticks) < count:
            k += 1
            acc += self.num
            if acc >= self.den:
                acc -= self.den
                ticks.append(t_detect_ns + k * FA_NS)
        return ticks


def decode_asclin(tr_ns_edges, init, t0, t1, m, rnd, delta=0.0):
    edges = tr_ns_edges

    def level_at(t):
        return init ^ (bisect.bisect_right(edges, t) & 1)

    def next_fall(t):
        k = bisect.bisect_right(edges, t)
        while k < len(edges):
            if (init ^ ((k + 1) & 1)) == 0:
                return edges[k], k
            k += 1
        return None, None

    out = []
    t = t0
    nbits = 11
    while True:
        s, _ = next_fall(t)
        if s is None or s > t1:
            break
        # detection aligned to next fA edge (+ filter delay is common-mode)
        td = (int(s // FA_NS) + 1) * FA_NS
        ticks = m.tick_times(td, rnd.randrange(m.den), nbits * m.ovs + 1)
        bits = []
        for bi in range(nbits):
            pts = [m.sp - 2, m.sp - 1, m.sp] if m.sm else [m.sp]
            votes = [level_at(ticks[bi * m.ovs + p - 1] + delta) for p in pts]
            bits.append(1 if sum(votes) * 2 > len(votes) else 0)
        v = 0
        for k in range(8):
            v |= bits[1 + k] << k
        par_ok = ((bin(v).count('1') + bits[9]) & 1) == 1
        stop_ok = bits[10] == 1
        out.append((s, v, par_ok, stop_ok))
        t = ticks[10 * m.ovs + m.sp - 1]
    return out


MODELS = [
    AsclinModel(8, 4, 5, 3, True, 2, 'CURRENT ovs8 SP3 med3'),
    AsclinModel(8, 4, 5, 4, True, 2, 'ovs8 SP4 med3'),
    AsclinModel(8, 4, 5, 5, True, 2, 'ovs8 SP5 med3'),
    AsclinModel(10, 1, 1, 5, True, 2, 'ovs10 SP5 med3'),
    AsclinModel(10, 1, 1, 6, True, 2, 'ovs10 SP6 med3'),
    AsclinModel(10, 1, 1, 7, True, 2, 'ovs10 SP7 med3'),
]


def run(path, ch, seconds, nframes, filt_samples=0):
    init, edges, _ = decode_channel(path, ch, int(seconds * SR))
    if filt_samples:
        edges = glitch_filter(init, edges, filt_samples)
    tr = Trace(init, edges)
    fr = [f for f in frames_from(tr, gap=100000)][1:1 + nframes]
    ens = [e * 4.0 for e in edges]
    res = {}
    for m in MODELS:
        rnd = random.Random(1)
        st = collections.Counter()
        for (a, z) in fr:
            bs = decode_asclin(ens, init, a * 4.0 - 1, z * 4.0 + 1, m, rnd)
            data = bytes(x[1] for x in bs)
            st['par'] += sum(1 for x in bs if not x[2])
            st['stop'] += sum(1 for x in bs if not x[3])
            if len(data) == 25608 and data[:4] == HEADER:
                ok = osram_crc32(data[4:25604]) == int.from_bytes(data[25604:25608], 'little')
                st['crc_ok' if ok else 'crc_bad'] += 1
            else:
                st['bad_len_or_hdr'] += 1
            if data[:4] != HEADER:
                st['hdr_bad'] += 1
        res[m.name] = st
        print('  ch%d %-24s frames=%d %s' % (ch, m.name, len(fr), dict(st)), flush=True)
    return res


if __name__ == '__main__':
    path = sys.argv[1]
    secs = float(sys.argv[2])
    nfr = int(sys.argv[3])
    for ch in [int(c) for c in sys.argv[4].split(',')]:
        run(path, ch, secs, nfr)
