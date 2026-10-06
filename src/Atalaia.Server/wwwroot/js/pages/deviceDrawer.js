import { html, raw, api, qs, $, $$, fmt, userInfo, ISSUE_LABEL, SEV_LABEL, level, LEVEL_LABEL, STATUS_LABEL, debounce } from "../core.js";
import { icon, PERIPHERAL_ICON } from "../icons.js";
import { store, byId, refresh, can } from "../store.js";
import { drawer, devIcon, stateChip, scorePill, sevChip, avatar, emptyState, toast, tipAttr, isLaptop, tile } from "../ui.js";
import { meter, lineChart } from "../charts.js";
import { feedItem } from "../events.js";

const TABS = [["summary", "Resumo", "activity"], ["security", "Segurança", "shield-check"], ["hardware", "Hardware", "cpu"], ["network", "Rede", "network"],
  ["software", "Software", "package"], ["peripherals", "Periféricos", "usb"], ["activity", "Atividade", "list"], ["performance", "Desempenho", "zap"], ["speed", "Velocidade", "gauge"]];
// Só o que desconta pontos no índice de saúde (SMB é normal em PC de domínio e não entra)
const RISKY = { 21: "FTP", 23: "Telnet", 3389: "RDP", 5800: "VNC", 5900: "VNC", 5901: "VNC" };

const kv = rows => html`<dl class="kv">${rows.map(([k, v]) => html`<dt>${k}</dt><dd>${v == null || v === "" ? html`<span class="muted">—</span>` : v}</dd>`)}</dl>`;
const section = (title, body, { sub, cls = "span-12", flush = false } = {}) =>
  html`<section class="card ${cls}"><div class="card-h"><div><div class="card-title">${title}</div>${sub && html`<div class="card-sub">${sub}</div>`}</div></div><div class="card-b ${flush ? "flush" : ""}">${body}</div></section>`;
const mono = v => v ? html`<span class="mono">${v}</span>` : null;

// Linha de verificação de segurança: ícone de estado + título + valor (o estado nunca depende só da cor)
function check({ state, title, detail, value }) {
  const ic = { ok: "check-circle", bad: "x-circle", warn: "alert-triangle", unk: "minus-circle" }[state];
  return html`<div class="check"><span>${icon(ic, 20, state)}</span><div><b style="font-weight:600">${title}</b>${detail && html`<small>${detail}</small>`}</div><span class="${state === "unk" ? "muted" : ""}">${value}</span></div>`;
}
const tri = (v, good = true) => v == null ? "unk" : v === good ? "ok" : "bad";
const word = (v, y = "Sim", n = "Não") => v == null ? "Desconhecido" : v ? y : n;

