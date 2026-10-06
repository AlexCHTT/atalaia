// Ponto de entrada: tema, navegação por URL (hash), busca global, sino de alertas e atualização automática.
import { html, render, $, $$, debounce, fmt, userInfo, api, ROLE_LABEL, loadPublicInfo, applyBrand } from "./core.js";
import { icon } from "./icons.js";
import { store, refresh, subscribe, active, markAlertsSeen, loadMe, can } from "./store.js";
import { initTooltips, drawer, toast, emptyState, avatar, modal, passwordField, strength, roleChip } from "./ui.js";
import { dashboard } from "./pages/dashboard.js";
import { devices } from "./pages/devices.js";
import { users } from "./pages/users.js";
import { tools } from "./pages/tools.js";
import { audit } from "./pages/audit.js";
import { access } from "./pages/access.js";
import { install } from "./pages/install.js";
import { settings } from "./pages/settings.js";
import { openDevice } from "./pages/deviceDrawer.js";
import { openUser } from "./pages/userDrawer.js";

const PAGES = { dashboard, devices, users, tools, audit, install, access, settings };
const NAV = [["dashboard", "Dashboard", "dashboard"], ["devices", "Dispositivos", "laptop"], ["users", "Usuários", "users"], ["tools", "Ferramentas", "sparkles"], ["audit", "Auditoria", "list"],
  ["install", "Instalação", "download", "admin"], ["access", "Acessos", "key", "admin"], ["settings", "Configurações", "settings", "admin"]];   // 4º item: perfil mínimo para ver a entrada no menu
const view = $("#view");

// Quem está logado. Sem sessão válida, api() já redireciona para /login.html e o resto deste arquivo nem chega a rodar.
await loadMe();
await loadPublicInfo(); applyBrand();   // nome da empresa e regra de senha definidos em Configurações
let current = { key: null, q: "" };

