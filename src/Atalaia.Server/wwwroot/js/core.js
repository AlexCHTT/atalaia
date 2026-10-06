// Núcleo: template seguro, acesso à API, formatadores e vocabulário compartilhado.

// ---------- Template seguro ----------
// Tudo que é interpolado em html`...` é escapado, exceto outro html`...` ou raw(). Os dados vêm das máquinas
// (hostname, nome de software, usuário...) e são tratados como NÃO confiáveis: um agente comprometido não pode injetar HTML.
class Safe { constructor(s) { this.s = s; } toString() { return this.s; } }
const ESC = { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" };
export const esc = v => String(v ?? "").replace(/[&<>"']/g, c => ESC[c]);
export const raw = s => new Safe(String(s));
const part = v => v == null || v === false ? "" : v instanceof Safe ? v.s : Array.isArray(v) ? v.map(part).join("") : esc(v);
export function html(strings, ...vals) {
  let out = strings[0];
  for (let i = 0; i < vals.length; i++) out += part(vals[i]) + strings[i + 1];
  return new Safe(out);
}
export const render = (el, safe) => { el.innerHTML = safe instanceof Safe ? safe.s : esc(safe); return el; };
export const $ = (sel, root = document) => root.querySelector(sel);
export const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];
export const debounce = (fn, ms = 250) => { let t; return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); }; };

// ---------- API ----------
// Regra de senha e nome da empresa, definidos pelo administrador em Configurações. Públicos: as telas de login e de configuração
// inicial ainda não têm sessão.
export const policy = { minPassword: 10, company: "" };
export async function loadPublicInfo() {
  try {
    const r = await fetch("/api/public/info");
    if (r.ok) { const d = await r.json(); policy.minPassword = d.minPasswordLength ?? 10; policy.company = d.company ?? ""; }
  } catch { /* sem rede: segue com os padrões */ }
  return policy;
}
/** Mostra o nome da empresa no lugar do lema do produto e no título da aba; sem nome, volta ao texto original. */
let titleSuffix = "";
export function applyBrand(root = document) {
  for (const el of root.querySelectorAll(".brand-tag")) { el.dataset.orig ??= el.textContent; el.textContent = policy.company || el.dataset.orig; }
  if (titleSuffix && document.title.endsWith(titleSuffix)) document.title = document.title.slice(0, -titleSuffix.length);   // tira o nome anterior
  titleSuffix = policy.company ? ` · ${policy.company}` : "";
  document.title += titleSuffix;
}

export class ApiError extends Error {
  constructor(status, data) { super(data?.message || `HTTP ${status}`); this.status = status; this.code = data?.error; this.data = data; }
}
export async function api(path, opts = {}) {
  const method = (opts.method || "GET").toUpperCase();
  // Contra CSRF: o servidor exige este cabeçalho em tudo que altera dados (um site de fora não consegue enviá-lo)
  const csrf = method === "GET" || method === "HEAD" ? {} : { "X-Requested-With": "Atalaia" };
  const res = await fetch(path, { ...opts, headers: { "Content-Type": "application/json", ...csrf, ...(opts.headers || {}) } });
  let data = null;
  if (res.status !== 204) { const t = await res.text(); try { data = t ? JSON.parse(t) : null; } catch { data = t; } }
  if (res.status === 401) { location.href = "/login.html"; throw new ApiError(401, data); }   // sessão expirou ou foi encerrada
  if (res.status === 403 && data?.error === "password_change_required") window.dispatchEvent(new Event("password-change-required"));
  if (!res.ok) throw new ApiError(res.status, data);
  return data;
}
export const qs = obj => {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries(obj)) if (v !== undefined && v !== null && v !== "") p.set(k, v);
  return p.toString();
};