// ---------- Abas ----------
function tabSummary(d, m) {
  const vol = d.volumes.find(v => v.letter?.toUpperCase() === "C:") ?? d.volumes[0];
  const diskPct = vol?.sizeBytes ? 100 * (vol.sizeBytes - vol.freeBytes) / vol.sizeBytes : null;
  const ramPct = d.hardware.ramTotalBytes && d.health.ramFreeBytes != null ? 100 * (1 - d.health.ramFreeBytes / d.hardware.ramTotalBytes) : null;
  const tone = p => p == null ? "info" : p >= 90 ? "critical" : p >= 80 ? "warning" : "info";
  const live = (label, pct, cap, iconName) => html`<div class="card mini"><div class="tile-top"><span class="tile-ic">${icon(iconName, 17)}</span><span class="tile-label">${label}</span></div>
    <div class="tile-value num">${fmt.pct(pct)}</div>${pct != null && meter(pct, tone(pct))}<div class="tile-cap" style="margin-top:6px">${cap}</div></div>`;
  const u = userInfo(d.identity.loggedUser);
  const top = [...d.issues].sort((a, b) => ({ critical: 0, warning: 1, info: 2 })[a.severity] - ({ critical: 0, warning: 1, info: 2 })[b.severity]);

  return html`<div class="grid g12">
    <section class="card span-12"><div class="card-b" style="display:grid;grid-template-columns:auto minmax(0,1fr);gap:22px;align-items:center">
      ${scorePill(d.healthScore, { big: true })}
      <div><div style="font-weight:650;font-size:16px">${LEVEL_LABEL[level(d.healthScore)]}: ${d.issues.length ? `${d.issues.length} ponto${d.issues.length === 1 ? "" : "s"} de atenção` : "nenhum problema encontrado"}</div>
        <div class="chips" style="margin-top:8px">${top.slice(0, 4).map(i => sevChip(i.severity, i.title))}${top.length > 4 && html`<span class="chip sm plain">+${top.length - 4}</span>`}</div></div></div></section>
    <div class="span-12 tilegrid">
      ${live("CPU", d.health.cpuLoadPercent, "uso no último check-in", "cpu")}
      ${live("Memória", ramPct, `${fmt.bytes(d.hardware.ramTotalBytes)} no total`, "ram")}
      ${live("Disco do sistema", diskPct, vol ? `${fmt.bytes(vol.freeBytes)} livres de ${fmt.bytes(vol.sizeBytes)}` : "—", "disk")}
      <div class="card mini"><div class="tile-top"><span class="tile-ic">${icon("clock", 17)}</span><span class="tile-label">Ligado há</span></div><div class="tile-value num">${fmt.dur(d.health.uptimeSeconds)}</div><div class="tile-cap">desde ${fmt.dt(d.health.lastBoot)}</div></div>
    </div>
    ${section("Identificação", kv([["Nome", d.identity.hostname], ["Domínio", d.identity.domain],
      ["Usuário", d.identity.loggedUser ? html`<a href="#/users/${encodeURIComponent(d.identity.loggedUser)}" class="identity" style="display:inline-flex;text-decoration:none;color:inherit">${avatar(d.identity.loggedUser, "sm")}<div class="t"><b style="font-weight:600">${u.name}</b><span>${d.identity.loggedUser}</span></div></a>` : null],
      ["Último check-in", fmt.dt(d.lastSeen)], ["Primeiro registro", fmt.dt(d.firstSeen)], ["IP visto pelo servidor", mono(d.remoteIp)], ["Agente", d.agentVersion], ["ID", mono(d.agentId)]]), { cls: "span-6" })}
    ${section("Sistema operacional", kv([["Sistema", d.os.name], ["Versão", d.os.version], ["Build", d.os.build], ["Arquitetura", d.os.architecture], ["Instalado em", fmt.date(d.os.installDate)],
      ["Ativado", word(d.os.activated)], ["Reinício pendente", word(d.security.pendingReboot)]]), { cls: "span-6" })}
    ${section("Ferramentas detectadas", d.tools.length ? html`<div class="chips">${d.tools.map(t => html`<span class="chip tone-${t.source === "em execução" ? "good" : "info"}">${icon("sparkles", 14)}${t.tool}<span class="muted" style="font-weight:500">· ${t.source}</span></span>`)}</div>`
      : html`<span class="muted">Nenhuma ferramenta da lista de interesse (apps de IA) neste dispositivo.</span>`)}
  </div>`;
}

