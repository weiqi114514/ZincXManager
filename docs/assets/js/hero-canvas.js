/* =====================================================================
   ZincXManager 官网 · 首屏程序化渲染(hero-canvas.js)
   纯 Canvas 2D,不依赖任何图片/库:透视网格 + 沿网格流动的光点。
   尊重 prefers-reduced-motion:减少动效时只画静态一帧。
   ===================================================================== */
(function () {
  var reduce = window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  var cv = document.getElementById("heroCanvas");
  if (!cv) return;
  var ctx = cv.getContext("2d");
  if (!ctx) return;

  var w = 0, h = 0, dpr = 1;
  var HORIZON = 0.42;   // 消失点高度占比
  var VANISH = 0.5;     // 消失点水平位置占比
  var dots = [];        // 流动光点

  function resize() {
    var r = cv.getBoundingClientRect();
    dpr = Math.min(2, window.devicePixelRatio || 1);
    w = Math.max(1, Math.floor(r.width));
    h = Math.max(1, Math.floor(r.height));
    cv.width = Math.floor(w * dpr);
    cv.height = Math.floor(h * dpr);
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  }

  function line(x1, y1, x2, y2, alpha, width) {
    ctx.strokeStyle = "rgba(78,156,156," + alpha + ")";
    ctx.lineWidth = width || 1;
    ctx.beginPath();
    ctx.moveTo(x1, y1);
    ctx.lineTo(x2, y2);
    ctx.stroke();
  }

  /* 透视网格:垂直线向消失点收敛,水平线由远及近指数加密 */
  function drawGrid() {
    var vy = h * HORIZON, vx = w * VANISH;
    var i, t, bx, k, y;

    for (i = 0; i <= 16; i++) {
      t = i / 16;
      bx = w * (t - 0.5) * 2.6 + w * 0.5;
      line(vx, vy, bx, h, 0.055 + 0.045 * Math.sin(t * Math.PI));
    }
    for (i = 0; i <= 14; i++) {
      k = Math.pow(i / 14, 2.2);
      y = vy + (h - vy) * k;
      line(0, y, w, y, 0.04 + 0.11 * k);
    }
  }

  function initDots() {
    dots = [];
    for (var i = 0; i < 28; i++) {
      dots.push({
        t: Math.random(),                     // 纵深进度 0=远 1=近
        lane: Math.random(),                  // 横向轨道
        speed: 0.00045 + Math.random() * 0.0013
      });
    }
  }

  function drawDots(dt) {
    var vy = h * HORIZON, vx = w * VANISH;
    for (var i = 0; i < dots.length; i++) {
      var d = dots[i];
      d.t += d.speed * dt;
      if (d.t > 1) { d.t -= 1; }

      var k = Math.pow(d.t, 2.4);
      var y = vy + (h - vy) * k;
      var bx = w * (d.lane - 0.5) * 2.6 + w * 0.5;
      var x = vx + (bx - vx) * (y - vy) / (h - vy + 1e-6);
      var r = 0.9 + 2.2 * k;

      ctx.fillStyle = "rgba(110,211,211," + (0.2 + 0.55 * k) + ")";
      ctx.beginPath();
      ctx.arc(x, y, r, 0, Math.PI * 2);
      ctx.fill();
    }
  }

  var last = 0;
  function frame(ts) {
    if (!last) last = ts;
    var dt = Math.min(50, ts - last);
    last = ts;
    ctx.clearRect(0, 0, w, h);
    drawGrid();
    drawDots(dt);
    if (!reduce) requestAnimationFrame(frame);
  }

  function still() {
    ctx.clearRect(0, 0, w, h);
    drawGrid();
    drawDots(0);
  }

  resize();
  initDots();
  if (reduce) {
    still();
  } else {
    requestAnimationFrame(frame);
  }

  window.addEventListener("resize", function () {
    resize();
    if (reduce) still();
  });
})();
