/* ZincXManager 下载列表 —— 数据实时取自 GitHub Releases,不用手改页面 */
(function () {
  // 仓库名:拉 releases 用
  const REPO = 'weiqi114514/ZincXManager';
  // 缓存键:避免每次刷新都打 GitHub API(匿名限流 60 次/小时)
  const CACHE_KEY = 'zxm_releases_v1';
  // 缓存有效期(毫秒):10 分钟
  const CACHE_TTL = 10 * 60 * 1000;

  // 当前筛选状态(默认从网址参数初始化,方便直接分享某个筛选,比如 ?kind=server&system=linux)
  const params = new URLSearchParams(location.search);
  const filter = {
    kind: params.get('kind') || 'all',
    system: params.get('system') || 'windows',
  };

  /** 取资产属于哪个端 */
  function kindOf(name) {
    if (/^ZincXManagerClient/i.test(name)) return { key: 'client', label: '客户端' };
    if (/^ZincXManagerServer/i.test(name)) return { key: 'server', label: '子服务端' };
    if (/^ZincXManagerTests/i.test(name)) return { key: 'tests', label: '测试工具' };
    if (/^ZincXManagerLicTool/i.test(name)) return { key: 'lictool', label: '许可证工具' };
    // 模组:ZXMM-x.y.z.jar(NeoForge 服务端模组)
    if (/^ZXMM/i.test(name) || /\.jar$/i.test(name)) return { key: 'mod', label: '模组 ZXMM' };
    return { key: 'other', label: '其它' };
  }

  /** 取资产面向哪个系统:名字里带 linux / mac 才认,其余按 Windows */
  function systemOf(name) {
    if (/linux/i.test(name)) return 'linux';
    if (/(macos|osx|darwin|mac-x64|mac-arm)/i.test(name)) return 'macos';
    return 'windows';
  }

  /** 文件名里判断包类型 */
  function formatOf(name) {
    if (/\.msi$/i.test(name)) return 'MSI';
    if (/\.zip$/i.test(name)) return 'ZIP';
    if (/\.exe$/i.test(name)) return 'EXE';
    return '文件';
  }

  /** 字节数 → 人类可读 */
  function sizeText(bytes) {
    if (!bytes) return '—';
    const mb = bytes / 1048576;
    return mb >= 1 ? mb.toFixed(1) + ' MB' : Math.max(1, Math.round(bytes / 1024)) + ' KB';
  }

  /** 日期 → yyyy-MM-dd */
  function dateText(iso) {
    if (!iso) return '';
    return iso.slice(0, 10);
  }

  /** 拉一次 releases(带 sessionStorage 缓存) */
  async function load() {
    const cached = sessionStorage.getItem(CACHE_KEY);
    if (cached) {
      try {
        const box = JSON.parse(cached);
        if (Date.now() - box.at < CACHE_TTL) return box.data;
      } catch (_) { /* 缓存坏了就当没有 */ }
    }

    const res = await fetch('https://api.github.com/repos/' + REPO + '/releases?per_page=30', {
      headers: { Accept: 'application/vnd.github+json' },
    });
    if (!res.ok) throw new Error('GitHub 返回 ' + res.status);
    const data = await res.json();
    sessionStorage.setItem(CACHE_KEY, JSON.stringify({ at: Date.now(), data: data }));
    return data;
  }

  /** 把 releases 摊平成一条条资产 */
  function flatten(releases) {
    const rows = [];
    for (const rel of releases) {
      if (rel.draft) continue;
      for (const asset of rel.assets || []) {
        rows.push({
          tag: rel.tag_name,
          name: asset.name,
          url: asset.browser_download_url,
          size: asset.size,
          date: rel.published_at,
          kind: kindOf(asset.name),
          system: systemOf(asset.name),
          format: formatOf(asset.name),
        });
      }
    }
    return rows;
  }

  /** 按当前筛选条件过滤 */
  function visible(rows) {
    return rows.filter((r) => {
      if (filter.system && r.system !== filter.system) return false;
      if (filter.kind === 'all') return true;
      return r.kind.key === filter.kind;
    });
  }

  /** 渲染列表 */
  function render(rows, root) {
    const list = visible(rows);
    if (!list.length) {
      root.innerHTML = '<div class="dl-empty">这个组合下没有文件。换个筛选条件,或去 <a href="https://github.com/' + REPO + '/releases" target="_blank" rel="noopener">Releases</a> 看看。</div>';
      return;
    }

    // 版本从新到旧(按 tag 出现顺序就是新到旧)
    const groups = new Map();
    for (const row of list) {
      if (!groups.has(row.tag)) groups.set(row.tag, []);
      groups.get(row.tag).push(row);
    }

    let html = '';
    for (const [tag, assets] of groups) {
      html += '<div class="dl-ver"><div class="dl-ver-head"><b>' + tag + '</b><span>' + dateText(assets[0].date) + '</span></div>';
      for (const a of assets) {
        html += '<div class="dl-row">'
          + '<span class="dl-kind">' + a.kind.label + '</span>'
          + '<span class="dl-name mono" title="' + a.name + '">' + a.name + '</span>'
          + '<span class="dl-size">' + sizeText(a.size) + '</span>'
          + '<a class="btn btn-primary dl-btn" href="' + a.url + '">下载</a>'
          + '</div>';
      }
      html += '</div>';
    }
    root.innerHTML = html;
  }

  /** 4 个统计格子:最新版本 / 各系统体积 */
  function renderStats(rows) {
    const latest = rows[0];
    const set = (id, text) => {
      const el = document.getElementById(id);
      if (el) el.textContent = text;
    };
    if (!latest) return;
    set('dl-latest', latest.tag);
    set('dl-date', dateText(latest.date));
    set('dl-stat-version', latest.tag);

    const pick = (kind, system) => rows.find((r) => r.kind.key === kind && r.system === system && r.format === 'MSI')
      || rows.find((r) => r.kind.key === kind && r.system === system);
    const win = pick('client', 'windows');
    const linux = pick('client', 'linux');
    const mac = pick('client', 'macos');
    set('dl-stat-win', win ? sizeText(win.size) : '—');
    set('dl-stat-linux', linux ? sizeText(linux.size) : '—');
    set('dl-stat-mac', mac ? sizeText(mac.size) : '—');
  }

  /** 绑定筛选按钮:按钮上 data-kind / data-system 就是要筛的值 */
  function bindFilters(root, rows) {
    root.querySelectorAll('[data-kind]').forEach((btn) => {
      btn.addEventListener('click', () => {
        filter.kind = btn.dataset.kind;
        root.querySelectorAll('[data-kind]').forEach((b) => b.classList.toggle('on', b === btn));
        render(rows, document.getElementById('dl-list'));
      });
    });
    root.querySelectorAll('[data-system]').forEach((btn) => {
      btn.addEventListener('click', () => {
        filter.system = btn.dataset.system;
        root.querySelectorAll('[data-system]').forEach((b) => b.classList.toggle('on', b === btn));
        render(rows, document.getElementById('dl-list'));
      });
    });
  }

  /** 打开/关闭小窗口 */
  function bindModal() {
    const modal = document.getElementById('dl-modal');
    const open = document.getElementById('dl-open');
    const close = document.getElementById('dl-close');
    if (!modal || !open) return;
    open.addEventListener('click', () => {
      modal.hidden = false;
      document.body.classList.add('dl-lock');
      // 高亮和当前筛选状态对齐(可能来自网址参数)
      modal.querySelectorAll('[data-kind]').forEach((b) => b.classList.toggle('on', b.dataset.kind === filter.kind));
      modal.querySelectorAll('[data-system]').forEach((b) => b.classList.toggle('on', b.dataset.system === filter.system));
    });
    const shut = () => { modal.hidden = true; document.body.classList.remove('dl-lock'); };
    if (close) close.addEventListener('click', shut);
    modal.addEventListener('click', (e) => { if (e.target === modal) shut(); });
    document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && !modal.hidden) shut(); });
  }

  /** 首页也拿最新版本号 */
  function renderHome(rows) {
    const tag = document.getElementById('hero-version');
    if (tag && rows[0]) {
      tag.textContent = ' 最新版本 · ' + rows[0].tag ;
    }
  }

  async function boot() {
    bindModal();
    try {
      const rows = flatten(await load());
      renderStats(rows);
      renderHome(rows);
      const list = document.getElementById('dl-list');
      if (list) {
        render(rows, list);
        bindFilters(document.getElementById('dl-modal'), rows);
      }
    } catch (err) {
      const list = document.getElementById('dl-list');
      if (list) {
        list.innerHTML = '<div class="dl-empty">拉取 GitHub 失败(' + err.message + ')。可以直接去 <a href="https://github.com/' + REPO + '/releases" target="_blank" rel="noopener">Releases 页面</a> 下载。</div>';
      }
    }
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', boot);
  } else {
    boot();
  }
})();
