import { html, api, qs, $, fmt, userInfo } from "../core.js";
import { icon } from "../icons.js";
import { byId, byHostname } from "../store.js";
import { drawer, avatar, scorePill, stateChip, issueChips, devIcon, tile, emptyState, toast } from "../ui.js";
import { feedItem } from "../events.js";

const PAGE = 20;

export async function openUser(name, onClose) {
  let hist, events, warns;
  try {
    [hist, events, warns] = await Promise.all([
      api("/api/user-history?" + qs({ name })),
      api("/api/events?" + qs({ user: name, limit: PAGE })),
      api("/api/events?" + qs({ user: name, severity: "warn", limit: 1000 })),
    ]);
  } catch {
    toast("Não foi possível carregar o perfil.");
    onClose?.();
    return;
  }
  const info = userInfo(name);
  const pcs = new Map();
  for (const a of hist) if (!pcs.has(a.agentId)) pcs.set(a.agentId, a);
  const current = hist.find(a => a.current);
  const m = current ? byId(current.agentId) : null;
  const first = hist.length ? hist[hist.length - 1].firstSeen : null, last = hist.length ? hist[0].lastSeen : null;

  // ferramentas de IA no PC atual (uma consulta; ignora falha)
  let tools = [];
  if (current) { try { tools = (await api(`/api/machines/${encodeURIComponent(current.agentId)}`)).tools ?? []; } catch { /* PC removido */ } }

  const panel = drawer.show(html`
    <div class="drawer-h">
      ${avatar(name, "lg")}
      <div class="grow"><h2>${info.name}</h2><div class="muted">${info.domain ? info.domain + "\\" : ""}${info.login}</div>
        <div class="chips" style="margin-top:8px">${current ? html`<span class="chip sm tone-good">${icon("laptop", 13)}Em uso agora: ${current.hostname}</span>` : html`<span class="chip sm plain">Sem PC atual</span>`}</div></div>
      <button class="btn sm ghost" id="us-close" type="button" aria-label="Fechar">${icon("x", 18)}</button></div>
    <div class="drawer-b"><div class="grid g12">
      <div class="span-12 tilegrid">
        ${tile({ iconName: "laptop", label: "Dispositivos usados", value: pcs.size, cap: pcs.size === 1 ? "1 PC no histórico" : "PCs no histórico", tone: "info" })}
        ${tile({ iconName: "clock", label: "Primeiro registro", value: fmt.date(first), cap: first ? fmt.ago(first) : "—", tone: "info" })}
        ${tile({ iconName: "activity", label: "Último registro", value: fmt.date(last), cap: last ? fmt.ago(last) : "—", tone: "info" })}
        ${tile({ iconName: "bell", label: "Alertas ligados", value: warns.length, cap: warns.length ? "eventos de atenção" : "Nenhum alerta", tone: warns.length ? "warning" : "good" })}
      </div>

      <section class="card span-12"><div class="card-h"><div><div class="card-title">Dispositivo atual</div></div></div><div class="card-b">
        ${m ? html`<div style="display:flex;align-items:center;gap:16px;flex-wrap:wrap"><div class="identity" style="flex:1;min-width:220px">${devIcon(m, "lg")}<div class="t"><b style="font-size:16px">${m.hostname}</b><span>${[m.manufacturer, m.model].filter(Boolean).join(" ")}</span></div></div>
            ${stateChip(m)}${scorePill(m.healthScore, { label: true })}<a class="btn sm" href="#/devices/${encodeURIComponent(m.agentId)}">Abrir dispositivo${icon("arrow-right", 15)}</a></div>
          <div style="margin-top:14px">${issueChips(m.issues, 4)}</div>
          ${tools.length ? html`<div class="chips" style="margin-top:14px">${tools.map(t => html`<span class="chip sm tone-info">${icon("sparkles", 13)}${t.tool}</span>`)}</div>` : ""}`
          : html`<span class="muted">${current ? `${current.hostname} não está mais no painel (removido).` : "Este usuário não está no console de nenhum PC no momento."}</span>`}
      </div></section>

      <section class="card span-5"><div class="card-h"><div><div class="card-title">Histórico de dispositivos</div><div class="card-sub">Do mais recente ao mais antigo</div></div></div><div class="card-b">
        ${hist.length ? html`<div class="timeline">${hist.map(a => { const pc = byId(a.agentId);
          return html`<div class="tl-item ${a.current ? "current" : ""}"><div class="identity"><div class="t"><b>${pc ? html`<a href="#/devices/${encodeURIComponent(a.agentId)}">${a.hostname}</a>` : html`${a.hostname} <span class="muted" style="font-weight:500">(removido)</span>`}
            ${a.current ? html` <span class="chip sm tone-good" style="margin-left:6px">Atual</span>` : html` <span class="chip sm plain" style="margin-left:6px">Anterior</span>`}</b>
            <span>${fmt.dt(a.firstSeen)} → ${a.current ? "agora" : fmt.dt(a.lastSeen)}</span></div></div></div>`; })}</div>` : html`<span class="muted">Sem histórico.</span>`}
      </div></section>

      <section class="card span-7"><div class="card-h"><div><div class="card-title">Atividade</div><div class="card-sub">Eventos ocorridos enquanto o usuário usava os PCs</div></div>
        <div class="card-act"><a class="btn ghost sm" href="#/audit?user=${encodeURIComponent(name)}">Ver na auditoria</a></div></div>
        <div class="card-b"><div class="feed" id="us-feed"></div><button class="btn sm" id="us-more" type="button" style="margin-top:12px" hidden>Carregar mais</button><div id="us-empty"></div></div></section>
    </div></div>`, onClose);

  // atividade com "carregar mais" (20 por vez)
  let rows = events, done = events.length < PAGE;
  const draw = () => {
    $("#us-feed", panel).innerHTML = String(html`${rows.map(e => feedItem(e))}`);
    $("#us-more", panel).hidden = done;
    $("#us-empty", panel).innerHTML = rows.length ? "" : String(html`<span class="muted">Nenhum evento ligado a este usuário ainda.</span>`);
  };
  $("#us-more", panel).onclick = async () => {
    const page = await api("/api/events?" + qs({ user: name, limit: PAGE, before: rows[rows.length - 1].id }));
    rows = rows.concat(page); done = page.length < PAGE; draw();
  };
  $("#us-close", panel).onclick = () => drawer.close();
  draw();
}