function tabSecurity(d) {
  const s = d.security, av = s.antivirusProducts ?? [];
  const third = av.some(p => !/defender/i.test(p));
  const sigDays = s.antivirusSignatureDate ? Math.floor((Date.now() - new Date(s.antivirusSignatureDate)) / 864e5) : null;
  const fwAll = [s.firewallDomain, s.firewallPrivate, s.firewallPublic];
  const fwState = fwAll.every(v => v == null) ? "unk" : fwAll.some(v => v === false) ? "bad" : "ok";
  const ports = (d.networkActivity?.listening ?? []).filter(p => RISKY[p.port]);

  return html`<div class="grid g12">
    ${section("Controles de segurança", html`
      ${check({ state: s.antivirusEnabled == null ? "unk" : s.antivirusEnabled || third ? "ok" : "bad", title: "Antivírus", detail: av.join(", ") || "Nenhum antivírus informado", value: word(s.antivirusEnabled || third || (s.antivirusEnabled == null ? null : false), "Ativo", "Inativo") })}
      ${check({ state: s.realTimeProtection == null ? "unk" : s.realTimeProtection || third ? "ok" : "bad", title: "Proteção em tempo real", value: word(s.realTimeProtection, "Ligada", "Desligada") })}
      ${check({ state: sigDays == null ? "unk" : sigDays > 7 ? "warn" : "ok", title: "Assinaturas do antivírus", detail: s.antivirusSignatureDate ? `Atualizadas em ${fmt.date(s.antivirusSignatureDate)}` : null, value: sigDays == null ? "Desconhecido" : sigDays === 0 ? "Hoje" : `${sigDays} dia${sigDays === 1 ? "" : "s"}` })}
      ${check({ state: fwState, title: "Firewall do Windows", detail: `Domínio: ${word(s.firewallDomain, "ligado", "desligado")} · Privada: ${word(s.firewallPrivate, "ligado", "desligado")} · Pública: ${word(s.firewallPublic, "ligado", "desligado")}`, value: fwState === "ok" ? "Ligado" : fwState === "bad" ? "Parcial/desligado" : "Desconhecido" })}
      ${check({ state: tri(s.bitLockerOnSystemDrive), title: "Criptografia do disco (BitLocker)", value: word(s.bitLockerOnSystemDrive, "Ativa", "Inativa") })}
      ${check({ state: s.tpmPresent == null ? "unk" : s.tpmPresent ? "ok" : "warn", title: "TPM", detail: s.tpmVersion ? `Versão ${s.tpmVersion}` : null, value: word(s.tpmPresent, "Presente", "Ausente") })}
      ${check({ state: tri(s.secureBoot), title: "Secure Boot", value: word(s.secureBoot, "Ativo", "Inativo") })}
      ${check({ state: tri(d.os.activated), title: "Windows ativado", value: word(d.os.activated) })}
      ${check({ state: s.pendingReboot ? "warn" : s.pendingReboot == null ? "unk" : "ok", title: "Reinício pendente", value: word(s.pendingReboot, "Sim", "Não") })}`,
      { sub: "Verificações feitas pelo agente a cada check-in. “Desconhecido” significa que o agente não conseguiu ler (ex.: sem privilégio)." })}
    ${section(`Problemas encontrados (${d.issues.length})`, d.issues.length ? html`<div class="tbl-wrap"><table class="tbl compact"><thead><tr><th>Gravidade</th><th>Problema</th><th class="nowrap">Pontos</th></tr></thead><tbody>
        ${[...d.issues].sort((a, b) => b.penalty - a.penalty).map(i => html`<tr><td>${sevChip(i.severity)}</td><td>${i.title}</td><td class="num">−${i.penalty}</td></tr>`)}</tbody></table></div>`
      : html`<span class="muted">Nenhum problema: índice 100.</span>`, { sub: "Cada problema desconta pontos do índice de saúde (100 menos a soma)." })}
    ${section("Serviços expostos à rede", ports.length ? html`<div class="chips">${ports.map(p => html`<span class="chip tone-warning"${tipAttr(`Porta ${p.port} (${p.process})`)}>${icon("alert-triangle", 14)}${RISKY[p.port]} · ${p.port} <span class="muted" style="font-weight:500">${p.process}</span></span>`)}</div>`
      : html`<span class="muted">Nenhuma porta de acesso remoto ou serviço legado exposto.</span>`, { sub: "RDP, VNC, SMB, FTP e Telnet escutando para outras máquinas." })}
  </div>`;
}

