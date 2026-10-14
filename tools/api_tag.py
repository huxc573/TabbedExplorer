# -*- coding: utf-8 -*-
"""建 / 重指附注标签（annotated tag）—— `api_push.py` 的姊妹工具。

为什么单独一个：Git Data API 推完分支后**远端头 sha 与本地不同**（远端那条是 API 新建的），
所以标签必须**显式**指向远端的提交 sha。`gh release create` 自动建的是**轻量标签**且指向 main 头 ——
不符合本仓库「标签是附注标签」的惯例（见 CHANGELOG 顶部说明）。

用法
----
    python tools/api_tag.py <owner/repo> <tag> <远端提交sha> [message]

- 标签**已存在** ⇒ `PATCH refs/tags/<t> -f force=true`；**不存在** ⇒ `POST refs/tags`。
  （对不存在的 ref 发 PATCH 会 422 `Reference does not exist`。）
- 完整 sha 从 `gh api repos/<o>/<r>/git/ref/heads/main --jq .object.sha` 取。
"""
import json
import subprocess
import sys
import time
import urllib.error
import urllib.request

API = 'https://api.github.com'
TOKEN = None


def token():
    global TOKEN
    if TOKEN is None:
        TOKEN = subprocess.check_output(['gh', 'auth', 'token'], text=True).strip()
    return TOKEN


def req(method, path, body=None, tries=6):
    url = API + path
    data = json.dumps(body).encode('utf-8') if body is not None else None
    last = None
    for i in range(tries):
        try:
            r = urllib.request.Request(url, data=data, method=method)
            r.add_header('Authorization', 'Bearer ' + token())
            r.add_header('Accept', 'application/vnd.github+json')
            r.add_header('User-Agent', 'tbe-tag')
            if data:
                r.add_header('Content-Type', 'application/json')
            with urllib.request.urlopen(r, timeout=120) as resp:
                raw = resp.read()
                return json.loads(raw) if raw else {}
        except urllib.error.HTTPError as e:
            if e.code == 404:
                raise
            last = e
            time.sleep(min(45, 2 ** (i + 1)))
        except Exception as e:
            last = e
            time.sleep(min(45, 2 ** (i + 1)))
    raise last


def main(owner_repo, tag, sha, msg):
    obj = req('POST', '/repos/%s/git/tags' % owner_repo,
              {'tag': tag, 'message': msg, 'object': sha, 'type': 'commit'})
    tsha = obj['sha']
    print('TAG_SHA=' + tsha)

    try:
        req('GET', '/repos/%s/git/ref/tags/%s' % (owner_repo, tag))
        exists = True
    except urllib.error.HTTPError as e:
        if e.code != 404:
            raise
        exists = False

    if exists:
        req('PATCH', '/repos/%s/git/refs/tags/%s' % (owner_repo, tag),
            {'sha': tsha, 'force': True})
        print('已存在 -> 覆盖')
    else:
        req('POST', '/repos/%s/git/refs' % owner_repo,
            {'ref': 'refs/tags/' + tag, 'sha': tsha})
        print('已建')
    print('OK ' + tag)


if __name__ == '__main__':
    if len(sys.argv) < 4:
        print(__doc__)
        sys.exit(1)
    token()
    main(sys.argv[1], sys.argv[2], sys.argv[3],
         sys.argv[4] if len(sys.argv) > 4 else sys.argv[2])
