/* ============================================================
   downloads.js — 下载按钮
   ------------------------------------------------------------
   - 依据 config.js 同步所有 [data-download] 按钮的链接
   - 点击时给出即时反馈（已开始下载 / 新窗口打开）
   HTML 中已写好同样的地址作为兜底，本模块只做增强。
   ============================================================ */

window.CDL = window.CDL || {};

(function (CDL) {
  'use strict';

  var U = CDL.utils;

  function init() {
    var config = window.CDL_CONFIG || {};

    /* ---------- 1. 写入下载地址 ---------- */

    U.qsa('[data-download]').forEach(function (el) {
      var key = el.getAttribute('data-download');
      var item = config[key];
      if (!item || !item.url) return;

      el.setAttribute('href', item.url);
      el.setAttribute('rel', 'noopener');
      // 说明：Gitee Releases 属跨域附件，download 属性会被浏览器忽略，
      // 这里只保留 title 提示，避免个别浏览器把附件当页面打开。
      if (item.file) el.setAttribute('title', '下载 ' + item.file);
    });

    /* ---------- 2. 点击反馈 ---------- */

    U.qsa('[data-download]').forEach(function (el) {
      var label = el.querySelector('span');
      if (!label) return;

      var original = label.textContent;
      var resetTimer = null;

      U.on(el, 'click', function () {
        if (resetTimer) window.clearTimeout(resetTimer);
        label.textContent = '已开始下载…';
        el.classList.add('is-downloading');

        resetTimer = window.setTimeout(function () {
          label.textContent = original;
          el.classList.remove('is-downloading');
        }, 2600);
      });
    });

    /* ---------- 3. 图标兜底 ---------- */

    U.qsa('.brand__logo, .footer__brand-logo').forEach(function (img) {
      U.imageFallback(img);
    });
  }

  CDL.initDownloads = init;
})(window.CDL);