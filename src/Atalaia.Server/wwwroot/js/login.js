import { icon } from "./icons.js";
import { loadPublicInfo, applyBrand } from "./core.js";

loadPublicInfo().then(() => applyBrand());   // nome da empresa definido em Configurações

const $ = id => document.getElementById(id);
$("ic-1").innerHTML = icon("shield-check", 20).s;
$("ic-2").innerHTML = icon("list", 20).s;
$("ic-3").innerHTML = icon("server", 20).s;
$("eye").innerHTML = icon("eye", 18).s;
$("caps").innerHTML = icon("alert-triangle", 14).s + " Caps Lock ativado";

// mostrar/ocultar senha (a página de login não carrega o resto do painel)
$("eye").addEventListener("click", () => {
  const input = $("p"), show = input.type === "password";
  input.type = show ? "text" : "password";
  $("eye").innerHTML = icon(show ? "eye-off" : "eye", 18).s;
});
$("p").addEventListener("keyup", e => { $("caps").hidden = !e.getModifierState?.("CapsLock"); });
$("p").addEventListener("blur", () => { $("caps").hidden = true; });

const showError = msg => { const e = $("err"); e.textContent = msg ?? ""; e.hidden = !msg; };

$("form").addEventListener("submit", async ev => {
  ev.preventDefault();
  const username = $("u").value.trim(), password = $("p").value;
  if (!username || !password) { showError("Informe o usuário e a senha."); return; }

  showError("");
  const go = $("go");
  go.disabled = true; go.textContent = "Entrando…";
  try {
    const res = await fetch("/api/auth/login", {
      method: "POST",
      headers: { "Content-Type": "application/json", "X-Requested-With": "Atalaia" },   // cabeçalho anti-CSRF exigido pelo servidor
      body: JSON.stringify({ username, password }),
    });
    if (res.ok) { location.replace("/"); return; }
    const data = await res.json().catch(() => null);
    showError(data?.message ?? "Não foi possível entrar. Tente novamente.");
    $("p").select();
  } catch {
    showError("Não consegui falar com o servidor. Verifique a conexão e tente de novo.");
  }
  go.disabled = false; go.textContent = "Entrar";
});
