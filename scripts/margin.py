import sys, random, collections
from asclin_sim import *

path = sys.argv[1]
init, edges, _ = decode_channel(path, 0, int(0.2 * SR))
filt = int(sys.argv[2]) if len(sys.argv) > 2 else 0
if filt:
    edges = glitch_filter(init, edges, filt)
print('glitch filter: remove pulses <', filt, 'samples (4 ns each)')
tr = Trace(init, edges)
fr = frames_from(tr, gap=100000)[1:4]
ens = [e * 4.0 for e in edges]
models = [
    AsclinModel(8, 4, 5, 3, True, 2, 'CURRENT ovs8 SP3'),
    AsclinModel(8, 4, 5, 5, True, 2, 'ovs8 SP5'),
    AsclinModel(10, 1, 1, 6, True, 2, 'ovs10 SP6'),
    AsclinModel(10, 1, 1, 7, True, 2, 'ovs10 SP7'),
]
for m in models:
    row = []
    for delta in (-20, -15, -10, -5, 0, 5, 10, 15, 20):
        rnd = random.Random(1)
        errs = 0
        for (a, z) in fr:
            bs = decode_asclin(ens, init, a * 4.0 - 1, z * 4.0 + 1, m, rnd, delta)
            errs += sum(1 for x in bs if not (x[2] and x[3]))
            if len(bs) != 25608:
                errs += 1000
        row.append('%+d:%d' % (delta, errs))
    print('%-18s %s' % (m.name, ' '.join(row)), flush=True)
