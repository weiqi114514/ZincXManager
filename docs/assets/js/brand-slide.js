/* 品牌名里的词条滑动:Zinc[X]Manager —— X 每 5 秒换一次(下移出、上移入),Manager 跟着词宽左右移动 */
(function () {
  var WORDS = ["X", "Admin", "User", "Player", "System"];
  var PERIOD = 5000;

  function makeSlot(word) {
    var slot = document.createElement("span");
    slot.className = "bm-slot";
    var cur = document.createElement("span");
    cur.className = "bm-cur";
    cur.textContent = word;
    var next = document.createElement("span");
    next.className = "bm-next";
    slot.appendChild(cur);
    slot.appendChild(next);
    return slot;
  }

  function measure(slot, word) {
    var probe = document.createElement("span");
    probe.className = "bm-cur bm-probe";
    probe.textContent = word;
    slot.appendChild(probe);
    var w = probe.getBoundingClientRect().width;
    slot.removeChild(probe);
    return w;
  }

  function init(box) {
    if (box.dataset.bmReady === "1") return;

    // 原文字形如 ZincXManager,末尾可能还有 <em>ZXM</em>
    var em = box.querySelector("em");
    var text = (box.textContent || "").trim();
    if (em) text = text.slice(0, text.lastIndexOf(em.textContent.trim())).trim();
    if (text.indexOf("Zinc") !== 0 || !text.endsWith("Manager")) return;

    var mid = text.slice(4, text.length - "Manager".length);
    if (mid !== "X") return;

    box.dataset.bmReady = "1";
    box.textContent = "";

    var pre = document.createElement("span");
    pre.textContent = "Zinc";
    var slot = makeSlot("X");
    var suf = document.createElement("span");
    suf.textContent = "Manager";

    box.appendChild(pre);
    box.appendChild(slot);
    box.appendChild(suf);
    if (em) box.appendChild(em);

    slot.style.width = measure(slot, "X") + "px";

    var i = 0;
    setInterval(function () {
      var cur = slot.querySelector(".bm-cur");
      var nxt = slot.querySelector(".bm-next");
      i = (i + 1) % WORDS.length;
      var word = WORDS[i];

      nxt.textContent = word;
      slot.classList.remove("bm-rolling");
      void slot.offsetWidth;          // 强制回流,让下一帧的过渡生效
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
    Array.prototype.forEach.call(document.querySelectorAll(".brand-text"), init);
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", boot);
  } else {
    boot();
  }
})();
