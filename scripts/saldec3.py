import sys
from saldec2 import load, chunks


def dec_scheme(b, i, end, scheme):
    vals = []
    while i < end:
        x = b[i]
        if x < 0x40:
            v = x; n = 1
        elif x < 0x80:
            v = ((x & 0x3f) << 8) | b[i+1]; n = 2
        else:
            n, v = scheme(b, i)
        vals.append(v)
        i += n
    return vals, i


def schemeA(b, i):
    x = b[i]
    if x < 0xc0:
        return 3, ((x & 0x3f) << 16) | (b[i+1] << 8) | b[i+2]
    if x < 0xe0:
        return 4, ((x & 0x1f) << 24) | (b[i+1] << 16) | (b[i+2] << 8) | b[i+3]
    if x < 0xf0:
        return 5, ((x & 0x0f) << 32) | (b[i+1] << 24) | (b[i+2] << 16) | (b[i+3] << 8) | b[i+4]
    return 9, int.from_bytes(b[i+1:i+9], 'big')


def schemeB(b, i):
    x = b[i]
    if x < 0xc0:
        return 3, ((x & 0x3f) << 16) | (b[i+1] << 8) | b[i+2]
    return 4, ((x & 0x3f) << 24) | (b[i+1] << 16) | (b[i+2] << 8) | b[i+3]


if __name__ == '__main__':
    b = load(sys.argv[1], int(sys.argv[2]))
    cs = chunks(b)
    for name, sc in (('A', schemeA), ('B', schemeB)):
        ok = 0; bad = []
        for k in range(len(cs) - 1):
            a, e, st, nb, d0 = cs[k]
            vals, i = dec_scheme(b, d0, d0 + nb, sc)
            s = sum(v + 1 for v in vals)
            if s == e - a and i == d0 + nb:
                ok += 1
            else:
                bad.append((k, e - a - s, i - d0 - nb))
        print(name, 'ok', ok, 'bad', len(bad), bad[:10])
