import sys, bisect, collections
from salan import *

path = sys.argv[1]
ch = int(sys.argv[2])
init, edges, _ = decode_channel(path, ch, int(0.25 * SR))
tr = Trace(init, edges)
fr = frames_from(tr, gap=100000)[1:7]
dev = {0: collections.Counter(), 1: collections.Counter()}
gl_pos = collections.Counter()
for (a, z) in fr:
    bs = uart_decode(tr, a - 1, z + 1, ideal_offsets)
    starts = [x[0] for x in bs]
    k0 = bisect.bisect_left(edges, a)
    k1 = bisect.bisect_right(edges, z)
    for k in range(k0, k1):
        t = edges[k]
        bi = bisect.bisect_right(starts, t) - 1
        if bi < 0:
            continue
        rel = t - starts[bi]
        if rel == 0:
            continue
        nom = round(rel / BIT) * BIT
        lv_after = init ^ ((k + 1) & 1)
        dev[lv_after][round((rel - nom) * 4)] += 1  # ns
        if k + 1 < len(edges) and edges[k + 1] - t <= 3:
            gl_pos[min(bi, 20) if bi < 20 else (100 if bi < 25604 else 25604)] += 1
for lv, name in ((1, 'rising'), (0, 'falling')):
    h = dev[lv]
    tot = sum(h.values())
    items = sorted(h.items())
    cum = 0; p = {}
    for v, c in items:
        cum += c
        for q in (0.0001, 0.001, 0.01, 0.5, 0.99, 0.999, 0.9999):
            if q not in p and cum >= q * tot:
                p[q] = v
    print('ch%d %s edges vs ideal grid (ns): n=%d min=%d max=%d pct=%s' % (ch, name, tot, items[0][0], items[-1][0], p))
print('glitch (<=12ns pulse) positions by byte index (<20 exact, 100=pixels, 25604=crc):', sorted(gl_pos.items()))
