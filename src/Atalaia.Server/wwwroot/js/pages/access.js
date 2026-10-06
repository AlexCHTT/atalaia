import { html, render, api, qs, $, $$, fmt, ROLE_LABEL, ROLE_DESC, ACCESS_ACTION, userInfo } from "../core.js";
import { icon } from "../icons.js";
import { store } from "../store.js";
import { avatar, roleChip, modal, confirmDialog, passwordField, strength, copyText, toast, emptyState, tipAttr } from "../ui.js";

const PAGE = 20;
const view = { tab: "users" };
let accounts = [], log = [], logDone = false;
const logFilter = { actor: "", action: "" };

// ---------- Janelas ----------
const roleOptions = selected => Object.entries(ROLE_LABEL).map(([k, v]) => html`<option value="${k}" ${selected === k ? "selected" : ""}>${v}</option>`);

function credentialsModal({ title, username, password, note }) {
  const ctl = modal({
    title, subtitle: "Entregue estes dados à pessoa por um canal seguro.", size: "sm",
    body: html`<div class="cred"><div><span class="muted">Endereço</span><b class="mono">${location.origin}</b></div>
        <div><span class="muted">Usuário</span><b class="mono">${username}</b></div>
        <div><span class="muted">Senha temporária</span><span class="cred-pw"><b class="mono" id="cred-pw">${password}</b>
          <button class="btn sm" type="button" id="cred-copy">${icon("copy", 15)}Copiar</button></span></div></div>
      <div class="callout" style="margin-top:14px">${icon("alert-triangle", 18)}<div>${note ?? "Esta senha aparece só agora e não pode ser consultada depois."} A pessoa será obrigada a criar uma senha própria no primeiro acesso.</div></div>`,
    actions: [{ label: "Concluir", kind: "primary" }],
  });
  ctl.$("#cred-copy").onclick = async e => { const ok = await copyText(password); e.currentTarget.lastChild.textContent = ok ? "Copiado" : "Selecione e copie"; };
}

function userForm(a) {   // a = conta existente (edição) ou null (nova)
  const self = a && a.id === store.me?.id;
  return html`<div class="fld-grid">
    <label class="fld"><span class="fld-l">Nome completo</span><input class="field" id="uf-name" maxlength="80" value="${a?.displayName ?? ""}" autocomplete="off"></label>
    ${a ? html`<label class="fld"><span class="fld-l">Usuário</span><input class="field" value="${a.username}" disabled></label>`
      : html`<label class="fld"><span class="fld-l">Usuário (para entrar)</span><input class="field" id="uf-user" maxlength="40" autocomplete="off" spellcheck="false"><span class="fld-h">3 a 40 caracteres: letras, números, ponto, hífen e sublinhado.</span></label>`}
    <label class="fld"><span class="fld-l">Perfil de acesso</span><select class="field" id="uf-role" ${self ? "disabled" : ""}>${roleOptions(a?.role ?? "viewer")}</select>
      <span class="fld-h" id="uf-role-d">${ROLE_DESC[a?.role ?? "viewer"]}</span>${self && html`<span class="fld-h">Você não pode alterar o seu próprio perfil.</span>`}</label>
    ${a ? html`<label class="chk"><input type="checkbox" id="uf-active" ${a.active ? "checked" : ""} ${self ? "disabled" : ""}><span>Conta ativa (pode entrar no painel)</span></label>` : html`
      <fieldset class="fld"><legend class="fld-l">Senha inicial</legend>
        <label class="chk"><input type="radio" name="uf-pwmode" value="gen" checked><span><b>Gerar uma senha temporária</b> (recomendado). A pessoa a troca no primeiro acesso.</span></label>
        <label class="chk"><input type="radio" name="uf-pwmode" value="manual"><span>Definir a senha agora</span></label></fieldset>
      <div id="uf-manual" hidden>${passwordField({ id: "uf-pw", label: "Senha", autocomplete: "new-password" })}
        <div class="strength"><div class="meter" id="uf-meter"><i style="width:0"></i></div><span class="fld-h" id="uf-strength"></span></div>
        <label class="chk"><input type="checkbox" id="uf-must" checked><span>Exigir troca de senha no primeiro acesso</span></label></div>`}
  </div>`;
}