function tabHardware(d) {
  const h = d.hardware;
  return html`<div class="grid g12">
    ${section("Equipamento", kv([["Fabricante", h.manufacturer], ["Modelo", h.model], ["Tipo", h.isLaptop ? "Notebook" : "Desktop"], ["Serial", mono(h.serialNumber)], ["UUID", mono(h.uuid)],
      ["BIOS", h.biosVersion && `${h.biosVersion} (${fmt.date(h.biosDate)})`]]), { cls: "span-6" })}
    ${section("Processador e memória", kv([["CPU", h.cpu], ["Núcleos / threads", `${h.cpuCores ?? "?"} / ${h.cpuThreads ?? "?"}`], ["Memória total", fmt.bytes(h.ramTotalBytes)],
      ["Módulos", h.ramModules.map(r => `${fmt.bytes(r.capacityBytes)} ${r.speedMhz ? r.speedMhz + " MHz" : ""}`).join(" + ")], ["Vídeo", h.gpus.map(g => g.name).join(", ")]]), { cls: "span-6" })}
    ${section("Discos", d.disks.length ? html`<div class="stack">${d.disks.map(k => html`<div class="identity"><span class="devicon">${icon("disk", 19)}</span><div class="t"><b>${k.model}</b>
      <span>${[k.mediaType, k.busType].filter(Boolean).join(" · ")} · ${fmt.bytes(k.sizeBytes)}</span></div><span style="margin-left:auto">${k.healthStatus === "Healthy" || k.healthStatus === "OK" ? html`<span class="chip sm tone-good">${icon("check-circle", 13)}Saudável</span>` : html`<span class="chip sm tone-critical">${icon("x-circle", 13)}${k.healthStatus ?? "Desconhecido"}</span>`}</span></div>`)}</div>` : html`<span class="muted">—</span>`, { cls: "span-6" })}
    ${section("Volumes", d.volumes.length ? html`<div class="stack">${d.volumes.map(v => { const used = v.sizeBytes ? 100 * (v.sizeBytes - v.freeBytes) / v.sizeBytes : 0;
      return html`<div><div style="display:flex;justify-content:space-between;gap:12px"><b style="font-weight:600">${v.letter} ${v.label ?? ""}</b><span class="muted">${fmt.bytes(v.freeBytes)} livres de ${fmt.bytes(v.sizeBytes)}</span></div>
        <div style="margin-top:6px">${meter(used, used >= 90 ? "critical" : used >= 80 ? "warning" : "info", `${fmt.pct(used)} ocupado`)}</div></div>`; })}</div>` : html`<span class="muted">—</span>`, { cls: "span-6" })}
    ${d.monitors.length ? section("Monitores", html`<div class="stack">${d.monitors.map(x => html`<div class="identity"><span class="devicon">${icon("monitor", 19)}</span><div class="t"><b>${[x.manufacturer, x.name].filter(Boolean).join(" ") || "Monitor"}</b><span>${[x.serialNumber, x.manufactureYear].filter(Boolean).join(" · ")}</span></div></div>`)}</div>`, { cls: "span-6" }) : ""}
    ${h.battery ? section("Bateria", html`<div class="identity"><span class="devicon">${icon("battery", 19)}</span><div class="t"><b>${h.battery.chargePercent}%</b><span>${h.battery.charging ? "Carregando" : "Na bateria"}</span></div></div>`, { cls: "span-6" }) : ""}
  </div>`;
}

function tabNetwork(d) {
  const n = d.networkActivity ?? { listening: [], connections: [] };
  return html`<div class="grid g12">
    ${d.network.map(a => section(html`${a.name} ${a.isPrimary && html`<span class="chip sm tone-good" style="margin-left:6px">Principal</span>`} ${a.isVirtual && html`<span class="chip sm plain" style="margin-left:6px">Virtual</span>`}`,
      kv([["Adaptador", a.description], ["MAC", mono(a.mac)], ["IPv4", a.ipv4.join(", ")], ["Gateway", a.gateways.join(", ")], ["DNS", a.dns.join(", ")], ["DHCP", word(a.dhcpEnabled)],
        ["Velocidade", a.speedBps ? fmt.bytes(a.speedBps / 8) + "/s" : null], ["Wi-Fi", a.wifiSsid]]), { cls: "span-6" }))}
    ${section("Portas abertas para a rede", n.listening.length ? html`<div class="chips">${n.listening.map(p => html`<span class="chip sm ${RISKY[p.port] ? "tone-warning" : "plain"}"${tipAttr(p.process)}>${RISKY[p.port] && icon("alert-triangle", 13)}${p.port} · ${p.process}</span>`)}</div>` : html`<span class="muted">Nada escutando para a rede.</span>`,
      { sub: "Serviços que respondem a outras máquinas (loopback fica de fora)." })}
    ${section("Conexões ativas por processo", n.connections.length ? html`<div class="tbl-wrap"><table class="tbl compact"><thead><tr><th>Processo</th><th>Conexões</th><th>Portas de destino</th></tr></thead><tbody>
      ${n.connections.map(c => html`<tr><td><b style="font-weight:600">${c.process}</b></td><td class="num">${c.established}</td><td class="mono muted">${c.remotePorts.join(", ")}</td></tr>`)}</tbody></table></div>` : html`<span class="muted">Sem conexões externas.</span>`,
      { sub: "O painel guarda só as portas de destino, nunca os endereços acessados." })}
  </div>`;
}

