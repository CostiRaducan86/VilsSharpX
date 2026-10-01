import sys, collections
from salan import *

path = sys.argv[1]
max_s = int(float(sys.argv[2]) * SR)
ch = int(sys.argv[3])
init, edges, _ = decode_channel(path, ch, max_s)
tr = Trace(init, edges)
# gap histogram
gaps = collections.Counter()
for k in range(1, len(edges)):
    w = edges[k] - edges[k - 1]
    if w > 60:
        gaps[w // 50 * 50] += 1
print('gaps (>60 samples, bucket 50):', sorted(gaps.items())[:40])
fr = frames_from(tr, gap=100000)
print('bursts (gap>400us):', len(fr))
for (a, z) in fr[:4]:
    bs = uart_decode(tr, a - 1, z + 1, ideal_offsets)
    data = bytes(x[1] for x in bs)
    print('burst len', len(bs), 'dur us %.1f' % ((z - a) / 250), 'head', data[:16].hex(' '))
    idx = [i for i in range(len(data) - 3) if data[i:i + 4] == HEADER]
    print('  header positions', idx[:10])
    errs = [i for i, x in enumerate(bs) if not (x[2] and x[3])]
    print('  err positions', errs[:20], len(errs))
    for i in idx:
        if i + 25608 <= len(data):
            ok = osram_crc32(data[i + 4:i + 25604]) == int.from_bytes(data[i + 25604:i + 25608], 'little')
            print('  frame@', i, 'crc', ok, 'tail', data[i + 25604:i + 25612].hex(' '))
