import { html, render, api, $, $$, fmt } from "../core.js";
import { icon } from "../icons.js";
import { copyText, toast, emptyState, tipAttr } from "../ui.js";

// Assistente de implantação: 1) procurar computadores na rede  2) escolher onde instalar  3) gerar e acompanhar a instalação.
// O estado fica no módulo, então sair da tela e voltar não perde a seleção nem o acompanhamento.
const view = { range: "", filter: "missing", q: "", selected: new Set(), scan: null, pkg: null, tokens: [], tokenId: null, serverUrl: location.origin,
  job: null, jobs: [], busy: false };
let gen = 0;   // cada render() da tela invalida os temporizadores da anterior

try { view.range = localStorage.getItem("at.deploy.range") ?? ""; } catch { /* navegador sem armazenamento: segue sem lembrar a faixa */ }

const PORT_LABEL = { 5985: "WinRM", 135: "RPC", 445: "SMB", 3389: "RDP" };
const hasAdminPort = h => h.openPorts.includes(5985) || h.openPorts.includes(135);
const hasAgent = h => !!h.agent;
const hostName = h => h.agent?.hostname ?? h.name;

const JOB_STATUS = {
  pending: ["Aguardando o script", "info", "clock"],
  sent: ["Enviado, aguardando o agente", "info", "activity"],
  installed: ["Instalado", "good", "check-circle"],
  failed: ["Falhou", "critical", "x-circle"],
  timeout: ["Sem resposta do agente", "warning", "alert-triangle"],
};
const statusChip = t => { const [label, tone, ic] = JOB_STATUS[t.status] ?? [t.status, "info", "clock"]; return html`<span class="chip sm tone-${tone}">${icon(ic, 13)}${label}</span>`; };

const stepHead = (n, title, sub) => html`<div class="card-h"><div style="display:flex;gap:12px;align-items:flex-start"><span class="stepno">${n}</span>
  <div><div class="card-title">${title}</div><div class="card-sub">${sub}</div></div></div></div>`;

// ---------- Passo 1: procurar ----------
function paintScan(root) {
  const s = view.scan, running = s?.state === "running";
  const pct = s && s.total ? Math.round(100 * s.done / s.total) : 0;
  render($("#dp-scan", root), html`${stepHead(1, "Procurar computadores na rede", "O servidor testa cada endereço da faixa. Só redes privadas, até 1024 endereços por vez.")}
    <div class="card-b"><form class="toolbar" id="sc-form" style="margin:0" novalidate>
        <input class="field" id="sc-range" style="min-width:260px" value="${view.range}" placeholder="Ex.: 192.168.1.0/24" aria-label="Faixa de IP" autocomplete="off" spellcheck="false" ${running ? "disabled" : ""}>
        ${running ? html`<button class="btn" id="sc-cancel" type="button">${icon("x", 16)}Cancelar</button>`
          : html`<button class="btn primary" id="sc-go" type="submit">${icon("search", 17)}Procurar computadores</button>`}</form>
      <div class="muted" style="margin-top:8px">Aceita <span class="mono">192.168.1.0/24</span>, um endereço (<span class="mono">192.168.1.10</span>) ou um intervalo (<span class="mono">192.168.1.10-50</span>).</div>
      ${s && html`<div style="margin-top:14px">${running ? html`<div class="meter thin tone-info"><i style="width:${pct}%"></i></div>
          <div class="muted" style="margin-top:6px">Procurando… ${s.done} de ${s.total} endereços · ${fmt.plural(s.hosts.length, "computador encontrado", "computadores encontrados")}</div>`
        : html`<div class="muted">${icon(s.state === "done" ? "check-circle" : "minus-circle", 15)} ${s.state === "done" ? "Concluída" : s.state === "cancelled" ? "Cancelada" : "Falhou"}: <b>${s.range}</b> · ${s.total} endereços · ${fmt.plural(s.hosts.length, "computador encontrado", "computadores encontrados")} · ${fmt.ago(s.finishedAt ?? s.startedAt)}</div>`}</div>`}</div>`);

  $("#sc-form", root).onsubmit = async e => { e.preventDefault(); await startScan(root); };
  const cancel = $("#sc-cancel", root);
  if (cancel) cancel.onclick = async () => { try { await api(`/api/discovery/scans/${view.scan.id}/cancel`, { method: "POST" }); } catch (err) { toast(err.message); } };
}

