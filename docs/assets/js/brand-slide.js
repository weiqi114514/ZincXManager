/* 首页大标题 Zinc[X]Manager:旧词下滑出、新词上滑入;槽宽用 rAF 两段式过渡,Manager 平滑滑动 */
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
    var i = 0;
    var busy = false;

    slot.style.width = widthOf(slot, cur.textContent) + "px";
    move(old, -100, false);
    move(cur, 0, false);

    setInterval(function () {
      if (busy) return;
      busy = true;

      i = (i + 1) % WORDS.length;
      var word = WORDS[i];
      var from = cur.textContent;

      // 第一段(无过渡):旧词下移到起点、新词仍藏在上方
      old.textContent = from;
      cur.textContent = word;
      move(old, 0, false);
      move(cur, -100, false);

      // 第二段(下一帧,带过渡):宽度 + 位移一起动
      requestAnimationFrame(function () {
        slot.style.width = widthOf(slot, word) + "px";
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