// ---------- Formatadores ----------
export const fmt = {
  num: n => n == null ? "—" : new Intl.NumberFormat("pt-BR").format(n),
  bytes(n) {
    if (n == null) return "—";
    const u = ["B", "KB", "MB", "GB", "TB"]; let i = 0;
    while (n >= 1024 && i < 4) { n /= 1024; i++; }
    return new Intl.NumberFormat("pt-BR", { maximumFractionDigits: i > 1 ? 1 : 0 }).format(n) + " " + u[i];
  },
  pct: (n, d = 0) => n == null ? "—" : new Intl.NumberFormat("pt-BR", { maximumFractionDigits: d }).format(n) + "%",
  ago(iso) {
    const s = (Date.now() - new Date(iso)) / 1000;
    if (s < 45) return "agora";
    if (s < 3600) return `há ${Math.round(s / 60)} min`;
    if (s < 86400) return `há ${Math.round(s / 3600)} h`;
    const d = Math.round(s / 86400);
    return d === 1 ? "ontem" : `há ${d} dias`;
  },
  dt: iso => iso ? new Date(iso).toLocaleString("pt-BR", { dateStyle: "short", timeStyle: "short" }) : "—",
  date: iso => iso ? new Date(iso).toLocaleDateString("pt-BR") : "—",
  time: iso => new Date(iso).toLocaleTimeString("pt-BR", { hour: "2-digit", minute: "2-digit" }),
  dur(s) {
    if (s == null) return "—";
    const d = Math.floor(s / 86400), h = Math.floor(s % 86400 / 3600), m = Math.floor(s % 3600 / 60);
    return d ? `${d}d ${h}h` : h ? `${h}h ${m}min` : `${m}min`;
  },
  plural: (n, one, many) => `${fmt.num(n)} ${n === 1 ? one : many}`,
};

// ---------- Usuários ----------
// "EMPRESA\ana.souza" -> { domain: "EMPRESA", login: "ana.souza", name: "Ana Souza", initials: "AS" }
export function userInfo(raw_) {
  const full = String(raw_ || "");
  const [domain, login] = full.includes("\\") ? full.split("\\") : ["", full];
  const words = (login || "").split(/[._\-\s]+/).filter(Boolean);
  const name = words.map(w => w[0].toUpperCase() + w.slice(1)).join(" ") || "—";
  const initials = (words.length > 1 ? words[0][0] + words[words.length - 1][0] : (words[0] || "?").slice(0, 2)).toUpperCase();
  return { full, domain, login, name, initials };
}
// Matiz estável por usuário (as 8 famílias de matiz da paleta do painel, usadas só como fundo suave do avatar)
const AVATAR_HUES = ["#2a78d6", "#eb6834", "#1baf7a", "#eda100", "#e87ba4", "#008300", "#4a3aa7", "#e34948"];
export function avatarHue(key) {
  let h = 0;
  for (const c of String(key)) h = (h * 31 + c.charCodeAt(0)) >>> 0;
  return AVATAR_HUES[h % AVATAR_HUES.length];
}

// ---------- Vocabulário ----------
export const level = score => score == null ? "unknown" : score >= 90 ? "good" : score >= 70 ? "warning" : "critical";
export const LEVEL_LABEL = { good: "Protegido", warning: "Atenção", critical: "Em risco", unknown: "Sem dados" };
export const STATUS_LABEL = { active: "Ativo", retired: "Aposentado", stock: "Em estoque" };
export const SEV_LABEL = { critical: "Crítico", warning: "Atenção", info: "Informativo" };

// Rótulos genéricos dos problemas (o texto detalhado, com números, vem do servidor em cada máquina)
export const ISSUE_LABEL = {
  av_off: "Antivírus desativado ou ausente", av_realtime_off: "Proteção em tempo real desligada", av_outdated: "Assinaturas do antivírus desatualizadas",
  firewall_off: "Firewall desligado", bitlocker_off: "Disco sem criptografia (BitLocker)", secureboot_off: "Secure Boot desativado", tpm_missing: "Sem TPM",
  not_activated: "Windows não ativado", pending_reboot: "Reinício pendente", disk_full: "Disco quase cheio", disk_high: "Disco com pouco espaço",
  disk_health: "Disco com problema de saúde", uptime_long: "Muito tempo sem reiniciar", rdp_exposed: "RDP exposto na rede", vnc_exposed: "VNC exposto na rede",
  telnet_exposed: "Telnet exposto", ftp_exposed: "FTP exposto", collect_errors: "Falhas de coleta no agente",
};