async function startScan(root) {
  const range = $("#sc-range", root).value.trim();
  view.range = range;
  try { localStorage.setItem("at.deploy.range", range); } catch { /* sem armazenamento */ }
  try {
    view.scan = await api("/api/discovery/scans", { method: "POST", body: JSON.stringify({ range }) });
    view.selected.clear();
    paintScan(root); paintHosts(root); paintInstall(root);
    pollScan(root);
  } catch (err) {
    if (err.status === 409 && err.data?.scan) { view.scan = err.data.scan; paintScan(root); pollScan(root); }
    toast(err.message ?? "Não foi possível iniciar a varredura.");
  }
}

function pollScan(root) {
  const mine = gen;
  const tick = async () => {
    if (mine !== gen || !root.isConnected) return;
    try { const r = await api("/api/discovery/scans/latest"); if (r.id) view.scan = r; } catch { /* tenta de novo */ }
    paintScan(root); paintHosts(root);
    if (view.scan?.state === "running") setTimeout(tick, 1000); else paintInstall(root);
  };
  setTimeout(tick, 600);
}

// ---------- Passo 2: escolher ----------
const visibleHosts = () => {
  const q = view.q.trim().toLowerCase();
  return (view.scan?.hosts ?? []).filter(h => (view.filter === "all" || (view.filter === "missing" ? !hasAgent(h) : hasAgent(h)))
    && (!q || h.ip.includes(q) || (hostName(h) ?? "").toLowerCase().includes(q)));
};

