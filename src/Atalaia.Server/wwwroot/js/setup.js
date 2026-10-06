import { icon } from "./icons.js";
import { strength } from "./ui.js";
import { loadPublicInfo, applyBrand, policy } from "./core.js";

const $ = id => document.getElementById(id);
$("ic-1").innerHTML = icon("key", 20).s;
$("ic-2").innerHTML = icon("shield-check", 20).s;
$("ic-3").innerHTML = icon("download", 20).s;
$("eye").innerHTML = icon("eye", 18).s;

await loadPublicInfo(); applyBrand();
const hint = () => `Mínimo de ${policy.minPassword} caracteres, sem o nome de usuário dentro.`;
$("strength").textContent = hint();

let code = "";   // o código confirmado no passo 1 vai junto no envio final (o servidor confere de novo)

const showError = (id, msg) => { const e = $(id); e.textContent = msg ?? ""; e.hidden = !msg; };
const post = (url, body) => fetch(url, {
  method: "POST",
  headers: { "Content-Type": "application/json", "X-Requested-With": "Atalaia" },   // cabeçalho anti-CSRF exigido pelo servidor
  body: JSON.stringify(body),
});
const failure = async res => (await res.json().catch(() => null))?.message ?? "Não foi possível concluir. Tente novamente.";

// ---- Passo 1: código de configuração ----
$("code").addEventListener("input", e => {
  // aceita colar com ou sem hífen; mostra sempre no formato XXXX-XXXX
  const raw = e.target.value.replace(/[^A-Za-z0-9]/g, "").toUpperCase().slice(0, 8);
  e.target.value = raw.length > 4 ? raw.slice(0, 4) + "-" + raw.slice(4) : raw;
});

$("step1").addEventListener("submit", async ev => {
  ev.preventDefault();
  const value = $("code").value.trim();
  if (value.replace("-", "").length < 8) { showError("err1", "Digite o código completo (8 caracteres)."); return; }
  showError("err1", "");
  const go = $("go1"); go.disabled = true; go.textContent = "Conferindo…";
  try {
    const res = await post("/api/setup/verify", { code: value });
    if (res.ok) { code = value; $("step1").hidden = true; $("step2").hidden = false; $("name").focus(); return; }
    if (res.status === 409) { location.replace("/login.html"); return; }   // já configurado por outra pessoa
    showError("err1", await failure(res));
    $("code").select();
  } catch { showError("err1", "Não consegui falar com o servidor. Verifique a conexão e tente de novo."); }
  go.disabled = false; go.textContent = "Continuar";
});

// ---- Passo 2: administrador ----
let userTouched = false;
$("user").addEventListener("input", () => { userTouched = true; });
$("name").addEventListener("input", () => {
  if (userTouched) return;
  const w = $("name").value.normalize("NFD").replace(/[̀-ͯ]/g, "").toLowerCase().trim().split(/\s+/).filter(Boolean);
  $("user").value = (w.length > 1 ? [w[0], w[w.length - 1]] : w).join(".").replace(/[^a-z0-9._-]/g, "");   // "Ana Souza" -> ana.souza
});

$("eye").addEventListener("click", () => {
  const show = $("pw").type === "password";
  $("pw").type = show ? "text" : "password";
  $("eye").innerHTML = icon(show ? "eye-off" : "eye", 18).s;
});
$("pw").addEventListener("input", () => {
  const s = strength($("pw").value, $("user").value);
  $("meter").className = `meter tone-${s.tone}`; $("meter").firstElementChild.style.width = s.pct + "%";
  $("strength").textContent = s.label || hint();
});

$("step2").addEventListener("submit", async ev => {
  ev.preventDefault();
  const displayName = $("name").value.trim(), username = $("user").value.trim(), password = $("pw").value;
  if (!displayName || !username) { showError("err2", "Preencha o seu nome e o usuário."); return; }
  if (!strength(password, username).ok) { showError("err2", strength(password, username).label || "Defina uma senha."); return; }
  if (password !== $("pw2").value) { showError("err2", "As duas senhas não são iguais."); $("pw2").focus(); return; }

  showError("err2", "");
  const go = $("go2"); go.disabled = true; go.textContent = "Criando…";
  try {
    const res = await post("/api/setup", { code, displayName, username, password });
    if (res.ok) { location.replace("/#/install"); return; }   // já logado: cai direto na tela de instalação dos agentes
    if (res.status === 409) { location.replace("/login.html"); return; }
    if (res.status === 403) { showError("err2", "O código deixou de valer (o servidor foi reiniciado ou houve muitas tentativas). Volte e informe o código atual."); }
    else showError("err2", await failure(res));
  } catch { showError("err2", "Não consegui falar com o servidor. Verifique a conexão e tente de novo."); }
  go.disabled = false; go.textContent = "Criar administrador e entrar";
});