function bindUserForm(ctl, existing) {
  const role = ctl.$("#uf-role");
  role.onchange = () => { ctl.$("#uf-role-d").textContent = ROLE_DESC[role.value]; };
  if (existing) return;
  // sugere o usuário a partir do nome ("Ana Souza" -> ana.souza) enquanto a pessoa não digitou o dela
  const name = ctl.$("#uf-name"), user = ctl.$("#uf-user");
  let touched = false;
  user.oninput = () => { touched = true; };
  name.oninput = () => {
    if (touched) return;
    const w = name.value.normalize("NFD").replace(/[̀-ͯ]/g, "").toLowerCase().trim().split(/\s+/).filter(Boolean);
    user.value = (w.length > 1 ? [w[0], w[w.length - 1]] : w).join(".").replace(/[^a-z0-9._-]/g, "");   // primeiro e último nome
  };
  for (const r of $$("[name=uf-pwmode]", ctl.el)) r.onchange = () => { ctl.$("#uf-manual").hidden = ctl.$("[name=uf-pwmode]:checked").value !== "manual"; };
  ctl.$("#uf-pw").oninput = e => {
    const s = strength(e.target.value, user.value);
    ctl.$("#uf-meter").className = `meter tone-${s.tone}`; ctl.$("#uf-meter i").style.width = s.pct + "%"; ctl.$("#uf-strength").textContent = s.label;
  };
}

function openNewUser(reload) {
  const ctl = modal({
    title: "Novo usuário do painel", subtitle: "Quem poderá entrar neste painel e o que poderá fazer.", body: userForm(null),
    actions: [{ label: "Cancelar", kind: "ghost" }, { label: "Criar usuário", kind: "primary", onClick: async c => {
      const manual = c.$("[name=uf-pwmode]:checked").value === "manual";
      const body = { displayName: c.$("#uf-name").value.trim(), username: c.$("#uf-user").value.trim(), role: c.$("#uf-role").value };
      if (!body.displayName || !body.username) throw new Error("Preencha o nome e o usuário.");
      if (manual) { body.password = c.$("#uf-pw").value; body.mustChangePassword = c.$("#uf-must").checked; }
      const res = await api("/api/accounts", { method: "POST", body: JSON.stringify(body) });
      toast(`Usuário ${res.account.username} criado.`);
      await reload();
      if (res.temporaryPassword) setTimeout(() => credentialsModal({ title: "Acesso criado", username: res.account.username, password: res.temporaryPassword }), 150);
    } }],
  });
  bindUserForm(ctl, null);
}

function openEditUser(a, reload) {
  const ctl = modal({
    title: `Editar ${a.displayName}`, subtitle: `@${a.username}`, body: userForm(a),
    actions: [{ label: "Cancelar", kind: "ghost" }, { label: "Salvar", kind: "primary", onClick: async c => {
      const self = a.id === store.me?.id;
      const body = { displayName: c.$("#uf-name").value.trim(), role: self ? a.role : c.$("#uf-role").value, active: self ? a.active : c.$("#uf-active").checked };
      if (!body.displayName) throw new Error("Informe o nome.");
      await api(`/api/accounts/${a.id}`, { method: "PUT", body: JSON.stringify(body) });
      toast("Usuário atualizado."); await reload();
    } }],
  });
  bindUserForm(ctl, a);
}

async function resetPassword(a, reload) {
  if (!await confirmDialog({ title: `Redefinir a senha de ${a.displayName}?`, confirmLabel: "Redefinir senha",
    text: "Será gerada uma senha temporária, a senha atual deixa de valer, a conta é destravada e as sessões abertas dela são encerradas." })) return;
  try {
    const res = await api(`/api/accounts/${a.id}/reset-password`, { method: "POST" });
    await reload();
    credentialsModal({ title: "Senha redefinida", username: a.username, password: res.temporaryPassword });
  } catch (e) { toast(e.message); }
}

async function toggleActive(a, reload) {
  const next = !a.active;
  if (!next && !await confirmDialog({ title: `Desativar ${a.displayName}?`, confirmLabel: "Desativar", danger: true,
    text: "A pessoa deixa de conseguir entrar e as sessões abertas dela caem agora. Dá para reativar depois." })) return;
  try { await api(`/api/accounts/${a.id}`, { method: "PUT", body: JSON.stringify({ displayName: a.displayName, role: a.role, active: next }) }); toast(next ? "Conta reativada." : "Conta desativada."); await reload(); }
  catch (e) { toast(e.message); }
}

