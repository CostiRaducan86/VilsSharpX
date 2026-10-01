import sys, collections
from salan import *

path = sys.argv[1]
max_s = int(float(sys.argv[2]) * SR)
chans = [int(c) for c in sys.argv[3].split(',')] if len(sys.argv) > 3 else [7, 0]
tick = BIT / 8

configs = [
    ('ideal-center', ideal_offsets),
    ('SP3 ovs8 (ticks1-3)', asclin_offsets_factory(8, 3, True, 0.0)),
    ('SP3 ovs8 +1tick', asclin_offsets_factory(8, 3, True, tick)),
    ('SP4 ovs8', asclin_offsets_factory(8, 4, True, 0.5 * tick)),
    ('SP5 ovs8', asclin_offsets_factory(8, 5, True, 0.5 * tick)),
    ('SP6 ovs8', asclin_offsets_factory(8, 6, True, 0.5 * tick)),
]
for ch in chans:
    init, edges, _ = decode_channel(path, ch, max_s)
    for filt in (0, 3):
        e2 = glitch_filter(init, edges, filt) if filt else edges
        tr = Trace(init, e2)
        for name, off in configs:
            st, det = check_frames(tr, off, max_frames=12)
            print('ch%d filt<%d %-20s %s' % (ch, filt, name, dict(st)))