function tabSoftware(d) {
  if (!d.software) return html`<div class="card">${emptyState("package", "Lista ainda não coletada", "O agente envia o software instalado ao iniciar e depois a cada 6 horas.")}</div>`;
  return html`<section class="card"><div class="card-h"><div><div class="card-title">Software instalado</div><div class="card-sub" id="sw-count">${fmt.plural(d.software.length, "programa", "programas")}</div></div></div>
    <div class="card-b"><div class="field-icon" style="margin-bottom:12px">${icon("search", 17)}<input class="field" id="sw-q" type="search" placeholder="Filtrar programas…" aria-label="Filtrar programas"></div>
    <div class="tbl-wrap" style="max-height:520px;overflow:auto"><table class="tbl compact"><thead><tr><th>Nome</th><th>Versão</th><th>Fabricante</th></tr></thead><tbody id="sw-rows"></tbody></table></div></div></section>`;
}
function bindSoftware(d, body) {
  const q = $("#sw-q", body), rows = $("#sw-rows", body);
  if (!q) return;
  const draw = () => {
    const t = q.value.trim().toLowerCase();
    const list = d.software.filter(s => !t || [s.name, s.publisher].some(v => v && v.toLowerCase().includes(t)));
    rows.innerHTML = String(html`${list.map(s => html`<tr><td>${s.name}</td><td class="muted nowrap">${s.version}</td><td class="muted">${s.publisher}</td></tr>`)}`);
    $("#sw-count", body).textContent = `${fmt.plural(list.length, "programa", "programas")}${t ? ` de ${d.software.length}` : ""}`;
  };
  q.addEventListener("input", debounce(draw, 100));
  draw();
}

function tabPeripherals(d) {
  if (!d.peripherals.length) return html`<div class="card">${emptyState("usb", "Nenhum periférico coletado", "Agentes antigos não enviam periféricos; atualize o agente.")}</div>`;
  return html`<section class="card"><div class="card-h"><div><div class="card-title">Periféricos (${d.peripherals.length})</div><div class="card-sub">Fabricante e modelo traduzidos a partir do código USB (VID:PID)</div></div></div>
    <div class="card-b flush"><div class="tbl-wrap"><table class="tbl"><thead><tr><th>Dispositivo</th><th>Tipo</th><th>Conexão</th><th>VID:PID</th></tr></thead><tbody>
    ${d.peripherals.map(p => { const real = p.vendorName ? `${p.vendorName}${p.productName ? " " + p.productName : ""}` : null;
      return html`<tr><td><div class="identity"><span class="devicon">${icon(PERIPHERAL_ICON[p.kind] ?? "usb", 18)}</span><div class="t"><b>${real ?? p.name}</b>${real && html`<span>${p.name}</span>`}</div></div></td>
        <td>${p.kind}</td><td>${p.kind === "Armazenamento USB" ? html`<span class="chip sm tone-warning">${icon("alert-triangle", 13)}USB</span>` : html`<span class="chip sm plain">${p.connection}</span>`}</td><td class="mono muted">${p.vendorId ? `${p.vendorId}:${p.productId}` : ""}</td></tr>`; })}
    </tbody></table></div></div></section>`;
}

function tabActivity(d, assigns, events) {
  return html`<div class="grid g12">
    ${section("Usuários que usaram este PC", assigns.length ? html`<div class="timeline">${assigns.map(a => html`<div class="tl-item ${a.current ? "current" : ""}">
      <div class="identity">${avatar(a.user, "sm")}<div class="t"><b><a href="#/users/${encodeURIComponent(a.user)}">${userInfo(a.user).name}</a> ${a.current && html`<span class="chip sm tone-good" style="margin-left:6px">Atual</span>`}</b>
      <span>${fmt.dt(a.firstSeen)} → ${a.current ? "agora" : fmt.dt(a.lastSeen)}</span></div></div></div>`)}</div>` : html`<span class="muted">Nenhum usuário registrado ainda.</span>`, { cls: "span-5" })}
    <section class="card span-7"><div class="card-h"><div><div class="card-title">Histórico de alterações</div><div class="card-sub">Últimos ${events.length} eventos deste dispositivo</div></div>
      <div class="card-act"><a class="btn ghost sm" href="#/audit?agentId=${encodeURIComponent(d.agentId)}">Ver na auditoria</a></div></div>
      <div class="card-b">${events.length ? html`<div class="feed">${events.map(e => feedItem(e, { host: false }))}</div>` : html`<span class="muted">Sem eventos.</span>`}</div></section>
  </div>`;
}