async function deleteAccount(a, reload) {
  if (!await confirmDialog({ title: `Excluir ${a.displayName}?`, confirmLabel: "Excluir usuário", danger: true,
    text: "A conta é apagada e não pode ser recuperada. O que ela fez continua no registro de acessos." })) return;
  try { await api(`/api/accounts/${a.id}`, { method: "DELETE" }); toast("Usuário excluído."); await reload(); }
  catch (e) { toast(e.message); }
}

// ---------- Telas ----------
const statusChip = a => !a.active ? html`<span class="chip sm plain">${icon("power", 13)}Desativado</span>`
  : a.locked ? html`<span class="chip sm tone-critical"${tipAttr("Travada até " + fmt.dt(a.lockedUntil))}>${icon("lock", 13)}Travado</span>`
  : a.mustChangePassword ? html`<span class="chip sm tone-warning"${tipAttr("Ainda não definiu a própria senha")}>${icon("key", 13)}Troca de senha pendente</span>`
  : html`<span class="chip sm tone-good">${icon("check-circle", 13)}Ativo</span>`;

function usersView() {
  const admins = accounts.filter(a => a.role === "admin" && a.active).length;
  return html`<div class="stack" style="gap:20px">
    <div class="roles">${Object.entries(ROLE_LABEL).map(([k, v]) => html`<div class="card role-card"><div style="display:flex;align-items:center;gap:10px">${roleChip(k)}
      <span class="muted">${accounts.filter(a => a.role === k).length} usuário(s)</span></div><p class="ink-2">${ROLE_DESC[k]}</p></div>`)}</div>
    <section class="card"><div class="card-b flush"><div class="tbl-wrap"><table class="tbl"><thead><tr><th>Usuário</th><th>Perfil</th><th>Situação</th><th class="nowrap">Último acesso</th><th class="nowrap">Criado</th><th></th></tr></thead><tbody>
      ${accounts.map(a => { const self = a.id === store.me?.id; return html`<tr>
        <td><div class="identity">${avatar(a.username)}<div class="t"><b>${a.displayName}${self && html` <span class="chip sm plain" style="margin-left:6px">Você</span>`}</b><span>@${a.username}</span></div></div></td>
        <td>${roleChip(a.role)}</td><td>${statusChip(a)}</td>
        <td class="nowrap muted">${a.lastLoginAt ? fmt.dt(a.lastLoginAt) : "Nunca entrou"}</td>
        <td class="nowrap muted"${tipAttr(a.createdBy ? `Criado por ${a.createdBy}` : "Criado na instalação")}>${fmt.date(a.createdAt)}</td>
        <td class="nowrap" style="text-align:right"><span class="row-actions">
          <button class="btn ghost sm" data-act="edit" data-id="${a.id}"${tipAttr("Editar")} aria-label="Editar ${a.displayName}">${icon("edit", 16)}</button>
          ${!self && html`<button class="btn ghost sm" data-act="reset" data-id="${a.id}"${tipAttr("Redefinir senha")} aria-label="Redefinir senha de ${a.displayName}">${icon("key", 16)}</button>
          <button class="btn ghost sm" data-act="toggle" data-id="${a.id}"${tipAttr(a.active ? "Desativar" : "Reativar")} aria-label="${a.active ? "Desativar" : "Reativar"} ${a.displayName}">${icon("power", 16)}</button>
          <button class="btn ghost sm danger" data-act="delete" data-id="${a.id}"${tipAttr("Excluir")} aria-label="Excluir ${a.displayName}">${icon("trash", 16)}</button>`}</span></td></tr>`; })}
    </tbody></table></div></div><div class="card-f">${fmt.plural(accounts.length, "usuário", "usuários")} · ${fmt.plural(admins, "administrador ativo", "administradores ativos")}. O sistema sempre mantém ao menos um administrador ativo.</div></section></div>`;
}

