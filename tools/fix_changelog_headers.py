import io, re

p = 'CHANGELOG.md'
s = io.open(p, encoding='utf-8', newline='').read()

times = {
    'v1.5.0': '2026-09-22 19:29',
    'v1.4.0': '2026-09-22 18:45',
    'v1.3.0': '2026-09-22 17:40',
    'v1.2.0': '2026-09-22 17:21',
    'v1.1.0': '2026-09-22 16:53',
    'v1.0.0': '2026-09-22 16:31',
}

hits = []
def fix(m):
    v = m.group(1)
    assert v in times, 'unknown version %s' % v
    hits.append(v)
    return '## [%s] - %s' % (v, times[v])

s2, n = re.subn(r'^## \[(v[0-9.]+)\] - .*$', fix, s, flags=re.M)
assert n == len(times), (n, len(times))

io.open(p, 'w', encoding='utf-8', newline='').write(s2)
print('fixed', n, hits)
print(re.findall(r'^## \[v[0-9.]+\] - .*$', s2, re.M))