function paintHosts(root) {
  const host = $("#dp-hosts", root);
  const hosts = view.scan?.hosts ?? [];
  if (!view.scan) { render(host, html`${stepHead(2, "Escolher onde instalar", "Os computadores encontrados aparecem aqui.")}<div class="card-b">${emptyState("network", "Nenhuma varredura ainda", "Informe a faixa de IP acima e clique em “Procurar computadores”.")}</div>`); return; }

  // A barra de ferramentas só é criada uma vez, para a busca não perder o foco enquanto a lista atualiza
  if (!$("#hs-q", host)) {
    render(host, html`${stepHead(2, "Escolher onde instalar", "Marque um, vários ou todos. Quem já tem agente não precisa ser marcado.")}
      <div class="card-b"><div class="toolbar" style="margin:0 0 12px">
        <select class="field" id="hs-filter" aria-label="Filtro"><option value="missing">Sem agente</option><option value="have">Com agente</option><option value="all">Todos</option></select>
        <div class="field-icon">${icon("search", 17)}<input class="field" id="hs-q" type="search" placeholder="Filtrar por IP ou nome" aria-label="Filtrar"></div>
        <button class="btn" id="hs-pick" type="button">${icon("check-circle", 16)}Marcar os que faltam</button>
        <button class="btn ghost" id="hs-clear" type="button">Limpar seleção</button>
        <span class="grow"></span><span class="muted" id="hs-count"></span></div><div id="hs-body"></div></div>`);
    $("#hs-filter", host).value = view.filter;
    $("#hs-filter", host).onchange = e => { view.filter = e.target.value; paintHosts(root); };
    $("#hs-q", host).oninput = e => { view.q = e.target.value; paintHosts(root); };
    // Lê a lista no momento do clique: esta barra é criada uma vez, e a varredura continua trazendo computadores depois disso
    $("#hs-pick", host).onclick = () => { for (const h of view.scan?.hosts ?? []) if (!hasAgent(h) && hasAdminPort(h)) view.selected.add(h.ip); paintHosts(root); paintInstall(root); };
    $("#hs-clear", host).onclick = () => { view.selected.clear(); paintHosts(root); paintInstall(root); };
    $("#hs-body", host).onclick = e => {
      const all = e.target.closest("#hs-all");
      if (all) { for (const h of visibleHosts()) all.checked ? view.selected.add(h.ip) : view.selected.delete(h.ip); paintHosts(root); paintInstall(root); return; }
      const cb = e.target.closest("[data-ip]"); if (!cb) return;
      cb.checked ? view.selected.add(cb.dataset.ip) : view.selected.delete(cb.dataset.ip);
      paintHosts(root); paintInstall(root);
    };
  }

  const list = visibleHosts();
  const withAgent = hosts.filter(hasAgent).length;
  $("#hs-count", host).textContent = `${view.selected.size} marcado(s) · ${hosts.length} encontrado(s), ${withAgent} com agente`;
  const allOn = list.length > 0 && list.every(h => view.selected.has(h.ip)), someOn = list.some(h => view.selected.has(h.ip));
  render($("#hs-body", host), list.length ? html`<div class="tbl-wrap"><table class="tbl compact"><thead><tr>
      <th style="width:36px"><input type="checkbox" id="hs-all" aria-label="Marcar todos os listados" ${allOn ? "checked" : ""}></th><th>Endereço</th><th>Nome</th><th>Sistema</th><th>Acesso remoto</th><th>Agente</th></tr></thead><tbody>
    ${list.map(h => { const win = h.openPorts.includes(135) || h.openPorts.includes(445);
      return html`<tr class="${view.selected.has(h.ip) ? "row-sel" : ""}"><td><input type="checkbox" data-ip="${h.ip}" ${view.selected.has(h.ip) ? "checked" : ""} aria-label="Marcar ${h.ip}"></td>
        <td class="mono nowrap">${h.ip}</td><td>${hostName(h) ?? html`<span class="muted">—</span>`}</td>
        <td>${win ? "Windows (provável)" : html`<span class="muted">Não identificado</span>`}</td>
        <td>${hasAdminPort(h) ? html`<div class="chips">${h.openPorts.map(p => html`<span class="chip sm plain">${PORT_LABEL[p]} · ${p}</span>`)}</div>`
          : html`<span class="chip sm tone-warning"${tipAttr("Nem WinRM (5985) nem RPC (135) respondem: o script não conseguirá instalar sem liberar o firewall.")}>${icon("alert-triangle", 13)}Sem acesso remoto</span>`}</td>
        <td>${h.agent ? html`<span class="chip sm tone-good">${icon("check-circle", 13)}Instalado</span> <span class="muted">${h.agent.agentVersion ?? ""} · visto ${fmt.ago(h.agent.lastSeen)}</span>`
          : html`<span class="chip sm tone-info">Sem agente</span>`}</td></tr>`; })}
    </tbody></table></div>`
    : emptyState("search", hosts.length ? "Nada neste filtro" : view.scan.state === "running" ? "Procurando…" : "Nenhum computador encontrado",
      hosts.length ? "Troque o filtro para ver os demais." : view.scan.state === "running" ? "Os computadores aparecem aqui conforme são encontrados." : "Confira a faixa de IP. PCs com firewall que descarta tudo não aparecem."));
  const all = $("#hs-all", host); if (all) all.indeterminate = someOn && !allOn;
}

// ---------- Passo 3: instalar ----------
const jobCounts = j => { const c = { pending: 0, sent: 0, installed: 0, failed: 0, timeout: 0 }; for (const t of j.targets) c[t.status] = (c[t.status] ?? 0) + 1; return c; };
const jobActive = j => j && j.targets.some(t => t.status === "pending" || t.status === "sent");

