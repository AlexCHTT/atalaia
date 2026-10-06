import { html, render, api, fmt, userInfo } from "../core.js";
import { icon } from "../icons.js";
import { active } from "../store.js";
import { meter } from "../charts.js";
import { devIcon, emptyState, avatar } from "../ui.js";
import { store } from "../store.js";

// Selo de duas letras: iniciais das palavras (ex.: "Microsoft Copilot" -> MC) ou as duas primeiras letras ("Claude" -> Cl)
const mark = name => {
  const w = name.replace(/\(.*?\)/g, "").trim().split(/\s+/);
  return w.length > 1 ? (w[0][0] + w[1][0]).toUpperCase() : w[0].slice(0, 2);
};

export const tools = {
  title: "Ferramentas",
  sub: "Ferramentas de interesse (ex.: assistentes de IA) detectadas nos dispositivos",

  async render(root) {
    const list = await api("/api/tools");
    const total = active().length;
    render(root, html`<div class="stack" style="gap:20px">
      <div class="callout">${icon("info", 18)}<div><b style="color:var(--ink)">Como a detecção funciona.</b> O agente procura uma lista de ferramentas (Claude, ChatGPT, Copilot, Cursor, Windsurf, Ollama, LM Studio e Perplexity)
        entre os processos em execução e os programas instalados. Quem usa só pelo navegador (por exemplo claude.ai ou chatgpt.com) <b style="color:var(--ink)">não</b> é detectado: isso só é possível no firewall ou no servidor DNS da empresa.</div></div>
      ${list.length ? list.map(t => {
        const running = t.hits.filter(h => h.source === "em execução").length;
        return html`<section class="card"><div class="toolcard-h"><span class="toolmark">${mark(t.tool)}</span>
          <div style="flex:1;min-width:0"><div class="card-title" style="font-size:16px">${t.tool}</div>
            <div class="card-sub">${fmt.plural(t.machines, "dispositivo", "dispositivos")} · ${total ? Math.round(100 * t.machines / total) : 0}% da frota · ${running} em uso agora</div></div>
          <div style="width:min(220px,30%)">${meter(total ? 100 * t.machines / total : 0, "info", `${t.machines} de ${total} dispositivos`)}</div></div>
          <div class="card-b flush"><div class="tbl-wrap"><table class="tbl compact"><thead><tr><th>Dispositivo</th><th>Usuário</th><th>Situação</th><th>Detalhe</th></tr></thead><tbody>
          ${t.hits.map(h => html`<tr class="click" data-href="#/devices/${encodeURIComponent(h.agentId)}"><td><div class="identity">${devIcon({ hostname: h.hostname })}<div class="t"><b>${h.hostname}</b></div></div></td>
            <td>${h.user ? html`<div class="identity">${avatar(h.user, "sm")}<div class="t"><b style="font-weight:550">${userInfo(h.user).name}</b></div></div>` : html`<span class="muted">—</span>`}</td>
            <td><span class="chip sm ${h.source === "em execução" ? "tone-good" : "tone-info"}">${icon(h.source === "em execução" ? "zap" : "package", 13)}${h.source === "em execução" ? "Em execução" : "Instalado"}</span></td>
            <td class="muted">${h.detail}</td></tr>`)}</tbody></table></div></div></section>`;
      }) : html`<div class="card">${emptyState("sparkles", "Nenhuma ferramenta detectada", "Nenhum dispositivo ativo tem as ferramentas da lista instaladas ou em execução.")}</div>`}
    </div>`);
  },
};
