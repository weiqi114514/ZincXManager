/* 首页大标题 Zinc[X]Manager:X 每 5 秒换一次(下移出/上移入),Manager 跟着词宽移动 */
(function () {
  var WORDS = ["X", "Admin", "User", "Player", "System"];
  var PERIOD = 5000;

  // 用一个独立的隐藏测量盒(不放进 slot,避免被 overflow 裁剪影响)
  var box = document.createElement("span");
  box.style.cssText = "position:absolute;left:-9999px;top:0;visibility:hidden;white-space:nowrap;";

  function measure(slot, word) {
    var cur = slot.querySelector(".bm-cur");
    var cs = window.getComputedStyle(cur);
    box.style.fontFamily = cs.fontFamily;
    box.style.fontSize = cs.fontSize;
    box.style.fontWeight = cs.fontWeight;
    box.style.fontStyle = cs.fontStyle;
    box.style.letterSpacing = cs.letterSpacing;
    box.textContent = word;

    if (!box.isConnected) document.body.appendChild(box);
    var w = Math.ceil(box.getBoundingClientRect().width);
    slot.dataset.measure = w;   // 方便排查:量到的宽度
    return w;
  }

  function start(hero) {
    var slot = hero.querySelector(".bm-slot");
    if (!slot || slot.dataset.ready === "1") return;
    slot.dataset.ready = "1";

    slot.style.width = measure(slot, "X") + "px";
    var i = 0;

    setInterval(function () {
      var cur = slot.querySelector(".bm-cur");
      var nxt = slot.querySelector(".bm-next");
      i = (i + 1) % WORDS.length;
      var word = WORDS[i];

      nxt.textContent = word;
      slot.classList.remove("bm-rolling");
      void slot.offsetWidth;
      slot.style.width = measure(slot, word) + "px";
      slot.classList.add("bm-rolling");

      setTimeout(function () {
        cur.textContent = word;
        nxt.textContent = "";
        slot.classList.remove("bm-rolling");
        slot.style.width = measure(slot, word) + "px";
      }, 440);
    }, PERIOD);
  }

  function boot() {
    var heroes = document.querySelectorAll(".zxm-hero");
    for (var i = 0; i < heroes.length; i++) start(heroes[i]);
  }

  if (document.fonts && document.fonts.ready) {
    document.fonts.ready.then(boot);
  } else if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", boot);
  } else {
    boot();
  }
})();
