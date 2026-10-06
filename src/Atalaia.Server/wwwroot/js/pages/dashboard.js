import { html, raw, api, fmt, level, render, ISSUE_LABEL, LEVEL_LABEL, SEV_LABEL, userInfo } from "../core.js";
import { icon } from "../icons.js";
import { store, active } from "../store.js";
import { hbars, hbarsTable, stackedBar, meter } from "../charts.js";
import { card, tile, scorePill, issueChips, stateChip, devIcon, avatar, emptyState, simpleTable } from "../ui.js";
import { feedItem } from "../events.js";

const ver = v => String(v || "0").split(".").map(n => parseInt(n, 10) || 0);
const cmpVer = (a, b) => { const x = ver(a), y = ver(b); for (let i = 0; i < Math.max(x.length, y.length); i++) { const d = (x[i] || 0) - (y[i] || 0); if (d) return d; } return 0; };
const group = (list, key) => {
  const m = new Map();
  for (const it of list) { const k = key(it) || "Desconhecido"; m.set(k, (m.get(k) || 0) + 1); }
  return [...m].map(([label, value]) => ({ label, value })).sort((a, b) => b.value - a.value || a.label.localeCompare(b.label));
};
const SEV_ORDER = { critical: 0, warning: 1, info: 2 };
const SEV_ICON = { critical: "x-circle", warning: "alert-triangle", info: "info" };

// Escudo grande do destaque: a cor e o símbolo mudam com o estado (nunca só a cor: o título e o número dizem o mesmo)
function emblem(lv) {
  const mark = lv === "good"
    ? '<path d="M41 72l17 17 31-34" stroke="#fff" stroke-width="9" stroke-linecap="round" stroke-linejoin="round" fill="none"/>'
    : lv === "warning"
      ? '<path d="M64 46v34" stroke="#0b1a33" stroke-width="9" stroke-linecap="round"/><circle cx="64" cy="99" r="5.5" fill="#0b1a33"/>'
      : '<path d="M64 46v34" stroke="#fff" stroke-width="9" stroke-linecap="round"/><circle cx="64" cy="99" r="5.5" fill="#fff"/>';
  return html`<svg viewBox="0 0 128 140" aria-hidden="true"><defs><linearGradient id="emb" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" style="stop-color:color-mix(in srgb, var(--tone) 82%, #fff)"/><stop offset="1" style="stop-color:color-mix(in srgb, var(--tone) 78%, #000)"/></linearGradient></defs>
    <path d="M64 4 116 22v48c0 34-22 56-52 66C34 126 12 104 12 70V22L64 4Z" fill="url(#emb)"/>
    <path d="M64 12 108 27v43c0 29-18 48-44 57-26-9-44-28-44-57V27L64 12Z" fill="none" stroke="#fff" stroke-opacity=".28" stroke-width="2"/>${raw(mark)}</svg>`;
}