function logView() {
  const names = [...new Set(accounts.map(a => a.username))].sort();
  return html`<div class="toolbar">
      <select class="field" id="lg-actor" aria-label="Quem"><option value="">Todos os usuários</option>${names.map(n => html`<option value="${n}" ${logFilter.actor === n ? "selected" : ""}>${n}</option>`)}</select>
      <select class="field" id="lg-action" aria-label="Ação"><option value="">Todas as ações</option>${Object.entries(ACCESS_ACTION).map(([k, [l]]) => html`<option value="${k}" ${logFilter.action === k ? "selected" : ""}>${l}</option>`)}</select>
      <span class="muted">Registro imutável de quem entrou, saiu e mexeu em quê.</span></div>
    <section class="card"><div class="card-b flush">${log.length ? html`<div class="tbl-wrap"><table class="tbl"><thead><tr><th>Quando</th><th>Quem</th><th>Ação</th><th>Alvo e detalhe</th><th>Endereço</th></tr></thead><tbody>
      ${log.map(e => { const [label, tone] = ACCESS_ACTION[e.action] ?? [e.action, "info"]; return html`<tr>
        <td class="nowrap"><b>${fmt.dt(e.at)}</b><div class="muted" style="font-size:12px">${fmt.ago(e.at)}</div></td>
        <td class="nowrap">${e.actor ? html`<div class="identity">${avatar(e.actor, "sm")}<div class="t"><b style="font-weight:550">${e.actor}</b></div></div>` : html`<span class="muted">Sistema</span>`}</td>
        <td class="nowrap"><span class="chip sm ${tone === "info" ? "plain" : "tone-" + tone}">${label}</span></td>
        <td>${e.target && html`<b style="font-weight:600">${e.target}</b> `}<span class="muted">${e.detail}</span></td><td class="mono muted nowrap">${e.ip ?? "—"}</td></tr>`; })}</tbody></table></div>`
      : emptyState("list", "Nenhum registro", "Nada corresponde aos filtros.")}</div></section>
    <div class="muted" style="text-align:center;margin-top:14px">${log.length ? `Mostrando ${fmt.plural(log.length, "registro", "registros")}${logDone ? " (fim da lista)" : ""}` : ""}</div>
    <div style="text-align:center;margin-top:10px">${!logDone && log.length ? html`<button class="btn" id="lg-more" type="button">Carregar mais ${PAGE}</button>` : ""}</div>`;
}

async function loadAccounts() { accounts = await api("/api/accounts"); }
async function loadLog(reset) {
  if (reset) { log = []; logDone = false; }
  const p = { actor: logFilter.actor, action: logFilter.action, limit: PAGE };
  if (!reset && log.length) p.before = log[log.length - 1].id;
  const page = await api("/api/access-log?" + qs(p));
  log = log.concat(page); logDone = page.length < PAGE;
}

export const access = {
  title: "Acessos",
  sub: "Quem entra no painel, o que cada perfil pode fazer e o que foi feito",

  async render(root) {
    await loadAccounts();
    if (view.tab === "log") await loadLog(true);
    render(root, html`<div class="toolbar"><div class="seg" id="ac-tabs" role="group" aria-label="Seção">
        <button type="button" data-t="users" class="${view.tab === "users" ? "on" : ""}">${icon("users", 16)}Usuários<span class="n">${accounts.length}</span></button>
        <button type="button" data-t="log" class="${view.tab === "log" ? "on" : ""}">${icon("list", 16)}Registro de acessos</button></div>
        <span class="grow"></span><button class="btn primary" id="ac-new" type="button">${icon("user-plus", 17)}Novo usuário</button></div><div id="ac-body"></div>`);
    const paint = () => { render($("#ac-body", root), view.tab === "users" ? usersView() : logView()); };
    paint();
    const reload = async () => { await loadAccounts(); if (view.tab === "log") await loadLog(true); $("#ac-tabs .n", root).textContent = accounts.length; paint(); };

    $("#ac-new", root).onclick = () => openNewUser(reload);
    $("#ac-tabs", root).onclick = async e => {
      const b = e.target.closest("button"); if (!b) return;
      view.tab = b.dataset.t; $$("#ac-tabs button", root).forEach(x => x.classList.toggle("on", x === b));
      $("#ac-new", root).hidden = view.tab !== "users";
      if (view.tab === "log") await loadLog(true);
      paint();
    };
    $("#ac-new", root).hidden = view.tab !== "users";
    $("#ac-body", root).onclick = async e => {
      if (e.target.closest("#lg-more")) { await loadLog(false); return paint(); }
      const b = e.target.closest("[data-act]"); if (!b) return;
      const a = accounts.find(x => x.id === b.dataset.id); if (!a) return;
      ({ edit: openEditUser, reset: resetPassword, toggle: toggleActive, delete: deleteAccount })[b.dataset.act](a, reload);
    };
    $("#ac-body", root).onchange = async e => {
      if (e.target.id === "lg-actor") logFilter.actor = e.target.value;
      else if (e.target.id === "lg-action") logFilter.action = e.target.value;
      else return;
      await loadLog(true); paint();
    };
  },
};
