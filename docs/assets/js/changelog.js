/* 更新日志:运行时从 GitHub Releases 拉,别在页面里硬编码版本 */
(function () {
  var REPO = 'weiqi114514/ZincXManager';
  var API = 'https://api.github.com/repos/' + REPO + '/releases?per_page=100';
  var CACHE_KEY = 'zxm.releases';
  var CACHE_TTL = 10 * 60 * 1000; // 10 分钟

  function esc(s) {
    return String(s == null ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;');
  }

  function inline(s) {
    return s
      .replace(/`([^`]+)`/g, '<span class="mono">$1</span>')
      .replace(/\*\*([^*]+)\*\*/g, '<b>$1</b>');
  }

  /* 极简 markdown → HTML:标题 / 列表 / 粗体 / 行内代码 / 段落 */
  function md(src) {
    if (!src) return '';
    var out = [];
    var inList = false;
    var lines = String(src).replace(/\r/g, '').split('\n');

    function closeList() { if (inList) { out.push('</ul>'); inList = false; } }

    for (var i = 0; i < lines.length; i++) {
      var line = lines[i];
      var t = line.trim();

      if (!t) { closeList(); continue; }

      var h = /^(#{1,6})\s+(.*)$/.exec(t);
      if (h) {
        closeList();
        var lv = Math.min(h[1].length + 2, 6);
        out.push('<h' + lv + '>' + inline(esc(h[2])) + '</h' + lv + '>');
        continue;
      }

      var li = /^[-*]\s+(.*)$/.exec(t);
      if (li) {
        if (!inList) { out.push('<ul>'); inList = true; }
        out.push('<li>' + inline(esc(li[1])) + '</li>');
        continue;
      }

      if (/^>/.test(t)) {
        closeList();
        out.push('<blockquote>' + inline(esc(t.replace(/^>\s?/, ''))) + '</blockquote>');
        continue;
      }

      closeList();
      out.push('<p>' + inline(esc(t)) + '</p>');
    }

    closeList();
    return out.join('\n');
  }

  function getReleases() {
    try {
      var raw = localStorage.getItem(CACHE_KEY);
      if (raw) {
        var c = JSON.parse(raw);
        if (c && c.t && Date.now() - c.t < CACHE_TTL && c.d) return Promise.resolve(c.d);
      }
    } catch (e) { /* 缓存坏了就算了 */ }

    return fetch(API, { headers: { Accept: 'application/vnd.github+json' } })
      .then(function (r) {
        if (!r.ok) throw new Error('HTTP ' + r.status);
        return r.json();
      })
      .then(function (data) {
        var list = (data || []).filter(function (x) { return !x.draft; });
        try { localStorage.setItem(CACHE_KEY, JSON.stringify({ t: Date.now(), d: list })); } catch (e) { }
        return list;
      });
  }

  function fmtDate(iso) {
    if (!iso) return '';
    var d = new Date(iso);
    return d.getFullYear() + '-' + String(d.getMonth() + 1).padStart(2, '0') + '-' + String(d.getDate()).padStart(2, '0');
  }

  /* 列表页:每个 release 一张卡 */
  function renderList(el) {
    el.innerHTML = '<p style="color:var(--fg-dim)">正在从 GitHub 拉取更新日志…</p>';
    getReleases().then(function (list) {
      if (!list.length) { el.innerHTML = '<p style="color:var(--fg-dim)">还没有发布任何版本。</p>'; return; }

      el.innerHTML = list.map(function (r) {
        var title = '[' + esc(r.tag_name) + '] - ' + fmtDate(r.published_at);
        var firstLine = (r.body || '').replace(/\r/g, '').split('\n').filter(function (x) { return x.trim() && !/^#/.test(x.trim()); })[0] || '';
        var summary = firstLine.replace(/^[-*>\s]+/, '').replace(/\*\*/g, '').slice(0, 160);
        return '<a class="card-block" href="changelog-detail.html?tag=' + encodeURIComponent(r.tag_name) + '" style="display:block;text-decoration:none;color:inherit">' +
          '<h3 style="margin:0 0 8px">' + title + '</h3>' +
          '<p style="margin:0;color:var(--fg-dim);font-size:13.5px">' + esc(summary) + '</p>' +
          '</a>';
      }).join('');
    }).catch(function (e) {
      el.innerHTML = '<p style="color:var(--fg-dim)">拉取失败:' + esc(e.message) +
        '<br>可以直接去 <a href="https://github.com/' + REPO + '/releases" target="_blank" rel="noopener">GitHub Releases</a> 看。</p>';
    });
  }

  /* 左侧版本列表 */
  function renderNav(el, current) {
    getReleases().then(function (list) {
      var html = '<div class="grp">Versions</div>';
      html += list.map(function (r) {
        var on = r.tag_name === current ? ' class="on"' : '';
        return '<a href="changelog-detail.html?tag=' + encodeURIComponent(r.tag_name) + '"' + on + '>[' + esc(r.tag_name) + '] - ' + fmtDate(r.published_at) + '</a>';
      }).join('');
      html += '<div class="grp">Links</div><a href="changelog.html">全部版本</a><a href="download.html">下载</a>';
      el.innerHTML = html;
    }).catch(function () {
      el.innerHTML = '<div class="grp">Links</div><a href="changelog.html">全部版本</a><a href="download.html">下载</a>';
    });
  }

  /* 详情页 */
  function renderDetail(el, tag) {
    if (!tag) { el.innerHTML = '<p style="color:var(--fg-dim)">没有指定版本。</p>'; return; }

    el.innerHTML = '<p style="color:var(--fg-dim)">正在从 GitHub 拉取 ' + esc(tag) + ' …</p>';
    getReleases().then(function (list) {
      var r = list.filter(function (x) { return x.tag_name === tag; })[0];
      if (!r) { el.innerHTML = '<p style="color:var(--fg-dim)">GitHub 上没找到版本 ' + esc(tag) + '。</p>'; return; }

      var idx = list.indexOf(r);
      var newer = idx > 0 ? list[0] : null;
      var older = idx >= 0 && idx < list.length - 1 ? list[idx + 1] : null;

      var html = '<h3>[' + esc(r.tag_name) + '] - ' + fmtDate(r.published_at) + '</h3>';
      html += md(r.body);
      html += '<div style="display:flex;gap:10px;flex-wrap:wrap;margin-top:34px;padding-top:22px;border-top:1px solid var(--line)">';
      html += '<a class="btn" href="changelog.html">全部版本</a>';
      if (newer) html += '<a class="btn" href="changelog-detail.html?tag=' + encodeURIComponent(newer.tag_name) + '">[' + esc(newer.tag_name) + '] →</a>';
      if (older) html += '<a class="btn" href="changelog-detail.html?tag=' + encodeURIComponent(older.tag_name) + '">← [' + esc(older.tag_name) + ']</a>';
      html += '<a class="btn" href="https://github.com/' + REPO + '/releases/tag/' + encodeURIComponent(r.tag_name) + '" target="_blank" rel="noopener">GitHub 上查看</a>';
      html += '</div>';

      el.innerHTML = html;
    }).catch(function (e) {
      el.innerHTML = '<p style="color:var(--fg-dim)">拉取失败:' + esc(e.message) + '</p>';
    });
  }

  window.ZxmChangelog = { renderList: renderList, renderNav: renderNav, renderDetail: renderDetail };
})();
