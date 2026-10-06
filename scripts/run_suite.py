"""Roda as suítes de teste com um servidor DESCARTÁVEL: sobe o servidor com um banco novo, espera ele responder, roda o teste e desliga.

    python scripts/run_suite.py auth setup settings deploy      (uma ou mais; sem argumentos roda todas as que o sistema suporta)

Pré-requisito: o servidor compilado (dotnet build src/Atalaia.Server -c Release). A suíte "deploy" também precisa do agente do Windows
publicado em publish/agent (dotnet publish src/Atalaia.Agent -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/agent)
e do Windows PowerShell, então só roda no Windows. Nunca aponta para o banco de verdade: o banco de cada suíte fica numa pasta temporária.
"""
import os, re, subprocess, sys, tempfile, time, urllib.request, shutil

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROJ = os.path.join(ROOT, "src", "Atalaia.Server")
DLL = os.path.join(PROJ, "bin", "Release", "net10.0", "Atalaia.Server.dll")
IS_WINDOWS = os.name == "nt"
ADMIN = {"Server__AdminUser": "ana.souza", "Server__AdminPassword": "uma-senha-longa-99", "Server__EnrollToken": "token-de-teste-123"}

# nome -> (porta, ambiente do servidor, variáveis extras do servidor)
SUITES = {
    "auth":     (5096, "Development", {}),
    "setup":    (5097, "Production", {}),
    "settings": (5088, "Production", {**ADMIN, "Server__OfflineAfterMinutes": "33"}),
    "deploy":   (5090, "Production", ADMIN),
}


def wait_up(port, proc, timeout=40):
    t0 = time.time()
    while time.time() - t0 < timeout:
        if proc.poll() is not None: return False
        try:
            if urllib.request.urlopen(f"http://127.0.0.1:{port}/healthz", timeout=2).status == 200: return True
        except Exception: time.sleep(0.5)
    return False


def run(name):
    port, env_name, extra = SUITES[name]
    tmp = tempfile.mkdtemp(prefix=f"atalaia-{name}-")
    db = os.path.join(tmp, "test.db")
    log_path = os.path.join(tmp, "server.log")
    env = {**os.environ, "ASPNETCORE_ENVIRONMENT": env_name, "ASPNETCORE_URLS": f"http://127.0.0.1:{port}", "Server__DbPath": db, **extra, "PYTHONIOENCODING": "utf-8"}
    print(f"\n=== suíte '{name}' (servidor na porta {port}, banco em {db})", flush=True)
    log = open(log_path, "wb")
    proc = subprocess.Popen(["dotnet", DLL], cwd=PROJ, env=env, stdout=log, stderr=subprocess.STDOUT)
    try:
        if not wait_up(port, proc):
            log.flush(); print("O servidor não subiu. Log:\n" + open(log_path, encoding="utf-8", errors="replace").read()[-2000:]); return 1
        tenv = {**os.environ, "PORT": str(port), "DB": db, "PYTHONIOENCODING": "utf-8"}
        py = sys.executable
        if name == "auth":
            cmd = [py, "scripts/test_auth.py", f"http://127.0.0.1:{port}", db]
        elif name == "setup":
            log.flush()
            m = re.findall(r"[A-Z2-9]{4}-[A-Z2-9]{4}", open(log_path, encoding="utf-8", errors="replace").read())
            if not m: print("Código de configuração não apareceu no log do servidor."); return 1
            cmd = [py, "scripts/test_setup.py", m[-1]]
        else:
            cmd = [py, f"scripts/test_{name}.py"]
        return subprocess.run(cmd, cwd=ROOT, env=tenv).returncode
    finally:
        proc.terminate()
        try: proc.wait(10)
        except subprocess.TimeoutExpired: proc.kill()
        log.close()
        shutil.rmtree(tmp, ignore_errors=True)


def main():
    if not os.path.exists(DLL):
        print(f"Servidor não compilado: {DLL}\nRode: dotnet build src/Atalaia.Server -c Release"); return 2
    wanted = sys.argv[1:] or [n for n in SUITES if n != "deploy" or IS_WINDOWS]
    unknown = [n for n in wanted if n not in SUITES]
    if unknown: print("Suíte desconhecida:", ", ".join(unknown), "| válidas:", ", ".join(SUITES)); return 2
    if "deploy" in wanted and not IS_WINDOWS: print("A suíte 'deploy' exige o Windows PowerShell: só roda no Windows."); return 2
    results = {n: run(n) for n in wanted}
    print("\n=== resumo")
    for n, code in results.items(): print(f"  {'OK   ' if code == 0 else 'FALHOU'} {n}")
    return 0 if all(c == 0 for c in results.values()) else 1


if __name__ == "__main__":
    sys.exit(main())
