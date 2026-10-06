"""
Cliente HTTP mínimo para os scripts de teste/demonstração.
Entra no painel com usuário e senha (cookie de sessão) e manda o cabeçalho anti-CSRF nas requisições que alteram dados.
Check-ins de agente (token no cabeçalho) não usam sessão.

A conta padrão é a de desenvolvimento (appsettings.Development.json): admin / dev-senha-123456.
"""
import http.cookiejar
import json
import urllib.error
import urllib.request


class Panel:
    def __init__(self, base, user="admin", password="dev-senha-123456"):
        self.base, self.user, self.password = base.rstrip("/"), user, password
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
        self._logged = False

    def _send(self, method, path, body=None, headers=None):
        h = {"Content-Type": "application/json", **(headers or {})}
        if method not in ("GET", "HEAD"):
            h["X-Requested-With"] = "Atalaia"
        req = urllib.request.Request(self.base + path, method=method, headers=h, data=json.dumps(body).encode() if body is not None else None)
        try:
            r = self.opener.open(req)
            status, raw = r.status, r.read()
        except urllib.error.HTTPError as e:
            status, raw = e.code, e.read()
        try:
            return status, (json.loads(raw) if raw else None)
        except ValueError:
            return status, raw.decode("utf-8", "replace")

    def call(self, method, path, body=None, token=None):
        """Com `token`: check-in de agente (sem sessão). Sem token: usa a sessão do painel, entrando na primeira vez."""
        if token:
            return self._send(method, path, body, {"X-Enroll-Token": token})
        if not self._logged:
            status, data = self._send("POST", "/api/auth/login", {"username": self.user, "password": self.password})
            if status != 200:
                raise SystemExit(f"Login no painel falhou ({status}): {data}. Confira usuário/senha e se o servidor é o de teste.")
            self._logged = True
        return self._send(method, path, body)
