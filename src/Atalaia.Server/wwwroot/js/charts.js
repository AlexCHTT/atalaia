// Gráficos feitos à mão em HTML/SVG. Regras (guia de dataviz): barras finas com ponta arredondada e base reta, vão de 2px entre
// segmentos, linha de 2px com área a ~10%, ponto final com anel, grade fina e contínua, dica ao passar o mouse e tabela equivalente.
import { html, raw, esc, fmt } from "./core.js";
import { tip, tipAttr } from "./ui.js";
import { icon } from "./icons.js";

// ---------- Barras horizontais: comparar magnitude entre categorias ----------
// items: { label, value, tone?: "dim"|"crit"|"warn"|"info", href?, iconName?, sub?, tip? }
export function hbars(items, { total, unit = "" } = {}) {
  if (!items.length) return html`<div class="muted">Sem dados.</div>`;
  const max = Math.max(...items.map(i => i.value), 1);
  const sum = total ?? items.reduce((a, i) => a + i.value, 0);
  return html`<div class="hb">${items.map(i => {
    const w = Math.max(2, Math.round((i.value / max) * (items.some(x => x.sub) ? 56 : 82)));   // sobra espaço para o valor (e o texto auxiliar) na ponta da barra
    const share = sum ? Math.round(100 * i.value / sum) : 0;
    const tag = i.href ? "a" : "div";
    const text = i.tip ?? `${i.label}: ${fmt.num(i.value)}${unit} (${share}%)`;
    return raw(`<${tag} class="hb-row" ${i.href ? `href="${esc(i.href)}"` : 'tabindex="0"'} data-tip="${esc(text)}">
      <span class="hb-label">${i.iconName ? icon(i.iconName, 16).s : ""}<span>${esc(i.label)}</span></span>
      <span class="hb-bar"><i class="${i.tone ?? ""}" style="width:${w}%"></i><b>${fmt.num(i.value)}${esc(unit)}</b>${i.sub ? `<small>${esc(i.sub)}</small>` : ""}</span></${tag}>`);
  })}</div>`;
}
export const hbarsTable = (items, head = ["Categoria", "Quantidade"]) =>
  html`<div class="tbl-wrap"><table class="tbl compact"><thead><tr><th>${head[0]}</th><th>${head[1]}</th></tr></thead><tbody>${items.map(i => html`<tr><td>${i.label}</td><td class="num">${fmt.num(i.value)}</td></tr>`)}</tbody></table></div>`;

// ---------- Barra empilhada: parte do todo (poucos segmentos, com estado = cor de status + rótulo) ----------
// segs: { label, value, tone: "good"|"warning"|"critical"|"off"|"info", iconName? }
export function stackedBar(segs) {
  const total = segs.reduce((a, s) => a + s.value, 0);
  const visible = segs.filter(s => s.value > 0);
  return html`<div role="img" aria-label="${segs.map(s => `${s.label}: ${s.value}`).join(", ")}">
    <div class="sbar">${visible.map(s => html`<i class="tone-${s.tone}" style="--v:${s.value}" data-tip="${s.label}: ${s.value} (${total ? Math.round(100 * s.value / total) : 0}%)"></i>`)}</div>
    <div class="legend">${segs.map(s => html`<span class="li tone-${s.tone}"><span class="sw"></span>${s.iconName && icon(s.iconName, 14)}${s.label} <b>${fmt.num(s.value)}</b></span>`)}</div></div>`;
}

export const meter = (pct, tone = "info", label) =>
  html`<div class="meter tone-${tone}" role="meter" aria-valuemin="0" aria-valuemax="100" aria-valuenow="${Math.round(pct)}"${label && tipAttr(label)}><i style="width:${Math.min(100, Math.max(0, pct))}%"></i></div>`;

