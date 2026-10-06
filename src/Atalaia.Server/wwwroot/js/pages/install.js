import { html, render, api, $, $$, fmt } from "../core.js";
import { icon } from "../icons.js";
import { modal, confirmDialog, copyText, toast, emptyState, tipAttr } from "../ui.js";
import { wizard } from "./deploy.js";

// Tela única para colocar o agente nos computadores. Três abas, da mais comum para a menos:
//   Vários computadores (assistente que procura na rede) · Um computador ou GPO (script pronto) · Tokens.
let info = { serverUrl: location.origin, tokens: [] };
let pkg = null;
let tab = "many";
let chosenId = null;   // token usado no script de um computador

const TABS = [["many", "Vários computadores", "network"], ["single", "Um computador ou GPO", "laptop"], ["tokens", "Tokens", "key"]];
const activeTokens = () => info.tokens.filter(t => t.active);
const chosen = () => activeTokens().find(t => t.id === chosenId) ?? activeTokens()[0];
const mask = t => t.slice(0, 8) + "••••••••••••••••" + t.slice(-4);
const isLocal = () => /^(localhost|127\.|\[?::1)/.test(new URL(info.serverUrl).hostname);

// ---------- Janelas ----------
function newTokenModal(reload) {
  const ctl = modal({
    title: "Novo token de agentes", subtitle: "Dê um nome que ajude a lembrar onde ele foi usado.", size: "sm",
    body: html`<label class="fld"><span class="fld-l">Nome</span><input class="field" id="tk-label" maxlength="80" placeholder="Ex.: Filial Centro · outubro/2026" autocomplete="off"></label>`,
    actions: [{ label: "Cancelar", kind: "ghost" }, { label: "Criar token", kind: "primary", onClick: async c => {
      const label = c.$("#tk-label").value.trim();
      if (!label) throw new Error("Dê um nome ao token.");
      const t = await api("/api/install/tokens", { method: "POST", body: JSON.stringify({ label }) });
      chosenId = t.id; toast("Token criado."); await reload();
    } }],
  });
  ctl.$("#tk-label").focus();
}

async function revoke(t, reload) {
  const last = activeTokens().length === 1;
  if (!await confirmDialog({ title: `Revogar “${t.label}”?`, confirmLabel: "Revogar token", danger: true,
    text: `Os agentes que ainda não se registraram com este token deixam de conseguir. ${t.useCount ? "Quem já está instalado e usa este token também para de enviar dados: reinstale com um token novo ou atualize a chave no computador." : "Ele nunca foi usado."}${last ? " Este é o ÚLTIMO token ativo: sem outro, nenhum agente consegue se registrar até você criar um novo." : ""}` })) return;
  try { await api(`/api/install/tokens/${t.id}`, { method: "DELETE" }); toast("Token revogado."); await reload(); }
  catch (e) { toast(e.message); }
}

// ---------- Aba: um computador ou GPO ----------
function singleTab() {
  const t = chosen();
  const cmd = "powershell -ExecutionPolicy Bypass -File .\\instalar-agente.ps1";
  return html`<div class="stack" style="gap:20px">
    ${isLocal() && html`<div class="callout">${icon("alert-triangle", 18)}<div>Você abriu o painel por <b class="mono">${info.serverUrl}</b>. Os outros computadores não alcançam este endereço: abra o painel pelo endereço real do servidor (nome ou IP na rede) antes de baixar o script.</div></div>`}
    ${!pkg?.present && html`<div class="callout">${icon("alert-triangle", 18)}<div>Este painel não encontrou o agente do Windows. Na imagem Docker ele já vem embutido; ao rodar do código, publique-o em <span class="mono">publish/agent</span> (veja o README).</div></div>`}

    <section class="card"><div class="card-h"><div><div class="card-title">Baixe o script de instalação</div>
      <div class="card-sub">Um arquivo só, já com o endereço do painel, o token e o hash do agente. Ele baixa o agente deste painel, confere, instala como serviço e inicia.</div></div></div>
      <div class="card-b"><div class="toolbar" style="margin:0">
        ${activeTokens().length ? html`<select class="field" id="sg-token" aria-label="Token">${activeTokens().map(x => html`<option value="${x.id}" ${x.id === t?.id ? "selected" : ""}>${x.label}</option>`)}</select>
          <a class="btn primary" id="sg-dl" href="/api/install/agent-script?tokenId=${encodeURIComponent(t?.id ?? "")}" download="instalar-agente.ps1">${icon("download", 16)}Baixar instalar-agente.ps1</a>`
          : html`<span class="muted">Nenhum token ativo. Crie um na aba <a href="#" data-tab="tokens">Tokens</a>.</span>`}
        ${pkg?.present && html`<span class="muted">Agente ${pkg.version}</span>`}</div>
        <div class="muted" style="margin-top:8px">O script contém o token: trate-o como senha e apague depois de usar.</div></div></section>

    <div class="grid g12">
      <section class="card span-6"><div class="card-h"><div><div class="card-title">Um computador</div></div></div><div class="card-b stack" style="gap:12px">
        <ol class="steps"><li>Copie o arquivo para o computador.</li>
          <li>Abra o PowerShell <b>como Administrador</b> na pasta do arquivo e rode:</li></ol>
        <div class="codeblk"><div class="codeblk-h"><b>PowerShell</b><button class="btn sm" type="button" data-copy="cmd-single">${icon("copy", 15)}Copiar</button></div><pre class="code" id="cmd-single">${cmd}</pre></div>
        <div class="muted">Em alguns minutos o computador aparece em <a href="#/devices">Dispositivos</a>. Para reinstalar ou atualizar com o agente já instalado, acrescente <span class="mono">-Force</span>.</div></div></section>

      <section class="card span-6"><div class="card-h"><div><div class="card-title">Por GPO (script de inicialização)</div></div></div><div class="card-b stack" style="gap:12px">
        <ol class="steps"><li>Salve o arquivo numa pasta que as <b>contas de computador</b> do domínio consigam ler.</li>
          <li>Na GPO: <i>Configuração do Computador → Políticas → Configurações do Windows → Scripts → Inicialização → aba “Scripts do PowerShell”</i> → Adicionar.</li>
          <li>No próximo boot, cada computador instala sozinho. Quem já tem o agente é ignorado.</li></ol>
        <div class="muted">Prefere instalar por <b>MSI</b> (“software atribuído” da GPO)? É opcional: veja “MSI (opcional)” no README.</div></div></section>
    </div></div>`;
}

// ---------- Aba: tokens ----------
function tokenRow(t) {
  return html`<tr class="${t.active ? "" : "dim"}">
    <td><b>${t.label}</b><div class="muted" style="font-size:12px">${t.createdBy ? `Criado por ${t.createdBy}` : "Criado na instalação"} · ${fmt.date(t.createdAt)}</div></td>
    <td class="mono nowrap">${t.active ? mask(t.token) : "—"}</td>
    <td class="nowrap">${t.lastUsedAt ? html`${fmt.ago(t.lastUsedAt)}<div class="muted" style="font-size:12px">${fmt.plural(t.useCount, "check-in", "check-ins")}</div>` : html`<span class="muted">Nunca usado</span>`}</td>
    <td>${t.active ? html`<span class="chip sm tone-good">${icon("check-circle", 13)}Ativo</span>` : html`<span class="chip sm plain"${tipAttr("Revogado em " + fmt.dt(t.revokedAt))}>${icon("power", 13)}Revogado</span>`}</td>
    <td class="nowrap" style="text-align:right">${t.active && html`<span class="row-actions">
      <button class="btn ghost sm" data-act="copy" data-id="${t.id}"${tipAttr("Copiar token")} aria-label="Copiar token">${icon("copy", 16)}</button>
      <button class="btn ghost sm danger" data-act="revoke" data-id="${t.id}"${tipAttr("Revogar")} aria-label="Revogar ${t.label}">${icon("trash", 16)}</button></span>`}</td></tr>`;
}

function tokensTab() {
  return html`<section class="card"><div class="card-h"><div><div class="card-title">Tokens de agentes</div><div class="card-sub">O token é a “senha de registro” dos computadores. Você pode ter vários ao mesmo tempo: crie um novo, instale com ele e só depois revogue o antigo.</div></div>
      <button class="btn primary" id="tk-new" type="button">${icon("plus", 17)}Novo token</button></div>
    <div class="card-b flush">${info.tokens.length ? html`<div class="tbl-wrap"><table class="tbl"><thead><tr><th>Nome</th><th>Token</th><th>Último uso</th><th>Situação</th><th></th></tr></thead><tbody>${info.tokens.map(tokenRow)}</tbody></table></div>`
      : emptyState("key", "Nenhum token", "Crie um token para começar a instalar agentes.")}</div></section>`;
}

// ---------- Tela ----------
export const install = {
  title: "Instalação",
  sub: "Coloque o agente nos computadores: vários de uma vez, um só ou por GPO",

  async render(root) {
    [info, pkg] = await Promise.all([api("/api/install/info"), api("/api/install/package")]);
    render(root, html`<div class="stack" style="gap:18px"><div class="toolbar" style="margin:0"><div class="seg" id="in-tabs" role="tablist" aria-label="Como instalar">
      ${TABS.map(([k, l, ic]) => html`<button type="button" role="tab" data-t="${k}" class="${tab === k ? "on" : ""}">${icon(ic, 16)}${l}</button>`)}</div></div><div id="in-body"></div></div>`);

    const body = $("#in-body", root);
    const reload = async () => { info = await api("/api/install/info"); paint(); };
    const paint = async () => {
      $$("#in-tabs button", root).forEach(b => b.classList.toggle("on", b.dataset.t === tab));
      if (tab === "many") return wizard.render(body);   // o assistente cuida do próprio desenho e dos próprios temporizadores
      render(body, tab === "single" ? singleTab() : tokensTab());
    };

    root.onclick = async e => {
      const t = e.target.closest("#in-tabs button, [data-tab]");
      if (t) { e.preventDefault(); tab = t.dataset.t ?? t.dataset.tab; return paint(); }
      if (e.target.closest("#tk-new")) return newTokenModal(reload);
      const c = e.target.closest("[data-copy]");
      if (c) { const ok = await copyText($("#" + c.dataset.copy, root).textContent); toast(ok ? "Copiado." : "Selecione o texto e copie."); return; }
      const b = e.target.closest("[data-act]"); if (!b) return;
      const tk = info.tokens.find(x => x.id === b.dataset.id); if (!tk) return;
      if (b.dataset.act === "copy") toast(await copyText(tk.token) ? "Token copiado." : "Não consegui copiar.");
      else if (b.dataset.act === "revoke") revoke(tk, reload);
    };
    root.onchange = e => { if (e.target.id === "sg-token") { chosenId = e.target.value; paint(); } };
    await paint();
  },
};
