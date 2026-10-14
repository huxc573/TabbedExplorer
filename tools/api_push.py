# -*- coding: utf-8 -*-
"""走 GitHub Git Data API 推送本地提交 —— git https 通道被代理挡死时的唯一通路。

为什么有这个文件：本机 github.com 恒被代理挡（`Connection was reset` / `CONNECT tunnel 502`），
而 `api.github.com` 通。发版走 `git push` 必然失败，只能走 Git Data API。

用法
----
    python tools/api_push.py <owner/repo> <branch>            # 推分支（自动推 HEAD 之前所有未推的提交）
    python tools/api_tag.py  <owner/repo> <tag> <sha> <msg>   # 建附注标签（见姊妹文件）

设计要点（踩过的坑都写在这）
--------------------------
- **一次推多条**：从「本地树 == 远端头树」的那条本地提交起，逐条叠 blob → tree → commit。
  远端头多半是上次走 API 生成的提交，本地**根本没有这个对象**，所以不能 `git rev-list <远端头>..HEAD`，
  只能拿远端的 **tree sha** 在本地 `git rev-list HEAD` 里反查基点。
- **每条都断言** `POST /git/blobs` 回来的 sha == 本地 `git rev-parse <c>:<path>`，
  且 `POST /git/trees` 回来的 tree sha == 本地 `rev-parse <c>^{tree}` —— 不等立即中止，**别去 PATCH ref**。
- **blob 内容取自索引，绝不读磁盘**：`.gitattributes` 有 `* text=auto eol=lf` 时工作区字节 != 索引字节
  （本仓库钉 `*.bat|*.cmd` 为 CRLF）；用 `git cat-file blob <sha>` 拿原始字节再 base64。
- **提交的 author/committer 含日期全写死**（`%aI` / `%cI`）⇒ 整轮幂等，失败直接重跑，不用清孤儿。
- **每个请求 8 次重试 + 指数退避**（撞过代理超时把整轮掐死，前面建的提交全成孤儿）；
  只对 429/5xx/网络异常重试。⚠ 见到 4xx **先原样重跑一遍**再怀疑内容（代理侧会偶发 400）。
- ⚠ **必须后台跑**（前台会被 SIGTERM 掐在半路，常见「提交全建完、就差最后那次 PATCH ref」）。
- ⚠ 脚本**只推分支、不推标签** —— 标签单独建（`tools/api_tag.py`）。
- ⚠ 必须在仓库目录里跑（靠 `git rev-parse HEAD` 取本地头）。
"""
import base64
import json
import subprocess
import sys
import time
import urllib.error
import urllib.request

API = 'https://api.github.com'


def token():
    global TOKEN
    if TOKEN is None:
        TOKEN = subprocess.check_output(['gh', 'auth', 'token'], text=True).strip()
    return TOKEN


TOKEN = None


def req(method, path, body=None, tries=8):
    url = API + path
    data = json.dumps(body).encode('utf-8') if body is not None else None
    last = None
    for i in range(tries):
        try:
            r = urllib.request.Request(url, data=data, method=method)
            r.add_header('Authorization', 'Bearer ' + token())
            r.add_header('Accept', 'application/vnd.github+json')
            r.add_header('User-Agent', 'tbe-push')
            if data:
                r.add_header('Content-Type', 'application/json')
            with urllib.request.urlopen(r, timeout=180) as resp:
                raw = resp.read()
                return json.loads(raw) if raw else {}
        except urllib.error.HTTPError as e:
            if e.code in (429, 500, 502, 503, 504):
                last = e
                wait = min(60, 2 ** (i + 1))
                ra = e.headers.get('Retry-After')
                if ra and ra.isdigit():
                    wait = max(wait, int(ra))
                print('  %s %s -> %s, %ds 后重试 (%d/%d)'
                      % (method, path, e.code, wait, i + 1, tries), flush=True)
                time.sleep(wait)
                continue
            raise
        except Exception as e:      # URLError / timeout / 连接被重置 …
            last = e
            wait = min(60, 2 ** (i + 1))
            print('  %s %s -> %s, %ds 后重试 (%d/%d)'
                  % (method, path, type(e).__name__, wait, i + 1, tries), flush=True)
            time.sleep(wait)
    raise last


