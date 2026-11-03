/* ============================================================
   main.js — 入口：DOM 就绪后依次初始化各模块
   ============================================================ */

(function (CDL) {
  'use strict';

  function boot() {
    // 各模块互相独立，任一失败不影响其余部分
    var tasks = [
      ['nav', CDL.initNav],
      ['reveal', CDL.initReveal],
      ['island', CDL.initIsland],
      ['downloads', CDL.initDownloads]
    ];

    tasks.forEach(function (pair) {
      var name = pair[0];
      var fn = pair[1];
      if (typeof fn !== 'function') return;
      try {
        fn();
      } catch (err) {
        // 单个模块出错不应让整页脚本停摆
        if (window.console && console.error) {
          console.error('[CDL] 模块初始化失败：' + name, err);
        }
      }
    });

    document.documentElement.classList.add('is-ready');
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', boot);
  } else {
    boot();
  }
})(window.CDL);
