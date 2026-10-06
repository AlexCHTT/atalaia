import { html, render, api, $, $$, fmt, loadPublicInfo, applyBrand } from "../core.js";
import { icon } from "../icons.js";
import { modal, confirmDialog, toast, emptyState, tipAttr } from "../ui.js";

// Tela Configurações (só administrador). Os valores ficam no servidor e valem na hora, sem reiniciar.
// A tela só mostra e envia: a validação e a confirmação de apagar dados são do servidor (aqui é conforto).
let data = { groups: [], items: [], system: {} };
let storage = null;
const draft = new Map();       // chave -> valor digitado, só do que difere do atual
const resets = new Set();      // chaves marcadas para "restaurar o padrão"
const errors = new Map();      // chave -> mensagem

const SOURCE = {
  default: ["Padrão", "plain", "É o valor de fábrica. Nada foi definido para este parâmetro."],
  config: ["Do servidor", "info", "Veio de uma variável de ambiente ou arquivo de configuração do servidor. Ao salvar aqui, o valor desta tela passa a valer."],
  db: ["Definido aqui", "good", "Foi definido nesta tela."],
};
const item = key => data.items.find(i => i.key === key);
const shown = i => resets.has(i.key) ? i.fallback : draft.has(i.key) ? draft.get(i.key) : i.value;
const dirty = i => resets.has(i.key) ? i.fallback !== i.value || i.source === "db" : draft.has(i.key) && draft.get(i.key) !== i.value;

function validate(i, raw) {
  if (i.kind === "bool") return null;
  if (i.kind === "text") return raw.length > i.max ? `No máximo ${i.max} caracteres.` : null;
  if (!/^\d+$/.test(raw.trim())) return "Informe um número inteiro.";
  const n = Number(raw);
  return n < i.min || n > i.max ? `Use um valor entre ${fmt.num(i.min)} e ${fmt.num(i.max)}.` : null;
}