// ---------- Gráfico de linha (uma série, eixo 0-100%) ----------
// Devolve um elemento DOM já com hover (linha de referência + dica) e botão de tabela.
// points: [{ at: ISO, v: number|null }]
export function lineChart({ title, points, unit = "%", domain = [0, 100], rangeHours = 24, aria }) {
  const pts = points.filter(p => p.v != null).map(p => ({ t: +new Date(p.at), v: p.v, at: p.at }));
  const root = document.createElement("div");
  root.className = "card";
  const last = pts[pts.length - 1];
  const avg = pts.length ? pts.reduce((a, p) => a + p.v, 0) / pts.length : null;
  const max = pts.length ? Math.max(...pts.map(p => p.v)) : null;

  root.innerHTML = html`<div class="card-h"><div><div class="card-title">${title}</div><div class="card-sub">${pts.length ? `média ${fmt.pct(avg)} · máx ${fmt.pct(max)}` : "sem amostras no período"}</div></div>
    <div class="card-act"><button class="btn ghost sm" data-toggle-table type="button" aria-pressed="false">${icon("list", 15)}Tabela</button></div></div>
    <div class="card-b"><div class="view-chart"><div class="lc-head"><b class="num">${last ? fmt.pct(last.v) : "—"}</b><span class="muted">agora</span></div><div class="lc"></div></div>
    <div class="view-table"></div></div>`.s;

  const host = root.querySelector(".lc");
  const tableHost = root.querySelector(".view-table");

  // tabela equivalente (no máx. 48 linhas, amostradas)
  const step = Math.max(1, Math.ceil(pts.length / 48));
  tableHost.innerHTML = html`<div class="tbl-wrap" style="max-height:260px;overflow:auto"><table class="tbl compact"><thead><tr><th>Quando</th><th>${title}</th></tr></thead>
    <tbody>${pts.filter((_, i) => i % step === 0 || i === pts.length - 1).reverse().map(p => html`<tr><td>${fmt.dt(p.at)}</td><td class="num">${fmt.pct(p.v, 1)}</td></tr>`)}</tbody></table></div>`.s;

  if (pts.length < 2) {
    host.innerHTML = `<div class="muted" style="padding:28px 0">Dados insuficientes: o gráfico aparece com ao menos 2 check-ins no período.</div>`;
    return root;
  }

  const [lo, hi] = domain, H = 168, M = { l: 34, r: 12, t: 10, b: 24 };
  const draw = () => {
    const W = Math.max(260, host.clientWidth), pw = W - M.l - M.r, ph = H - M.t - M.b;
    const t0 = pts[0].t, t1 = pts[pts.length - 1].t, span = Math.max(1, t1 - t0);
    const x = t => M.l + (t - t0) / span * pw, y = v => M.t + ph - (Math.min(hi, Math.max(lo, v)) - lo) / (hi - lo) * ph;
    const line = pts.map((p, i) => `${i ? "L" : "M"}${x(p.t).toFixed(1)},${y(p.v).toFixed(1)}`).join("");
    const area = `${line}L${x(t1).toFixed(1)},${y(lo)}L${x(t0).toFixed(1)},${y(lo)}Z`;
    const ticks = [lo, (lo + hi) / 2, hi];
    const fmtT = t => rangeHours <= 36 ? fmt.time(new Date(t)) : new Date(t).toLocaleDateString("pt-BR", { day: "2-digit", month: "2-digit" });
    const xl = [t0, t0 + span / 2, t1];
    host.innerHTML = `<svg viewBox="0 0 ${W} ${H}" role="img" aria-label="${esc(aria ?? title)}">
      ${ticks.map(v => `<line class="grid-l" x1="${M.l}" x2="${W - M.r}" y1="${y(v)}" y2="${y(v)}"/><text class="axis-t" x="${M.l - 8}" y="${y(v) + 4}" text-anchor="end">${v}${unit}</text>`).join("")}
      ${xl.map((t, i) => `<text class="axis-t" x="${x(t)}" y="${H - 6}" text-anchor="${i === 0 ? "start" : i === 2 ? "end" : "middle"}">${fmtT(t)}</text>`).join("")}
      <path class="ar" d="${area}"/><path class="ln" d="${line}"/>
      <line class="cross" y1="${M.t}" y2="${M.t + ph}"/>
      <circle class="dotr hover" r="4.5" cy="0" cx="0"/>
      <circle class="dotr" r="4.5" cx="${x(t1)}" cy="${y(pts[pts.length - 1].v)}"/>
      <rect x="${M.l}" y="${M.t}" width="${pw}" height="${ph}" fill="transparent" class="hit"/></svg>`;

    const svg = host.querySelector("svg"), cross = svg.querySelector(".cross"), dot = svg.querySelector(".dotr.hover");
    const near = clientX => {
      const r = svg.getBoundingClientRect(), t = t0 + ((clientX - r.left) * (W / r.width) - M.l) / pw * span;
      let lo_ = 0, hi_ = pts.length - 1;
      while (hi_ - lo_ > 1) { const m = (lo_ + hi_) >> 1; (pts[m].t < t ? (lo_ = m) : (hi_ = m)); }
      return Math.abs(pts[lo_].t - t) < Math.abs(pts[hi_].t - t) ? pts[lo_] : pts[hi_];
    };
    const move = e => {
      const p = near(e.clientX), cx = x(p.t);
      cross.setAttribute("x1", cx); cross.setAttribute("x2", cx); cross.style.opacity = 1;
      dot.setAttribute("cx", cx); dot.setAttribute("cy", y(p.v)); dot.style.opacity = 1;
      const box = document.createElement("div");
      box.append(Object.assign(document.createElement("div"), { textContent: fmt.dt(p.at), style: "opacity:.75;font-size:11.5px" }));
      const row = document.createElement("div");
      row.innerHTML = `<span class="key"></span>`;
      row.append(Object.assign(document.createElement("b"), { textContent: fmt.pct(p.v, 1) }), document.createTextNode(" " + title));
      box.append(row);
      tip.show(box, e.clientX, e.clientY);
    };
    const leave = () => { cross.style.opacity = 0; dot.style.opacity = 0; tip.hide(); };
    svg.addEventListener("pointermove", move);
    svg.addEventListener("pointerleave", leave);
  };
  requestAnimationFrame(draw);
  new ResizeObserver(() => { if (host.isConnected) draw(); }).observe(host);
  return root;
}
