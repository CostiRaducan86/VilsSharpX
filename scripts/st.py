import sys, collections
from salan import load_raw, chunk_list

path = sys.argv[1]
ch = int(sys.argv[2])
b = load_raw(path, ch)
cs = chunk_list(b)
rows = []
for (a, e, st, nb, d0) in cs[:300]:
    i = d0; n = 0; gi = None; first = None
    while i < d0 + nb:
        x = b[i]
        if x < 0x40:
            w = x + 1; i += 1
        elif x < 0x80:
            w = (((x & 0x3f) << 8) | b[i + 1]) + 1; i += 2
        else:
            w = None; i += 2; gi = n
        if first is None:
            first = w
        n += 1
    rows.append((st, n, gi, first))
c = collections.Counter()
for k, (st, n, gi, first) in enumerate(rows):
    if gi is None:
        continue
    init = 1 ^ (gi & 1)  # gap is high: level(gi) = init ^ (gi&1) = 1
    last = init ^ ((n - 1) & 1)
    c[('st==init' if st == init else 'st!=init', 'st==last' if st == last else 'st!=last', 'n odd' if n & 1 else 'n even')] += 1
print(c)
# gapless chunks: show st and n parity and previous
print(rows[:12])
