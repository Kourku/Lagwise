// Lagwise site: theme switch, scroll reveal, hero tilt, counters and the "how it works" demo.
// No third-party code, no tracking. The only thing stored is the visitor's theme choice, in their own browser.
(function () {
  var root = document.documentElement;
  root.classList.add('js');
  var reduceMotion = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  // ---- theme switch ---------------------------------------------------------------------------------------------
  function systemTheme() { return window.matchMedia && window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark'; }
  function currentTheme() { return root.getAttribute('data-theme') || systemTheme(); }
  function labelToggles() {
    var next = currentTheme() === 'dark' ? 'light' : 'dark';
    document.querySelectorAll('.theme-toggle').forEach(function (b) {
      b.setAttribute('aria-label', 'Switch to ' + next + ' theme');
      b.setAttribute('title', 'Switch to ' + next + ' theme');
    });
  }
  document.querySelectorAll('.theme-toggle').forEach(function (b) {
    b.addEventListener('click', function () {
      var next = currentTheme() === 'dark' ? 'light' : 'dark';
      root.setAttribute('data-theme', next);
      try { localStorage.setItem('lagwise-theme', next); } catch (e) { /* private window: the switch still works for this visit */ }
      labelToggles();
    });
  });
  labelToggles();

  // ---- reveal sections as they scroll into view -----------------------------------------------------------------
  var reveal = document.querySelectorAll('[data-reveal]');
  if (reduceMotion || !('IntersectionObserver' in window)) {
    reveal.forEach(function (el) { el.classList.add('in'); });
  } else {
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (e) { if (e.isIntersecting) { e.target.classList.add('in'); io.unobserve(e.target); } });
    }, { rootMargin: '0px 0px -8% 0px', threshold: 0.08 });
    reveal.forEach(function (el) { io.observe(el); });
  }

  // ---- hero screenshot follows the mouse a little ---------------------------------------------------------------
  var tilt = document.querySelector('[data-tilt]');
  if (tilt && !reduceMotion && window.matchMedia('(pointer: fine)').matches) {
    var box = tilt.parentElement;
    box.addEventListener('mousemove', function (ev) {
      var r = box.getBoundingClientRect();
      var x = (ev.clientX - r.left) / r.width - 0.5, y = (ev.clientY - r.top) / r.height - 0.5;
      tilt.style.transform = 'perspective(1600px) rotateY(' + (-4 + x * 6).toFixed(2) + 'deg) rotateX(' + (2 - y * 5).toFixed(2) + 'deg)';
    });
    box.addEventListener('mouseleave', function () { tilt.style.transform = ''; });
  }

  // ---- counters -------------------------------------------------------------------------------------------------
  var counters = document.querySelectorAll('[data-count-to]');
  function runCounter(el) {
    var from = +el.getAttribute('data-count-from'), to = +el.getAttribute('data-count-to'), suffix = el.getAttribute('data-suffix') || '';
    if (reduceMotion) { el.textContent = to + suffix; return; }
    var start = null, dur = 1400;
    function step(t) {
      if (start === null) start = t;
      var p = Math.min(1, (t - start) / dur), eased = 1 - Math.pow(1 - p, 3);
      el.textContent = Math.round(from + (to - from) * eased) + suffix;
      if (p < 1) requestAnimationFrame(step);
    }
    requestAnimationFrame(step);
  }
  if ('IntersectionObserver' in window) {
    var co = new IntersectionObserver(function (entries) {
      entries.forEach(function (e) { if (e.isIntersecting) { runCounter(e.target); co.unobserve(e.target); } });
    }, { threshold: 0.6 });
    counters.forEach(function (c) { co.observe(c); });
  } else counters.forEach(runCounter);

  // ---- "how it works" demo: simulated readings, cycling through the verdicts ----------------------------------------
  var demo = document.getElementById('demo');
  if (!demo) return;
  var svg = demo.querySelector('svg.chart');
  var routerLine = svg.querySelector('.l-router'), netLine = svg.querySelector('.l-net');
  var verdictEl = demo.querySelector('.demo-verdict'), cpuEl = demo.querySelector('.demo-cpu b');
  var nodes = document.querySelectorAll('.chain [data-step]'), pills = document.querySelectorAll('.verdicts [data-scn]');
  var W = 600, H = 160, N = 48, CYCLE = 60, BURST_FROM = 22, BURST_TO = 34, router = [], net = [];

  var scenarios = [
    { id: 'steady',  text: 'Your connection was steady',            cls: 'g', hot: null,       cpu: 18 },
    { id: 'wifi',    text: 'Lag came from your Wi-Fi or router',    cls: 'a', hot: 'router',   cpu: 21 },
    { id: 'beyond',  text: 'Lag came from beyond your router',      cls: 'a', hot: 'provider', cpu: 19 },
    { id: 'pc',      text: 'This PC was busy: processor at 97%',    cls: 'b', hot: 'pc',       cpu: 97 },
    { id: 'drop',    text: 'The connection dropped beyond your router', cls: 'r', hot: 'internet', cpu: 17 }
  ];
  var current = 0, tick = 0, shownFor = 0;

  function sample(s, i) {
    var jitter = function (a) { return (Math.random() - 0.5) * a; };
    var r = 3 + jitter(1.2), n = 26 + jitter(4);
    var burst = tick % CYCLE >= BURST_FROM && tick % CYCLE < BURST_TO;   // the trouble comes and goes inside each scenario
    if (s.id === 'wifi' && burst) { r = 90 + jitter(60); n = 115 + jitter(60); }
    if (s.id === 'beyond' && burst) { n = 140 + jitter(50); }
    if (s.id === 'drop' && burst) { n = null; }
    return [r, n];
  }
  function toPoints(arr) {
    var out = [], seg = [];
    arr.forEach(function (v, i) {
      if (v === null) { if (seg.length) out.push(seg.join(' ')); seg = []; return; }
      var x = (i / (N - 1)) * W, y = H - 8 - Math.min(v, 200) / 200 * (H - 20);
      seg.push(x.toFixed(1) + ',' + y.toFixed(1));
    });
    if (seg.length) out.push(seg.join(' '));
    return out;
  }
  function draw() {
    // a gap (a dropped connection) splits the line, so each line is drawn as one path of several pieces
    routerLine.setAttribute('d', toPoints(router).map(function (p) { return 'M' + p.replace(/ /g, ' L'); }).join(' '));
    netLine.setAttribute('d', toPoints(net).map(function (p) { return 'M' + p.replace(/ /g, ' L'); }).join(' '));
  }
  function show(i) {
    var s = scenarios[i];
    verdictEl.textContent = '';
    verdictEl.appendChild(document.createElement('i'));
    verdictEl.appendChild(document.createTextNode(s.text));
    verdictEl.className = 'demo-verdict pill ' + s.cls;
    nodes.forEach(function (n) {
      var step = n.getAttribute('data-step');
      n.classList.toggle('hot', step === s.hot || (s.id === 'beyond' && step === 'internet'));
      n.classList.toggle('ok', s.hot === null);
    });
    pills.forEach(function (p) { p.classList.toggle('on', p.getAttribute('data-scn') === s.id); });
    cpuEl.textContent = s.cpu + '%';
    cpuEl.parentElement.classList.toggle('busy', s.cpu > 80);
  }
  for (var i = 0; i < N; i++) { var v = sample(scenarios[0], i); router.push(v[0]); net.push(v[1]); }
  draw(); show(0);

  // a click on a verdict pill jumps to that scenario
  pills.forEach(function (p) {
    p.addEventListener('click', function () {
      current = scenarios.findIndex(function (s) { return s.id === p.getAttribute('data-scn'); });
      // start the chosen scenario from a calm minute, so no spike of the previous one is left on screen
      router = []; net = [];
      for (var j = 0; j < N; j++) { var c = sample(scenarios[0], 0); router.push(c[0]); net.push(c[1]); }
      tick = BURST_FROM - 4; shownFor = current; show(current); draw();
    });
  });

  if (reduceMotion) return;   // a still chart and the first verdict: nothing moves
  var visible = true;
  if ('IntersectionObserver' in window) new IntersectionObserver(function (e) { visible = e[0].isIntersecting; }).observe(demo);
  setInterval(function () {
    if (!visible || document.hidden) return;
    tick++;
    var v = sample(scenarios[current], tick);
    router.push(v[0]); router.shift(); net.push(v[1]); net.shift();
    draw();
    // the next scenario starts with calm readings; its verdict appears when its own trouble does, by which time the
    // previous scenario's spike has scrolled off the chart (N samples < CYCLE - BURST_TO + BURST_FROM)
    if (tick % CYCLE === 0) current = (current + 1) % scenarios.length;
    if (tick % CYCLE === BURST_FROM && shownFor !== current) { shownFor = current; show(current); }
  }, 110);
})();
