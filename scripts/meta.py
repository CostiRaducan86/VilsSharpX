import zipfile, json, re, sys

for f in sys.argv[1:]:
    m = json.loads(zipfile.ZipFile(f).read('meta.json'))
    d = m['data']
    print(f, list(d.keys()))
    s = json.dumps(d)
    print(sorted(set(re.findall(r'"(?:name|label)": "[^"]*"', s)))[:60])
    for k in d:
        if k not in ('analyzers', 'renderViewState', 'highLevelAnalyzers'):
            print(k, json.dumps(d[k])[:600])
