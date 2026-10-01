import sys
from salan import load_raw, chunk_list

path = sys.argv[1]
for ch in (7, 0, 6):
    b = load_raw(path, ch)
    cs = chunk_list(b)
    agree_start = agree_end = 0
    level_end_prev = None
    rows = []
    for (a, e, st, nb, d0) in cs[:200]:
        i = d0; n = 0; longest = (0, -1)
        end = d0 + nb
        while i < end:
            x = b[i]
            if x < 0x40:
                w = x + 1; i += 1
            elif x < 0x80:
                w = (((x & 0x3f) << 8) | b[i + 1]) + 1; i += 2
            else:
                w = 10**9; i += 2
            if w > longest[0]:
                longest = (w, n)
            n += 1
        # if longest run is idle (high): level of run j = st ^ (j&1) under 'st=initial' hypothesis
        lv_init_hyp = st ^ (longest[1] & 1)
        rows.append((st, n, longest[1], lv_init_hyp))
    ok = sum(1 for r in rows if r[3] == 1)
    print('ch', ch, 'chunks', len(rows), 'idle-high consistent with st=initial level:', ok, 'of', len(rows))
    print('  sample', rows[:6])
