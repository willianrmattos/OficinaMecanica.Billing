# Evidencia de cobertura de testes

Registro da cobertura de linhas e da contagem de testes do servico
**OficinaMecanica.Billing**. Os numeros sao os da medicao mais recente, registrados no
`CLAUDE.md` deste repositorio, e foram conferidos contra o relatorio gerado por
`bash scripts/cobertura.sh`.

| Item | Valor |
|------|-------|
| Data da medicao | **2026-11-08** (testes entre 19:23 e 19:28, relatorio gerado as 19:28, horario local da maquina de desenvolvimento) |
| Como foi gerado | `bash scripts/cobertura.sh` (fase `tudo`: testes sem Docker, testes de infraestrutura e relatorio), com Docker ligado |
| Codigo medido | `src/` e `tests/` deste repositorio. O arquivo mais recente de `src/` e `tests/` foi modificado as 19:21:53, antes do inicio da medicao (19:23), entao os numeros valem para o codigo desta data; este documento, o `README.md`, o `CLAUDE.md` e a pasta `scripts/` (remocao do `aplicar-ruleset.sh` e dos casos dele no `testar-scripts.sh`) foram editados depois e nao entram na medicao, que so le `src/` e `tests/` |
| Resultado dos testes | 983 executados, 983 aprovados, 0 falhas |
| Gate do CI | cobertura de linhas **>= 80%** sobre o numero "com exclusoes" |