async function bindPerformance(d, body) {
  body.innerHTML = String(html`<div class="toolbar"><label class="muted" style="display:flex;gap:8px;align-items:center">Período
      <select class="field sm" id="perf-range"><option value="24">Últimas 24 horas</option><option value="168">7 dias</option><option value="720">30 dias</option></select></label>
      <span class="muted">Uma amostra por check-in. A CPU é o valor do instante, não a média do intervalo.</span></div><div class="grid" id="perf-charts" style="grid-template-columns:1fr"></div>`);
  const range = $("#perf-range", body), host = $("#perf-charts", body);
  const load = async () => {
    const hours = +range.value;
    const pts = await api(`/api/machines/${encodeURIComponent(d.agentId)}/metrics?hours=${hours}`);
    host.replaceChildren(
      lineChart({ title: "CPU", points: pts.map(p => ({ at: p.at, v: p.cpu })), rangeHours: hours }),
      lineChart({ title: "Memória em uso", points: pts.map(p => ({ at: p.at, v: p.ramPct })), rangeHours: hours }),
      lineChart({ title: "Disco do sistema ocupado", points: pts.map(p => ({ at: p.at, v: p.diskPct })), rangeHours: hours }));
  };
  range.addEventListener("change", load);
  await load();
}

// ---------- Velocidade ----------
const mbps = v => v == null ? "—" : v >= 1000 ? `${(v / 1000).toFixed(2)} Gbps` : `${v.toFixed(1)} Mbps`;
const SPEED_TARGET = { server: "Até o servidor", internet: "Até a internet" };
const SPEED_NOTE = {
  server: "Mede a velocidade entre este PC e o servidor do painel (o que importa dentro da rede interna). Passando por um proxy, o proxy entra na medição.",
  internet: "Mede a velocidade até a internet usando os servidores públicos da Cloudflare (speed.cloudflare.com). O tráfego sai do PC para fora da empresa.",
};
const speedStatus = t => ({
  pending: html`<span class="chip sm tone-info">${icon("clock", 13)}Aguardando o agente</span>`,
  running: html`<span class="chip sm tone-info">${icon("activity", 13)}Medindo</span>`,
  done: html`<span class="chip sm tone-good">${icon("check-circle", 13)}Concluído</span>`,
  failed: html`<span class="chip sm tone-critical"${tipAttr(t.error ?? "")}>${icon("x-circle", 13)}Falhou</span>`,
  expired: html`<span class="chip sm plain"${tipAttr("O agente não buscou o pedido em 3 minutos: máquina desligada ou agente antigo, sem esta função.")}>${icon("minus-circle", 13)}Expirado</span>`,
  cancelled: html`<span class="chip sm plain">${icon("x-circle", 13)}Cancelado</span>`,
}[t.status]);

