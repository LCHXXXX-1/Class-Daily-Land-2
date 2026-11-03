/* ============================================================
   utils.js — 通用小工具（不依赖任何库）
   ============================================================ */

window.CDL = window.CDL || {};

(function (CDL) {
  'use strict';

  var U = {};

  /* ---------- 选择器 ---------- */

  U.qs = function (sel, ctx) {
    return (ctx || document).querySelector(sel);
  };

  U.qsa = function (sel, ctx) {
    return Array.prototype.slice.call((ctx || document).querySelectorAll(sel));
  };

  U.clamp = function (v, min, max) {
    return v < min ? min : (v > max ? max : v);
  };

  /* ---------- 数字动画 ---------- */

  /** 把元素内的数字从 0 递增到目标值 */
  U.countUp = function (el, target, duration) {
    if (!el) return;
    var dur = duration || 1100;
    var start = null;

    function step(ts) {
      if (start === null) start = ts;
      var p = U.clamp((ts - start) / dur, 0, 1);
      // easeOutCubic
      var eased = 1 - Math.pow(1 - p, 3);
      el.textContent = Math.round(target * eased);
      if (p < 1) requestAnimationFrame(step);
      else el.textContent = target;
    }

    requestAnimationFrame(step);
  };

  /* ---------- 事件 ---------- */

  U.on = function (el, type, fn, opts) {
    if (!el) return;
    el.addEventListener(type, fn, opts || false);
  };

  /**
   * 合并多次快速触发为一次（用于 scroll / resize）
   * 不传 immediate 时在空闲帧执行。
   */
  U.rafThrottle = function (fn) {
    var queued = false;
    var lastArgs = null;

    return function () {
      lastArgs = arguments;
      if (queued) return;
      queued = true;
      requestAnimationFrame(function () {
        queued = false;
        fn.apply(null, lastArgs);
      });
    };
  };

  /* ---------- 能力检测 ---------- */

  U.prefersReducedMotion = function () {
    return window.matchMedia &&
      window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  };

  U.supportsObserver = function () {
    return typeof window.IntersectionObserver === 'function';
  };

  /* ---------- 图片兜底 ---------- */
  /**
   * 图标加载失败时的兜底：只负责给 <img> 打上 .is-broken，
   * 具体外观由 CSS 决定（见 components.css 的 img.is-broken）。
   * 不给 <img> 用 ::after —— 替换元素不渲染伪元素。
   */
  U.imageFallback = function (img) {
    if (!img) return;

    function fail() {
      img.classList.add('is-broken');
    }

    if (img.complete && img.naturalWidth === 0) {
      fail();
      return;
    }
    U.on(img, 'error', fail);
  };

  CDL.utils = U;
})(window.CDL);