export const EVENT_LABEL = {
  registered: "Máquina registrada", changed: "Alteração", software_installed: "Software instalado", software_updated: "Software atualizado",
  software_removed: "Software removido", user_changed: "Troca de usuário", online: "Voltou online", offline: "Ficou offline",
  status_changed: "Mudança de status", removed: "Removida do painel", peripheral_connected: "Periférico conectado",
  peripheral_removed: "Periférico removido", retention_purge: "Limpeza por retenção",
};
export const EVENT_ICON = {
  registered: "plus", changed: "edit", software_installed: "package", software_updated: "package", software_removed: "package", user_changed: "user",
  online: "wifi", offline: "wifi-off", status_changed: "tag", removed: "trash", peripheral_connected: "usb", peripheral_removed: "usb", retention_purge: "trash",
};
export const FIELD_LABEL = {
  "identity.hostname": "Nome do computador", "identity.domain": "Domínio", "identity.loggedUser": "Usuário", "os.name": "Sistema operacional",
  "os.build": "Versão do Windows", "os.activated": "Windows ativado", "network.primaryIp": "IP principal", "network.macs": "Endereços MAC",
  "hardware.manufacturer": "Fabricante", "hardware.model": "Modelo", "hardware.serial": "Serial", "hardware.cpu": "Processador", "hardware.ram": "Memória RAM",
  "hardware.disks": "Discos", "hardware.gpus": "Placa de vídeo", "security.antivirusEnabled": "Antivírus ativo", "security.realTimeProtection": "Proteção em tempo real",
  "security.antivirusProducts": "Antivírus instalado", "security.firewallDomain": "Firewall (rede de domínio)", "security.firewallPrivate": "Firewall (rede privada)",
  "security.firewallPublic": "Firewall (rede pública)", "security.bitLocker": "BitLocker", "security.tpm": "TPM", "security.secureBoot": "Secure Boot",
  "agent.version": "Versão do agente", status: "Status",
};
const BOOL = { true: "sim", false: "não" };
export const ROLE_LABEL = { admin: "Administrador", operator: "Operador", viewer: "Leitor" };
export const ROLE_RANK = { admin: 3, operator: 2, viewer: 1 };
export const ROLE_DESC = {
  admin: "Faz tudo: gerencia usuários, remove dispositivos, muda status e exporta.",
  operator: "Muda o status dos dispositivos e exporta CSV. Não remove dispositivos nem gerencia usuários.",
  viewer: "Só consulta: vê dashboard, dispositivos, usuários e auditoria. Não altera nem exporta.",
};
export const ACCESS_ACTION = {
  bootstrap: ["Primeiro administrador criado", "info"], recovery_reset: ["Acesso recuperado por configuração", "warning"],
  login_ok: ["Login", "good"], login_failed: ["Login com falha", "warning"], login_blocked: ["Login em conta travada", "warning"], login_denied: ["Login negado", "warning"],
  account_locked: ["Conta travada", "critical"], logout: ["Saída", "info"], password_changed: ["Senha alterada", "info"], password_change_failed: ["Troca de senha com falha", "warning"],
  account_created: ["Usuário criado", "info"], account_updated: ["Usuário alterado", "info"], account_deleted: ["Usuário excluído", "warning"], password_reset: ["Senha redefinida", "warning"],
  deploy_scan: ["Varredura de rede", "info"], package_uploaded: ["Instalador enviado", "info"], install_script_downloaded: ["Script de instalação baixado", "info"], deploy_job_created: ["Instalação em massa gerada", "info"], deploy_script_downloaded: ["Script de instalação baixado", "info"],
  speedtest_requested: ["Teste de velocidade pedido", "info"], speedtest_cancelled: ["Teste de velocidade cancelado", "info"], setup_completed: ["Painel configurado", "good"], token_created: ["Token de agentes criado", "info"], token_revoked: ["Token de agentes revogado", "warning"],
  device_status: ["Status de dispositivo", "info"], device_removed: ["Dispositivo removido", "warning"], events_export: ["Exportação CSV", "info"],
};
export const evVal = v => v == null || v === "" ? "—" : BOOL[v] ?? STATUS_LABEL[v] ?? v;