async function bindSpeed(d, body, m) {
  const id = encodeURIComponent(d.agentId);
  const feats = await api("/api/features").catch(() => ({ speedtestInternet: false }));
  const canRun = can("operator");
  let list = [], target = "server";

  const paint = () => {
    const active = list.find(t => t.status === "pending" || t.status === "running");
    const last = list.find(t => t.status === "done");
    const r = last?.result;
    body.innerHTML = String(html`<div class="stack" style="gap:16px">
      <section class="card"><div class="card-h"><div><div class="card-title">Teste de velocidade</div>
        <div class="card-sub">Usa até ~150 MB em cada sentido e leva cerca de 15 segundos. O agente busca o pedido a cada 30 segundos.</div></div></div>
        <div class="card-b">${canRun ? html`<div class="toolbar" style="margin:0 0 10px">
          <select class="field" id="sp-target" aria-label="Destino do teste" ${active ? "disabled" : ""}>
            <option value="server" ${target === "server" ? "selected" : ""}>${SPEED_TARGET.server}</option>
            ${feats.speedtestInternet && html`<option value="internet" ${target === "internet" ? "selected" : ""}>${SPEED_TARGET.internet}</option>`}</select>
          <button class="btn primary" id="sp-go" type="button" ${active ? "disabled" : ""}>${icon("gauge", 17)}Testar velocidade</button></div>
          <div class="muted" id="sp-note">${SPEED_NOTE[target]}</div>`
          : html`<div class="muted">Seu perfil só consulta. Operadores e administradores podem iniciar um teste.</div>`}
          ${active && html`<div class="callout" style="margin-top:14px">${icon("activity", 18)}<div><b>${active.status === "running" ? "Medindo agora…" : "Pedido enviado, aguardando o agente."}</b>
            ${active.status === "pending" && !m.online ? " Esta máquina está offline: o teste roda se ela voltar em até 3 minutos." : ""}
            <div class="muted">${SPEED_TARGET[active.target]} · pedido por ${active.requestedBy ?? "—"} ${fmt.ago(active.requestedAt)}${active.status === "pending" ? " · expira se o agente não buscar em 3 minutos" : ""}</div></div>
            ${canRun && html`<button class="btn sm" id="sp-cancel" type="button" style="margin-left:auto;align-self:center">${icon("x", 15)}Cancelar</button>`}</div>`}</div></section>

      ${r ? html`<div class="tilegrid">
          ${tile({ iconName: "download", label: "Download", value: mbps(r.downMbps), cap: `${(r.bytesDown / 1e6).toFixed(0)} MB recebidos`, tone: "info" })}
          ${tile({ iconName: "upload", label: "Upload", value: mbps(r.upMbps), cap: `${(r.bytesUp / 1e6).toFixed(0)} MB enviados`, tone: "info" })}
          ${tile({ iconName: "clock", label: "Latência", value: `${r.latencyMs} ms`, cap: `variação de ${r.jitterMs} ms`, tone: "info" })}</div>
        <div class="muted">Último teste concluído: ${SPEED_TARGET[last.target]} · ${fmt.dt(last.completedAt)} (${fmt.ago(last.completedAt)}) · pedido por ${last.requestedBy ?? "—"}</div>`
        : !active && emptyState("gauge", "Nenhum teste concluído ainda", canRun ? "Escolha o destino e clique em “Testar velocidade”." : "Quando um operador rodar um teste, o resultado aparece aqui.")}

      ${list.length > 0 && html`<section class="card"><div class="card-h"><div><div class="card-title">Histórico</div><div class="card-sub">Últimos ${list.length} testes</div></div></div>
        <div class="card-b flush"><div class="tbl-wrap"><table class="tbl compact"><thead><tr><th>Quando</th><th>Destino</th><th>Download</th><th>Upload</th><th>Latência</th><th>Pedido por</th><th>Situação</th></tr></thead><tbody>
        ${list.map(t => html`<tr><td class="nowrap">${fmt.dt(t.requestedAt)}</td><td>${SPEED_TARGET[t.target] ?? t.target}</td>
          <td class="nowrap">${t.result ? mbps(t.result.downMbps) : "—"}</td><td class="nowrap">${t.result ? mbps(t.result.upMbps) : "—"}</td><td class="nowrap">${t.result ? `${t.result.latencyMs} ms` : "—"}</td>
          <td>${t.requestedBy ?? "—"}</td><td>${speedStatus(t)}</td></tr>`)}</tbody></table></div></div></section>`}
    </div>`);

    const sel = $("#sp-target", body);
    if (sel) sel.onchange = () => { target = sel.value; $("#sp-note", body).textContent = SPEED_NOTE[target]; };
    const cancel = $("#sp-cancel", body);
    if (cancel) cancel.onclick = async () => {
      cancel.disabled = true;
      try { await api(`/api/machines/${id}/speedtest/cancel`, { method: "POST" }); toast("Teste cancelado."); }
      catch (e) { toast(e.message ?? "Não foi possível cancelar."); }
      await load();
    };
    const go = $("#sp-go", body);
    if (go) go.onclick = async () => {
      go.disabled = true;
      try { await api(`/api/machines/${id}/speedtest`, { method: "POST", body: JSON.stringify({ target }) }); toast("Pedido enviado. O agente executa em até 30 segundos."); }
      catch (e) { toast(e.message ?? "Não foi possível pedir o teste."); }
      await load();
    };
  };

  // Enquanto há teste em andamento e esta aba continua aberta, confere o andamento a cada 3 s
  const load = async () => {
    if (body.dataset.view !== "speed" || !body.isConnected) return;
    try { list = await api(`/api/machines/${id}/speedtests?limit=10`); } catch { return; }
    paint();
    if (list.some(t => t.status === "pending" || t.status === "running")) setTimeout(load, 3000);
  };
  await load();
}

