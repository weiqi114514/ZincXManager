/* 首页大标题 Zinc[X]Manager:槽宽固定(最宽的词),Manager 在槽内用 translateX 平滑跟随 */
(function () {
  var WORDS = ["X", "Admin", "User", "Player", "System"];
  var PERIOD = 5000;
  var DUR = 460;

  var probe = document.createElement("span");
  probe.className = "bm-cur";
  probe.style.cssText = "position:absolute;left:-9999px;top:0;visibility:hidden;white-space:nowrap;";

  function widthOf(slot, word) {
    var cur = slot.querySelector(".bm-cur");
    var cs = window.getComputedStyle(cur);
    probe.style.fontFamily = cs.fontFamily;
    probe.style.fontSize = cs.fontSize;
    probe.style.fontWeight = cs.fontWeight;
    probe.style.fontStyle = cs.fontStyle;
    probe.textContent = word;
    if (!probe.isConnected) document.body.appendChild(probe);
    return Math.ceil(probe.getBoundingClientRect().width);
  }

  function move(el, y, animated) {
    el.style.transition = animated ? "transform " + DUR + "ms cubic-bezier(.22,.61,.36,1)" : "none";
    el.style.transform = "translateY(" + y + "%)";
  }

  function start(hero) {
    var slot = hero.querySelector(".bm-slot");
    if (!slot || slot.dataset.ready === "1") return;
    slot.dataset.ready = "1";

    var cur = slot.querySelector(".bm-cur");
    var old = slot.querySelector(".bm-next");
    var mgr = slot.querySelector(".bm-manager");
    var i = 0;
    var busy = false;

    var maxW = 0;
    for (var k = 0; k < WORDS.length; k++) {
      maxW = Math.max(maxW, widthOf(slot, WORDS[k]));
    }

    // 槽要同时装下"最宽的词 + Manager",否则 Manager 会被裁掉
    var mgrW = 0;
    if (mgr) {
      mgr.style.position = "absolute";
      mgr.style.left = "0";
      mgr.style.top = "0";
      mgr.style.whiteSpace = "nowrap";
      mgr.style.transition = "transform " + DUR + "ms cubic-bezier(.22,.61,.36,1)";
      mgr.style.transform = "translateX(0)";
      mgrW = Math.ceil(mgr.getBoundingClientRect().width);
    }

    slot.style.width = (maxW + mgrW + 2) + "px";

    if (mgr) {
      mgr.style.transform = "translateX(" + widthOf(slot, cur.textContent) + "px)";
    }

    move(old, -100, false);
    move(cur, 0, false);

    setInterval(function () {
      if (busy) return;
      busy = true;

      i = (i + 1) % WORDS.length;
      var word = WORDS[i];

      old.textContent = cur.textContent;
      cur.textContent = word;
      move(old, 0, false);
      move(cur, -100, false);

      requestAnimationFrame(function () {
        if (mgr) {
          mgr.style.transform = "translateX(" + widthOf(slot, word) + "px)";
        }
        move(old, 100, true);
        move(cur, 0, true);
      });

      setTimeout(function () {
        move(old, -100, false);
        old.textContent = "";
        busy = false;
      }, DUR + 40);
    }, PERIOD);
  }

  function boot() {
    var heroes = document.querySelectorAll(".zxm-hero");
    for (var i = 0; i < heroes.length; i++) start(heroes[i]);
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", boot);
  } else {
    boot();
  }
})();