// ---------- Uso de disco ----------
function storageCard() {
  const s = storage;
  if (!s) return emptyState("activity", "Carregando…", "");
  const used = s.dbBytes + s.walBytes;
  const pctDisk = s.diskTotal ? 100 * used / s.diskTotal : null;
  const maxBytes = Math.max(1, ...s.tables.map(t => t.bytes ?? 0));
  const growth = s.growth.filter(g => g.bytesPerMonth != null);
  return html`<div class="card-b stack" style="gap:16px">
    ${s.overLimit && html`<div class="callout warn">${icon("alert-triangle", 18)}<div><b>O banco passou do limite de aviso.</b> Ele ocupa ${fmt.bytes(used)} e o limite definido é ${fmt.bytes(s.alertBytes)}. Reduza os prazos de retenção abaixo ou libere espaço.</div></div>`}
    <div class="tilegrid">
      <div class="card tile"><div class="tile-top"><span class="tile-ic tone-info">${icon("server", 18)}</span><span class="tile-label">Banco de dados</span></div>
        <div class="tile-foot"><div class="tile-value num">${fmt.bytes(used)}</div><div class="tile-cap">${s.walBytes ? `inclui ${fmt.bytes(s.walBytes)} de log de gravação` : "arquivo do banco"}</div></div></div>
      <div class="card tile"><div class="tile-top"><span class="tile-ic tone-${s.diskFree != null && s.diskTotal && s.diskFree / s.diskTotal < 0.1 ? "critical" : "good"}">${icon("disk", 18)}</span><span class="tile-label">Espaço livre no disco</span></div>
        <div class="tile-foot"><div class="tile-value num">${s.diskFree != null ? fmt.bytes(s.diskFree) : "—"}</div><div class="tile-cap">${s.diskTotal ? `de ${fmt.bytes(s.diskTotal)}${pctDisk != null ? ` · o banco usa ${pctDisk < 0.1 ? "menos de 0,1" : fmt.num(Math.round(pctDisk * 10) / 10)}%` : ""}` : "não informado"}</div></div></div>
      <div class="card tile"><div class="tile-top"><span class="tile-ic tone-info">${icon("activity", 18)}</span><span class="tile-label">Crescimento estimado</span></div>
        <div class="tile-foot"><div class="tile-value num">${growth.length ? "+" + fmt.bytes(growth.reduce((a, g) => a + g.bytesPerMonth, 0)) : "—"}</div><div class="tile-cap">${growth.length ? "por mês, no ritmo da última semana" : "ainda sem dados suficientes"}</div></div></div></div>

    ${growth.some(g => g.steadyStateBytes != null) && html`<div class="muted">${icon("clock", 14)} Com a retenção atual o banco tende a estabilizar: ${growth.filter(g => g.steadyStateBytes != null).map(g => html`<b>${g.label}</b> em ~${fmt.bytes(g.steadyStateBytes)} (${g.retentionDays} dias)`).reduce((a, b) => html`${a}; ${b}`)}.${growth.some(g => g.steadyStateBytes == null) ? " Sem prazo definido, " + growth.filter(g => g.steadyStateBytes == null).map(g => g.label.toLowerCase()).join(" e ") + " crescem sem parar." : ""}</div>`}

    <div class="tbl-wrap"><table class="tbl compact"><thead><tr><th>Tabela</th><th class="num">Registros</th><th>Tamanho</th><th>Período dos dados</th></tr></thead><tbody>
      ${s.tables.map(t => html`<tr><td><b style="font-weight:600">${t.label}</b></td><td class="num">${fmt.num(t.rows)}</td>
        <td style="min-width:200px">${t.bytes != null ? html`<div style="display:flex;align-items:center;gap:10px"><div class="meter thin tone-info" style="flex:1"><i style="width:${Math.max(2, 100 * t.bytes / maxBytes)}%"></i></div><span class="muted nowrap">${s.sizesAreEstimates ? "~" : ""}${fmt.bytes(t.bytes)}</span></div>` : html`<span class="muted">—</span>`}</td>
        <td class="muted nowrap">${t.oldest ? `${fmt.date(t.oldest)} → ${fmt.date(t.newest)}` : "—"}</td></tr>`)}</tbody></table></div>
    ${s.sizesAreEstimates && html`<div class="muted">Os tamanhos por tabela são <b>estimativas</b>: a soma do conteúdo de cada tabela, sem índices nem espaço livre, por isso ficam um pouco abaixo do tamanho real do arquivo.</div>`}
    <div class="toolbar" style="margin:0"><button class="btn" id="st-purge" type="button">${icon("trash", 16)}Aplicar retenção agora</button>
      <button class="btn" id="st-health" type="button">${icon("refresh", 16)}Recalcular índices de saúde</button>
      <span class="muted">A retenção também roda sozinha a cada 6 horas.</span></div></div>`;
}

// ---------- Campos ----------
function control(i) {
  const v = shown(i);
  if (i.kind === "bool") return html`<label class="chk"><input type="checkbox" data-key="${i.key}" ${v === "true" ? "checked" : ""}><span>${v === "true" ? "Ligado" : "Desligado"}</span></label>`;
  if (i.kind === "text") return html`<input class="field" data-key="${i.key}" value="${v}" maxlength="${i.max}" autocomplete="off" placeholder="Ex.: Prefeitura de Exemplo">`;
  return html`<span class="set-num"><input class="field" type="number" inputmode="numeric" data-key="${i.key}" value="${v}" min="${i.min}" max="${i.max}" step="1" aria-label="${i.label}"><span class="muted">${i.unit ?? ""}</span></span>`;
}

