/* ============================================================
   nav.js — 顶部导航：毛玻璃吸顶、滚动进度、滚动高亮、移动端抽屉
   ============================================================ */

window.CDL = window.CDL || {};

(function (CDL) {
  'use strict';

  var U = CDL.utils;

  function initNav() {
    var nav = U.qs('#nav');
    var progress = U.qs('#scrollProgress');
    if (!nav) return;

    var toggle = U.qs('#navToggle');
    var links = U.qs('#navLinks');
    var isMobile = function () {
      return window.matchMedia('(max-width: 640px)').matches;
    };

    /* ---------- 1. 吸顶状态 + 进度条 ---------- */

    var onScroll = U.rafThrottle(function () {
      var y = window.pageYOffset || document.documentElement.scrollTop;
      nav.classList.toggle('is-stuck', y > 12);

      if (progress) {
        var doc = document.documentElement;
        var max = doc.scrollHeight - window.innerHeight;
        var ratio = max > 0 ? U.clamp(y / max, 0, 1) : 0;
        progress.style.width = (ratio * 100).toFixed(2) + '%';
      }
    });

    U.on(window, 'scroll', onScroll, { passive: true });
    U.on(window, 'resize', onScroll, { passive: true });
    onScroll();

    /* ---------- 2. 滚动高亮当前区块 ---------- */

    var anchors = U.qsa('.nav__links a[href^="#"]');

    if (U.supportsObserver() && anchors.length) {
      var map = {};
      anchors.forEach(function (a) {
        map[a.getAttribute('href').slice(1)] = a;
      });

      var targets = Object.keys(map)
        .map(function (id) { return document.getElementById(id); })
        .filter(Boolean);

      var spy = new IntersectionObserver(function (entries) {
        entries.forEach(function (entry) {
          if (!entry.isIntersecting) return;
          anchors.forEach(function (a) { a.classList.remove('is-active'); });
          var active = map[entry.target.id];
          if (active) active.classList.add('is-active');
        });
      }, {
        // 只在视口中部一条窄带里判定，切换更稳定
        rootMargin: '-45% 0px -50% 0px',
        threshold: 0
      });

      targets.forEach(function (t) { spy.observe(t); });
    }

    /* ---------- 3. 移动端抽屉 ---------- */

    function closeMenu() {
      if (!links) return;
      links.classList.remove('is-open');
      if (toggle) toggle.setAttribute('aria-expanded', 'false');
    }

    function openMenu() {
      if (!links) return;
      links.classList.add('is-open');
      if (toggle) toggle.setAttribute('aria-expanded', 'true');
    }

    if (toggle && links) {
      U.on(toggle, 'click', function (e) {
        e.stopPropagation();
        if (links.classList.contains('is-open')) closeMenu();
        else openMenu();
      });

      // 点击链接后收起
      U.qsa('a', links).forEach(function (a) {
        U.on(a, 'click', closeMenu);
      });

      // 点击空白处收起
      U.on(document, 'click', function (e) {
        if (!links.classList.contains('is-open')) return;
        if (links.contains(e.target) || toggle.contains(e.target)) return;
        closeMenu();
      });

      // Esc 收起
      U.on(document, 'keydown', function (e) {
        if (e.key === 'Escape') closeMenu();
      });

      // 视口变宽时复位
      U.on(window, 'resize', function () {
        if (!isMobile()) closeMenu();
      });
    }
  }

  CDL.initNav = initNav;
})(window.CDL);