function paintInstall(root) {
  const host = $("#dp-install", root);
  const pkg = view.pkg, chosen = view.selected.size;
  const token = view.tokens.find(t => t.id === view.tokenId) ?? view.tokens[0];
  const local = /^(localhost|127\.|\[?::1)/.test(new URL(view.serverUrl).hostname);
  const ready = pkg?.present && token && chosen > 0 && !view.busy;

  render(host, html`${stepHead(3, "Instalar", "O painel gera um script com a lista marcada. Você o roda no seu PC, com a sua conta de administrador, e acompanha o resultado aqui.")}
    <div class="card-b stack" style="gap:16px">
      ${local && html`<div class="callout">${icon("alert-triangle", 18)}<div>Você abriu o painel por <b class="mono">${view.serverUrl}</b>. Os outros computadores não alcançam este endereço: abra o painel pelo endereço real do servidor (nome ou IP na rede) antes de gerar a instalação.</div></div>`}

      <div class="req"><div class="req-h"><b>Agente do Windows</b>
        ${pkg?.present ? html`<span class="chip sm tone-good">${icon("check-circle", 13)}Incluído no painel</span>` : html`<span class="chip sm tone-critical">${icon("x-circle", 13)}Indisponível</span>`}</div>
        ${pkg?.present ? html`<div class="muted">Versão ${pkg.version} · ${fmt.bytes(pkg.size)} · SHA-256 <span class="mono">${pkg.sha256.slice(0, 16)}…</span>. Os computadores baixam direto deste painel e conferem o hash antes de instalar. Não há nada para gerar nem enviar.</div>`
          : html`<div class="muted">Este painel não encontrou o agente. Na imagem Docker ele já vem embutido; ao rodar a partir do código, publique-o com <span class="mono">dotnet publish src/Atalaia.Agent -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/agent</span>.</div>`}</div>

      <div class="req"><div class="req-h"><b>Token dos agentes</b></div>
        ${view.tokens.length ? html`<select class="field" id="tk-sel" aria-label="Token">${view.tokens.map(t => html`<option value="${t.id}" ${t.id === token?.id ? "selected" : ""}>${t.label}</option>`)}</select>`
          : html`<div class="muted">Nenhum token ativo. Crie um na tela <a href="#/install">Instalação</a>.</div>`}</div>

      <div class="toolbar" style="margin:0"><button class="btn primary lg" id="jb-create" type="button" ${ready ? "" : "disabled"}>${icon("terminal", 17)}Gerar instalação para ${fmt.plural(chosen, "computador", "computadores")}</button>
        <span class="muted">${!pkg?.present ? "O agente não está disponível neste painel." : chosen === 0 ? "Marque computadores no passo 2." : "A lista marcada vai dentro do script."}</span></div>

      <div id="jb-area"></div></div>`);

  const sel = $("#tk-sel", host); if (sel) sel.onchange = () => { view.tokenId = sel.value; };
  $("#jb-create", host).onclick = () => createJob(root);
  paintJob(root);
}

async function createJob(root) {
  const hosts = new Map((view.scan?.hosts ?? []).map(h => [h.ip, h]));
  const targets = [...view.selected].map(ip => ({ ip, name: hostName(hosts.get(ip) ?? {}) }));
  view.busy = true; paintInstall(root);
  try {
    view.job = await api("/api/deploy/jobs", { method: "POST", body: JSON.stringify({ targets, tokenId: view.tokenId ?? view.tokens[0]?.id }) });
    view.jobs = await api("/api/deploy/jobs");
    toast("Instalação gerada. Baixe o script abaixo.");
    pollJob(root);
  } catch (e) { toast(e.message); }
  view.busy = false; paintInstall(root);
}

function pollJob(root) {
  const mine = gen, id = view.job?.id;
  const tick = async () => {
    if (mine !== gen || !root.isConnected || view.job?.id !== id) return;
    try { view.job = await api(`/api/deploy/jobs/${id}`); } catch { /* tenta de novo */ }
    paintJob(root);
    if (jobActive(view.job)) setTimeout(tick, 3000);
  };
  setTimeout(tick, 3000);
}

function paintJob(root) {
  const area = $("#jb-area", root), j = view.job;
  if (!j) { render(area, html`${view.jobs.length ? html`<div class="muted">Instalações anteriores: ${view.jobs.slice(0, 5).map(x => html`<a href="#" data-job="${x.id}" style="margin-right:12px">${fmt.dt(x.createdAt)} (${x.targets.length})</a>`)}</div>` : ""}`); bindJobLinks(root); return; }
  const c = jobCounts(j), total = j.targets.length;
  const done = c.installed, pct = Math.round(100 * done / total);
  const cmd = "powershell -ExecutionPolicy Bypass -File .\\instalar-atalaia.ps1";
  render(area, html`<section class="card inner"><div class="card-h"><div><div class="card-title">Instalação de ${fmt.dt(j.createdAt)}</div>
      <div class="card-sub">${fmt.plural(total, "computador", "computadores")} · token “${j.tokenLabel}” · gerada por ${j.createdBy ?? "—"}</div></div>
      <div class="card-act"><a class="btn primary" href="/api/deploy/jobs/${j.id}/script" download="instalar-atalaia.ps1">${icon("download", 16)}Baixar o script</a></div></div>
    <div class="card-b stack" style="gap:14px">
      <ol class="steps"><li><b>Baixe o script</b> e salve no seu computador (a conta que vai instalar precisa ser administradora dos computadores de destino).</li>
        <li>Abra o PowerShell <b>como Administrador</b> na pasta do arquivo e rode:
          <div class="codeblk" style="margin-top:8px"><div class="codeblk-h"><b>PowerShell</b><button class="btn sm" type="button" id="jb-copy">${icon("copy", 15)}Copiar</button></div><pre class="code" id="jb-cmd">${cmd}</pre></div>
          <div class="muted" style="margin-top:6px">Para usar outra conta: <span class="mono">-Credential (Get-Credential)</span> · testar em um só: <span class="mono">-Only 192.168.1.10</span> · só simular: <span class="mono">-DryRun</span>. A senha fica no seu PowerShell e nunca é enviada ao painel.</div></li>
        <li>Acompanhe abaixo: o computador vira <b>Instalado</b> quando o agente se registra no painel.</li></ol>
      <div><div class="meter thin tone-good"><i style="width:${pct}%"></i></div>
        <div class="muted" style="margin-top:6px">${c.installed} instalado(s) de ${total}${c.failed ? ` · ${c.failed} falha(s)` : ""}${c.timeout ? ` · ${c.timeout} sem resposta` : ""}${c.sent ? ` · ${c.sent} aguardando o agente` : ""}${c.pending ? ` · ${c.pending} aguardando o script` : ""}</div></div>
      <div class="tbl-wrap"><table class="tbl compact"><thead><tr><th>Endereço</th><th>Nome</th><th>Situação</th><th>Detalhe</th></tr></thead><tbody>
        ${j.targets.map(t => html`<tr><td class="mono nowrap">${t.ip}</td><td>${t.foundHostname ?? t.name ?? html`<span class="muted">—</span>`}</td><td class="nowrap">${statusChip(t)}</td>
          <td class="muted">${t.message ?? ""}</td></tr>`)}</tbody></table></div>
      ${jobActive(j) && html`<div class="muted">${icon("activity", 14)} Atualizando sozinho a cada 3 segundos. Um computador “enviado” sem agente após 15 minutos passa para “sem resposta”.</div>`}</div></section>
    ${view.jobs.length > 1 && html`<div class="muted">Outras: ${view.jobs.filter(x => x.id !== j.id).slice(0, 5).map(x => html`<a href="#" data-job="${x.id}" style="margin-right:12px">${fmt.dt(x.createdAt)} (${x.targets.length})</a>`)}</div>`}`);
  const copy = $("#jb-copy", root); if (copy) copy.onclick = async () => toast(await copyText(cmd) ? "Copiado." : "Selecione o texto e copie.");
  bindJobLinks(root);
}

function bindJobLinks(root) {
  for (const a of $$("[data-job]", root)) a.onclick = async e => {
    e.preventDefault();
    try { view.job = await api(`/api/deploy/jobs/${a.dataset.job}`); paintJob(root); if (jobActive(view.job)) pollJob(root); } catch (err) { toast(err.message); }
  };
}

// Conteúdo da aba "Vários computadores" da tela Instalação
export const wizard = {
  async render(root) {
    gen++;
    render(root, html`<div class="stack" style="gap:20px"><section class="card" id="dp-scan"></section><section class="card" id="dp-hosts"></section><section class="card" id="dp-install"></section></div>`);
    const [scan, pkg, info, jobs] = await Promise.all([api("/api/discovery/scans/latest"), api("/api/install/package"), api("/api/install/info"), api("/api/deploy/jobs")]);
    view.scan = scan.id ? scan : view.scan; view.pkg = pkg; view.tokens = info.tokens.filter(t => t.active); view.serverUrl = info.serverUrl; view.jobs = jobs;
    if (!view.tokens.some(t => t.id === view.tokenId)) view.tokenId = view.tokens[0]?.id ?? null;
    if (!view.job && jobs.length) view.job = jobs[0];
    else if (view.job) view.job = jobs.find(j => j.id === view.job.id) ?? view.job;
    paintScan(root); paintHosts(root); paintInstall(root);
    if (view.scan?.state === "running") pollScan(root);
    if (jobActive(view.job)) pollJob(root);
  },
};
