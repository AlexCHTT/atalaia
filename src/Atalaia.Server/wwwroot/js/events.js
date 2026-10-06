// Apresentação dos eventos de auditoria (usada na Auditoria, no feed do dashboard e nas gavetas).
import { html, EVENT_LABEL, EVENT_ICON, FIELD_LABEL, evVal, fmt } from "./core.js";
import { icon } from "./icons.js";
import { tipAttr } from "./ui.js";

export const eventTone = e => e.severity === "warn" ? "warning" : e.type === "registered" || e.type === "online" ? "good" : "info";

export function eventDetail(e) {
  const field = FIELD_LABEL[e.field] ?? e.field;
  switch (e.type) {
    case "changed": case "status_changed":
      return html`<b>${field}</b>: <span class="muted">${evVal(e.oldValue)}</span> → ${evVal(e.newValue)}`;
    case "user_changed": return html`${e.oldValue} → <b>${e.newValue}</b>`;
    case "software_installed": return html`<b>${e.field}</b> <span class="muted">${e.newValue}</span>`;
    case "software_removed": return html`<b>${e.field}</b> <span class="muted">${e.oldValue}</span>`;
    case "software_updated": return html`<b>${e.field}</b> <span class="muted">${e.oldValue} → ${e.newValue}</span>`;
    case "peripheral_connected": return html`<b>${e.field}</b> ${e.newValue}`;
    case "peripheral_removed": return html`<b>${e.field}</b> <span class="muted">${e.oldValue}</span>`;
    case "registered": return html`<span class="muted">Primeira vez vista pelo painel</span>`;
    default: return html`<span class="muted">${e.newValue ?? ""}</span>`;
  }
}

// Item de feed (dashboard, gavetas)
export function feedItem(e, { host = true } = {}) {
  const tone = eventTone(e);
  return html`<div class="feed-item tone-${tone}">
    <span class="feed-ic">${icon(e.severity === "warn" ? "alert-triangle" : EVENT_ICON[e.type] ?? "activity", 17)}</span>
    <div><div class="feed-t">${EVENT_LABEL[e.type] ?? e.type}${host && e.hostname && html` <span class="muted" style="font-weight:500">· ${e.hostname}</span>`}</div>
      <div class="feed-d">${eventDetail(e)}</div></div>
    <span class="feed-m"${tipAttr(fmt.dt(e.at))}>${fmt.ago(e.at)}</span></div>`;
}

// Linha de tabela (Auditoria)
export function eventRow(e) {
  const tone = eventTone(e);
  return html`<tr>
    <td class="nowrap"><div class="identity"><span class="feed-ic tone-${tone}">${icon(e.severity === "warn" ? "alert-triangle" : EVENT_ICON[e.type] ?? "activity", 16)}</span>
      <div class="t"><b>${fmt.dt(e.at)}</b><span>${fmt.ago(e.at)}</span></div></div></td>
    <td class="nowrap"><a href="#/devices/${encodeURIComponent(e.agentId)}"><b>${e.hostname ?? "—"}</b></a></td>
    <td class="nowrap">${e.user ? html`<a href="#/users/${encodeURIComponent(e.user)}">${e.user}</a>` : html`<span class="muted">—</span>`}</td>
    <td class="nowrap">${e.severity === "warn" ? html`<span class="chip sm tone-warning">${icon("alert-triangle", 13)}${EVENT_LABEL[e.type] ?? e.type}</span>` : html`<span class="chip sm plain">${EVENT_LABEL[e.type] ?? e.type}</span>`}</td>
    <td>${eventDetail(e)}</td></tr>`;
}