// ---------- Gaveta ----------
export async function openDevice(id, onClose) {
  let d, assigns, events;
  try {
    [d, assigns, events] = await Promise.all([api(`/api/machines/${encodeURIComponent(id)}`), api(`/api/machines/${encodeURIComponent(id)}/users`), api(`/api/machines/${encodeURIComponent(id)}/events?limit=30`)]);
  } catch {
    toast("Dispositivo não encontrado (pode ter sido removido).");
    onClose?.();
    return;
  }
  // agentes antigos não mandam estes campos
  d.peripherals ??= []; d.tools ??= []; d.networkActivity ??= { listening: [], connections: [] }; d.issues ??= []; d.network ??= [];
  const m = byId(id) ?? { ...d, hostname: d.identity.hostname, loggedUser: d.identity.loggedUser, model: d.hardware.model, online: false };
  const panel = drawer.show(html`
    <div class="drawer-h">
      ${devIcon({ hostname: d.identity.hostname, model: d.hardware.model }, "lg")}
      <div class="grow"><h2>${d.identity.hostname}</h2>
        <div class="chips" style="margin:6px 0 2px">${stateChip(m)}${scorePill(d.healthScore, { label: true })}</div>
        <div class="muted">${[d.hardware.manufacturer, d.hardware.model].filter(Boolean).join(" ")}${d.hardware.serialNumber ? ` · Serial ${d.hardware.serialNumber}` : ""}</div></div>
      <div style="display:flex;gap:8px;align-items:center;flex-wrap:wrap">
        <select class="field sm" id="dv-status" aria-label="Status do dispositivo" ${can("operator") ? "" : "disabled"}>${Object.entries(STATUS_LABEL).map(([k, v]) => html`<option value="${k}" ${d.status === k ? "selected" : ""}>${v}</option>`)}</select>
        ${can("admin") && html`<button class="btn sm danger" id="dv-del" type="button">${icon("trash", 15)}<span>Remover</span></button>`}
        <button class="btn sm ghost" id="dv-close" type="button" aria-label="Fechar">${icon("x", 18)}</button></div></div>
    <div class="tabs" role="tablist">${TABS.map(([k, l, ic], i) => html`<button class="tab ${i ? "" : "on"}" role="tab" data-tab="${k}">${icon(ic, 16)}${l}</button>`)}</div>
    <div class="drawer-b" id="dv-body"></div>`, onClose);

  const body = $("#dv-body", panel);
  const show = async tab => {
    $$(".tab", panel).forEach(t => t.classList.toggle("on", t.dataset.tab === tab));
    body.scrollTop = 0;
    body.dataset.view = tab;   // a aba Velocidade confere isto para parar de consultar quando você sai dela
    if (tab === "performance") return bindPerformance(d, body);
    if (tab === "speed") return bindSpeed(d, body, m);
    const content = { summary: () => tabSummary(d, m), security: () => tabSecurity(d), hardware: () => tabHardware(d), network: () => tabNetwork(d), software: () => tabSoftware(d),
      peripherals: () => tabPeripherals(d), activity: () => tabActivity(d, assigns, events) }[tab]();
    body.innerHTML = content.s;
    if (tab === "software") bindSoftware(d, body);
  };
  $(".tabs", panel).addEventListener("click", e => { const t = e.target.closest(".tab"); if (t) show(t.dataset.tab); });
  $("#dv-close", panel).onclick = () => drawer.close();
  $("#dv-status", panel).onchange = async e => {
    try { await api(`/api/machines/${encodeURIComponent(id)}/status`, { method: "PUT", body: JSON.stringify({ status: e.target.value }) }); toast("Status atualizado."); refresh(); }
    catch { toast("Não foi possível alterar o status."); }
  };
  const del = $("#dv-del", panel);
  if (del) del.onclick = async () => {
    if (!del.classList.contains("armed")) { del.classList.add("armed"); del.querySelector("span").textContent = "Confirmar remoção?"; setTimeout(() => { del.classList.remove("armed"); del.querySelector("span").textContent = "Remover"; }, 4000); return; }
    try { await api(`/api/machines/${encodeURIComponent(id)}`, { method: "DELETE" }); toast("Dispositivo removido. O histórico dele continua na auditoria."); drawer.close(); refresh(); }
    catch { toast("Não foi possível remover."); }
  };
  show("summary");
}