function row(i) {
  const [label, tone, tip] = SOURCE[i.source];
  return html`<div class="set-row ${dirty(i) ? "dirty" : ""}" data-row="${i.key}">
    <div class="set-info"><b>${i.label}</b>${i.retention && html` <span class="chip sm tone-warning"${tipAttr("Reduzir este prazo apaga dados antigos. O servidor mostra o impacto e pede confirmação.")}>${icon("alert-triangle", 12)}Apaga dados</span>`}
      <div class="muted">${i.help}</div>
      <div class="set-meta"><span class="chip sm ${tone === "plain" ? "plain" : "tone-" + tone}"${tipAttr(tip)}>${label}</span>
        ${(i.source === "db" || resets.has(i.key)) && html`<button class="linkbtn" type="button" data-reset="${i.key}">Restaurar padrão${i.fallback !== "" ? ` (${i.kind === "bool" ? (i.fallback === "true" ? "ligado" : "desligado") : i.fallback}${i.unit ? " " + i.unit : ""})` : ""}</button>`}</div></div>
    <div class="set-ctl">${control(i)}<div class="field-err" data-err="${i.key}" role="alert">${errors.get(i.key) ?? ""}</div></div></div>`;
}

function groupCard(g) {
  return html`<section class="card" id="grp-${g.id}"><div class="card-h"><div><div class="card-title">${g.label}</div><div class="card-sub">${g.help}</div></div></div>
    <div class="card-b flush set-list">${data.items.filter(i => i.group === g.id).map(row)}</div></section>`;
}

function systemCard() {
  const s = data.system;
  return html`<section class="card"><div class="card-h"><div><div class="card-title">Sistema</div><div class="card-sub">Definidos na instalação do servidor: não mudam por esta tela.</div></div></div>
    <div class="card-b"><dl class="kv"><dt>Versão do painel</dt><dd>${s.serverVersion}</dd><dt>Agente embutido</dt><dd>${s.agentVersion ?? html`<span class="muted">não disponível</span>`}</dd>
      <dt>Computadores no inventário</dt><dd>${fmt.num(s.machines)}</dd><dt>Banco de dados</dt><dd class="mono">${s.dbPath}</dd>
      <dt>Proxies confiáveis</dt><dd>${s.trustedProxies ? html`<span class="mono">${s.trustedProxies}</span>` : html`<span class="muted">nenhum</span>`}</dd></dl></div></section>`;
}

// ---------- Barra de salvar ----------
const changed = () => data.items.filter(dirty);
function paintBar(root) {
  const n = changed().length, bad = errors.size > 0;
  const bar = $("#sv-bar", root);
  bar.hidden = n === 0;
  $("#sv-count", root).textContent = `${fmt.plural(n, "alteração não salva", "alterações não salvas")}${bad ? " · corrija os campos em vermelho" : ""}`;
  $("#sv-save", root).disabled = bad || n === 0;
}

function paintRow(root, key) {
  const i = item(key), el = $(`[data-row="${key}"]`, root);
  if (!el) return;
  el.classList.toggle("dirty", dirty(i));
  const err = $(`[data-err="${key}"]`, root); if (err) err.textContent = errors.get(key) ?? "";
  const lbl = $(".chk span", el); if (lbl && i.kind === "bool") lbl.textContent = shown(i) === "true" ? "Ligado" : "Desligado";
}

// ---------- Salvar ----------
async function impactDialog(impact) {
  return new Promise(resolve => {
    modal({
      title: "Esta alteração apaga dados", size: "sm",
      body: html`<p class="ink-2">Os dados abaixo estão fora do novo prazo e serão <b>apagados agora</b>. Isso não pode ser desfeito.</p>
        <ul class="impact">${impact.map(x => html`<li><b>${x.label}</b><div class="muted">${x.detail}</div></li>`)}</ul>`,
      actions: [{ label: "Cancelar", kind: "ghost", onClick: () => resolve(false) }, { label: "Apagar e salvar", kind: "danger", onClick: () => resolve(true) }],
    });
  });
}