def git(*a):
    return subprocess.check_output(['git'] + list(a), text=True).strip()


def cat_blob(sha):
    return subprocess.check_output(['git', 'cat-file', 'blob', sha])


def main(owner_repo, branch):
    local_head = git('rev-parse', 'HEAD')
    print('本地头 =', local_head[:10], flush=True)

    ref = req('GET', '/repos/%s/git/ref/heads/%s' % (owner_repo, branch))
    remote_head = ref['object']['sha']
    print('远端头 =', remote_head[:10], flush=True)

    if remote_head == local_head:
        print('已同步，无需推送')
        return
    # ⚠ 远端头多半是上次走 API 生成的提交，本地**没有这个对象** ⇒ 只能借它的 tree sha，
    #   别 `git cat-file -t <远端头>`（会在本地报 unknown revision）。
    rc = req('GET', '/repos/%s/git/commits/%s' % (owner_repo, remote_head))
    remote_tree = rc['tree']['sha']

    # 基点 = 本地那条「树 == 远端头树」的提交
    base = None
    for sha in git('rev-list', 'HEAD').split('\n'):
        if sha and git('rev-parse', sha + '^{tree}') == remote_tree:
            base = sha
            break
    if base is None:
        print('!! 远端头的内容在本地历史里找不到（树对不上）。先看清远端多了什么，别硬推。')
        sys.exit(2)
    print('基点（本地树 == 远端头树）=', base[:10], flush=True)

    chain = [c for c in git('rev-list', '--reverse', '%s..HEAD' % base).split('\n') if c]
    print('待推 %d 条' % len(chain), flush=True)

    parent = remote_head
    prev_tree = remote_tree
    for i, c in enumerate(chain, 1):
        prev_local = base if i == 1 else chain[i - 2]
        files = [f for f in git('diff', '--name-only', prev_local, c).split('\n') if f]

        entries = []
        for f in files:
            st = git('ls-tree', c, '--', f)
            if not st:
                continue                      # 这条提交里被删除的文件：不加 entry 即在树里消失
            meta, path = st.split('\t', 1)
            mode, _typ, sha = meta.split(' ')
            blob = req('POST', '/repos/%s/git/blobs' % owner_repo,
                       {'content': base64.b64encode(cat_blob(sha)).decode('ascii'),
                        'encoding': 'base64'})
            assert blob['sha'] == sha, 'blob sha 不符: %s %s != %s' % (f, blob['sha'], sha)
            entries.append({'path': path, 'mode': mode, 'type': 'blob', 'sha': sha})

        tree = req('POST', '/repos/%s/git/trees' % owner_repo,
                   {'base_tree': prev_tree, 'tree': entries})
        want = git('rev-parse', c + '^{tree}')
        assert tree['sha'] == want, 'tree sha 不符: %s != %s' % (tree['sha'], want)

        msg = git('log', '-1', '--format=%B', c)
        a = git('log', '-1', '--format=%an <%ae>|%aI', c).split('|')
        m = git('log', '-1', '--format=%cn <%ce>|%cI', c).split('|')

        def who(s):
            return {'name': s[0].split('<')[0].strip(),
                    'email': s[0].split('<')[1].rstrip('>'),
                    'date': s[1]}

        newc = req('POST', '/repos/%s/git/commits' % owner_repo,
                   {'message': msg, 'tree': tree['sha'], 'parents': [parent],
                    'author': who(a), 'committer': who(m)})
        parent = newc['sha']
        prev_tree = tree['sha']
        print('[%d/%d] ok %s -> %s  %s'
              % (i, len(chain), c[:8], parent[:8], git('log', '-1', '--format=%s', c)[:60]), flush=True)

    req('PATCH', '/repos/%s/git/refs/heads/%s' % (owner_repo, branch),
        {'sha': parent, 'force': True})
    print('REMOTE_HEAD=' + parent)


if __name__ == '__main__':
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(1)
    TOKEN = token()
    try:
        subprocess.check_output(['git', 'rev-parse', '--git-dir'], stderr=subprocess.DEVNULL)
    except Exception:
        print('!! 请在仓库目录里跑')
        sys.exit(3)
    main(sys.argv[1], sys.argv[2])
