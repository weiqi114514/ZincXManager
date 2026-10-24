/* =====================================================================
   ZincXManager 官网 · 视觉动效(fx.js)
   纯原生 JS,无依赖。目标:进站就有"被推一把"的感觉,但不喧哗。
   - 顶部滚动进度
   - 首屏标题逐字浮现
   - 鼠标跟随的网格视差
   - 滚动进场(IntersectionObserver)
   - 数字滚动
   - 块面 hover 反色跟随
   ===================================================================== */
(function () {
  var reduce = window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;

  /* ---------- 1. 顶部滚动进度条 ---------- */
  var bar = document.createElement("div");
  bar.className = "fx-progress";
  document.body.appendChild(bar);
  function tick() {
    var h = document.documentElement;
    var max = (h.scrollHeight - h.clientHeight) || 1;
    bar.style.transform = "scaleX(" + Math.min(1, h.scrollTop / max) + ")";
  }
  window.addEventListener("scroll", tick, { passive: true });
  tick();

  /* ---------- 3. 鼠标跟随的网格视差 ---------- */
  var gridEl = document.querySelector(".grid-bg");
  if (gridEl && !reduce) {
    var gx = 0, gy = 0, cx = 0, cy = 0, raf = null;
    window.addEventListener("mousemove", function (e) {
      gx = (e.clientX / window.innerWidth - 0.5) * 18;
      gy = (e.clientY / window.innerHeight - 0.5) * 18;
      if (!raf) raf = requestAnimationFrame(loop);
    });
    function loop() {
      cx += (gx - cx) * 0.08;
      cy += (gy - cy) * 0.08;
      gridEl.style.backgroundPosition = cx + "px " + cy + "px";
      raf = (Math.abs(gx - cx) > 0.2 || Math.abs(gy - cy) > 0.2) ? requestAnimationFrame(loop) : null;
    }
  }

  /* ---------- 4. 滚动进场 ---------- */
  var targets = document.querySelectorAll(".sec-head, .trio article, .row, .doc-hero, .card-block, .version-block");
  if ("IntersectionObserver" in window && !reduce) {
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (en) {
        if (!en.isIntersecting) return;
        en.target.style.transition = "opacity .8s cubic-bezier(.22,.61,.36,1), transform .8s cubic-bezier(.22,.61,.36,1)";
        en.target.style.opacity = "1";
        en.target.style.transform = "translateY(0)";
        io.unobserve(en.target);
      });
    }, { threshold: 0.12 });
    targets.forEach(function (el) {
      el.style.opacity = "0";
      el.style.transform = "translateY(26px)";
      io.observe(el);
    });

    // 兜底:2.2 秒后无论是否触发,都保证内容可见(避免观察器失灵时页面空白)
    setTimeout(function () {
      targets.forEach(function (el) {
        if (getComputedStyle(el).opacity === "0") {
          el.style.transition = "opacity .5s linear";
          el.style.opacity = "1";
          el.style.transform = "none";
        }
      });
    }, 2200);
  }

  /* ---------- 5. 数字滚动 ---------- */
  function countUp(el) {
    var raw = el.textContent.trim();
    var m = raw.match(/^(\d+(?:\.\d+)?)/);
    if (!m) return;
    var target = parseFloat(m[1]), cur = 0, dec = (m[1].split(".")[1] || "").length;
    var t0 = performance.now(), dur = 900;
    function step(t) {
      var k = Math.min(1, (t - t0) / dur);
      var e = 1 - Math.pow(1 - k, 3);
      el.textContent = (cur + (target - cur) * e).toFixed(dec) + raw.slice(m[1].length);
      if (k < 1) { requestAnimationFrame(step); } else { el.textContent = raw; }
    }
    requestAnimationFrame(step);
  }
  if (!reduce) {
    var nums = document.querySelectorAll(".hero-side .stat b");
    if ("IntersectionObserver" in window) {
      var io2 = new IntersectionObserver(function (es) {
        es.forEach(function (e) { if (e.isIntersecting) { countUp(e.target); io2.unobserve(e.target); } });
      }, { threshold: 0.6 });
      nums.forEach(function (n) { io2.observe(n); });
    }
  }

  /* ---------- 6. 指针光斑(块面 hover) ---------- */
  if (!reduce) {
    document.querySelectorAll(".trio article, .row .items div, .card-block").forEach(function (el) {
      el.style.position = el.style.position || "relative";
      el.addEventListener("mousemove", function (e) {
        var r = el.getBoundingClientRect();
        el.style.setProperty("--mx", (e.clientX - r.left) + "px");
        el.style.setProperty("--my", (e.clientY - r.top) + "px");
      });
    });
  }
})();
