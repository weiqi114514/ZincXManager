/* 首页大标题 Zinc[X]Manager:X 每 5 秒换一次(旧词下滑出 / 新词上滑入),Manager 随真实宽度移动 */
(function () {
  var WORDS = ["X", "Admin", "User", "Player", "System"];
  var PERIOD = 5000;

  function start(hero) {
    var slot = hero.querySelector(".bm-slot");
    if (!slot || slot.dataset.ready === "1") return;
    slot.dataset.ready = "1";

    var cur = slot.querySelector(".bm-cur");
    var old = slot.querySelector(".bm-next");
    var i = 0;

    setInterval(function () {
      i = (i + 1) % WORDS.length;
      var word = WORDS[i];

      // 旧词交给 old(它贴在下方),新词进 cur(在文档流里,宽度就是真实宽度)
      old.textContent = cur.textContent;
      old.classList.add("bm-out");
      cur.textContent = word;
      cur.classList.add("bm-in");
      slot.style.width = "auto";   // 让 Manager 立即跟到新宽度

      setTimeout(function () {
        old.classList.remove("bm-out");
        old.textContent = "";
        cur.classList.remove("bm-in");
      }, 460);
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