export const dashboard = {
  title: "Dashboard",
  sub: "Visão geral da segurança e da disponibilidade da frota",

  async render(root) {
    const all = store.machines, act = active();
    if (!all.length) {
      return render(root, html`<div class="card">${emptyState("laptop", "Nenhum dispositivo ainda", "Instale o agente nos computadores pela tela Instalação (menu lateral) e eles aparecem aqui em até 5 minutos.")}</div>`);
    }
    const [tools, alerts] = await Promise.all([api("/api/tools"), api("/api/events?severity=warn&limit=7")]);

    // ----- saúde da frota -----
    const scored = act.filter(m => m.healthScore != null);
    const avg = scored.length ? Math.round(scored.reduce((a, m) => a + m.healthScore, 0) / scored.length) : null;
    const nGood = scored.filter(m => m.healthScore >= 90).length, nWarn = scored.filter(m => m.healthScore >= 70 && m.healthScore < 90).length, nCrit = scored.filter(m => m.healthScore < 70).length;
    let lv = level(avg); if (lv === "good" && nCrit > 0) lv = "warning";
    const headline = { good: "Frota protegida", warning: "Atenção necessária", critical: "Frota em risco", unknown: "Aguardando dados" }[lv];
    const summary = scored.length
      ? `${nCrit ? `${fmt.plural(nCrit, "dispositivo está", "dispositivos estão")} em risco, ` : ""}${fmt.plural(nWarn, "pede", "pedem")} atenção e ${fmt.plural(nGood, "está protegido", "estão protegidos")}, de ${fmt.plural(act.length, "ativo", "ativos")}.`
      : "Os índices aparecem após o primeiro check-in de cada agente.";

    // ----- disponibilidade -----
    const online = act.filter(m => m.online), offline = act.filter(m => !m.online);
    const offLong = offline.filter(m => Date.now() - new Date(m.lastSeen) > 7 * 864e5);
    const inactive = all.length - act.length;

    // ----- problemas mais frequentes -----
    const byCode = new Map();
    for (const m of act) for (const i of m.issues ?? []) {
      const e = byCode.get(i.code) ?? { code: i.code, severity: i.severity, count: 0 }; e.count++; byCode.set(i.code, e);
    }
    const issues = [...byCode.values()].sort((a, b) => SEV_ORDER[a.severity] - SEV_ORDER[b.severity] || b.count - a.count).slice(0, 8)
      .map(i => ({ label: ISSUE_LABEL[i.code] ?? i.code, value: i.count, tone: { critical: "crit", warning: "warn", info: "info" }[i.severity], iconName: SEV_ICON[i.severity], href: `#/devices?issue=${i.code}` }));

    // ----- precisam de atenção -----
    const needs = [...scored].sort((a, b) => a.healthScore - b.healthScore || (a.hostname || "").localeCompare(b.hostname || "")).filter(m => m.healthScore < 90).slice(0, 8);

    // ----- distribuições -----
    const os = group(act, m => (m.os || "").replace(/^Microsoft\s+/i, ""));
    const makers = group(act, m => m.manufacturer).slice(0, 6);
    const latest = act.map(m => m.agentVersion).filter(Boolean).sort(cmpVer).pop();
    const versions = group(act, m => m.agentVersion).sort((a, b) => cmpVer(b.label, a.label))
      .map(v => ({ ...v, tone: v.label === latest ? "" : "dim", sub: v.label === latest ? "mais recente" : "atualizar" }));
    const toolBars = tools.map(t => ({ label: t.tool, value: t.machines, sub: `${t.hits.filter(h => h.source === "em execução").length} em uso agora`, href: "#/tools" }));

    const issuesTable = simpleTable(["Problema", "Gravidade", "Dispositivos"], [...byCode.values()].sort((a, b) => b.count - a.count).map(i => [ISSUE_LABEL[i.code] ?? i.code, SEV_LABEL[i.severity], i.count]));

    render(root, html`<div class="grid g12">
      <section class="card hero span-7 tone-${lv}">
        <div class="emblem">${emblem(lv)}</div>
        <div>
          <div class="eyebrow">Postura de segurança da frota</div>
          <h2 class="hero-title">${headline}</h2>
          <p class="hero-sub">${summary}</p>
          <div class="hero-figure"><span class="hero-num">${avg ?? "—"}</span><span class="hero-den">/ 100</span><span class="hero-lbl">índice de saúde médio</span></div>
          ${meter(avg ?? 0, lv === "good" ? "good" : lv === "warning" ? "warning" : "critical", `Índice médio: ${avg ?? "—"} de 100`)}
          <div style="margin-top:18px">${stackedBar([
            { label: "Protegidos", value: nGood, tone: "good", iconName: "check-circle" },
            { label: "Atenção", value: nWarn, tone: "warning", iconName: "alert-triangle" },
            { label: "Em risco", value: nCrit, tone: "critical", iconName: "x-circle" }])}</div>
          <p class="help">O índice reúne antivírus, firewall, criptografia, espaço de disco, atualizações pendentes e portas de acesso remoto abertas. Mede a higiene da configuração, não é um detector de vírus.</p>
        </div>
      </section>

      <div class="span-5 grid" style="grid-template-columns:repeat(2,minmax(0,1fr));grid-auto-rows:1fr">
        ${tile({ iconName: "laptop", label: "Dispositivos ativos", value: fmt.num(act.length), cap: inactive ? `${fmt.plural(inactive, "aposentado ou em estoque", "aposentados ou em estoque")}` : "Todos em operação", tone: "info", href: "#/devices" })}
        ${tile({ iconName: "wifi", label: "Online agora", value: fmt.num(online.length), cap: `${act.length ? Math.round(100 * online.length / act.length) : 0}% dos ativos`, tone: "good", href: "#/devices?filter=online" })}
        ${tile({ iconName: "bell", label: "Alertas em 24 h", value: fmt.num(store.alerts24h), cap: store.alerts24h ? "Ver na auditoria" : "Nenhum alerta", tone: store.alerts24h ? "warning" : "good", href: "#/audit?severity=warn&period=1" })}
        ${tile({ iconName: "wifi-off", label: "Offline há +7 dias", value: fmt.num(offLong.length), cap: offLong.length ? "Reveja ou aposente" : "Nenhum parado", tone: offLong.length ? "serious" : "good", href: "#/devices?filter=offline" })}
      </div>

      ${card({ cls: "span-5", title: "Principais problemas", sub: "Dispositivos afetados, por tipo. Clique para listar.", body: issues.length ? hbars(issues, { total: act.length }) : emptyState("shield-check", "Nenhum problema", "Todas as verificações estão em ordem."), table: issuesTable })}

      <section class="card span-7">
        <div class="card-h"><div><div class="card-title">Precisam de atenção</div><div class="card-sub">Menores índices de saúde entre os dispositivos ativos</div></div>
          <div class="card-act"><a class="btn ghost sm" href="#/devices?filter=risk">Ver todos${icon("arrow-right", 15)}</a></div></div>
        <div class="card-b flush">${needs.length ? html`<div class="tbl-wrap"><table class="tbl"><thead><tr><th>Dispositivo</th><th>Saúde</th><th>Problemas</th></tr></thead><tbody>
          ${needs.map(m => html`<tr class="click" data-href="#/devices/${encodeURIComponent(m.agentId)}">
            <td class="dev"><div class="identity">${devIcon(m)}<div class="t"><b>${m.hostname}</b><span>${userInfo(m.loggedUser).name === "—" ? "Sem usuário" : userInfo(m.loggedUser).name}</span></div></div></td>
            <td>${scorePill(m.healthScore)}</td><td>${issueChips(m.issues, 1)}</td></tr>`)}</tbody></table></div>`
          : emptyState("shield-check", "Tudo certo", "Nenhum dispositivo abaixo de 90 pontos.")}</div></section>

      ${card({ cls: "span-4", title: "Disponibilidade", sub: "Estado de conexão dos dispositivos", body: html`${stackedBar([
        { label: "Online", value: online.length, tone: "good", iconName: "wifi" },
        { label: "Offline", value: offline.length - offLong.length, tone: "off", iconName: "wifi-off" },
        { label: "Offline há +7 dias", value: offLong.length, tone: "serious", iconName: "wifi-off" },
        { label: "Aposentados / estoque", value: inactive, tone: "info", iconName: "box" }])}
        <p class="muted" style="margin-top:14px;font-size:12.5px">Uma máquina vira offline após 20 minutos sem check-in. O agente envia dados a cada 5 minutos.</p>`,
      table: simpleTable(["Estado", "Dispositivos"], [["Online", online.length], ["Offline", offline.length - offLong.length], ["Offline há +7 dias", offLong.length], ["Aposentados / estoque", inactive]]) })}

      ${card({ cls: "span-4", title: "Sistemas operacionais", sub: "Dispositivos ativos", body: hbars(os, { total: act.length }), table: hbarsTable(os, ["Sistema", "Dispositivos"]) })}

      ${card({ cls: "span-4", title: "Versões do agente", sub: latest ? `Mais recente em uso: ${latest}` : "", body: hbars(versions, { total: act.length }), table: hbarsTable(versions, ["Versão", "Dispositivos"]) })}

      <section class="card span-5">
        <div class="card-h"><div><div class="card-title">Atividade recente</div><div class="card-sub">Alertas da auditoria</div></div>
          <div class="card-act"><a class="btn ghost sm" href="#/audit">Ver auditoria${icon("arrow-right", 15)}</a></div></div>
        <div class="card-b">${alerts.length ? html`<div class="feed">${alerts.map(e => feedItem(e))}</div>` : emptyState("check-circle", "Sem alertas", "Nada de errado foi registrado.")}</div></section>

      ${card({ cls: "span-4", title: "Ferramentas de IA", sub: "Instaladas ou em uso nos dispositivos", body: toolBars.length ? hbars(toolBars, { total: act.length }) : emptyState("sparkles", "Nenhuma detectada", "Das ferramentas da lista de interesse."), table: hbarsTable(toolBars, ["Ferramenta", "Dispositivos"]) })}

      ${card({ cls: "span-3", title: "Fabricantes", sub: "Dispositivos ativos", body: hbars(makers, { total: act.length }), table: hbarsTable(makers, ["Fabricante", "Dispositivos"]) })}
    </div>`);
  },
};
