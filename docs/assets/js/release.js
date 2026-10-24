/* ZincXManager 官网:下载信息动态化
 * ---------------------------------------------------------------------------
 * 运行时从 GitHub Releases 拉最新版本,自动填:版本号 / 发布页链接 / 三个安装包
 * 的文件名、大小、GitHub 直链、本站镜像链接。这样以后发新版**不用再改官网**,
 * 在 GitHub 发一个 Release(带那三个附件)就自动生效。
 *
 * HTML 里保留的旧版本号只是"兜底":拉不到(离线 / API 限流 / 被墙)时页面照常可用。
 * 依赖的 GitHub API 支持跨域(api.github.com 返回 Access-Control-Allow-Origin: *)。
 * ---------------------------------------------------------------------------
 */
(function () {
  'use strict';

  var REPO = 'weiqi114514/ZincXManager';
  var API = 'https://api.github.com/repos/' + REPO + '/releases/latest';
  var REL = 'https://github.com/' + REPO + '/releases';

  function all(sel) { return Array.prototype.slice.call(document.querySelectorAll(sel)); }
  function setText(sel, v) { if (!v) return false; all(sel).forEach(function (el) { el.textContent = v; }); return true; }
  function setHref(sel, v) { if (!v) return false; all(sel).forEach(function (el) { el.href = v; }); return true; }
  function mb(n) { return n ? (n / 1048576).toFixed(1) + ' MB' : ''; }

  /* 三个附件按文件名挑:MSI / 免安装 ZIP / 子服务端 ZIP(排除 portable) */
  function pickAssets(list) {
    var out = { msi: null, port: null, srv: null };
    (list || []).forEach(function (a) {
      var n = a.name || '';
      if (/-x64\.msi$/i.test(n)) { out.msi = out.msi || a; return; }
      if (/-win-x64-portable\.zip$/i.test(n)) { out.port = out.port || a; return; }
      if (/-win-x64\.zip$/i.test(n) && !/portable/i.test(n)) { out.srv = out.srv || a; }
    });
    return out;
  }

  if (!window.fetch) return;

  fetch(API, { headers: { Accept: 'application/vnd.github+json' } })
    .then(function (r) { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); })
    .then(function (rel) {
      var tag = rel.tag_name || '';
      var ver = String(tag).replace(/^v/i, '');
      var date = String(rel.published_at || '').slice(0, 10);
      var a = pickAssets(rel.assets);

      /* 版本号 / 日期 */
      setText('.js-ver', ver);
      setText('.js-tag', tag);
      setText('.js-date', date);

      /* 三个包:文件名 / 大小 / 本站镜像 / GitHub 直链 */
      [['msi', a.msi], ['port', a.port], ['srv', a.srv]].forEach(function (pair) {
        var k = pair[0], asset = pair[1];
        if (!asset) return;
        setText('.js-name-' + k, asset.name);
        setText('.js-size-' + k, mb(asset.size));
        if (setHref('.js-mirror-' + k, '/downloads/' + asset.name)) {
          setText('.js-mirror-' + k, '下载 ' + asset.name);
        }
        setHref('.js-link-' + k, asset.browser_download_url);
        if (asset.download_count != null) setText('.js-count-' + k, asset.download_count + ' 次下载');
      });

      /* 发布页(带版本号) */
      setHref('.js-relpage', REL + '/tag/' + tag);
      /* 纯 Releases 列表页 */
      setHref('.js-releases', REL);

      /* 给页面一个"已用实时数据"的标记,便于样式上区分 */
      document.documentElement.setAttribute('data-release', tag || 'unknown');
    })
    .catch(function () {
      /* 拉取失败就保持 HTML 里的静态内容,不打断页面 */
      document.documentElement.setAttribute('data-release', 'fallback');
    });
})();
