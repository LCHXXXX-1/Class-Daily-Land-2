/* ============================================================
   island.js — 灵动岛演示
   1) Hero 区自动循环播放四种状态（上课前 → 倒计时 → 上课中 → 下课提醒）
   2) 灵动岛区块的标签页手动切换状态
   全部为原生 DOM 操作，无第三方动画库。
   ============================================================ */

window.CDL = window.CDL || {};

(function (CDL) {
  'use strict';

  var U = CDL.utils;

  /* 环形进度周长：2 * π * r，r = 15.5 → ≈ 97.39 */
  var RING_LEN = 97.4;

  function setRing(circle, ratio) {
    if (!circle) return;
    var r = U.clamp(ratio, 0, 1);
    circle.style.strokeDashoffset = String(RING_LEN * (1 - r));
  }

  /**
   * 不停顿地把环归位。
   * 直接改 dashoffset 会触发 CSS 补间，出现"倒着涨回去"的怪现象，
   * 所以先关掉过渡、强制回流让新值立即生效，再恢复过渡，
   * 这样后续的倒计时仍然是一秒一格地平滑收缩。
   */
  function snapRing(circle, ratio) {
    if (!circle) return;
    var host = circle.closest ? (circle.closest('.island__ring') || circle) : circle;
    host.classList.add('no-transition');
    setRing(circle, ratio);
    void host.offsetWidth;            // 强制回流
    window.setTimeout(function () {
      host.classList.remove('no-transition');
    }, 50);
  }

  function fmtTime(totalSec) {
    var m = Math.floor(totalSec / 60);
    var s = totalSec % 60;
    return m + ':' + (s < 10 ? '0' : '') + s;
  }

  /* ============================================================
     1) Hero 自动演示
     ============================================================ */

  function initHeroDemo() {
    var island = U.qs('#demoIsland');
    if (!island) return;

    var elLabel = U.qs('#islandLabel');
    var elCourse = U.qs('#islandCourse');
    var elTime = U.qs('#islandTime');
    var elHint = U.qs('#stageHint');
    var ring = U.qs('#ringFill');

    // 每一步：状态、停留时长(ms)、文案
    var STEPS = [
      {
        state: 'compact',
        hold: 4200,
        label: '下节 · 第 3 节',
        course: '数学',
        time: '10:00',
        hint: '简略状态：课时还远，只留必要信息'
      },
      {
        state: 'upcoming',
        hold: 5200,
        label: '即将上课',
        course: '数学',
        time: '2 分钟',
        hint: '上课前：按设定的提前量亮起并开始倒计时'
      },
      {
        state: 'countdown',
        hold: 6000,
        label: '上课中 · 剩余',
        course: '数学 · 第 3 节',
        time: '45:00',
        hint: '倒计时：环形进度随剩余时间收缩',
        tick: 45 * 60
      },
      {
        state: 'alert',
        hold: 4400,
        label: '下课提醒',
        course: '数学 · 第 3 节已下课',
        time: '10:45',
        hint: '下课提醒：弹出并保持一会儿后自动收回'
      }
    ];

    var idx = 0;
    var timer = null;
    var ticker = null;
    var started = false;

    function clearTimers() {
      if (timer) { window.clearTimeout(timer); timer = null; }
      if (ticker) { window.clearInterval(ticker); ticker = null; }
    }

    function apply(step) {
      clearTimers();
      island.setAttribute('data-state', step.state);

      if (elLabel) elLabel.textContent = step.label;
      if (elCourse) elCourse.textContent = step.course;
      if (elTime) elTime.textContent = step.time;
      if (elHint) elHint.textContent = step.hint;

      // 环形进度：倒计时从满格开始收缩，提醒出现时先补满再排空
      if (step.state === 'countdown') {
        snapRing(ring, 1);
        if (step.tick) {
          var remain = step.tick;
          ticker = window.setInterval(function () {
            remain -= 1;
            if (remain <= 0) {
              window.clearInterval(ticker);
              ticker = null;
              setRing(ring, 0);
              return;
            }
            setRing(ring, remain / step.tick);
            if (elTime) elTime.textContent = fmtTime(remain);
          }, 1000);
        }
      } else if (step.state === 'alert') {
        snapRing(ring, 1);
        window.requestAnimationFrame(function () {
          window.requestAnimationFrame(function () { setRing(ring, 0); });
        });
      } else {
        snapRing(ring, step.state === 'upcoming' ? 0.85 : 1);
      }

      timer = window.setTimeout(next, step.hold);
    }

    function next() {
      idx = (idx + 1) % STEPS.length;
      apply(STEPS[idx]);
    }

    function start() {
      if (started) return;
      started = true;
      apply(STEPS[0]);
    }

    function stop() {
      started = false;
      clearTimers();
    }

    // 进入视口才开始播，离开则暂停：省电，也避免后台跑动画
    if (U.supportsObserver() && !U.prefersReducedMotion()) {
      var io = new IntersectionObserver(function (entries) {
        entries.forEach(function (e) {
          if (e.isIntersecting) start();
          else stop();
        });
      }, { threshold: 0.25 });
      io.observe(island);
    } else {
      // 减少动效：只呈现一个静止的倒计时状态
      island.setAttribute('data-state', 'countdown');
      if (elLabel) elLabel.textContent = STEPS[2].label;
      if (elCourse) elCourse.textContent = STEPS[2].course;
      if (elTime) elTime.textContent = STEPS[2].time;
      if (elHint) elHint.textContent = STEPS[2].hint;
      setRing(ring, 0.62);
    }
  }

  /* ============================================================
     2) 标签页手动切换（灵动岛区块）
     ============================================================ */

  function initStateTabs() {
    var island = U.qs('#stateIsland');
    var tabs = U.qsa('.state-tab');
    if (!island || !tabs.length) return;

    var elLabel = U.qs('#stateLabel');
    var elCourse = U.qs('#stateCourse');
    var elTime = U.qs('#stateTime');
    var ring = island.querySelector('.ring__fill');

    // 与标签顺序一致
    var VIEWS = {
      compact: {
        label: '上课中 · 第 2 节',
        course: '英语',
        time: '08:55',
        ring: 1
      },
      upcoming: {
        label: '即将上课 · 第 3 节',
        course: '数学',
        time: '2 分钟',
        ring: 0.9
      },
      countdown: {
        label: '上课中 · 剩余',
        course: '数学',
        time: '45:00',
        ring: 0.68
      },
      alert: {
        label: '下课提醒',
        course: '第 2 节已下课',
        time: '08:45',
        ring: 0.25
      }
    };

    function select(key) {
      var view = VIEWS[key];
      if (!view) return;

      island.setAttribute('data-state', key);
      if (elLabel) elLabel.textContent = view.label;
      if (elCourse) elCourse.textContent = view.course;
      if (elTime) elTime.textContent = view.time;
      snapRing(ring, view.ring);

      tabs.forEach(function (t) {
        var on = t.getAttribute('data-target') === key;
        t.classList.toggle('is-active', on);
        t.setAttribute('aria-selected', on ? 'true' : 'false');
        // 只把当前项加入 Tab 顺序，其余留给方向键
        t.tabIndex = on ? 0 : -1;
      });
    }

    tabs.forEach(function (tab, i) {
      U.on(tab, 'click', function () {
        select(tab.getAttribute('data-target'));
      });

      // 左右方向键在标签间移动（无障碍）
      U.on(tab, 'keydown', function (e) {
        var delta = 0;
        if (e.key === 'ArrowDown' || e.key === 'ArrowRight') delta = 1;
        else if (e.key === 'ArrowUp' || e.key === 'ArrowLeft') delta = -1;
        if (!delta) return;

        e.preventDefault();
        var nextIdx = (i + delta + tabs.length) % tabs.length;
        tabs[nextIdx].focus();
        select(tabs[nextIdx].getAttribute('data-target'));
      });
    });

    // 初始化
    select('compact');
  }

  function init() {
    initHeroDemo();
    initStateTabs();
  }

  CDL.initIsland = init;
})(window.CDL);