async function save(root) {
  const changes = {};
  for (const i of changed()) changes[i.key] = resets.has(i.key) ? "" : draft.get(i.key);
  let confirm = false;
  for (;;) {
    try {
      const res = await api("/api/settings", { method: "PUT", body: JSON.stringify({ changes, confirm }) });
      data.items = res.items; draft.clear(); resets.clear(); errors.clear();
      const p = res.purged ? res.purged.events + res.purged.accessLog + res.purged.metrics + res.purged.speedTests : 0;
      toast(`Configurações salvas.${p ? ` ${fmt.plural(p, "registro apagado", "registros apagados")}.` : ""}${res.recalculated ? " Índices de saúde recalculados." : ""}`);
      await loadPublicInfo(); applyBrand();
      storage = await api("/api/system/storage");
      return paintAll(root);
    } catch (e) {
      if (e.status === 409 && e.data?.error === "confirmation_required") {
        if (!await impactDialog(e.data.impact)) return;
        confirm = true; continue;
      }
      if (e.status === 400 && e.data?.errors) {
        for (const [k, m] of Object.entries(e.data.errors)) errors.set(k, m);
        for (const k of Object.keys(e.data.errors)) paintRow(root, k);
        paintBar(root); toast("Corrija os campos em vermelho.");
        return;
      }
      toast(e.message ?? "Não foi possível salvar."); return;
    }
  }
}

// ---------- Tela ----------
function paintAll(root) {
  render($("#st-body", root), html`
    <section class="card"><div class="card-h"><div><div class="card-title">Uso de disco</div><div class="card-sub">Quanto o banco ocupa, onde, e para onde ele cresce.</div></div></div>${storageCard()}</section>
    ${data.groups.map(groupCard)}
    ${systemCard()}`);
  paintBar(root);
}

export const settings = {
  title: "Configurações",
  sub: "Retenção de dados, limites, segurança e aparência. As mudanças valem na hora, sem reiniciar",

  async render(root) {
    draft.clear(); resets.clear(); errors.clear();
    [data, storage] = await Promise.all([api("/api/settings"), api("/api/system/storage")]);
    render(root, html`<div id="st-body" class="stack" style="gap:20px"></div>
      <div class="savebar" id="sv-bar" hidden><span id="sv-count"></span><span class="grow"></span>
        <button class="btn ghost" id="sv-discard" type="button">Descartar</button><button class="btn primary" id="sv-save" type="button">${icon("check-circle", 16)}Salvar alterações</button></div>`);
    paintAll(root);

    root.oninput = root.onchange = e => {
      const el = e.target.closest("[data-key]"); if (!el) return;
      const i = item(el.dataset.key); resets.delete(i.key);
      const raw = i.kind === "bool" ? String(el.checked) : el.value;
      if (raw === i.value) draft.delete(i.key); else draft.set(i.key, raw);
      const err = validate(i, raw);
      err ? errors.set(i.key, err) : errors.delete(i.key);
      paintRow(root, i.key); paintBar(root);
    };

    root.onclick = async e => {
      const reset = e.target.closest("[data-reset]");
      if (reset) {   // marca para restaurar e mostra o valor que valerá
        const i = item(reset.dataset.reset); draft.delete(i.key); errors.delete(i.key); resets.add(i.key);
        const rowEl = $(`[data-row="${i.key}"]`, root); rowEl.outerHTML = String(row(i)); paintBar(root); return;
      }
      if (e.target.closest("#sv-discard")) { draft.clear(); resets.clear(); errors.clear(); return paintAll(root); }
      if (e.target.closest("#sv-save")) return save(root);
      if (e.target.closest("#st-purge")) {
        if (!await confirmDialog({ title: "Aplicar a retenção agora?", confirmLabel: "Apagar o que passou do prazo", danger: true,
          text: "Apaga agora os dados que estão fora dos prazos definidos nesta tela (auditoria, registro de acessos, métricas e testes de velocidade). Isso não pode ser desfeito. Prazos 0 guardam para sempre." })) return;
        try { const r = await api("/api/system/purge-now", { method: "POST" }); toast(`Retenção aplicada: ${fmt.plural(r.events + r.accessLog + r.metrics + r.speedTests, "registro removido", "registros removidos")}.`); storage = await api("/api/system/storage"); paintAll(root); }
        catch (err) { toast(err.message); }
        return;
      }
      if (e.target.closest("#st-health")) {
        try { const r = await api("/api/system/recalculate-health", { method: "POST" }); toast(`Índice de saúde recalculado em ${fmt.plural(r.recalculated, "dispositivo", "dispositivos")}.`); }
        catch (err) { toast(err.message); }
      }
    };
  },
};
