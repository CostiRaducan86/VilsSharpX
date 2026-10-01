import zipfile, struct, sys, collections


def load(path, ch):
    z = zipfile.ZipFile(path)
    return z.read('digital-%d.bin' % ch)


def dec_vals(b, i, end):
    vals = []
    while i < end:
        t = b[i] >> 6
        if t == 0:
            v = b[i] & 0x3f; i += 1
        elif t == 1:
            v = ((b[i] & 0x3f) << 8) | b[i+1]; i += 2
        elif t == 2:
            v = ((b[i] & 0x3f) << 16) | (b[i+1] << 8) | b[i+2]; i += 3
        else:
            v = ('T3', ((b[i] & 0x3f) << 8) | b[i+1]); i += 2
        vals.append(v)
    return vals, i


def chunks(b):
    # first chunk header ends at 77: [u64 a @51][u64 start @59][u16 state @67][u64 nbytes @69]
    hdr = 51
    out = []
    while hdr + 26 <= len(b):
        a, start, state, nb = struct.unpack_from('<QQHQ', b, hdr)
        d0 = hdr + 26
        out.append((a, start, state, nb, d0))
        hdr = d0 + nb
    return out


if __name__ == '__main__':
    b = load(sys.argv[1], int(sys.argv[2]))
    cs = chunks(b)
    print('chunks', len(cs))
    for c in cs[:5]:
        print(c)
    print(cs[-1])
    # validate sums
    for k in range(min(4, len(cs) - 1)):
        a, start, state, nb, d0 = cs[k]
        vals, _ = dec_vals(b, d0, d0 + nb)
        t3 = [v for v in vals if isinstance(v, tuple)]
        s = sum(v for v in vals if not isinstance(v, tuple))
        n = sum(1 for v in vals if not isinstance(v, tuple))
        print(k, 'n', n, 'sum', s, 'sum+n', s + n, 'next-start', cs[k+1][1] - start, 't3', t3[:10], len(t3))
