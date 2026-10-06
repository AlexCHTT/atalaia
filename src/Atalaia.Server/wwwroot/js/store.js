// Estado compartilhado: lista de máquinas e contagem de alertas, atualizados a cada 30 s.
import { api, qs, ROLE_RANK } from "./core.js";

export const store = { machines: [], alerts24h: 0, alertsUnread: 0, ok: true, at: 0, loading: false, me: null };

/** Quem está logado (a sessão vem do cookie). Sem sessão válida, api() já manda para a tela de login. */
export async function loadMe() { store.me = await api("/api/auth/me"); return store.me; }
/** O perfil atual alcança o mínimo exigido? (A tela só esconde o que não dá para usar; quem manda é o servidor.) */
export const can = minRole => (ROLE_RANK[store.me?.role] ?? 0) >= (ROLE_RANK[minRole] ?? 99);

// "Visto" = id do último alerta que a pessoa já olhou, guardado neste navegador. Usar o id (e não a hora) evita erro por diferença de relógio.
const SEEN_KEY = "atalaia-alerts-seen";
const getSeen = () => { try { const v = localStorage.getItem(SEEN_KEY); return v == null ? null : +v; } catch { return null; } };
const setSeen = id => { try { localStorage.setItem(SEEN_KEY, String(id)); } catch { /* sem armazenamento: o selo volta no próximo recarregamento */ } };

/** Marca todos os alertas atuais como vistos (sino e selo da Auditoria zeram; o total de 24 h do dashboard continua igual). */
export async function markAlertsSeen() {
  try {
    const [latest] = await api("/api/events?" + qs({ severity: "warn", limit: 1 }));
    setSeen(latest?.id ?? 0);
    store.alertsUnread = 0;
    subs.forEach(f => f());
  } catch { /* sem conexão: tenta de novo no próximo clique */ }
}
const subs = new Set();
export const subscribe = fn => { subs.add(fn); return () => subs.delete(fn); };
export const byId = id => store.machines.find(m => m.agentId === id);
export const byHostname = h => store.machines.find(m => (m.hostname || "").toLowerCase() === (h || "").toLowerCase());

export async function refresh() {
  store.loading = true; subs.forEach(f => f());
  try {
    const since = new Date(Date.now() - 864e5).toISOString();
    const seen = getSeen();
    const [machines, count, unread] = await Promise.all([
      api("/api/machines"),
      api("/api/events/count?" + qs({ severity: "warn", from: since })),
      seen == null ? null : api("/api/events/count?" + qs({ severity: "warn", from: since, after: seen })),
    ]);
    store.machines = machines; store.alerts24h = count.count; store.alertsUnread = unread ? unread.count : count.count; store.ok = true; store.at = Date.now();
  } catch (e) {
    // Troca de senha pendente não é falha de conexão: o servidor está de pé, só recusa dados até a pessoa trocar
    if (e?.code !== "password_change_required") store.ok = false;
  }
  store.loading = false;
  subs.forEach(f => f());
}

// Máquinas ativas (as aposentadas/em estoque não entram nos totais de saúde)
export const active = () => store.machines.filter(m => m.status === "active");
