"""
Testa periféricos, métricas, rede e ferramentas usando um relatório REAL do agente, enviado como máquina fictícia "sim-pc-0002".
Uso (da raiz do repositório):  python scripts/simulate_peripherals.py [http://localhost:5099]
"""
import copy, json, subprocess, sys

from _client import Panel

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://localhost:5099"
ID = "sim-pc-0002"
AGENT = "src/Atalaia.Agent/bin/Debug/net10.0-windows/Atalaia.Agent.dll"
call = Panel(BASE).call


def checkin(report):
    status, _ = call("POST", "/api/checkin", report, token="dev-enroll-token")
    assert status == 204, f"checkin falhou: {status}"


out = subprocess.run(["dotnet", AGENT, "--dump"], capture_output=True, text=True, encoding="utf-8").stdout
real = json.loads(out[out.index("{"):])
real["agentId"] = ID
real["identity"]["hostname"] = "SIM-PC-REAL"

call("DELETE", f"/api/machines/{ID}")
print("1) check-in com dados reais do agente:", len(real["peripherals"]), "perifericos,", len(real["networkActivity"]["listening"]), "portas,", len(real["tools"]), "ferramenta(s)")
checkin(real)

print("2) conectando um receptor Logitech (046D:C534) e um pendrive SanDisk")
r2 = copy.deepcopy(real)
r2["peripherals"] += [
    {"kind": "Mouse", "name": "Dispositivo de entrada USB", "manufacturer": "Microsoft", "vendorId": "046D", "productId": "C534", "connection": "USB"},
    {"kind": "Armazenamento USB", "name": "SanDisk Ultra USB Device", "manufacturer": "SanDisk", "vendorId": None, "productId": None, "connection": "USB"},
]
checkin(r2)

print("3) desconectando o mouse")
r3 = copy.deepcopy(r2)
r3["peripherals"] = [p for p in r3["peripherals"] if p["kind"] != "Mouse" or p["vendorId"] is None]
checkin(r3)

_, ev = call("GET", f"/api/machines/{ID}/events")
print("\n== EVENTOS DE PERIFERICO")
for e in reversed(ev):
    if e["type"].startswith("peripheral"):
        print(f"  [{e['severity']:4}] {e['type']:21} {e['field']:18} {e['oldValue'] or '':45} {e['newValue'] or ''}")

_, d = call("GET", f"/api/machines/{ID}")
print("\n== TRADUCAO NO DETALHE (vendorName / productName a partir do usb.ids)")
for p in d["peripherals"]:
    if p["vendorId"]:
        print(f"  {p['vendorId']}:{p['productId']}  ->  {p['vendorName']} / {p['productName']}")

_, m = call("GET", f"/api/machines/{ID}/metrics?hours=24")
print(f"\n== METRICAS: {len(m)} amostras; ultima: cpu={m[-1]['cpu']}% ram={m[-1]['ramPct']}% disco={m[-1]['diskPct']}%")

_, tools = call("GET", "/api/tools")
print("\n== FERRAMENTAS (visao geral)")
for t in tools:
    print(f"  {t['tool']}: {t['machines']} maquina(s) -> {[(h['hostname'], h['source']) for h in t['hits']]}")

call("DELETE", f"/api/machines/{ID}")
