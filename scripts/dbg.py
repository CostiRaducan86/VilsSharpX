import sys, bisect
from salan import *

path = sys.argv[1]
ch = int(sys.argv[2])
max_s = int(0.1 * SR)
b = load_raw(path, ch)
cs = chunk_list(b)
bounds = [c[0] for c in cs]
init, edges, _ = decode_channel(path, ch, max_s)
tr = Trace(init, edges)
fr = frames_from(tr, gap=100000)
a, z = fr[1]
bs = uart_decode(tr, a - 1, z + 1, ideal_offsets)
errs = [i for i, x in enumerate(bs) if not (x[2] and x[3])]
k = errs[0]
t = bs[k][0]
print('first err byte', k, 't', t)
j = bisect.bisect_left(bounds, t)
print('nearest chunk bounds', bounds[j - 1] - t, bounds[j] - t if j < len(bounds) else None)
for kk in range(k - 3, k + 3):
    print(kk, bs[kk][0] - t, hex(bs[kk][1]), bs[kk][2], bs[kk][3])
e0 = bisect.bisect_left(edges, t - 400)
print('runs around:', [(edges[i] - t, edges[i + 1] - edges[i], tr.level_at(edges[i])) for i in range(e0, e0 + 20)])
