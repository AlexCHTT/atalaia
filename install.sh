#!/usr/bin/env bash
# Instalador do servidor Atalaia em Docker (Linux / macOS / Git Bash).
#
#   bash install.sh                                 pergunta o que precisa
#   bash install.sh --mode direct --port 8080         sem perguntas: HTTP direto no IP:porta
#   bash install.sh --mode proxy --proxy-ip 10.0.0.5  atrás de Nginx Proxy Manager (HTTPS no proxy)
#
# Modos:
#   direct  O painel responde em http://IP-DO-SERVIDOR:PORTA. Simples, para rede interna. Sem HTTPS: usuário, senha e token
#           dos agentes trafegam sem criptografia dentro da rede.
#   proxy   Um proxy reverso (Nginx Proxy Manager, Caddy...) fica na frente e cuida do HTTPS. Informe o IP dele para o painel
#           mostrar o IP real das máquinas. Restrinja a porta do container ao proxy no firewall.
set -euo pipefail
cd "$(dirname "$0")"

MODE=""; PORT=""; PROXY_IP=""; ASSUME_YES=0
while [ $# -gt 0 ]; do
  case "$1" in
    --mode) MODE="${2:-}"; shift 2 ;;
    --port) PORT="${2:-}"; shift 2 ;;
    --proxy-ip) PROXY_IP="${2:-}"; shift 2 ;;
    -y|--yes) ASSUME_YES=1; shift ;;
    -h|--help) sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Opção desconhecida: $1 (use --help)" >&2; exit 2 ;;
  esac
done

say()  { printf '%s\n' "$*"; }
die()  { printf 'ERRO: %s\n' "$*" >&2; exit 1; }
ask()  { # ask "pergunta" "padrão"  -> resposta em $REPLY
  if [ "$ASSUME_YES" = 1 ] || [ ! -t 0 ]; then REPLY="$2"; return; fi
  read -r -p "$1 [$2]: " REPLY || true; REPLY="${REPLY:-$2}"
}

# ---- Pré-requisitos ----
command -v docker >/dev/null 2>&1 || die "Docker não encontrado. Instale em https://docs.docker.com/engine/install/ e rode de novo."
docker info >/dev/null 2>&1 || die "O Docker está instalado mas não responde. Ele está ligado? (em Linux talvez precise de sudo ou do grupo 'docker')"
if docker compose version >/dev/null 2>&1; then DC="docker compose"
elif command -v docker-compose >/dev/null 2>&1; then DC="docker-compose"
else die "Docker Compose não encontrado (docker compose). Atualize o Docker."; fi

# ---- Perguntas ----
if [ -z "$MODE" ]; then
  say ""
  say "Como o painel vai ser acessado?"
  say "  1) Direto, por IP e porta em HTTP (mais simples; rede interna)"
  say "  2) Atrás de um proxy reverso com HTTPS (Nginx Proxy Manager, Caddy...)"
  ask "Escolha" "1"
  case "$REPLY" in 1|direct) MODE=direct ;; 2|proxy) MODE=proxy ;; *) die "Opção inválida: $REPLY" ;; esac
fi
case "$MODE" in direct|proxy) ;; *) die "--mode deve ser 'direct' ou 'proxy'" ;; esac

if [ -z "$PORT" ]; then ask "Porta a abrir na rede" "8080"; PORT="$REPLY"; fi
case "$PORT" in ''|*[!0-9]*) die "Porta inválida: $PORT" ;; esac
{ [ "$PORT" -ge 1 ] && [ "$PORT" -le 65535 ]; } || die "Porta fora de 1-65535: $PORT"

if [ "$MODE" = proxy ] && [ -z "$PROXY_IP" ]; then
  say ""
  say "Para o painel mostrar o IP real de cada máquina, informe o IP do proxy (ou uma faixa, ex.: 10.0.0.0/24)."
  ask "IP do proxy (deixe vazio para pular)" ""; PROXY_IP="$REPLY"
fi
[ "$MODE" = direct ] && PROXY_IP=""

# Porta já em uso (só avisa quando conseguimos checar). Se a porta já é do nosso próprio container (reinstalar/atualizar), não há conflito.
OWN_PORT="$($DC port atalaia 8080 2>/dev/null | sed 's/^.*://' | head -1 || true)"
if [ "$OWN_PORT" != "$PORT" ] && command -v ss >/dev/null 2>&1 && ss -ltn 2>/dev/null | awk '{print $4}' | grep -qE "[:.]${PORT}$"; then
  say "AVISO: a porta $PORT já está em uso neste servidor."
  ask "Continuar mesmo assim? (s/n)" "n"; case "$REPLY" in s|S|y|Y) ;; *) die "Cancelado. Rode de novo com outra porta (--port)." ;; esac
fi

# ---- .env (preserva o que já existe) ----
[ -f .env ] || cp .env.example .env
set_env() { # chave valor
  local tmp; tmp="$(mktemp)"
  grep -v -E "^$1=" .env > "$tmp" || true
  printf '%s=%s\n' "$1" "$2" >> "$tmp"
  cat "$tmp" > .env; rm -f "$tmp"
}
set_env HTTP_PORT "$PORT"
set_env BIND_ADDRESS "0.0.0.0"
set_env TRUSTED_PROXIES "$PROXY_IP"

# ---- Subir ----
say ""; say "Construindo e iniciando o servidor (a primeira vez demora alguns minutos)..."
$DC up -d --build

say "Aguardando o servidor responder..."
ok=0
for _ in $(seq 1 60); do
  if command -v curl >/dev/null 2>&1; then
    curl -fsS "http://127.0.0.1:${PORT}/healthz" >/dev/null 2>&1 && { ok=1; break; }
  elif $DC logs atalaia 2>&1 | grep -q "Now listening"; then ok=1; break; fi
  sleep 2
done
[ "$ok" = 1 ] || { $DC logs --tail 30 atalaia || true; die "O servidor não respondeu em 2 minutos. Veja os logs acima (ou: $DC logs atalaia)."; }

# ---- Resultado ----
HOST_IP="$( (hostname -I 2>/dev/null | awk '{print $1}') || true )"; HOST_IP="${HOST_IP:-IP-DO-SERVIDOR}"
CODE="$($DC logs atalaia 2>&1 | grep -oE '[A-Z2-9]{4}-[A-Z2-9]{4}' | tail -1 || true)"

say ""
say "============================================================"
say " Atalaia no ar"
if [ "$MODE" = direct ]; then
  say " Abra no navegador:  http://${HOST_IP}:${PORT}/"
  say " (sem HTTPS: use apenas em rede interna confiável)"
else
  say " O proxy deve encaminhar para:  http://${HOST_IP}:${PORT}"
  say " Depois abra o endereço HTTPS que você configurou no proxy."
  say " Dica: no firewall, deixe a porta ${PORT} acessível só pelo proxy."
fi
if [ -n "$CODE" ]; then
  say " Código do assistente de configuração:  ${CODE}"
else
  say " Painel já configurado (não há código de primeira execução)."
fi
say "============================================================"
say "Comandos úteis:  $DC logs -f atalaia   |   $DC down   |   $DC up -d --build (atualizar)"
