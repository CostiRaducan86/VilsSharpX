import zipfile, struct, sys, collections

def load(path, ch):
    z = zipfile.ZipFile(path)
    b = z.read('digital-%d.bin' % ch)
    return b

def decode(b, start=77, limit=None, verbose=False):
    i = start
    vals = []
    tags = collections.Counter()
    n = len(b)
    while i < n:
        t = b[i] >> 6
        tags[t] += 1
        if t == 0:
            v = b[i] & 0x3f; i += 1
        elif t == 1:
            v = ((b[i] & 0x3f) << 8) | b[i+1]; i += 2
        elif t == 2:
            v = ((b[i] & 0x3f) << 16) | (b[i+1] << 8) | b[i+2]; i += 3
        else:
            v = -(((b[i] & 0x3f) << 8) | b[i+1]); i += 2
        vals.append(v)
        if limit and len(vals) >= limit:
            break
    return vals, tags, i

if __name__ == '__main__':
    b = load(sys.argv[1], int(sys.argv[2]))
    vals, tags, i = decode(b)
    print(len(b), i, tags)
    c = collections.Counter(v for v in vals)
    print(c.most_common(40))
    neg = [k for k, v in enumerate(vals) if v < 0]
    print('tag3 count', len(neg), [vals[k] for k in neg[:20]])
    big = [(k, v) for k, v in enumerate(vals) if v > 1000]
    print('big', len(big), big[:20])
