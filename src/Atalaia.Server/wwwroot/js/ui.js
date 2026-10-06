// Componentes de interface reutilizáveis.
import { html, raw, esc, $, $$, userInfo, avatarHue, level, LEVEL_LABEL, SEV_LABEL, ISSUE_LABEL, ROLE_LABEL, fmt, policy } from "./core.js";
import { icon } from "./icons.js";

// ---------- Selos e indicadores ----------
export const tipAttr = text => raw(` data-tip="${esc(text)}"`);

export function scorePill(score, { big = false, label = false } = {}) {
  const lv = level(score);
  return html`<span class="score tone-${lv} ${big ? "lg" : ""}"${tipAttr(`${LEVEL_LABEL[lv]}${score == null ? "" : `: índice de saúde ${score} de 100`}`)}>
    <span class="pill">${score ?? "—"}</span>${label && html`<span class="lbl">${LEVEL_LABEL[lv]}</span>`}</span>`;
}

const SEV_ICON = { critical: "x-circle", warning: "alert-triangle", info: "info" };
export function sevChip(sev, text, small = true) {
  return html`<span class="chip ${small ? "sm" : ""} tone-${sev === "critical" ? "critical" : sev === "warning" ? "warning" : "info"}">${icon(SEV_ICON[sev] ?? "info", small ? 13 : 15)}${text ?? SEV_LABEL[sev]}</span>`;
}

export function issueChips(issues, max = 2) {
  if (!issues?.length) return html`<span class="chip sm tone-good">${icon("check-circle", 13)}Sem problemas</span>`;
  const order = { critical: 0, warning: 1, info: 2 };
  const sorted = [...issues].sort((a, b) => order[a.severity] - order[b.severity] || b.penalty - a.penalty);
  const more = sorted.length - max;
  return html`<span class="chips">${sorted.slice(0, max).map(i => sevChip(i.severity, ISSUE_LABEL[i.code] ?? i.title))}${more > 0 && html`<span class="chip sm plain"${tipAttr(sorted.slice(max).map(i => ISSUE_LABEL[i.code] ?? i.title).join("\n"))}>+${more}</span>`}</span>`;
}

// Estado de conexão: online / offline (com tempo) / aposentado
export function stateChip(m) {
  if (m.status !== "active") return html`<span class="chip sm plain">${icon("box", 13)}${m.status === "retired" ? "Aposentado" : "Em estoque"}</span>`;
  if (m.online) return html`<span class="chip sm tone-good">${icon("wifi", 13)}Online</span>`;
  const days = (Date.now() - new Date(m.lastSeen)) / 86400000;
  return html`<span class="chip sm ${days > 7 ? "tone-serious" : "tone-off"}"${tipAttr("Último sinal " + fmt.dt(m.lastSeen))}>${icon("wifi-off", 13)}Offline ${fmt.ago(m.lastSeen)}</span>`;
}

export function avatar(user, size = "") {
  const u = userInfo(user);
  return html`<span class="avatar ${size}" style="--av:${avatarHue(u.login || "?")}" aria-hidden="true">${u.initials}</span>`;
}

const LAPTOP_RE = /^(nb|lp|nt|note)/i, LAPTOP_MODEL = /latitude|thinkpad|book|aspire|vivobook|inspiron|laptop|zenbook|yoga|swift|ideapad|vostro 3|83gl/i;
export const isLaptop = m => LAPTOP_RE.test(m.hostname || "") || LAPTOP_MODEL.test(m.model || "");
export function devIcon(m, size = "") {
  return html`<span class="devicon ${size}">${icon(isLaptop(m) ? "laptop" : "desktop", size ? 26 : 19)}</span>`;
}

const ROLE_ICON = { admin: "shield-check", operator: "edit", viewer: "eye" };
export const roleChip = role =>
  html`<span class="chip sm ${role === "admin" ? "tone-info" : "plain"}">${icon(ROLE_ICON[role] ?? "user", 13)}${ROLE_LABEL[role] ?? role}</span>`;

// Campo de senha com botão de mostrar/ocultar (o clique é tratado em initTooltips)
export const passwordField = ({ id, label, autocomplete = "off", hint }) =>
  html`<label class="fld" for="${id}"><span class="fld-l">${label}</span>
    <span class="pw"><input class="field" id="${id}" type="password" autocomplete="${autocomplete}" spellcheck="false">
    <button type="button" class="pw-eye" data-eye="${id}" aria-label="Mostrar ou ocultar a senha">${icon("eye", 18)}</button></span>${hint && html`<span class="fld-h">${hint}</span>`}</label>`;

