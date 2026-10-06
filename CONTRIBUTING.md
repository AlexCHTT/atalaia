# Como contribuir com o Atalaia

Obrigado pelo interesse! Contribuições são bem-vindas: correções, melhorias, testes, documentação e relatos de problemas. Pode escrever em português ou em inglês.

## Antes de começar

- **Bug ou ideia:** abra uma *issue* descrevendo o que aconteceu ou o que você quer. Para mudanças grandes, converse na issue antes de escrever código, para não perder trabalho.
- **Vulnerabilidade de segurança:** **não** abra issue pública. Veja o [SECURITY.md](SECURITY.md).

## Rodando o projeto

Veja a seção **"Rodar em desenvolvimento"** do [README](README.md). Em resumo (PowerShell, na pasta do projeto):

```powershell
$env:ASPNETCORE_ENVIRONMENT="Development"; $env:ASPNETCORE_URLS="http://localhost:5099"
dotnet run --project src/Atalaia.Server --no-launch-profile
```

Precisa do **.NET SDK 10**. O agente é só para Windows; o servidor roda em Windows, Linux e Docker.

## Testes

```bash
dotnet build src/Atalaia.Server -c Release
python scripts/run_suite.py            # roda todas as suítes que o seu sistema suporta
python scripts/run_suite.py auth       # ou uma só: auth, setup, settings, deploy
```

Cada suíte sobe um **servidor descartável com banco novo** (numa pasta temporária) e o desliga no fim. Nunca aponte os testes para o banco de verdade. A suíte `deploy` precisa do agente publicado (`publish/agent`) e do Windows PowerShell, então só roda no Windows. Detalhes no cabeçalho de cada `scripts/test_*.py`.

**Toda mudança de comportamento deve vir com teste.** Se você achou um bug, o melhor começo é um teste que falha.

## Como o código é escrito

- **Siga o estilo do que já existe** (nomes, comentários, formatação). Não reformate arquivos inteiros numa mudança que não é sobre isso.
- **Comentários em português explicando o porquê**, não o quê. Nada de comentário que só repete a linha de código.
- **Painel sem dependências externas:** HTML, CSS e módulos JavaScript puros, sem CDN (ele funciona offline). Todo texto que vem de um computador é tratado como não confiável e escapado.
- **Dependências novas:** só se forem realmente necessárias e com licença livre compatível com a Apache-2.0. Prefira a biblioteca padrão. Converse antes na issue.
- **Gratuito:** o projeto não depende de nenhuma ferramenta ou serviço pago.

## Áreas que pedem mais cuidado

Mudanças nestes pontos serão revisadas com atenção redobrada:

- **Autenticação, sessões e permissões** (`src/Atalaia.Server/Auth`).
- **O agente e suas tarefas:** o agente **nunca** executa comando, script ou endereço vindo do servidor. As tarefas são uma lista fixa no código. Não amplie isso.
- **Scripts de instalação** (`AgentInstallScript`, `DeployScript`): tudo que entra no script passa por validação rígida de caracteres.
- **Apagar dados:** a auditoria e o registro de acessos são "somente acréscimo"; apagar exige confirmação e deixa registro.

## Enviando uma contribuição (Pull Request)

1. Faça um *fork* e crie uma branch a partir de `main`.
2. Faça **uma mudança por pull request**, pequena e focada.
3. Rode as suítes (`python scripts/run_suite.py`) e confirme que passam.
4. **Não inclua** bancos de dados (`*.db`), arquivos `.env`, binários, tokens ou senhas. O `.gitignore` já cobre os usuais.
5. Escreva uma descrição clara: o que mudou, por quê e como você testou. Mensagem de commit curta no imperativo, em português, por exemplo: *"Corrige o cálculo de offline com fuso horário"*.

## Licença

Ao contribuir, você concorda que sua contribuição será licenciada sob a [Apache License 2.0](LICENSE), a mesma do projeto (seção 5 da licença). Não há termo extra para assinar.
