/* =========================================================
   Zync Master — launcher behaviour (framework-free)
   - Brand card scrolls back to top
   - Ambient constellation (canvas) behind the hero
   - Device-network overlay (SVG glyphs + travelling comets)
   - Scroll reveal via IntersectionObserver
   All decorative; honours prefers-reduced-motion.
   ========================================================= */
(function () {
  "use strict";

  var reduce = matchMedia("(prefers-reduced-motion: reduce)").matches;

  // ---- brand card scrolls back to the top ----
  var brand = document.getElementById("brandCard");
  if (brand) {
    brand.addEventListener("click", function (e) {
      e.preventDefault();
      window.scrollTo({ top: 0, behavior: reduce ? "auto" : "smooth" });
    });
  }

  // ---- ambient constellation (canvas) ----
  (function () {
    var c = document.getElementById("net");
    if (!c) return;
    var x = c.getContext("2d"), W, H, dpr = Math.min(devicePixelRatio || 1, 2);
    var pts = [], mouse = { x: -9999, y: -9999 };

    function build() {
      pts = [];
      var n = Math.round(innerWidth / 18);
      if (n > 80) n = 80;
      for (var i = 0; i < n; i++) {
        pts.push({
          x: (0.4 + Math.random() * 0.62) * W,
          y: Math.random() * H,
          vx: (Math.random() - .5) * .22 * dpr,
          vy: (Math.random() - .5) * .22 * dpr,
          r: (Math.random() * 1.5 + 0.7) * dpr
        });
      }
    }
    function rs() {
      W = c.width = innerWidth * dpr;
      H = c.height = innerHeight * dpr;
      c.style.width = innerWidth + "px";
      c.style.height = innerHeight + "px";
      build();
    }
    addEventListener("resize", rs, { passive: true });
    addEventListener("mousemove", function (e) { mouse.x = e.clientX * dpr; mouse.y = e.clientY * dpr; }, { passive: true });
    addEventListener("mouseleave", function () { mouse.x = mouse.y = -9999; });

    function frame() {
      x.clearRect(0, 0, W, H);
      for (var i = 0; i < pts.length; i++) {
        var p = pts[i];
        if (!reduce) {
          p.x += p.vx; p.y += p.vy;
          var dx = p.x - mouse.x, dy = p.y - mouse.y, dd = dx * dx + dy * dy, R = 130 * dpr;
          if (dd < R * R) {
            var f = (1 - Math.sqrt(dd) / R) * .6;
            p.x += dx / Math.sqrt(dd + 1) * f * 3;
            p.y += dy / Math.sqrt(dd + 1) * f * 3;
          }
        }
        if (p.x < W * 0.34) p.x = W;
        if (p.x > W) p.x = W * 0.34;
        if (p.y < 0) p.y = H;
        if (p.y > H) p.y = 0;
      }
      var TH = 120 * dpr;
      for (var i = 0; i < pts.length; i++) {
        for (var j = i + 1; j < pts.length; j++) {
          var a = pts[i], b = pts[j], dx = a.x - b.x, dy = a.y - b.y, d = dx * dx + dy * dy;
          if (d < TH * TH) {
            var al = (1 - Math.sqrt(d) / TH) * .2;
            x.strokeStyle = "rgba(140,170,255," + al + ")";
            x.lineWidth = dpr;
            x.beginPath(); x.moveTo(a.x, a.y); x.lineTo(b.x, b.y); x.stroke();
          }
        }
      }
      for (var i = 0; i < pts.length; i++) {
        var p = pts[i];
        x.fillStyle = "rgba(180,200,255,.45)";
        x.beginPath(); x.arc(p.x, p.y, p.r, 0, 6.28); x.fill();
      }
      if (!reduce) requestAnimationFrame(frame);
    }
    rs(); frame();
  })();

  // ---- device network overlay (SVG device glyphs + packets) ----
  (function () {
    var host = document.getElementById("devnet");
    if (!host) return;
    var glyph = {
      laptop: '<rect x="-15" y="-13" width="30" height="21" rx="2.5"/><path d="M-20 11h40l-3.5 4.5H-16.5z"/><path d="M-9 -7h12M-9 -2.5h8M-9 2h11"/>',
      browser: '<rect x="-15" y="-13" width="30" height="26" rx="3.5"/><path d="M-15 -6h30"/><path d="M-9 0.5h18M-9 6h11"/>',
      tablet: '<rect x="-11.5" y="-15" width="23" height="30" rx="3.5"/><path d="M-4 11.5h8"/><path d="M-6.5 -9h13M-6.5 -4h9"/>',
      phone: '<rect x="-8" y="-15" width="16" height="30" rx="4"/><path d="M-2.5 -12h5"/><path d="M-3.5 11.5h7"/>',
      server: '<rect x="-13" y="-15" width="26" height="30" rx="2.5"/><path d="M-13 0h26"/><path d="M-9 -9.5h11M-9 5.5h11"/>'
    };
    var nodes = [
      { x: 150, y: 175, t: "laptop", l: "Laptop", c: "#5b8cff" },
      { x: 405, y: 120, t: "browser", l: "Browser", c: "#e8916f" },
      { x: 300, y: 330, t: "tablet", l: "Tablet", c: "#5fd093" },
      { x: 475, y: 380, t: "phone", l: "Phone", c: "#9a86e0" },
      { x: 165, y: 445, t: "server", l: "Server", c: "#8fb0ff" }
    ];
    var edges = [[0, 1], [0, 2], [1, 2], [1, 3], [2, 3], [2, 4], [3, 4], [0, 4]];
    var edgeStr = "", pkStr = "";
    edges.forEach(function (e, ei) {
      var a = nodes[e[0]], b = nodes[e[1]];
      var mx = (a.x + b.x) / 2, my = (a.y + b.y) / 2, dx = b.x - a.x, dy = b.y - a.y, len = Math.hypot(dx, dy);
      var nx = -dy / len, ny = dx / len, bow = 20 * ((ei % 2) ? 1 : -1), cx = mx + nx * bow, cy = my + ny * bow;
      var fwd = "M" + a.x + " " + a.y + " Q" + cx.toFixed(1) + " " + cy.toFixed(1) + " " + b.x + " " + b.y;
      var rev = "M" + b.x + " " + b.y + " Q" + cx.toFixed(1) + " " + cy.toFixed(1) + " " + a.x + " " + a.y;
      edgeStr += '<path class="dn-edge" d="' + fwd + '"/>';
      if (reduce) return; // no travelling comets under reduced motion
      var dur = (2.4 + len / 150).toFixed(2);
      function comet(p, col, r, beg) {
        return '<circle r="' + r + '" fill="' + col + '" style="filter:drop-shadow(0 0 5px ' + col + ') drop-shadow(0 0 9px ' + col + ')">' +
          '<animateMotion dur="' + dur + 's" begin="' + beg + 's" repeatCount="indefinite" calcMode="linear" keyPoints="0;1" keyTimes="0;1" path="' + p + '"/>' +
          '<animate attributeName="opacity" dur="' + dur + 's" begin="' + beg + 's" repeatCount="indefinite" values="0;1;1;1;0" keyTimes="0;0.12;0.5;0.88;1"/></circle>';
      }
      pkStr += comet(fwd, a.c, 3.4, (-ei * 0.5).toFixed(2));
      pkStr += comet(fwd, a.c, 2.2, (-ei * 0.5 - dur / 2).toFixed(2));
      if (ei % 2 === 0) pkStr += comet(rev, b.c, 3, (-ei * 0.5 - 1.1).toFixed(2));
    });
    var nodeStr = "";
    nodes.forEach(function (n) {
      nodeStr += '<g transform="translate(' + n.x + ',' + n.y + ')"><g class="dn-node" style="animation-delay:' + (Math.random() * -3).toFixed(2) + 's">' +
        '<circle r="27" fill="rgba(13,19,33,.82)" stroke="' + n.c + '" stroke-width="1.5" style="filter:drop-shadow(0 0 11px ' + n.c + '73)"/>' +
        '<g stroke="' + n.c + '" fill="none" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">' + glyph[n.t] + '</g>' +
        '</g></g>';
    });
    host.innerHTML = '<svg viewBox="0 0 600 600" preserveAspectRatio="xMidYMid meet">' +
      '<g class="dn-edges">' + edgeStr + "</g>" + nodeStr + '<g class="dn-pks">' + pkStr + "</g></svg>";

    if (!reduce && matchMedia("(pointer:fine)").matches) {
      var svg = host.querySelector("svg");
      addEventListener("mousemove", function (e) {
        var rx = (e.clientX / innerWidth - 0.5), ry = (e.clientY / innerHeight - 0.5);
        svg.style.transform = "translate(" + (rx * 16).toFixed(1) + "px," + (ry * 14).toFixed(1) + "px)";
        svg.style.transition = "transform .3s ease-out";
      }, { passive: true });
    }
  })();

  // ---- scroll reveal ----
  (function () {
    var r = [].slice.call(document.querySelectorAll(".reveal"));
    if (reduce || !("IntersectionObserver" in window)) {
      r.forEach(function (e) { e.classList.add("in"); });
      return;
    }
    var io = new IntersectionObserver(function (es) {
      es.forEach(function (e) {
        if (e.isIntersecting) { e.target.classList.add("in"); io.unobserve(e.target); }
      });
    }, { threshold: .15, rootMargin: "0px 0px -8% 0px" });
    r.forEach(function (e) { io.observe(e); });
  })();
})();