// ---------- Tema ----------
const themeNow = () => document.documentElement.dataset.theme || (matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");
function paintThemeBtn() {
  const dark = themeNow() === "dark";
  render($("#theme-btn"), html`${icon(dark ? "sun" : "moon", 17)}<span>${dark ? "Tema claro" : "Tema escuro"}</span>`);
}
$("#theme-btn").addEventListener("click", () => {
  const next = themeNow() === "dark" ? "light" : "dark";
  document.documentElement.dataset.theme = next;
  try { localStorage.setItem("atalaia-theme", next); } catch { /* sem armazenamento: vale só nesta sessão */ }
  paintThemeBtn();
});

// ---------- Casca ----------
const navItem = ([k, label, ic]) =>
  html`<a class="nav-item" href="#/${k}" data-nav="${k}" title="${label}">${icon(ic, 20)}<span class="t">${label}</span><span class="nav-badge" id="nb-${k}" hidden></span></a>`;
render($("#nav"), html`<div class="nav-label">Visão geral</div>${NAV.filter(n => !n[3]).map(navItem)}
  ${can("admin") && html`<div class="nav-label">Administração</div>${NAV.filter(n => n[3] && can(n[3])).map(navItem)}`}`);
render($("#bell"), html`${icon("bell", 19)}<span class="badge-dot" id="bell-n" hidden></span>`);
render($("#menu-btn"), icon("menu", 20));
render($("#search-ic"), icon("search", 18));
paintThemeBtn();
const app = $(".app"), SB_KEY = "atalaia-sidebar";
try { const v = localStorage.getItem(SB_KEY); if (v === "rail" || v === "wide") app.classList.add(v); } catch { /* sem armazenamento */ }
$("#menu-btn").onclick = () => {
  if (matchMedia("(max-width: 760px)").matches) { $("#sidebar").classList.toggle("open"); return; }
  // Em telas até 1100px a barra já nasce recolhida; acima disso nasce larga. O botão inverte o estado atual.
  const narrow = matchMedia("(max-width: 1100px)").matches;
  const isRail = narrow ? !app.classList.contains("wide") : app.classList.contains("rail");
  app.classList.remove("rail", "wide");
  const next = isRail ? "wide" : "rail";
  const differsFromDefault = narrow ? next === "wide" : next === "rail";
  if (differsFromDefault) app.classList.add(next);
  try { differsFromDefault ? localStorage.setItem(SB_KEY, next) : localStorage.removeItem(SB_KEY); } catch { /* vale só nesta sessão */ }
  $("#menu-btn").setAttribute("aria-expanded", String(!(narrow ? !app.classList.contains("wide") : app.classList.contains("rail"))));
};
$("#nav").addEventListener("click", () => $("#sidebar").classList.remove("open"));

// ---------- Usuário logado: menu do avatar, troca de senha, sair ----------
const me = store.me, meInfo = userInfo(me.username);
render($("#me-av"), me.displayName.split(/\s+/).slice(0, 2).map(w => w[0]).join("").toUpperCase());
$("#me-av").style.setProperty("--av", "#2a78d6");
$("#me-name").textContent = me.displayName;
$("#me-role").textContent = ROLE_LABEL[me.role];
render($("#me-chev"), icon("chevron-down", 15));

const userMenu = $("#user-menu");
render(userMenu, html`<div class="um-head"><b>${me.displayName}</b><span>@${me.username}</span><div style="margin-top:8px">${roleChip(me.role)}</div></div>
  <button class="um-item" role="menuitem" data-um="password">${icon("key", 18)}Alterar minha senha</button>
  <button class="um-item" role="menuitem" data-um="logout">${icon("log-out", 18)}Sair</button>`);
const toggleMenu = open => { userMenu.hidden = !open; $("#me-btn").setAttribute("aria-expanded", String(open)); };
$("#me-btn").addEventListener("click", e => { e.stopPropagation(); toggleMenu(userMenu.hidden); });
document.addEventListener("click", e => { if (!e.target.closest("#me-wrap")) toggleMenu(false); });
document.addEventListener("keydown", e => { if (e.key === "Escape") toggleMenu(false); });
userMenu.addEventListener("click", async e => {
  const b = e.target.closest("[data-um]"); if (!b) return;
  toggleMenu(false);
  if (b.dataset.um === "password") changePassword({ forced: false });
  if (b.dataset.um === "logout") { try { await api("/api/auth/logout", { method: "POST" }); } catch { /* sessão já caiu */ } location.replace("/login.html"); }
});

// Janela de troca de senha. forced = a conta tem senha temporária e não pode usar o painel antes de trocar (não dá para fechar).
function changePassword({ forced }) {
  const ctl = modal({
    title: forced ? "Defina a sua senha" : "Alterar minha senha", dismissible: !forced, size: "sm",
    subtitle: forced ? "Você entrou com uma senha temporária. Crie uma senha só sua para continuar." : "As suas outras sessões abertas serão encerradas.",
    body: html`${passwordField({ id: "cp-cur", label: forced ? "Senha temporária" : "Senha atual", autocomplete: "current-password" })}
      ${passwordField({ id: "cp-new", label: "Nova senha", autocomplete: "new-password", hint: "Mínimo de 10 caracteres. Frases longas funcionam bem." })}
      <div class="strength"><div class="meter" id="cp-meter"><i style="width:0"></i></div><span class="fld-h" id="cp-strength"></span></div>
      ${passwordField({ id: "cp-rep", label: "Repita a nova senha", autocomplete: "new-password" })}`,
    actions: [...(forced ? [] : [{ label: "Cancelar", kind: "ghost" }]), { label: "Salvar senha", kind: "primary", onClick: async c => {
      const cur = c.$("#cp-cur").value, next = c.$("#cp-new").value;
      if (next !== c.$("#cp-rep").value) throw new Error("As duas senhas novas não são iguais.");
      if (!strength(next, me.username).ok) throw new Error(strength(next, me.username).label || "Escolha uma senha mais longa.");
      await api("/api/auth/change-password", { method: "POST", body: JSON.stringify({ current: cur, new: next }) });
      toast("Senha alterada.");
      if (forced) { location.reload(); return false; }
    } }],
  });
  ctl.$("#cp-new").oninput = e => {
    const s = strength(e.target.value, me.username);
    ctl.$("#cp-meter").className = `meter tone-${s.tone}`; ctl.$("#cp-meter i").style.width = s.pct + "%"; ctl.$("#cp-strength").textContent = s.label;
  };
}
if (me.mustChangePassword) changePassword({ forced: true });
// qualquer chamada recusada por "troca de senha pendente" reabre a janela (ex.: a sessão veio de outra aba)
window.addEventListener("password-change-required", () => { if (!document.querySelector(".modal-root")) changePassword({ forced: true }); });

// ---------- Roteamento ----------
function parse() {
  const [path, q = ""] = location.hash.replace(/^#\/?/, "").split("?");
  const segs = path.split("/").filter(Boolean).map(decodeURIComponent);
  if (segs[0] === "deploy") segs[0] = "install";   // a antiga tela Implantação virou aba da Instalação: links antigos continuam funcionando
  return { section: PAGES[segs[0]] ? segs[0] : "dashboard", arg: segs[1], query: new URLSearchParams(q) };
}

async function mount(page, query, fresh) {
  view.classList.add("loading");
  try { await page.render(view, query, { fresh }); }
  catch (e) {
    console.error(e);
    render(view, html`<div class="card">${emptyState("alert-triangle", "Não foi possível carregar esta tela", "Verifique a conexão com o servidor e tente novamente.")}<div style="text-align:center;padding-bottom:24px"><button class="btn" id="retry">Tentar de novo</button></div></div>`);
    $("#retry")?.addEventListener("click", () => route());
  }
  view.classList.remove("loading");
}

async function route() {
  const { section, arg, query } = parse();
  if ((section === "access" || section === "install" || section === "settings") && !can("admin")) { location.hash = "#/dashboard"; return; }   // a tela some do menu; isto cobre quem digita o endereço
  const page = PAGES[section];
  $$("[data-nav]").forEach(a => { const on = a.dataset.nav === section; a.classList.toggle("active", on); on ? a.setAttribute("aria-current", "page") : a.removeAttribute("aria-current"); });
  $("#page-title").textContent = page.title;
  $("#page-sub").textContent = page.sub;
  document.title = `${page.title} · Atalaia`; applyBrand();

  // Fechar a gaveta volta para a lista sem refazê-la; abrir uma gaveta mantém a lista por trás.
  const sectionChanged = current.key !== section;
  const queryChanged = !arg && query.toString() !== current.q;
  if (section === "audit") markAlertsSeen();   // quem abre a Auditoria está vendo os alertas
  if (sectionChanged || queryChanged) {
    drawer.close(true);
    current = { key: section, q: arg ? "" : query.toString() };
    await mount(page, query, sectionChanged);
    scrollTo({ top: 0 });
  }
  if (arg && (section === "devices" || section === "users")) {
    const onClose = () => { if (parse().arg) location.hash = `#/${section}`; };
    section === "devices" ? openDevice(arg, onClose) : openUser(arg, onClose);
  } else {
    drawer.close(true);
  }
}
addEventListener("hashchange", route);

// Linhas de tabela clicáveis (data-href)
document.addEventListener("click", e => {
  const row = e.target.closest("[data-href]");
  if (row && !e.target.closest("a, button, input, select")) location.hash = row.dataset.href;
});

// ---------- Busca global ----------
const gs = $("#gs"), results = $("#gs-results");
let sel = -1;
function hits(q) {
  const t = q.trim().toLowerCase();
  if (!t) return { devs: [], usrs: [] };
  const devs = store.machines.filter(m => [m.hostname, m.primaryIp, m.mac, m.serial, m.model, m.loggedUser].some(v => v && String(v).toLowerCase().includes(t))).slice(0, 6);
  const seen = new Set();
  const usrs = store.machines.map(m => m.loggedUser).filter(u => u && !seen.has(u) && seen.add(u) && (u.toLowerCase().includes(t) || userInfo(u).name.toLowerCase().includes(t))).slice(0, 4);
  return { devs, usrs };
}
function paintResults() {
  const { devs, usrs } = hits(gs.value);
  if (!gs.value.trim()) { results.hidden = true; return; }
  sel = -1;
  render(results, html`${devs.length && html`<div class="sr-group">Dispositivos</div>${devs.map(m => html`<a class="sr-item" href="#/devices/${encodeURIComponent(m.agentId)}">${icon("laptop", 17)}<div><b style="font-weight:600">${m.hostname}</b> <span class="muted">${m.primaryIp ?? ""} · ${userInfo(m.loggedUser).name === "—" ? "sem usuário" : userInfo(m.loggedUser).name}</span></div></a>`)}`}
    ${usrs.length && html`<div class="sr-group">Usuários</div>${usrs.map(u => html`<a class="sr-item" href="#/users/${encodeURIComponent(u)}">${avatar(u, "sm")}<div><b style="font-weight:600">${userInfo(u).name}</b> <span class="muted">${u}</span></div></a>`)}`}
    ${!devs.length && !usrs.length && html`<div class="sr-item muted">Nada encontrado para “${gs.value}”.</div>`}
    ${devs.length ? html`<a class="sr-item" href="#/devices?q=${encodeURIComponent(gs.value.trim())}" style="color:var(--accent-ink)">${icon("arrow-right", 16)}Ver todos os resultados na lista</a>` : ""}`);
  results.hidden = false;
}
gs.addEventListener("input", debounce(paintResults, 80));
gs.addEventListener("focus", () => { if (gs.value) paintResults(); });
gs.addEventListener("keydown", e => {
  const items = $$(".sr-item[href]", results);
  if (e.key === "ArrowDown" || e.key === "ArrowUp") {
    e.preventDefault(); if (!items.length) return;
    sel = (sel + (e.key === "ArrowDown" ? 1 : -1) + items.length) % items.length;
    items.forEach((a, i) => a.classList.toggle("sel", i === sel));
  } else if (e.key === "Enter") {
    const target = items[sel] ?? items[0]; if (target) { location.hash = target.getAttribute("href"); gs.value = ""; results.hidden = true; gs.blur(); }
  } else if (e.key === "Escape") { results.hidden = true; gs.blur(); }
});
results.addEventListener("click", () => { gs.value = ""; results.hidden = true; });
document.addEventListener("click", e => { if (!e.target.closest("#search")) results.hidden = true; });
addEventListener("keydown", e => { if (e.key === "/" && !/input|select|textarea/i.test(document.activeElement.tagName)) { e.preventDefault(); gs.focus(); } });

// ---------- Estado vivo: sino, selos, conexão ----------
// Clicar no sino marca os alertas como vistos (o contador zera); o link dele leva à Auditoria já filtrada por alertas.
$("#bell").addEventListener("click", () => markAlertsSeen());
function paintShell() {
  const act = active(), risk = act.filter(m => (m.healthScore ?? 100) < 70).length;
  const badge = (id, n, alert) => { const el = $(id); el.hidden = !n; el.textContent = n; el.classList.toggle("alert", !!alert); };
  badge("#nb-devices", risk, true); badge("#nb-audit", store.alertsUnread, true);
  const bell = $("#bell-n"); bell.hidden = !store.alertsUnread; bell.textContent = store.alertsUnread > 99 ? "99+" : store.alertsUnread;
  $("#srv").classList.toggle("down", !store.ok);
  $("#srv-t").textContent = store.ok ? "Servidor conectado" : "Sem conexão";
  $("#srv-s").textContent = store.at ? `atualizado ${fmt.ago(store.at).replace("agora", "agora mesmo")}` : "atualizando…";
}
subscribe(paintShell);
setInterval(paintShell, 15000);

// ---------- Início ----------
initTooltips();
await refresh();
await route();

// Atualização automática a cada 30 s, sem refazer o que a pessoa está fazendo (gaveta aberta, filtros digitados)
setInterval(async () => {
  await refresh();
  const page = PAGES[current.key];
  if (!page || document.hidden) return;
  try {
    const fn = page.update ?? page.refresh;
    await (fn ? fn.call(page, view) : page.render(view, new URLSearchParams(current.q), {}));
  } catch (e) { console.error(e); }
}, 30000);