// Medidor simples de senha: o servidor exige o mínimo definido em Configurações; aqui só orienta (comprimento pesa mais que símbolos)
export function strength(pw, username = "") {
  if (!pw) return { tone: "off", label: "", pct: 0, ok: false };
  const min = policy.minPassword;
  if (pw.length < min) return { tone: "critical", label: `Faltam ${min - pw.length} caractere${min - pw.length === 1 ? "" : "s"} (mínimo ${min})`, pct: Math.max(8, pw.length * 8), ok: false };
  if (username && pw.toLowerCase().includes(username.toLowerCase())) return { tone: "critical", label: "Não pode conter o nome de usuário", pct: 30, ok: false };
  const kinds = [/[a-z]/, /[A-Z]/, /\d/, /[^A-Za-z0-9]/].filter(r => r.test(pw)).length;
  const score = pw.length + kinds * 3;
  return score >= 24 ? { tone: "good", label: "Senha forte", pct: 100, ok: true }
    : score >= 17 ? { tone: "warning", label: "Razoável: quanto mais longa, melhor", pct: 66, ok: true }
    : { tone: "warning", label: "Fraca: misture letras, números e símbolos", pct: 45, ok: true };
}

// ---------- Blocos ----------
export function tile({ iconName, label, value, cap, tone = "info", href, tip }) {
  const inner = html`<div class="tile-top"><span class="tile-ic tone-${tone}">${icon(iconName, 18)}</span><span class="tile-label">${label}</span></div>
    <div class="tile-foot"><div class="tile-value num">${value}</div><div class="tile-cap">${cap}</div></div>`;
  return href ? html`<a class="card tile" href="${href}"${tip && tipAttr(tip)}>${inner}</a>` : html`<div class="card tile">${inner}</div>`;
}

// Cartão com título e (opcional) alternância gráfico/tabela: toda visualização tem sua "gêmea" em tabela, para acessibilidade
export function card({ title, sub, actions, body, table, cls = "", flush = false }) {
  const toggle = table ? html`<button class="btn ghost sm" data-toggle-table type="button" aria-pressed="false">${icon("list", 15)}Tabela</button>` : "";
  return html`<section class="card ${cls}">
    ${(title || actions || table) && html`<div class="card-h"><div><div class="card-title">${title}</div>${sub && html`<div class="card-sub">${sub}</div>`}</div>
      <div class="card-act">${actions}${toggle}</div></div>`}
    <div class="card-b ${flush ? "flush" : ""}">
      ${table ? html`<div class="view-chart">${body}</div><div class="view-table">${table}</div>` : body}
    </div></section>`;
}

export const emptyState = (iconName, title, text) =>
  html`<div class="empty">${icon(iconName, 34)}<b>${title}</b><div>${text}</div></div>`;

export function simpleTable(head, rows) {
  return html`<div class="tbl-wrap"><table class="tbl compact"><thead><tr>${head.map(h => html`<th>${h}</th>`)}</tr></thead>
    <tbody>${rows.map(r => html`<tr>${r.map(c => html`<td>${c}</td>`)}</tr>`)}</tbody></table></div>`;
}

// ---------- Dica flutuante (hover/foco). Texto sempre via textContent: nunca HTML ----------
let tipEl;
export const tip = {
  show(content, x, y) {
    tipEl ??= document.body.appendChild(Object.assign(document.createElement("div"), { className: "tip", role: "tooltip" }));
    tipEl.replaceChildren(content instanceof Node ? content : document.createTextNode(content));
    tipEl.classList.add("on");
    const r = tipEl.getBoundingClientRect();
    tipEl.style.left = Math.min(x + 14, innerWidth - r.width - 10) + "px";
    tipEl.style.top = (y + 18 + r.height > innerHeight ? y - r.height - 12 : y + 18) + "px";
  },
  hide() { tipEl?.classList.remove("on"); },
};
export function initTooltips() {
  const target = e => e.target.closest?.("[data-tip]");
  document.addEventListener("pointerover", e => { const t = target(e); if (t) tip.show(t.dataset.tip, e.clientX, e.clientY); });
  document.addEventListener("pointermove", e => { const t = target(e); if (t) tip.show(t.dataset.tip, e.clientX, e.clientY); });
  document.addEventListener("pointerout", e => { if (target(e)) tip.hide(); });
  document.addEventListener("focusin", e => { const t = target(e); if (t) { const r = t.getBoundingClientRect(); tip.show(t.dataset.tip, r.left, r.bottom - 14); } });
  document.addEventListener("focusout", () => tip.hide());
  // mostrar/ocultar senha
  document.addEventListener("click", e => {
    const eye = e.target.closest("[data-eye]");
    if (!eye) return;
    const input = document.getElementById(eye.dataset.eye), show = input.type === "password";
    input.type = show ? "text" : "password";
    eye.innerHTML = icon(show ? "eye-off" : "eye", 18).s;
  });
  // alternância gráfico/tabela de qualquer cartão
  document.addEventListener("click", e => {
    const b = e.target.closest("[data-toggle-table]");
    if (!b) return;
    const c = b.closest(".card"), on = c.classList.toggle("show-table");
    b.setAttribute("aria-pressed", on);
    b.lastChild.textContent = on ? "Gráfico" : "Tabela";
  });
}