> Esta medicao foi feita **localmente**. O CI no GitHub Actions ainda nao rodou (o
> repositorio nao foi publicado); a evidencia equivalente do CI so existe depois da
> primeira execucao (ver [Onde fica no CI](#onde-fica-no-ci)).

## Resumo

| Medicao | Linhas cobertas / cobertaveis | Cobertura |
|---------|-------------------------------|-----------|
| **Bruta** (conta tudo) | 3508 / 3559 | **98,57%** |
| **Com exclusoes** (valida o gate) | 1991 / 1999 | **99,60%** |

O relatorio HTML do ReportGenerator arredonda para baixo em uma casa decimal (mostra
98,5% e 99,5%); o gate usa a contagem exata de linhas, sem esse arredondamento.

## Por assembly

Cada assembly e um projeto de producao do servico. Os projetos de teste e o
`Tests.Common` ficam fora da medicao (`coverage.runsettings`).

| Assembly | Bruta (cobertas / cobertaveis) | Bruta | Com exclusoes (cobertas / cobertaveis) | Com exclusoes | Linhas excluidas |
|----------|--------------------------------|-------|----------------------------------------|---------------|------------------|
| `OficinaMecanica.Billing.Domain` | 323 / 325 | 99,38% | 323 / 325 | 99,38% | 0 |
| `OficinaMecanica.Billing.Application` | 721 / 721 | 100% | 712 / 712 | 100% | 9 |
| `OficinaMecanica.Billing.Infrastructure` | 2097 / 2121 | 98,87% | 700 / 703 | 99,57% | 1418 |
| `OficinaMecanica.Billing.API` | 283 / 308 | 91,88% | 172 / 175 | 98,29% | 133 |
| `OficinaMecanica.Contracts` | 84 / 84 | 100% | 84 / 84 | 100% | 0 |
| **Total** | **3508 / 3559** | **98,57%** | **1991 / 1999** | **99,60%** | **1560** |

A API fica em 91,88% na medicao bruta por causa do `Program.cs` (83,4% nessa
medicao), que e uma das exclusoes abaixo. Informativo (nao entra no gate): a cobertura
de **ramos** e de 96,8% na medicao bruta (634 de 655) e de 97,8% com exclusoes (613 de 627).

## Testes por projeto

| Projeto de teste | Testes | O que cobre |
|------------------|--------|-------------|
| `OficinaMecanica.Contracts.Tests` | 41 | Contratos de mensagem contra os golden JSON |
| `OficinaMecanica.Billing.Domain.Tests` | 190 | Agregados e regras de dominio (sem mocks) |
| `OficinaMecanica.Billing.Application.Tests` | 268 | Handlers, validadores e mapeamentos (Moq nas portas) |
| `OficinaMecanica.Billing.Integration.Tests` (sem Docker) | 412 | Pipeline HTTP, middleware de excecoes, assinatura do webhook, gateways, consumers, reconciliador, health |
| `OficinaMecanica.Billing.Integration.Tests` (`Category=Infra`, Testcontainers) | 72 | SQL Server e RabbitMQ reais em containers: outbox, inbox, concorrencia, migration, repositorios, prontidao, cancelamento de requisicao com comando SQL em voo |
| **Total** | **983** | |

## Exclusoes da medicao "com exclusoes"

Sao quatro padroes, e **so eles** (a lista e a mesma do `scripts/cobertura.sh`, do
`ci.yml` e do `sonar.coverage.exclusions` do SonarCloud). Todos sao arquivos gerados
ou de composicao, sem regra de negocio, e continuam sendo verificados por outros
testes:

| Padrao | O que e | Por que fica fora | Como continua verificado |
|--------|---------|-------------------|--------------------------|
| `**/Migrations/**` | Codigo gerado pelo EF Core (`dotnet ef migrations add`) | Nao e codigo escrito a mao | `RepositoriosSqlTests` aplica a migration em um SQL Server real e confere as tabelas, os indices unicos, os tipos monetarios e que o modelo nao tem mudancas pendentes em relacao a ela |
| `**/Program.cs` | Raiz de composicao da API: host, Swagger, middlewares e rotas | Logica de montagem do processo, nao de negocio | Os testes de pipeline HTTP (`WebApplicationFactory`) e de partida da configuracao o exercitam; o log "API iniciada com sucesso" foi verificado a mao com o compose, e executando a API com configuracao invalida (nao aparece quando a validacao de partida a derruba) |
| `**/DependencyInjection*.cs` | Registro de servicos no container de injecao de dependencia | Apenas liga as pecas | `DependencyInjectionTests` (Application e Integration), `PartidaDaConfiguracaoTests` e `ValidadoresDeOpcoesTests` |
| `**/Data/Configurations/**` | Mapeamentos do EF Core (tabelas, tipos, indices) | Declarativos: so tem efeito quando executados contra um banco | Os testes de infraestrutura de esquema, de indices e dos repositorios |

A lista e curta de proposito (ver o
[ADR 0003](../adr/0003-ci-cd-independente-com-gate-de-qualidade.md)), e o numero bruto e
publicado ao lado do numero com exclusoes.

### O que **nao** e excluido, de proposito

- **`CompilerGeneratedAttribute` nao esta na lista de exclusao.** Excluir esse
  atributo tiraria da medicao os iteradores, as expressoes lambda e as maquinas de
  estado de `async`, que sao codigo escrito por nos, e inflaria o numero. Esta
  explicado no `coverage.runsettings`.
- **`[ExcludeFromCodeCoverage]` e honrado pelo coverlet** (assim como
  `GeneratedCodeAttribute`): o codigo marcado some, em silencio, das **duas** medicoes
  e do gate. Por isso o uso e proibido sem justificativa em ADR, e o
  `scripts/cobertura.sh relatorio` lista as ocorrencias no log e no Job Summary.
  **Hoje ha 0 ocorrencias em `src/`.**

## Classes abaixo de 100% (medicao com exclusoes)

As 8 linhas nao cobertas estao em:

| Classe | Cobertura de linhas |
|--------|---------------------|
| `OficinaMecanica.Billing.API.Models.TextoOuNumeroConverter` | 62,5% |
| `OficinaMecanica.Billing.Domain.Common.LimitesDeValor` | 0% |
| `OficinaMecanica.Billing.Infrastructure.BackgroundServices.ReconciliadorDePagamentosBackgroundService` | 97,6% |
| `OficinaMecanica.Billing.Infrastructure.Data.BillingDbContext` | 95,2% |
| `OficinaMecanica.Billing.Infrastructure.Messaging.RabbitMqHealthCheck` | 97% |

Todas as demais classes medidas estao em 100%. Na medicao bruta ainda aparecem abaixo de
100% o `Program` (83,4%) e a migration `Inicial` (97,3%), que sao exatamente o que as
exclusoes removem.

## Como regenerar

```bash
bash scripts/cobertura.sh          # roda tudo e aplica o gate (precisa de Docker para os testes de infraestrutura)
bash scripts/ci-local.sh           # os mesmos passos do job "Build & Test" do CI, incluindo esta cobertura (~9 min)
```

`scripts/cobertura.sh` aceita uma fase: `testes-sem-infra`, `testes-infra` ou `relatorio`
(este so une os resultados ja gerados e aplica o gate). A saida fica em `TestResults/`
(ignorada pelo git):

| Arquivo | Conteudo |
|---------|----------|
| `TestResults/report-com-exclusoes/index.html` | **Relatorio HTML**: a medicao que vale para o gate |
| `TestResults/report-bruto/index.html` | Relatorio HTML da medicao bruta |
| `TestResults/report-com-exclusoes/Summary.txt` | Resumo em texto (`Covered lines`, `Coverable lines`), que o gate le |
| `TestResults/resumo-cobertura.md` | Resumo em Markdown (vai para o Job Summary do GitHub Actions) |
| `TestResults/<projeto>/*.trx` | Resultado de cada projeto de teste |

O limite vem de `COBERTURA_MINIMA` (padrao 80). Abaixo dele, `scripts/verificar-cobertura.sh`
sai com codigo diferente de zero e o CI reprova.

## Onde fica no CI

No job **Build & Test** (`.github/workflows/ci.yml`), os steps "Testes (Category!=Infra)",
"Testes (Category=Infra, Testcontainers)" e "Cobertura mesclada e gate" produzem os mesmos
arquivos. Depois da execucao, ficam disponiveis:

- o artefato **`relatorio-de-cobertura`** (pastas `report-com-exclusoes` e `report-bruto`,
  com o `index.html` de cada uma);
- o artefato **`cobertura-opencover`** (os `coverage.opencover.xml` que o job `Quality`
  importa no SonarCloud);
- o artefato **`resultados-dos-testes`** (os `.trx`);
- o **Job Summary** do run, com a tabela de cobertura e as ocorrencias de
  `[ExcludeFromCodeCoverage]`.

**Ainda nao executado no GitHub**: nenhum desses artefatos existe ate a primeira
execucao do pipeline.

## SonarCloud

- Badge/link do SonarCloud: **preencher apos a publicacao**
- Cobertura e Quality Gate reportados pelo SonarCloud: **preencher apos a publicacao**
  (o job `Quality` ainda nao rodou; depende da organizacao, do projeto e do `SONAR_TOKEN`)
