/* 首页大标题 Zinc[X]Manager:旧词向下滑出、新词从上方滑入,Manager 随宽度移动 */
(function () {
  var WORDS = ["X", "Admin", "User", "Player", "System"];
  var PERIOD = 5000;
  var DUR = 440;

  function setMove(el, y, animated) {
    el.style.transition = animated ? "transform " + DUR + "ms cubic-bezier(.22,.61,.36,1)" : "none";
    el.style.transform = "translateY(" + y + "%)";
  }

  function start(hero) {
    var slot = hero.querySelector(".bm-slot");
    if (!slot || slot.dataset.ready === "1") return;
    slot.dataset.ready = "1";

    var cur = slot.querySelector(".bm-cur");   // 在文档流里:宽度由它决定
    var old = slot.querySelector(".bm-next");  // 绝对定位:只用来播放旧词
    var i = 0;
    var busy = false;

    setMove(old, -100, false);
    setMove(cur, 0, false);

    setInterval(function () {
      if (busy) return;
      busy = true;

      i = (i + 1) % WORDS.length;
      var word = WORDS[i];

      // 旧词交给绝对层,先摆回原位;新词先藏到上方
      old.textContent = cur.textContent;
      cur.textContent = word;
      setMove(old, 0, false);
      setMove(cur, -100, false);
      void slot.offsetWidth;   // 强制回流,让下面的过渡有起点

      // 旧词向下滑出,新词从上方滑入
      setMove(old, 100, true);
      setMove(cur, 0, true);

      setTimeout(function () {
        setMove(old, -100, false);
        old.textContent = "";
        busy = false;
      }, DUR + 20);
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
