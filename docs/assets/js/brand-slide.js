/* 首页大标题 Zinc[X]Manager:X 每 5 秒换一次(下移出/上移入),Manager 跟着词宽移动 */
(function () {
  var WORDS = ["X", "Admin", "User", "Player", "System"];
  var PERIOD = 5000;
  var canvas = document.createElement("canvas");
  var ctx = canvas.getContext("2d");

  // 用 canvas 按真实字体量宽度(比插一个隐藏 span 稳)
  function measure(slot, word) {
    var cs = window.getComputedStyle(slot.closest(".zxm-hero") || slot);
    ctx.font = [cs.fontStyle, cs.fontWeight, cs.fontSize, cs.fontFamily].join(" ");
    return Math.ceil(ctx.measureText(word).width);
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
