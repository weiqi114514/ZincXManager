/* 首页大标题 Zinc[X]Manager 的词条滑动:X 每 5 秒换一次(下移出/上移入),Manager 跟着词宽移动 */
(function () {
  var WORDS = ["X", "Admin", "User", "Player", "System"];
  var PERIOD = 5000;

  function measure(slot, word) {
    var probe = document.createElement("span");
    probe.className = "bm-cur bm-probe";
    probe.textContent = word;
    slot.appendChild(probe);
    var w = probe.getBoundingClientRect().width;
    slot.removeChild(probe);
    return w;
  }

  function init(hero) {
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
      }, 440);
    }, PERIOD);
  }

  function boot() {
    Array.prototype.forEach.call(document.querySelectorAll(".zxm-hero"), init);
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", boot);
  } else {
    boot();
  }
})();