// ---------- Janela (modal) ----------
// actions: [{ label, kind: "primary"|"danger"|"ghost", onClick(ctl) }]. Se onClick devolver false, a janela fica aberta.
// ctl: { close(), error(msg), busy(on), $(sel), el }. Enter em um campo aciona o botão principal; Esc fecha (se dismissible).
export function modal({ title, subtitle, body, actions = [], dismissible = true, size = "" }) {
  const root = document.createElement("div");
  root.className = "modal-root";
  root.innerHTML = html`<div class="modal-scrim" data-close></div>
    <div class="modal ${size}" role="dialog" aria-modal="true" aria-labelledby="modal-title">
      <div class="modal-h"><div><h2 id="modal-title">${title}</h2>${subtitle && html`<p class="muted">${subtitle}</p>`}</div>
        ${dismissible && html`<button type="button" class="btn ghost sm" data-close aria-label="Fechar">${icon("x", 18)}</button>`}</div>
      <div class="modal-b">${body}</div><div class="modal-err" role="alert" hidden></div>
      <div class="modal-f">${actions.map((a, i) => html`<button type="button" class="btn ${a.kind ?? ""}" data-act="${i}">${a.label}</button>`)}</div></div>`.s;
  document.body.append(root);

  const ctl = {
    el: root, $: sel => root.querySelector(sel),
    close() { root.remove(); document.removeEventListener("keydown", onKey, true); },
    error(msg) { const e = ctl.$(".modal-err"); e.textContent = msg ?? ""; e.hidden = !msg; },
    busy(on) { $$("[data-act],[data-close]", root).forEach(b => { b.disabled = on; }); root.classList.toggle("busy", on); },
  };
  const run = async i => {
    ctl.error(""); ctl.busy(true);
    try { if ((await actions[i].onClick?.(ctl)) !== false) return ctl.close(); }
    catch (e) { ctl.error(e?.message || "Não foi possível concluir."); }
    ctl.busy(false);
  };
  const primary = actions.findIndex(a => a.kind === "primary" || a.kind === "danger");
  root.addEventListener("click", e => {
    if (e.target.closest("[data-close]") && dismissible) return ctl.close();
    const b = e.target.closest("[data-act]"); if (b) run(+b.dataset.act);
  });
  root.addEventListener("keydown", e => {
    if (e.key === "Enter" && e.target.matches("input:not([type=checkbox]):not([type=radio])") && primary >= 0) { e.preventDefault(); run(primary); }
  });
  function onKey(e) {
    if (e.key !== "Escape" || !dismissible || !root.isConnected) return;
    if (root !== [...document.querySelectorAll(".modal-root")].pop()) return;   // só a janela de cima reage
    e.stopPropagation(); ctl.close();
  }
  document.addEventListener("keydown", onKey, true);
  requestAnimationFrame(() => (root.querySelector("input:not([type=hidden]), select, textarea") ?? root.querySelector("[data-act]"))?.focus());
  return ctl;
}

export function confirmDialog({ title, text, confirmLabel = "Confirmar", danger = false }) {
  return new Promise(resolve => {
    modal({ title, body: html`<p class="ink-2">${text}</p>`, size: "sm", actions: [
      { label: "Cancelar", kind: "ghost", onClick: () => resolve(false) },
      { label: confirmLabel, kind: danger ? "danger" : "primary", onClick: () => resolve(true) },
    ] });
  });
}

// Copiar: a API de área de transferência só existe em HTTPS/localhost; em HTTP interno cai no método antigo
export async function copyText(text) {
  try { await navigator.clipboard.writeText(text); return true; } catch { /* sem contexto seguro */ }
  const ta = Object.assign(document.createElement("textarea"), { value: text });
  ta.style.cssText = "position:fixed;opacity:0";
  document.body.append(ta); ta.select();
  const ok = document.execCommand("copy"); ta.remove(); return ok;
}

// ---------- Notificações ----------
export function toast(msg, tone = "info") {
  const host = $(".toasts") ?? document.body.appendChild(Object.assign(document.createElement("div"), { className: "toasts" }));
  const el = document.createElement("div");
  el.className = "toast";
  el.append(document.createTextNode(msg));
  host.append(el);
  setTimeout(() => el.remove(), 4200);
}

// ---------- Gaveta lateral ----------
let scrim, panel, onCloseCb;
export const drawer = {
  get isOpen() { return panel?.classList.contains("on"); },
  show(safeHtml, onClose) {
    scrim ??= document.body.appendChild(Object.assign(document.createElement("div"), { className: "scrim" }));
    panel ??= document.body.appendChild(Object.assign(document.createElement("aside"), { className: "drawer", role: "dialog" }));
    scrim.onclick = () => drawer.close();
    onCloseCb = onClose;
    panel.innerHTML = safeHtml.s;
    panel.setAttribute("aria-modal", "true");
    requestAnimationFrame(() => { scrim.classList.add("on"); panel.classList.add("on"); });
    return panel;
  },
  body: () => $(".drawer-b", panel),
  el: () => panel,
  close(silent = false) {
    if (!panel?.classList.contains("on")) return;
    scrim.classList.remove("on"); panel.classList.remove("on");
    const cb = onCloseCb; onCloseCb = null;
    if (!silent) cb?.();
  },
};
document.addEventListener("keydown", e => { if (e.key === "Escape") drawer.close(); });
