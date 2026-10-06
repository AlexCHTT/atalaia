import { html, render, $, $$, debounce, fmt, userInfo, ISSUE_LABEL, level } from "../core.js";
import { icon } from "../icons.js";
import { store, active } from "../store.js";
import { scorePill, issueChips, stateChip, devIcon, avatar, emptyState } from "../ui.js";

// Estado da tela, preservado entre as atualizações automáticas
const view = { filter: "all", q: "", issue: "", sort: "hostname", dir: 1 };

const FILTERS = [
  ["all", "Todos", m => m.status === "active"],
  ["online", "Online", m => m.status === "active" && m.online],
  ["offline", "Offline", m => m.status === "active" && !m.online],
  ["risk", "Em risco", m => m.status === "active" && (m.healthScore ?? 100) < 70],
  ["retired", "Aposentados / estoque", m => m.status !== "active"],
];

const SORTS = {
  hostname: m => (m.hostname || "").toLowerCase(),
  user: m => userInfo(m.loggedUser).name.toLowerCase(),
  score: m => m.healthScore ?? -1,
  seen: m => +new Date(m.lastSeen),
  os: m => (m.os || "").toLowerCase(),
  ip: m => (m.primaryIp || "").split(".").map(n => n.padStart(3, "0")).join("."),
  agent: m => (m.agentVersion || "").split(".").map(n => n.padStart(4, "0")).join("."),
};

function visible() {
  const q = view.q.trim().toLowerCase();
  const f = FILTERS.find(x => x[0] === view.filter) ?? FILTERS[0];
  const rows = store.machines.filter(m => f[2](m) &&
    (!view.issue || (m.issues ?? []).some(i => i.code === view.issue)) &&
    (!q || [m.hostname, m.primaryIp, m.mac, m.loggedUser, m.serial, m.model, m.manufacturer, m.os].some(v => v && String(v).toLowerCase().includes(q))));
  const key = SORTS[view.sort];
  return rows.sort((a, b) => (key(a) > key(b) ? 1 : key(a) < key(b) ? -1 : 0) * view.dir);
}

const COLS = [
  ["hostname", "Dispositivo"], ["user", "Usuário"], ["score", "Saúde"], [null, "Problemas"], ["ip", "IP", "hide-1500"], ["os", "Sistema", "hide-1500"], ["seen", "Situação"], ["agent", "Agente", "hide-1280"],
];

function rowsHtml(rows) {
  return rows.map(m => {
    const u = userInfo(m.loggedUser);
    return html`<tr class="click" data-href="#/devices/${encodeURIComponent(m.agentId)}">
      <td class="dev"><div class="identity">${devIcon(m)}<div class="t"><b>${m.hostname}</b><span>${[m.manufacturer, m.model].filter(Boolean).join(" ") || "—"}</span></div></div></td>
      <td>${m.loggedUser ? html`<div class="identity">${avatar(m.loggedUser, "sm")}<div class="t"><b style="font-weight:550">${u.name}</b></div></div>` : html`<span class="muted">Sem usuário</span>`}</td>
      <td>${scorePill(m.healthScore)}</td>
      <td>${issueChips(m.issues, 1)}</td>
      <td class="nowrap mono hide-1500">${m.primaryIp ?? "—"}</td>
      <td class="nowrap hide-1500">${(m.os || "—").replace(/^Microsoft\s+/i, "")}</td>
      <td class="nowrap">${stateChip(m)}</td>
      <td class="nowrap muted hide-1280">${m.agentVersion ?? "—"}</td></tr>`;
  });
}

export const devices = {
  title: "Dispositivos",
  sub: "Inventário, saúde e situação de cada máquina",

  async render(root, query, { fresh } = {}) {
    if (fresh && !query.has("filter") && !query.has("issue") && !query.has("q")) Object.assign(view, { filter: "all", q: "", issue: "" });
    // filtros vindos da URL (links do dashboard)
    if (query.has("filter")) view.filter = query.get("filter");
    if (query.has("issue")) view.issue = query.get("issue");
    else if (query.has("filter")) view.issue = "";
    if (query.has("q")) view.q = query.get("q");

    // esqueleto uma vez só: a tabela é redesenhada sem recriar a barra de filtros (o campo de busca não perde o foco)
    if (!$("#dev-table", root)) {
      render(root, html`<div class="toolbar">
          <div class="seg" id="dev-seg" role="group" aria-label="Filtro"></div>
          <div class="field-icon grow">${icon("search", 17)}<input class="field" id="dev-q" type="search" placeholder="Buscar por nome, usuário, IP, MAC, serial, modelo…" aria-label="Buscar dispositivos"></div>
          <select class="field" id="dev-issue" aria-label="Filtrar por problema"></select></div>
        <section class="card"><div class="card-b flush" id="dev-table"></div></section>`);
      $("#dev-q", root).addEventListener("input", debounce(e => { view.q = e.target.value; this.draw(root); }, 120));
      $("#dev-issue", root).addEventListener("change", e => { view.issue = e.target.value; this.draw(root); });
      $("#dev-seg", root).addEventListener("click", e => { const b = e.target.closest("button"); if (b) { view.filter = b.dataset.f; this.draw(root); } });
      $("#dev-table", root).addEventListener("click", e => {
        const th = e.target.closest("th.sort");
        if (th) { const k = th.dataset.k; view.dir = view.sort === k ? -view.dir : (k === "score" || k === "seen" ? -1 : 1); view.sort = k; this.draw(root); }
      });
    }
    this.draw(root);
  },

  draw(root) {
    const seg = $("#dev-seg", root);
    seg.innerHTML = FILTERS.map(([k, label, fn]) => html`<button type="button" data-f="${k}" class="${view.filter === k ? "on" : ""}" aria-pressed="${view.filter === k}">${label}<span class="n">${store.machines.filter(fn).length}</span></button>`).map(String).join("");

    const sel = $("#dev-issue", root);
    const counts = new Map();
    for (const m of active()) for (const i of m.issues ?? []) counts.set(i.code, (counts.get(i.code) ?? 0) + 1);
    sel.innerHTML = String(html`<option value="">Todos os problemas</option>${[...counts].sort((a, b) => b[1] - a[1]).map(([code, n]) => html`<option value="${code}" ${view.issue === code ? "selected" : ""}>${ISSUE_LABEL[code] ?? code} (${n})</option>`)}`);
    if (view.issue && !counts.has(view.issue)) sel.insertAdjacentHTML("beforeend", String(html`<option value="${view.issue}" selected>${ISSUE_LABEL[view.issue] ?? view.issue} (0)</option>`));
    if (document.activeElement !== $("#dev-q", root)) $("#dev-q", root).value = view.q;

    const rows = visible();
    const arrow = k => view.sort === k ? html`<span class="arrow">${view.dir > 0 ? "▲" : "▼"}</span>` : "";
    render($("#dev-table", root), rows.length ? html`<div class="tbl-wrap"><table class="tbl"><thead><tr>${COLS.map(([k, l, c]) => k ? html`<th class="sort ${c ?? ""}" data-k="${k}">${l}${arrow(k)}</th>` : html`<th>${l}</th>`)}</tr></thead>
        <tbody>${rowsHtml(rows)}</tbody></table></div>
        <div class="card-f">${fmt.plural(rows.length, "dispositivo", "dispositivos")}${view.q || view.issue || view.filter !== "all" ? " com os filtros aplicados" : ""}</div>`
      : emptyState("search", "Nenhum dispositivo encontrado", "Ajuste a busca ou os filtros."));
  },

  refresh(root, query) { return this.render(root, new URLSearchParams()); },
};
