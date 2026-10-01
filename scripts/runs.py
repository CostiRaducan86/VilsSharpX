import sys, collections
from salan import *

path = sys.argv[1]
max_s = int(float(sys.argv[2]) * SR)
for ch in (7, 0, 6):
    init, edges, _ = decode_channel(path, ch, max_s)
    # run widths by level for runs < 200 samples (in-frame)
    hist = {0: collections.Counter(), 1: collections.Counter()}
    for k in range(1, len(edges)):
        w = edges[k] - edges[k - 1]
        lv = init ^ (k & 1)  # level during run between edge k-1 and k
        if w < 200:
            hist[lv][w] += 1
    print('ch', ch)
    for lv in (0, 1):
        h = hist[lv]
        # group per nominal bit count
        groups = collections.defaultdict(collections.Counter)
        for w, c in h.items():
            nb = max(1, round(w / BIT)) if w > 6 else 0
            groups[nb][w] += c
        line = []
        for nb in sorted(groups)[:6]:
            g = groups[nb]
            tot = sum(g.values())
            mean = sum(w * c for w, c in g.items()) / tot
            line.append('%db:n=%d mean=%.2f min=%d max=%d' % (nb, tot, mean, min(g), max(g)))
        print('  level', lv, ' | '.join(line))
