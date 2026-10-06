import { html, render, api, $, debounce, fmt, userInfo } from "../core.js";
import { icon } from "../icons.js";
import { byHostname } from "../store.js";
import { avatar, scorePill, stateChip, emptyState, tipAttr } from "../ui.js";

const view = { q: "", sort: "recent" };

export const users = {
  title: "Usuários",
  sub: "Perfis, dispositivos e histórico de uso",

  async render(root) {
    const list = await api("/api/users");
    if (!$("#usr-grid", root)) {
      render(root, html`<div class="toolbar">
          <div class="field-icon grow">${icon("search", 17)}<input class="field" id="usr-q" type="search" placeholder="Buscar usuário ou dispositivo…" aria-label="Buscar usuários"></div>
          <select class="field" id="usr-sort" aria-label="Ordenar"><option value="recent">Mais recentes</option><option value="name">Nome (A–Z)</option><option value="pcs">Mais dispositivos</option></select></div>
        <div id="usr-grid"></div>`);
      $("#usr-q", root).addEventListener("input", debounce(e => { view.q = e.target.value; this.draw(root); }, 120));
      $("#usr-sort", root).addEventListener("change", e => { view.sort = e.target.value; this.draw(root); });
    }
    this.list = list;
    this.draw(root);
  },

  draw(root) {
    const q = view.q.trim().toLowerCase();
    const rows = (this.list ?? []).filter(u => !q || [u.user, userInfo(u.user).name, u.lastHostname].some(v => v && v.toLowerCase().includes(q)));
    rows.sort(view.sort === "name" ? (a, b) => userInfo(a.user).name.localeCompare(userInfo(b.user).name)
      : view.sort === "pcs" ? (a, b) => b.machines - a.machines || +new Date(b.lastSeen) - +new Date(a.lastSeen)
      : (a, b) => +new Date(b.lastSeen) - +new Date(a.lastSeen));

    render($("#usr-grid", root), rows.length ? html`<div class="cards-users">${rows.map(u => {
      const info = userInfo(u.user), m = byHostname(u.lastHostname);
      return html`<a class="card ucard" href="#/users/${encodeURIComponent(u.user)}" style="text-decoration:none">
        <div class="identity">${avatar(u.user)}<div class="t"><b>${info.name}</b><span class="truncate">${info.domain ? info.domain + "\\" : ""}${info.login}</span></div></div>
        <div style="display:flex;align-items:center;gap:10px;flex-wrap:wrap;min-height:26px">
          ${m ? html`${icon(/^(nb|lp)/i.test(m.hostname) ? "laptop" : "desktop", 17)}<b style="font-weight:600">${m.hostname}</b>${scorePill(m.healthScore)}` : html`${icon("desktop", 17)}<span class="muted">${u.lastHostname ?? "—"}</span>`}</div>
        <div class="ucard-foot"><span>${fmt.plural(u.machines, "dispositivo", "dispositivos")}</span><span${tipAttr(fmt.dt(u.lastSeen))}>visto ${fmt.ago(u.lastSeen)}</span></div></a>`;
    })}</div><p class="muted" style="margin-top:16px">${fmt.plural(rows.length, "usuário", "usuários")}. “Usuário” é quem estava no console do PC no momento do check-in.</p>`
      : html`<div class="card">${emptyState("users", "Nenhum usuário encontrado", "Os usuários aparecem conforme os agentes fazem check-in.")}</div>`);
  },
};
