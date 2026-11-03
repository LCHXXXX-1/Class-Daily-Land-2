/* ============================================================
   reveal.js — 滚动进场：用 IntersectionObserver 触发错峰上浮淡入
   ============================================================ */

window.CDL = window.CDL || {};

(function (CDL) {
  'use strict';

  var U = CDL.utils;

  function initReveal() {
    var items = U.qsa('[data-reveal]');
    if (!items.length) return;

    // 把延迟写成 CSS 变量，交给 animations.css 的 transition-delay 使用
    items.forEach(function (el) {
      var d = el.getAttribute('data-reveal-delay');
      if (d) el.style.setProperty('--reveal-delay', parseInt(d, 10) + 'ms');
    });

    // 无 IntersectionObserver 或用户要求减少动效：直接全部显示
    if (!U.supportsObserver() || U.prefersReducedMotion()) {
      items.forEach(function (el) { el.classList.add('is-visible'); });
      startCounters(items);
      return;
    }

    var observer = new IntersectionObserver(function (entries, obs) {
      entries.forEach(function (entry) {
        if (!entry.isIntersecting) return;
        var el = entry.target;
        el.classList.add('is-visible');
        obs.unobserve(el);

        // 动画结束后释放合成层
        window.setTimeout(function () {
          el.classList.add('is-settled');
        }, 1400);
      });
    }, {
      rootMargin: '0px 0px -12% 0px',
      threshold: 0.12
    });

    items.forEach(function (el) { observer.observe(el); });

    startCounters(items);
  }

  /* ---------- 数字递增（进入视口时触发一次） ---------- */

  function startCounters(scope) {
    var nums = U.qsa('[data-count]', scope);
    if (!nums.length) return;

    function run(el) {
      if (el.dataset.counted === '1') return;
      el.dataset.counted = '1';
      var target = parseFloat(el.getAttribute('data-count')) || 0;
      if (U.prefersReducedMotion() || !U.supportsObserver()) {
        el.textContent = target;
        return;
      }
      U.countUp(el, target, 1100);
    }

    if (!U.supportsObserver()) {
      nums.forEach(run);
      return;
    }

    var obs = new IntersectionObserver(function (entries, o) {
      entries.forEach(function (entry) {
        if (!entry.isIntersecting) return;
        run(entry.target);
        o.unobserve(entry.target);
      });
    }, { threshold: 0.6 });

    nums.forEach(function (el) { obs.observe(el); });
  }

  CDL.initReveal = initReveal;
  CDL.startCounters = startCounters;
})(window.CDL);
