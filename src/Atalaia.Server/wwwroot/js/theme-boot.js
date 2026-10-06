// Aplica o tema salvo antes de a página ser pintada (evita piscar claro/escuro).
// Fica num arquivo próprio porque a política de segurança (CSP) do servidor não permite script embutido na página.
try { var t = localStorage.getItem("atalaia-theme"); if (t) document.documentElement.dataset.theme = t; } catch (e) { /* sem armazenamento */ }
