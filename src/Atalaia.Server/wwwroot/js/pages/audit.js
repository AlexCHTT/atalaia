import { html, render, api, qs, $, debounce, fmt, EVENT_LABEL } from "../core.js";
import { icon } from "../icons.js";
import { emptyState } from "../ui.js";
import { can } from "../store.js";
import { eventRow } from "../events.js";

const PAGE = 20;
const f = { agentId: "", user: "", type: "", severity: "", from: "", to: "", q: "" };
let rows = [], done = false, built = false;

const params = () => {
  const p = { agentId: f.agentId, user: f.user, type: f.type, severity: f.severity, q: f.q.trim() };
  // As datas são do calendário local; o servidor guarda em UTC. "Até" inclui o dia inteiro.
  if (f.from) p.from = new Date(f.from + "T00:00:00").toISOString();
  if (f.to) { const d = new Date(f.to + "T00:00:00"); d.setDate(d.getDate() + 1); p.to = d.toISOString(); }
  return p;
};

function paint(root) {
  $("#au-body", root).innerHTML = String(rows.length ? html`<div class="tbl-wrap"><table class="tbl"><thead><tr><th>Quando</th><th>Dispositivo</th><th>Usuário</th><th>Evento</th><th>Detalhe</th></tr></thead>
      <tbody>${rows.map(eventRow)}</tbody></table></div>` : emptyState("list", "Nenhum evento", "Nada corresponde aos filtros atuais."));
  $("#au-count", root).textContent = rows.length ? `Mostrando ${fmt.plural(rows.length, "evento", "eventos")}${done ? " (fim da lista)" : ""}` : "";
  $("#au-more", root).hidden = done || !rows.length;
  const exp = $("#au-export", root); if (exp) exp.href = "/api/events/export?" + qs(params());
}

async function load(root, reset) {
  if (reset) { rows = []; done = false; }
  const p = params(); p.limit = PAGE;
  if (!reset && rows.length) p.before = rows[rows.length - 1].id;
  const page = await api("/api/events?" + qs(p));
  rows = rows.concat(page); done = page.length < PAGE;
  paint(root);
}

// Atualização automática: só o que é mais novo que o topo, sem encolher o que a pessoa já expandiu
async function refreshTop(root) {
  if (!rows.length) return load(root, true);
  const p = params(); p.limit = 100; p.after = rows[0].id;
  const fresh = await api("/api/events?" + qs(p));
  if (fresh.length >= 100) return load(root, true);
  if (fresh.length) { rows = fresh.concat(rows); paint(root); }
}

async function loadFilters(root) {
  const o = await api("/api/events/filters");
  const fill = (id, opts, first) => {
    const el = $(id, root), cur = el.value;
    el.innerHTML = String(html`<option value="">${first}</option>${opts.map(([v, t]) => html`<option value="${v}">${t}</option>`)}`);
    el.value = opts.some(([v]) => v === cur) ? cur : (opts.some(([v]) => v === f[el.dataset.k]) ? f[el.dataset.k] : "");
  };
  fill("#au-agent", o.pcs.map(p => [p.agentId, p.hostname || p.agentId]), "Todos os dispositivos");
  fill("#au-user", o.users.map(u => [u, u]), "Todos os usuários");
}

export const audit = {
  title: "Auditoria",
  sub: "Registro imutável de tudo que muda nos dispositivos",

  async render(root, query, { fresh } = {}) {
    if (fresh) Object.assign(f, { agentId: "", user: "", type: "", severity: "", from: "", to: "", q: "" });
    // filtros vindos de links (sino de alertas, dashboard, perfis)
    for (const k of ["agentId", "user", "type", "severity"]) if (query.has(k)) f[k] = query.get(k);
    if (query.has("period")) { const d = new Date(Date.now() - (+query.get("period") - 1) * 864e5); f.from = d.toLocaleDateString("sv-SE"); f.to = ""; }

    if (!built || !$("#au-body", root)) {
      render(root, html`<div class="toolbar">
          <select class="field" id="au-agent" data-k="agentId" aria-label="Dispositivo"></select>
          <select class="field" id="au-user" data-k="user" aria-label="Usuário"></select>
          <select class="field" id="au-type" data-k="type" aria-label="Tipo de evento"><option value="">Todos os eventos</option>${Object.entries(EVENT_LABEL).map(([k, v]) => html`<option value="${k}">${v}</option>`)}</select>
          <select class="field" id="au-sev" data-k="severity" aria-label="Severidade"><option value="">Qualquer severidade</option><option value="warn">Somente alertas</option></select></div>
        <div class="toolbar">
          <label class="muted" style="display:flex;gap:8px;align-items:center">De <input class="field" type="date" id="au-from" data-k="from"></label>
          <label class="muted" style="display:flex;gap:8px;align-items:center">Até <input class="field" type="date" id="au-to" data-k="to"></label>
          <div class="field-icon grow">${icon("search", 17)}<input class="field" id="au-q" data-k="q" type="search" placeholder="Buscar dispositivo, usuário, campo ou valor…" aria-label="Buscar"></div>
          <button class="btn" id="au-clear" type="button">Limpar filtros</button>
          ${can("operator") && html`<a class="btn" id="au-export" href="/api/events/export" title="Baixa um CSV com os filtros atuais">${icon("download", 16)}Exportar CSV</a>`}</div>
        <section class="card"><div class="card-b flush" id="au-body"></div></section>
        <div class="muted" id="au-count" style="text-align:center;margin-top:14px"></div>
        <div style="text-align:center;margin-top:10px"><button class="btn" id="au-more" type="button" hidden>Carregar mais ${PAGE}</button></div>`);
      for (const el of root.querySelectorAll("[data-k]")) {
        const run = () => { f[el.dataset.k] = el.value; load(root, true); };
        el.addEventListener(el.id === "au-q" ? "input" : "change", el.id === "au-q" ? debounce(run, 300) : run);
      }
      $("#au-clear", root).onclick = () => { Object.assign(f, { agentId: "", user: "", type: "", severity: "", from: "", to: "", q: "" }); syncFields(root); load(root, true); };
      $("#au-more", root).onclick = () => load(root, false);
      built = true;
    }
    await loadFilters(root);
    syncFields(root);
    await load(root, true);
  },

  refresh(root) { return refreshTop(root); },
};

function syncFields(root) {
  for (const el of root.querySelectorAll("[data-k]")) el.value = f[el.dataset.k] ?? "";
}
