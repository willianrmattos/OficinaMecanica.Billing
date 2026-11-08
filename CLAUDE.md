# OficinaMecanica.Billing - Orcamento e Pagamento (Mercado Pago)

## Visao Geral

Microsservico do ecossistema OficinaMecanica responsavel por **orcamento** (gerar/cancelar)
e **pagamento** (Mercado Pago Checkout Pro, webhook, estorno). Participa da Saga orquestrada
que vive no `OficinaMecanica` (OS Service): recebe **comandos** (`GerarOrcamento`,
`SolicitarPagamento`, `CancelarOrcamento`, `CancelarPagamento`, `EstornarPagamento`) e responde
com **eventos** (`OrcamentoGerado`, `PagamentoSolicitado`, `PagamentoAprovado`, ...). O Billing e
a autoridade sobre o pagamento: cancelar um pagamento ja aprovado responde `PagamentoAprovado`.

Repositorio irmao de `OficinaMecanica`, `OficinaMecanica.Execucao`, `OficinaMecanica.Seguranca`,
`OficinaMecanica.Infra` e `OficinaMecanica.Banco`. Sem dependencia de compilacao com nenhum deles.

## Estado atual (ondas 2 e F7)

Existem **Domain**, **Application**, **Infrastructure** (EF Core 9 + outbox/inbox do MassTransit 8.5.12, consumers,
gateways Mercado Pago/Fake, validador de assinatura, metricas, reconciliador, JWT/JWKS), **API** (webhook, consultas,
Swagger, `/health`, `/metrics`), `Dockerfile`, `docker-compose.yml`, ADRs, os testes (unitarios, pipeline HTTP e
infraestrutura com Testcontainers) e, da **F7**, o CI/CD (`.github/`), os manifests (`k8s/billing/`), a protecao de branch
versionada e os scripts de `scripts/`. Falta o README final (F9). Os contratos sao a copia da onda 1.
O repositorio ainda nao existe no GitHub: a F7 foi validada so localmente (`actionlint`, `kubeconform`, `docker build`,
execucao dos scripts e do `ci-local.sh`); a primeira execucao real do workflow acontece na publicacao.

## Estrutura do Projeto

```
src/
  OficinaMecanica.Contracts/               # COPIA do subconjunto usado pelo Billing (ver "Contratos")
  OficinaMecanica.Billing.Domain/          # Orcamento, Pagamento, TentativaPagamento, Estorno, WebhookRecebido,
                                           # CancelamentoPrevio, enums, DomainException, interfaces de repositorio/UoW
  OficinaMecanica.Billing.Application/     # Commands/Queries (MediatR), Validators (FluentValidation),
                                           # ValidationBehavior, DTOs/mappers, portas (Interfaces/), mensagens de integracao
                                           # (MensagensDeIntegracao: dominio->contrato; ComandosDeMensagem: contrato->command)
  OficinaMecanica.Billing.Infrastructure/  # Data/ (BillingDbContext, Configurations/, Migrations/), Repositories/,
                                           # Messaging/ (AddMensageria, consumers, publisher, bloqueio por ordem),
                                           # Gateways/ (MercadoPago/, FakePagamentoGateway), Security/ (assinatura do
                                           # webhook, JWT/JWKS), Observability/ (MetricasDoBilling), Health/ (cache de prontidao), BackgroundServices/
                                           # (reconciliador), Options/, DependencyInjection.cs
  OficinaMecanica.Billing.API/             # Program.cs, Controllers/ (Orcamentos, Pagamentos, Webhooks, DevPagamentos),
                                           # Middleware/ExceptionHandlingMiddleware, Models/, appsettings*.json
tests/
  OficinaMecanica.Contracts.Tests/         # goldens JSON (bytes identicos aos do monolito)
  OficinaMecanica.Billing.Domain.Tests/    # sem mocks
  OficinaMecanica.Billing.Application.Tests/  # Moq nas portas
  OficinaMecanica.Billing.Integration.Tests/  # assinatura (vetores), gateways (HttpMessageHandler falso), publisher/consumers,
                                           # retry em memoria, reconciliador, API (WebApplicationFactory) e Infra/ (Testcontainers)
  OficinaMecanica.Billing.Tests.Common/    # builders Bogus (OrcamentoBuilder, PagamentoBuilder, ...) e RelogioFixo
docs/adr/                                  # 0001 (Mercado Pago + gateway abstrato), 0002 (SQL, outbox/inbox), 0003 (CI/CD)
docs/openapi.json                          # contrato OpenAPI versionado (o CI falha se estiver desatualizado)
.github/                                   # workflows/ci.yml, rulesets/protect-main-release.json, dependabot.yml, PULL_REQUEST_TEMPLATE.md
k8s/billing/                               # deployment, service, ingress, hpa, secret-provider-class
scripts/                                   # cobertura.sh, verificar-cobertura.sh, verificar-masstransit.sh, verificar-workflow.sh, testar-scripts.sh,
                                           # exportar-swagger.sh, validar-k8s.sh, ci-local.sh, aplicar-ruleset.sh
Dockerfile  docker-compose.yml  .env.example  coverage.runsettings  dotnet-tools.json
```

Clean Architecture (Domain -> Application -> Infrastructure -> API), DDD, CQRS via MediatR,
`ValidationBehavior` (FluentValidation no pipeline), Repository + Unit of Work. Os builders de
command/DTO do gateway ficam em `Application.Tests/Builders` (nao em `Tests.Common`) para o
`Domain.Tests` nao puxar a Application por referencia transitiva.

## Convencoes

- **Idioma do dominio**: entidades, propriedades, enums, mensagens de erro/validacao em portugues
  (`Orcamento`, `Pagamento`, `StatusPagamento.Estornando`); mensagens de erro com acentos
- **Idioma da arquitetura**: namespaces (`OficinaMecanica.Billing.Domain|Application`), interfaces,
  design patterns e nomes de camada em ingles
- Comentario so quando explica um "porque" nao obvio, em 1a pessoa ("optei por"); nunca 2a pessoa
- O dominio nunca le o relogio: recebe o "agora" por parametro; a Application usa `TimeProvider` (DI)
- Valores monetarios: `decimal` com no maximo 2 casas e sempre > 0, com teto nomeado em `LimitesDeValor`
  (preco unitario <= 1.000.000,00, quantidade <= 10.000, valor total do orcamento/pagamento <= 1.000.000,00):
  valor absurdo vira `DomainException` (`OrcamentoNaoGerado`), nunca `OverflowException`/Fault
- Mesmo padrao de testes do monolito: xUnit + FluentAssertions (sem `Assert.*`) + Moq (Application.Tests e, no
  Integration.Tests, so onde a fronteira e uma porta/`IMediator`: consumers, reconciliador, composicao) + Bogus em
  builders de `Tests.Common`. Domain.Tests e Contracts.Tests nao usam mocks.

## Regras de mensageria (Application)

- Toda mensagem de integracao sai via `IIntegrationEventPublisher` (`PublishAsync` para evento,
  `SendAsync(fila, ...)` para comando) **antes** do `IUnitOfWork.SaveChangesAsync` do handler (a implementacao
  da Infrastructure grava no outbox na mesma transacao). Nunca depois do SaveChanges. Os testes provam a
  ordem com `GravadorDeChamadas`.
- `MessageId` deterministico (`IdentificadorDeMensagem`, derivado do agregado + tipo da mensagem)
  para reentregas republicarem a mesma mensagem e o inbox do consumidor deduplicar. O comando de estorno
  automatico inclui o numero de tentativas de estorno no id (reentrega antes de tentar = mesmo id; depois
  de uma falha = id novo, o estorno retoma).
- Falha de **negocio** vira evento explicito (`OrcamentoNaoGerado`, `PagamentoNaoSolicitado`,
  `EstornoFalhou`), nao excecao. Falha **tecnica** (timeout, 5xx do gateway) propaga para o retry do
  consumidor: so `PagamentoRecusadoPeloGatewayException` (recusa permanente, 4xx definitivo) vira
  `PagamentoNaoSolicitado`. Estorno que nao pode ser feito (pagamento inexistente/nao estornavel) responde
  `EstornoFalhou` com texto fixo, nunca deixa a saga em `Estornando`.
- Nenhum texto vindo do gateway (mensagem de excecao do SDK) e persistido ou publicado: `Estorno.Erro` e
  `EstornoFalhou.Erro` usam texto fixo curto em portugues; a excecao completa so vai para o `ILogger`.
- Pagamento e **pre-persistido** como `Pendente` antes de chamar o gateway e o estorno persiste `Estornando`
  antes de chamar o gateway. Isso so sobrevive a uma queda **fora** de um consumer; dentro da transacao do
  MassTransit esse Save intermediario nao e commit, e a seguranca vem da idempotencia: a chave
  `refund-{PagamentoId}` faz o gateway devolver o mesmo estorno e uma preferencia orfa (sem link gravado) nao
  cobra ninguem. Por isso a chamada ao gateway, que fica com a transacao aberta, exige timeout curto (abaixo).
- `rejected` do gateway nao e falha final (so registra tentativa); falha final = `Cancelado`/`Expirado`.
  Tentativa `approved` nunca e rebaixada. Cancelamento por prazo (`CancelarPagamento` depois de `ExpiraEm`)
  registra `Expirado`.
- Webhook: assinatura (`IAssinaturaWebhookValidator`) -> dedupe por `WebhookRecebido.RequestId` ->
  **sempre** consulta o gateway antes de agir -> localiza o `Pagamento` por `ExternalReference`.
  - `ObterPagamentoAsync == null` (gateway ainda nao conhece o pagamento) e transitorio: lanca
    `PagamentoDoGatewayIndisponivelException` (API responde 5xx para o gateway reentregar). So se ignora
    (200) quando o pagamento do gateway existe mas nao tem `Pagamento` local.
  - Divergencia de valor/moeda/referencia: nao aprova, responde 200, mas deixa sinal duravel
    (`LogError` com os valores + `IMetricasDoBilling.RegistrarDivergenciaDePagamento`).
  - Aprovacao tardia (depois de `Cancelado`/`Expirado`): persiste `AprovacaoTardia` e envia o comando
    contratual `EstornarPagamento` para `Filas.Billing` no mesmo SaveChanges; a reentrega do webhook
    reenvia enquanto o estorno nao terminar (`Estornando`/`EstornoFalhou` com `AprovacaoTardia`).
  - Segundo pagamento aprovado para o mesmo `Pagamento`: `ResultadoAprovacao.AprovacaoDuplicada` -
    registra a tentativa, `LogWarning` com os ids e `RegistrarAprovacaoDuplicada`; nao estorna no webhook:
    o reconciliador estorna a cobranca em duplicidade (`TentativaPagamento.GatewayRefundId` marca as ja devolvidas).
- Cancelamento que chega antes do agregado existir vira um `CancelamentoPrevio` (tombstone por
  `OrdemDeServicoId` + tipo); o `GerarOrcamento`/`SolicitarPagamento` atrasado nao cria nada e responde
  `OrcamentoNaoGerado`/`PagamentoNaoSolicitado`. `CancelarPagamento` sem pagamento responde
  `PagamentoCancelado` com `PagamentoId = null`.
- Ids opcionais/desconhecidos nos comandos de compensacao nao geram `ValidationException` (a saga ficaria sem
  resposta): `Guid.Empty` em `CancelarOrcamento`/`CancelarPagamento` e tratado como ausente (localiza pela ordem
  de servico); `EstornarPagamento` com `PagamentoId` inexistente responde `EstornoFalhou`.
- `Motivo` dos comandos de compensacao nao e validado (contrato nao o restringe): o handler usa
  `NormalizadorDeMotivo` (texto padrao se vazio, truncado em 500) para sempre responder a saga.
- `ValidationBehavior` usa `where TRequest : notnull` (nao `IRequest<TResponse>`) para tambem valer
  nos commands sem retorno (`IRequest`) e cria um `ValidationContext` por validador.

## Infrastructure e API (decisoes da onda 2)

- **Persistencia**: `BillingDbContext : DbContext, IUnitOfWork`, EF Core 9.0.22, dinheiro `decimal(18,2)`, `Version` como
  token de concorrencia (incrementado no `SaveChangesAsync` para agregados `Modified`, como no monolito), `ValueGeneratedNever`
  em todo Id gerado pelo dominio, indices unicos por `Orcamento.OrdemDeServicoId`, `Pagamento.OrdemDeServicoId`,
  `TentativaPagamento.GatewayPaymentId`, `WebhookRecebido.RequestId`, `CancelamentoPrevio(OrdemDeServicoId, Tipo)` e
  `Estorno.PagamentoId`. Migration `Inicial` em `Infrastructure/Data/Migrations` (inclui inbox/outbox/outbox-state e
  `Estornos.AbandonadoEm`).
  Domain events nao existem aqui (a onda 1 decidiu nao levanta-los): nao ha interceptor de dominio.
- **Mensageria** (`AddMensageria`): ver ADR 0002. `MassTransitHostOptions.WaitUntilStarted = true`; bus outbox +
  consumer outbox, `IsolationLevel` NUNCA definido (nunca `Serializable`); filtros `UseMessageRetry` -> outbox ->
  consumers no endpoint `Filas.Billing`; `EnableRetryOnFailure` do EF so quando `IScopedConsumeContextProvider.HasContext`
  e falso. Pontos de troca para teste: `Mensageria:Transporte` (`RabbitMq`|`EmMemoria`) e `Mensageria:Persistencia`
  (`EntityFramework`|`Nenhuma`). `MassTransitIntegrationEventPublisher` copia o `MessageId` do contrato para o do envelope.
  Valor sem nome em `Transporte`/`Persistencia` (o binder aceita `0`, `9`) e recusado em QUALQUER ambiente, tanto pelo validador quanto pelo
  `AddMensageria`, que e fail-closed (`switch` que lanca no valor desconhecido; nunca cai em "memoria" por exclusao).
  **`EmMemoria` e `Nenhuma` so sao aceitos em `Development` e `Testing`**: `ValidadorDasOpcoesDaMensageria` (`ValidateOnStart`) derruba a
  partida em qualquer outro ambiente, com mensagem em portugues, porque sem RabbitMQ e sem outbox a API sobe "saudavel" mas perde
  mensagem em silencio. **O k8s (F7) NAO pode usar `ASPNETCORE_ENVIRONMENT=Development` nem as chaves em memoria**: usar `Production`
  (ou `Staging`) com `Mensageria__Transporte=RabbitMq` e `Mensageria__Persistencia=EntityFramework` (os padroes). `Testing` existe
  so para os testes do proprio repo.
- **Exclusividade por ordem de servico**: todo consumer pede `sp_getapplock` (dono `Transaction`, espera maxima de 20 s, menor que o
  timeout de 30 s do comando SQL para o erro final ser sempre o do servidor) da ordem antes do handler
  (`IBloqueioPorOrdemDeServico`). Sem ele, Gerar e Cancelar da mesma ordem processados juntos nao se enxergam (tombstone e
  orcamento coexistem e o cancelamento se perde); `SemBloqueioPorOrdemDeServico` quando `Persistencia=Nenhuma`.
- **Gateways**: `Pagamento:Provider` = `MercadoPago` (padrao) | `Fake`. O Fake so em `Development` (valida na partida).
  `FakePagamentoGateway` e singleton, em memoria, com estado manipulavel (aprovar/recusar preferencia, falha de estorno).
  `MercadoPagoGateway` usa o SDK `mercadopago-sdk` 3.7.1 com `HttpClient` injetado (`MercadoPago:TimeoutSegundos`, 10),
  token por requisicao, retry do SDK desligado, 4xx definitivo -> `PagamentoRecusadoPeloGatewayException` (401/403/408/409/
  423/425/429 e 5xx NAO: sao falha tecnica), 404 de `GET /v1/payments/{id}` -> `null`, `Valor` com 2 casas.
- **Assinatura do webhook** (`AssinaturaWebhookMercadoPago`): HMAC-SHA256 do manifest `id:<data.id minusculo>;request-id:<x-request-id>;ts:<ts>;`,
  tempo constante, tolerancia `MercadoPago:ToleranciaDaAssinaturaSegundos` (300). Com `Provider=Fake` o segredo e `Fake:WebhookSecret`.
- **Reconciliador** (`ReconciliadorDePagamentosBackgroundService`): a cada `Billing:ReconciliadorIntervaloSegundos` (300) lista
  ate `ReconciliadorTamanhoDoLote` pagamentos (`ListarPagamentosParaReconciliacaoQuery`) e, em escopo proprio por pagamento,
  executa `ReconciliarPagamentoCommand`: reenvia `EstornarPagamento` (MessageId deterministico) enquanto
  `Pagamento.EstornoPendente` e ainda nao abandonado (ao atingir `ReconciliadorMaximoDeTentativasDeEstorno` o estorno e marcado
  `Estorno.AbandonadoEm`, com log de erro e `estornos_abandonados_total` **uma unica vez por pagamento**; dai em diante o pagamento
  sai da consulta e o staff refaz o estorno, o que limpa a marca) e estorna no gateway as cobrancas em duplicidade
  (`refund-duplicidade-{payment.id}`). Usa `TimeProvider`. `Billing:ReconciliadorHabilitado=false` desliga; habilitado, intervalo/lote
  (1 a 1000)/teto de tentativas > 0 sao validados na partida (`ValidadorDasOpcoesDoBilling`). A consulta SQL espelha o predicado do
  dominio (teste de infra confere).
- **Health** (mesmo desenho do OS Service e da Execucao): `GET /health` e liveness puro, 200 sempre, **sem dependencias** (exclui os checks
  da tag `ready`: uma queda de dependencia nao pode reiniciar o pod). `GET /health/ready` (anonimo, corpo JSON `status` + `checks.{nome}.{status,descricao}`
  escrito por `ProntidaoResponseWriter`, que **nunca** escreve a excecao: ela traz host, porta e credencial) roda os checks `ready`:
  `sqlserver` (`SqlServerHealthCheck`, `CanConnectAsync` com limite de 3 s) -> **503** (`Unhealthy`) so com o banco fora, porque sem ele a API nao
  aceita escrita; `rabbitmq` (`RabbitMqHealthCheck`, conexao AMQP de verdade, `RabbitMQ.Client` que ja vem com o `MassTransit.RabbitMQ`, 3 s, so com
  `Mensageria__Transporte=RabbitMq`) -> **`Degraded`, HTTP 200** com o broker fora, porque o outbox segura as mensagens e tirar o pod de rotacao por
  causa do broker viraria indisponibilidade total; `masstransit-bus` (registrado pelo MassTransit) com a falha rebaixada a `Degraded`. Provado com
  Testcontainers: RabbitMQ parado (`rabbitmqctl stop_app`) = ready 200 com `rabbitmq` Degraded, escrita aceita (webhook) e `PagamentoAprovado` entregue
  quando o broker volta; banco OFFLINE = ready 503 e `/health` 200.
  **Limites de tempo**: os dois checks desistem em 3 s tambem contra um servidor que aceita a conexao TCP e nao responde (testado com um `TcpListener`
  mudo); o cliente AMQP sozinho levava ~8 s para desistir, entao o `RabbitMqHealthCheck` usa `WaitAsync` e fecha a conexao que abrir depois do
  limite (em TODO caminho de falha, inclusive quando quem cancelou foi o chamador: o cancelamento do chamador propaga, nunca vira `Degraded`, mas a
  tentativa abandonada ainda e fechada/observada). Assim a prontidao termina antes do `timeoutSeconds: 5` da readiness. Os testes de tempo usam a janela
  2,5 s a 3,8 s (um limite de 4,5 s reprova); o construtor `internal` do check recebe a fabrica de conexao e `Health/RabbitMqHealthCheckTests` prova que a
  conexao que completa depois do limite (ou depois do cancelamento) e fechada.
  **Risco de exposicao e cache**: `/health/ready` e anonimo e o wildcard/passthrough da APIM (`/billingserver/*`) o repassa a internet; sem cache cada
  requisicao abriria uma conexao SQL e uma AMQP (vetor de esgotamento de conexoes). `HealthCheckComCache` guarda o resultado de `sqlserver` e
  `rabbitmq` por `Health__ProntidaoCacheSegundos` (padrao **5 s**; 0 desliga, e os testes de infraestrutura usam 0 para ver cada queda na hora):
  as requisicoes simultaneas esperam uma unica avaliacao e excecao/cancelamento nunca e guardado. O custo fica limitado a 1 conexao SQL + 1 AMQP a cada
  5 s, qualquer que seja a taxa de requisicoes; o cache e menor que o `periodSeconds` da readiness (30 s), entao cada probe ve uma medicao nova. O corpo
  so traz nome, status e descricao fixa dos checks. **Mitigacao pendente (F9/Infra)**: policy da APIM bloqueando `/health/ready` (e `/metrics`) vindos de
  fora, ja que a probe do Kubernetes bate direto no pod e nunca passa pela APIM; a APIM so repassa hoje.
  **Auto-pause do Azure SQL serverless**: a readiness NAO e o que o impede. O outbox do MassTransit consulta o banco a cada `Mensageria__OutboxQueryDelayMs`
  (1000 ms) com o pod de pe; o `periodSeconds: 30` so reduz a carga das probes.
  O alerta operacional do broker (e do tamanho do outbox) ainda nao existe como metrica: nao ha gauge de mensagens pendentes no outbox; fica como
  pendencia da F9.
- **API**: `/metrics` (prometheus-net, tambem expoe o Meter `OficinaMecanica.Billing`) anonimo; controllers com
  `[Authorize]` (JWT RS256 via JWKS: `JwtSettings:Issuer/Audience/JwksUri`, `RequireHttps` so se o JwksUri for https);
  `WebhooksController` anonimo; `DevPagamentosController` so existe com `Provider=Fake` + `Development` (feature provider
  remove o controller, a rota nem aparece no Swagger). `ExceptionHandlingMiddleware`: Domain/Validation 400, KeyNotFound 404,
  `AssinaturaWebhookInvalidaException` 401, `PagamentoDoGatewayIndisponivelException` 503 + `Retry-After`,
  `DbUpdateConcurrencyException`/violacao de indice unico 409, demais 500 sem detalhe. Migration automatica so em Development.
- **Metricas** (`MetricasDoBilling`, Meter `OficinaMecanica.Billing`): `divergencias_de_pagamento_total`,
  `aprovacoes_tardias_total`, `aprovacoes_duplicadas_total`, `orcamentos_gerados_total`, `pagamentos_aprovados_total`,
  `pagamentos_recusados_total` (tentativas), `pagamentos_recusados_pelo_gateway_total` (solicitacao cancelada porque o gateway recusou
  criar a preferencia: pode ser defeito do nosso request, entao tambem vai como `LogError`), `pagamentos_estornados_total`,
  `estornos_abandonados_total`. Todos contam (e os avisos de log de erro saem) so depois do `SaveChanges`; dentro de um consumer esse
  save ainda nao e commit, entao um rollback posterior (retry da mensagem) pode contar de novo. Em producao exportar com
  `OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE=delta` (k8s, F7).
  **`/metrics` (prometheus-net)**: o Meter aparece como `oficinamecanica_billing_*` com `# TYPE ... gauge` (o adaptador do
  prometheus-net expoe o contador como gauge) e so depois do primeiro incremento que ele observa: incrementos anteriores ao
  primeiro scrape de `/metrics` nao aparecem, e um contador nunca incrementado nem e listado. Para valores confiaveis use o
  export OTLP (`Otel__Endpoint`); `/metrics` serve para os contadores HTTP e para uma olhada local.

## Requisitos da onda 2: todos atendidos (referencia)

Reconciliador (inclusive `AprovacaoDuplicada`), retry do MassTransit (nao repete Validation/Domain/AssinaturaInvalida; repete
indisponibilidade do gateway), publisher com copia do `MessageId` (acessor por tipo compilado uma vez), `HttpClient` com timeout
curto, adaptador com normalizacao/404/4xx e sem corpo do SDK em mensagem, consumer de `EstornarPagamento`, metricas com
`System.Diagnostics.Metrics`.

## Pendencias e requisitos cruzados

- **F7 (CI/k8s)**: entregue (secao "CI/CD e Kubernetes" abaixo). O Deployment usa `ASPNETCORE_ENVIRONMENT=Production` (nunca
  `Development`: habilitaria migration automatica e o gateway Fake), RabbitMq + EntityFramework e
  `OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE=delta`. O que sobra e da publicacao (lista no fim da secao).
- **F9**: README final, evidencias de cobertura e a metrica/alerta do broker e do outbox (gauge de mensagens pendentes no outbox e alerta de
  `rabbitmq` `Degraded` por mais de N minutos): hoje o Degraded so aparece no corpo de `/health/ready`.
- **Requisito cruzado (saga do OS Service)**: o estorno automatico por aprovacao tardia faz o Billing emitir
  `PagamentoEstornado`/`EstornoFalhou` com a saga FORA do estado `Estornando` (ela ja esta em `Cancelada` ou
  `CancelandoPagamento`); a saga precisa de `Ignore` desses dois eventos nesses estados. O mesmo vale para o estorno refeito pelo
  reconciliador ou pelo staff (`POST /api/pagamentos/{id}/estornar`): `PagamentoEstornado` e o que tira a saga de
  `EstornoPendenteManual`.
- O Billing nao cancela o orcamento em cascata quando recebe `PagamentoNaoSolicitado`/falha do gateway: a
  saga precisa enviar `CancelarOrcamento` (o `CancelarPagamento` ja cancela o orcamento `Aprovado`).
- A saga deve esperar `OrcamentoGerado`/`PagamentoSolicitado` repetidos com o mesmo `MessageId` (comando reentregue): usar o inbox
  e tratar o evento como idempotente. `PagamentoCancelado.PagamentoId` pode ser `null`.

## CI/CD e Kubernetes (F7)

Decisoes no ADR 0003. Pipeline proprio em `.github/workflows/ci.yml` (nome `CI`; gatilhos push/PR em `main`/`release` e
`workflow_dispatch`; `concurrency` por ref, `cancel-in-progress` so em PR; `permissions` minimas, `id-token: write` so nos jobs
de push da imagem e deploy; o job que roda Trivy e as actions de terceiros so tem `contents: read`, e `verificar-workflow.sh` reprova o CI se uma action de terceiros voltar para um job com `id-token`; `actions/checkout` com `persist-credentials: false`).
Todas as actions com versao pinada por **tag de major/minor**, que e mutavel: pinar por SHA e requisito BLOQUEANTE da publicacao (ultima secao). O bloco `env:` do topo do `ci.yml` concentra o que muda de um servico
para outro (ACR, imagem, AKS, Key Vault, secret da connection string, pastas, projetos do EF).

| Job (`name`) | Quando | O que faz |
|---|---|---|
| `build-and-test` (**`Build & Test`**, check obrigatorio) | sempre | restore, build Release, `testar-scripts.sh` (casos de reprovacao do gate, do guardrail e da separacao de privilegios do `ci.yml` + mutantes), `verificar-masstransit.sh`, `validar-k8s.sh` (kubeconform), `exportar-swagger.sh --verificar`, testes `Category!=Infra` e `Category=Infra`, `cobertura.sh relatorio` (merge + gate), `dorny/test-reporter` com os `.trx`, Job Summary de cobertura, artifacts `openapi.json`, `relatorio-de-cobertura`, `resultados-dos-testes` e `cobertura-opencover` |
| `quality` (**`Quality`**, check obrigatorio) | `needs: build-and-test`; pulado em PR de fork | SonarCloud (`dotnet-sonarscanner` begin/build/end), importa o OpenCover do artifact, `sonar.qualitygate.wait=true`; falha com mensagem clara se faltar `SONAR_TOKEN`/variavel |
| `scan-image` (`Scan Image (Trivy)`) | so push/dispatch em `main`/`release`, `needs: [build-and-test, quality]` | **`contents: read` SOMENTE** (sem `id-token`): build local (buildx, cache gha, `load: true`), **Trivy antes do push** (HIGH/CRITICAL com correcao, `ignore-unfixed`), `docker save` como artifact `imagem-escaneada`. E o unico job que executa actions de terceiros com a imagem, e nao consegue pedir o token OIDC |
| `build-and-push-image` | idem, `needs: scan-image` | `id-token: write`; so actions do GitHub/Microsoft: baixa o artifact, `docker load`, `azure/login` (OIDC), `az acr login`, push da MESMA imagem escaneada (sem rebuild); tags `oficinamecanica-billing:<sha>` sempre e `:latest` so na `main` |
| `deploy-to-aks` | idem, `needs: build-and-push-image`; `concurrency: deploy-aks-billing` (`cancel-in-progress: false`: `main` e `release` fazem deploy no mesmo Deployment e enfileiram) | OIDC, **migrations** (`dotnet ef database update` com a connection string lida do Key Vault `billing-sql-connection-string`, ate 5 tentativas por causa do serverless), `aks-set-context` (admin, `rgfiap`/`aksfiap`), `envsubst '${IMAGE}'` (com guarda `${IMAGE:?}`: imagem vazia falha antes do apiserver), `kubectl apply`, `set image`, `rollout status` (300 s). Namespace `default` |

**O que o repositorio do GitHub precisa ter (Settings > Secrets and variables > Actions) quando existir:**

| Tipo | Nome | Valor |
|---|---|---|
| variavel | `AZURE_CLIENT_ID` | `terraform output billing_github_actions_client_id` do `OficinaMecanica.Infra` (so existe depois de preencher `billing_github_repo_id`) |
| variavel | `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` | os mesmos dos demais repositorios |
| variavel | `SONAR_PROJECT_KEY`, `SONAR_ORGANIZATION` | projeto e organizacao no SonarCloud |
| secret | `SONAR_TOKEN` | token do SonarCloud; e o **unico** secret de longa duracao (o SonarCloud nao suporta OIDC). Criar tambem em Settings > Secrets > **Dependabot**: os PRs do Dependabot nao enxergam os secrets de Actions e reprovariam em `Quality` |

Nenhum segredo do Azure: o login e OIDC (a identidade so aceita `push` em `main`/`release`, por isso `Build & Test` e `Quality` em PR nunca
tocam o Azure). Os valores reais do Mercado Pago, do RabbitMQ e do banco vivem so no Key Vault.

**Scripts** (`scripts/`, todos com `bash`; o `ci.yml` e o `ci-local.sh` chamam os mesmos):

| Script | Funcao |
|---|---|
| `ci-local.sh [--sem-infra]` | roda localmente os mesmos passos do `Build & Test` (restore, build, guardrail, k8s, OpenAPI, testes, cobertura com gate); `--sem-infra` pula o Testcontainers e, sem a cobertura completa, nao avalia o gate |
| `cobertura.sh [fase]` | `tudo` (padrao), `testes-sem-infra`, `testes-infra`, `relatorio`; cada projeto roda uma vez com OpenCover + `.trx`; `COBERTURA_MINIMA` (80) e `COBERTURA_SEM_BUILD=true` (CI) |
| `verificar-cobertura.sh <Summary.txt> [min]` | o gate: falha (exit 1) abaixo do minimo; calcula de `Covered lines`/`Coverable lines` (nao do `Line coverage`, que o ReportGenerator arredonda: 79,95% apareceria como 80%); vale para o numero "com exclusoes" |
| `testar-scripts.sh [--sem-mutantes]` | **71 casos e 32 mutantes.** Testa com entradas sinteticas o gate (79,9% reprova, 80,0% aprova, numero ausente = exit 2, `COBERTURA_MINIMA` vazio = 80), o guardrail (MassTransit/`MassTransit.EntityFrameworkCore` 9.x direto e transitivo = exit 1, so 8.x = 0) e o `verificar-workflow.sh` (workflows sinteticos com a action do Trivy no job de push = exit 1, e o `ci.yml` real = 0); exercita de ponta a ponta o `cobertura.sh relatorio` sobre um OpenCover sintetico (79% reprova, 81% aprova, o gate vale para o numero "com exclusoes" de Migrations/`Program.cs`/`DependencyInjection*`/EF Configurations, o aviso de `[ExcludeFromCodeCoverage]` lista o arquivo de `src/` (fora de `obj/`) e conta 2 no resumo, fase invalida e relatorio sem OpenCover = exit 2: prova que o gate esta LIGADO ao relatorio), o `validar-k8s.sh` (kubeconform em Docker sobre manifests sinteticos; pulado com aviso sem Docker), o `exportar-swagger.sh --verificar` (com um `dotnet` de mentira: em dia aprova, divergente/sem `docs/openapi.json` reprova, argumento invalido = exit 2) e o `ci-local.sh` (argumento invalido = exit 2), e confere que `ci.yml` e `ci-local.sh` chamam os mesmos passos. Depois planta 32 defeitos em copias fora do repo (gate que sai 0, minimo padrao 0, guardrail que aceita o major 9, verificador que libera `aquasecurity/*`, chamada do gate apagada do `cobertura.sh`, cada filtro de exclusao removido, `-strict` retirado...) exigindo que cada um seja detectado. Roda no `Build & Test` e no `ci-local.sh`; e por ele que o `verificar-workflow.sh` entra no CI e no ci-local; ~2,5 min |
| `verificar-masstransit.sh` | falha se qualquer pacote `MassTransit*`, direto ou transitivo, resolver para major diferente de 8 |
| `verificar-workflow.sh [ci.yml]` | falha (exit 1) se um job que declara `id-token` executa action fora de `actions/*` e `azure/*`, ou se `id-token`/`write-all` esta no nivel do workflow; ignora comentarios do YAML; exit 2 para arquivo ausente/sem jobs |
| `exportar-swagger.sh [--verificar] [--sem-build]` | `swagger tofile` (ferramenta fixada em `dotnet-tools.json`) para `docs/openapi.json`; com `--verificar` gera em `artifacts/openapi.json` e falha se o versionado difere |
| `validar-k8s.sh` | renderiza `${IMAGE}` e valida `k8s/billing/` com `kubeconform -strict` (Docker; o CRD do SecretProviderClass e ignorado) |
| `aplicar-ruleset.sh [owner/repo] [--dry-run]` | cria/atualiza o ruleset via `gh api`; **so roda na publicacao** |

- O `swagger tofile` executa `Program.Main` e INICIA o host: o script sobe a API em `Testing` com transporte em memoria, sem outbox e
  reconciliador desligado, e credenciais de fachada do Mercado Pago (so para passar a validacao de opcoes). O documento e o mesmo de
  `Production` (o controller de simulacao so existe em `Development`). O arquivo e gravado com LF (`.gitattributes` fixa `docs/openapi.json`
  e `*.sh` em LF).
- **Protecao de branch**: `.github/rulesets/protect-main-release.json` (alvo `main` e `release`; `deletion`, `non_fast_forward`,
  `pull_request` com 0 aprovacoes e merge/squash/rebase, `required_status_checks` com `Build & Test` e `Quality`). Replica o ruleset dos
  outros repositorios (aqui sem acesso ao GitHub para comparar: confirmar na publicacao).
- **Dependabot**: nuget, github-actions e docker, semanal; ignora major de `MassTransit*` (9.x e comercial), de
  `Microsoft.EntityFrameworkCore*` (continua em 9.x por causa do `net8.0`) e das imagens base `dotnet/aspnet`/`dotnet/sdk`.

**`k8s/billing/`** (namespace `default`, nomes `oficinamecanica-billing`, container `api`, Secret `billing-secrets`):
- `deployment.yaml`: sem `replicas` (o HPA manda: 1 a 2), `maxSurge: 0`/`maxUnavailable: 1` (no unico sem CPU para pod extra; com uma replica ha
  uma janela sem pod no rollout, e a saga espera no RabbitMQ). Unico placeholder `${IMAGE}` (o CI renderiza com `envsubst`; aplicar
  sem renderizar falha com nome de imagem invalido de proposito). `runAsNonRoot` (uid 1654, o usuario `app` da imagem),
  `readOnlyRootFilesystem` com `emptyDir` em `/tmp`, `capabilities: drop ALL`, `automountServiceAccountToken: false`.
  Requests `50m`/`192Mi`, limits `300m`/`384Mi`: CPU e o recurso escasso do no; medido no container, a API usa ~90 MiB em repouso e ~110 MiB
  depois de algumas centenas de requisicoes, entao o request de memoria deixa o repouso em ~45% (abaixo dos 80% do HPA, que e sobre o request).
- **Probes**: `startupProbe` em `/health` de 120 s (24 x 5 s): o Kestrel so abre a porta depois do MassTransit subir (ate 60 s com o broker
  lento; sem broker o processo cai ao fim desse prazo), entao `/health` nao responde antes. `livenessProbe` em `/health` (liveness puro) e
  `readinessProbe` em `/health/ready` (banco 503, broker `Degraded` 200; ver "Health"), ambas com `timeoutSeconds: 5` (os checks de prontidao levam
  ate 3 s e o padrao do Kubernetes e 1 s) e `periodSeconds: 30` (so limita a carga das probes: o auto-pause do Azure SQL serverless ja e
  impedido pelo outbox do MassTransit, que consulta o banco a cada segundo; ver "Health", incluindo o cache de 5 s e o risco do endpoint anonimo).
- `ingress.yaml`: prefixo `/billing(/|$)(.*)` com `use-regex` + `rewrite-target: /$2` (`ImplementationSpecific`); o monolito segue catch-all em `/`.
  **Sem** `x-forwarded-prefix`: o `X-Forwarded-Prefix: /billingserver` vem da policy da APIM e o ingress o repassa.
- `secret-provider-class.yaml`: os 5 secrets do Key Vault `kvfiap` (`billing-sql-connection-string`, `mercadopago-access-token`,
  `mercadopago-webhook-secret`, `rabbitmq-username`, `rabbitmq-password`) sincronizados no Secret `billing-secrets`; identidade e tenant sao
  os mesmos literais do monolito. RabbitMQ em `rabbitmq.messaging.svc.cluster.local:5672`; JWKS e `NotificationBaseUrl` pela APIM
  (`segurancaserver` / `billingserver`).
- **Desvio do plano (secao 8)**: os testes de Infra (Testcontainers) rodam num step separado do MESMO job `Build & Test`, nao num job proprio. A cobertura
  e o gate precisam do OpenCover de todos os projetos no mesmo workspace e o job de Infra reconstruiria tudo e transferiria artifacts; o resultado de
  cada grupo continua separado (steps, `.trx`, `dorny/test-reporter`) e os nomes dos checks obrigatorios ficam estaveis. Justificativa completa no ADR 0003.
- `kubectl apply --dry-run=client` nao roda sem API server (precisa de discovery); a validacao offline e o `kubeconform -strict`.

**Clonar para outro servico (Execucao, OS Service)**: trocar o bloco `env:` do `ci.yml`; os nomes de projeto/DLL no topo de
`exportar-swagger.sh`, os projetos e filtros de `cobertura.sh` e `coverage.runsettings`, `K8S_DIR`/imagem em `validar-k8s.sh`, o repo padrao do
`aplicar-ruleset.sh`; os nomes, labels e a lista de secrets dos manifests; o MassTransit/EF/Mongo do guardrail e do Dependabot conforme o servico.

**Fica para a publicacao**: criar o repositorio `willianrmattos/OficinaMecanica.Billing` e empurrar; preencher `billing_github_repo_id` no
`OficinaMecanica.Infra` (aplicar) e configurar as variaveis OIDC; criar o secret `SONAR_TOKEN` e as variaveis do Sonar (e o projeto no
SonarCloud); rodar uma vez o CI para os checks existirem e entao `bash scripts/aplicar-ruleset.sh`; **BLOQUEANTE antes do primeiro push
real: pinar `aquasecurity/trivy-action`, `dorny/test-reporter` e as demais actions de terceiros (`docker/*`, `azure/*`, `SonarSource`...) por SHA completo
verificado no repositorio de cada action** (hoje estao por tag, que e mutavel, e as tags nao puderam ser conferidas sem tocar o GitHub; o Dependabot de
`github-actions` mantem os SHAs depois); confirmar a premissa da APIM (concatenacao do path do backend `/billing`) e criar a policy que bloqueia
`/health/ready` e `/metrics` vindos de fora; `kubectl describe node` com folga de CPU antes do primeiro deploy.

## Contratos

`OficinaMecanica.Contracts` e uma **copia** (mesmos arquivos e namespaces `Commands`/`Events`/`Common`)
do subconjunto usado pelo Billing, a partir de `E:\FIAP\Pos\OficinaMecanica\src\OficinaMecanica.Contracts`.
Os goldens JSON em `tests/OficinaMecanica.Contracts.Tests/Golden/` devem ter bytes identicos aos do
monolito (`.gitattributes` fixa LF). Nunca editar aqui: a mudanca nasce no monolito e e re-copiada
(evolucao so aditiva).

## Variaveis de ambiente (padrao `Secao__Chave`)

| Variavel | Padrao | Observacao |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | vazio | SQL Server (`BillingDb`); em producao, Key Vault (usuario dedicado, nunca o admin) |
| `JwtSettings__Issuer` / `__Audience` / `__JwksUri` | Seguranca / Client / APIM | JWKS do `OficinaMecanica.Seguranca` |
| `RabbitMq__Host` / `__Port` / `__VirtualHost` / `__Username` / `__Password` | localhost / 5672 / `/` / - / - | broker |
| `Pagamento__Provider` | `MercadoPago` (compose/Development: `Fake`) | `Fake` so em Development |
| `Fake__WebhookSecret` | - | obrigatorio com `Fake` |
| `MercadoPago__AccessToken` / `__WebhookSecret` / `__NotificationBaseUrl` | - | obrigatorios com `MercadoPago`; so env/Key Vault |
| `MercadoPago__TimeoutSegundos` / `__ToleranciaDaAssinaturaSegundos` | 10 / 300 | |
| `MercadoPago__BackUrlSucesso` / `__BackUrlPendente` / `__BackUrlFalha` | vazio | opcionais (`auto_return=approved` se houver sucesso) |
| `Mensageria__Transporte` / `__Persistencia` | `RabbitMq` / `EntityFramework` | `EmMemoria`/`Nenhuma` so em Development/Testing (a partida falha nos demais) |
| `Mensageria__RetryIntervalosMs__0..n` | 500, 2000, 10000, 30000, 60000 | |
| `Mensageria__OutboxQueryDelayMs` / `__TimeoutDeInicioSegundos` | 1000 / 60 | |
| `Billing__ReconciliadorHabilitado` | true | |
| `Billing__ReconciliadorIntervaloSegundos` / `__ReconciliadorTamanhoDoLote` / `__ReconciliadorMaximoDeTentativasDeEstorno` | 300 / 50 / 10 | > 0 (lote ate 1000); validados na partida quando habilitado |
| `Health__ProntidaoCacheSegundos` | 5 | cache do resultado de `/health/ready` (`sqlserver`, `rabbitmq`); 0 desliga (so testes de infraestrutura) |
| `Otel__Endpoint` | vazio | OTLP gRPC; vazio desliga traces/metricas OTel |

## Como rodar

```bash
cp .env.example .env            # preencher (MSSQL_SA_PASSWORD, RABBITMQ_*, FAKE_WEBHOOK_SECRET); .env e ignorado pelo git
docker compose --profile standalone up -d --build   # API :5001, SQL :1435, RabbitMQ :5673/:15673 (proprio); use RABBITMQ_HOST=rabbitmq
docker compose up -d                                 # sem o broker proprio: usa o RabbitMQ do compose do OficinaMecanica
                                                     # (RABBITMQ_HOST=host.docker.internal, porta 5672; suba o monolito antes)
# Swagger em http://localhost:5001/ ; fluxo sem Mercado Pago: POST /dev/pagamentos/{preferenceId}/aprovar
```
Portas do host (nao colidem com os outros composes): API 5001, SQL 1435, RabbitMQ 5673/15673 (monolito 5000/1433/5672/15672,
Seguranca 7071/1434). Todas sobrescrevem por `BILLING_*_HOST_PORT` e o prefixo dos containers por `BILLING_CONTAINER_PREFIX`
(padrao `oficinamecanica-billing`).

## Comandos Uteis

```bash
dotnet build -c Release                                           # sem avisos
dotnet test -c Release --filter "Category!=Infra"                 # unitarios + pipeline HTTP (sem Docker)
dotnet test -c Release --filter "Category=Infra"                  # Testcontainers: SQL Server + RabbitMQ (precisa de Docker)
dotnet tool restore                                                # dotnet-ef 9.0.22 e reportgenerator (dotnet-tools.json)
dotnet ef migrations add <Nome> --project src/OficinaMecanica.Billing.Infrastructure --startup-project src/OficinaMecanica.Billing.API --output-dir Data/Migrations
bash scripts/cobertura.sh                                          # cobertura mesclada (abaixo); aceita fase: testes-sem-infra | testes-infra | relatorio
bash scripts/ci-local.sh                                           # os mesmos passos do job "Build & Test" do CI (--sem-infra se nao ha Docker)
bash scripts/exportar-swagger.sh                                   # regera docs/openapi.json (rode e versione quando a API mudar)
bash scripts/validar-k8s.sh                                        # kubeconform nos manifests (Docker)
```

## Testes e cobertura

- `Integration.Tests` roda em serie (`DisableTestParallelization`): Meter, variaveis e containers sao compartilhados.
  Os testes `[Trait("Category","Infra")]` usam Testcontainers com imagens fixadas por digest, `MSSQL_MEMORY_LIMIT_MB=1024`, um banco e um
  virtual host por teste, e provam: outbox atomico (commit entrega, rollback nao), mensagem duplicada (mesmo `MessageId`) processada 1x,
  concorrencia da mesma ordem, tombstone, webhook ponta a ponta com o gateway Fake, reconciliador, retry do EF fora de consumer,
  esquema/indices da migration e repositorios.
- **Teste que espera uma tarefa nao pode travar o run**: todo `await` de teste que depende de um `TaskCompletionSource` ou de uma avaliacao que o
  codigo sob teste pode deixar de liberar passa por `AguardarAsync` (`Support/EsperaComLimite.cs`, limite de 10 s, falha com a explicacao do que
  deveria ter acontecido). Sem isso, remover a protecao que o teste exercita (a espera pela trava do `HealthCheckComCache` respeitar o token, o `WaitAsync`
  do `RabbitMqHealthCheck`) deixaria o teste pendurado em vez de reprova-lo (provado em copia: as duas remocoes reprovam em ~10 s por teste, sem hang).
  Rede de seguranca: `scripts/cobertura.sh` roda `dotnet test` com `--blame-hang-timeout 5m --blame-hang-dump-type none`, entao um teste preso por
  qualquer outro motivo derruba o testhost em 5 min (sem isso so o `timeout-minutes: 45` do job o encerraria, e o `ci-local.sh` nao tem timeout).
  A tentativa de conexao abandonada pelo `RabbitMqHealthCheck` tem a excecao observada: os testes `...DeveTerAExcecaoObservada` fabricam a tentativa
  tardia que falha, forcam a coleta (`GC.Collect` + `WaitForPendingFinalizers`) e conferem, pela instancia da excecao, que `TaskScheduler.UnobservedTaskException`
  nao a viu (remover o `catch` de `FecharSeConectarDepoisAsync` reprova os dois).
- **Cobertura de linhas mesclada** (todos os projetos de teste, Infra incluido): `bash scripts/cobertura.sh` roda cada projeto com
  `coverage.runsettings` (coverlet, OpenCover) e une com `reportgenerator`. Numero **bruto** conta tudo; **com exclusoes** tira so:
  `**/Migrations/**`, `**/Program.cs`, `**/DependencyInjection*.cs` e `**/Data/Configurations/**` (EF Configurations). Meta: >= 80%,
  **gate no CI** (`COBERTURA_MINIMA`, padrao 80) sobre o numero com exclusoes (`scripts/verificar-cobertura.sh`); os mesmos arquivos
  vao para o SonarCloud (`sonar.coverage.exclusions`). O gate usa a contagem exata de linhas, sem o arredondamento do resumo.
  **`[ExcludeFromCodeCoverage]` e honrado** (`coverage.runsettings`): o codigo marcado some, em silencio, das DUAS medicoes e do gate. **Uso proibido para
  "esconder" codigo sem justificativa em ADR**; hoje nao ha nenhum em `src/`, e o `cobertura.sh relatorio` lista as ocorrencias no log e no Job Summary.
  Medicao mais recente: **bruta 98,5%** (3467/3518 linhas; API 90,9% por causa do `Program.cs`, Domain 99,3%, Application 100%,
  Infrastructure 98,8%, Contracts 100%) e **com exclusoes 99,59%** (1950/1958). Testes: Contracts 41, Domain 190, Application 268,
  Integration 354 sem Docker + 63 de infraestrutura (916 no total), incluindo os de health/readiness.
- O log "API iniciada com sucesso" (`app.Lifetime.ApplicationStarted` em `Program.cs`, fora da cobertura) foi verificado manualmente com o
  compose (aparece depois de `Bus started`) e executando a API em `Production` com configuracao invalida (nao aparece quando a
  validacao de partida a derruba).
- Partida que falha dentro do `WebApplicationFactory` (validacao de opcoes) e uma corrida: ora chega a `OptionsValidationException`, ora um
  `ObjectDisposedException`. `ApiConfiguracaoTests` parte de uma base valida e varia uma unica chave: quando a `OptionsValidationException` chega, afirma a falha unica
  e a mensagem esperadas; quando vem a corrida, aceita somente `ObjectDisposedException` (qualquer outro erro reprova). Tipo e mensagem
  sem corrida ficam no host generico (`ValidadoresDeOpcoesTests` e `PartidaDaConfiguracaoTests`, tambem uma chave por caso).

## NuGet

`nuget.config` local aponta so para `nuget.org`. Ao adicionar pacotes:
```bash
dotnet add <projeto> package <pacote> --source https://api.nuget.org/v3/index.json
```
Versoes fixadas como no monolito (MediatR 12.2.0, FluentValidation 11.9.0, xunit 2.4.2, Moq 4.20.70,
Bogus 35.6.1), exceto `Microsoft.NET.Test.Sdk` 17.12.0: os 3 projetos de teste fixam tambem `System.Net.Http`
4.3.4 e `System.Text.RegularExpressions` 4.3.1, porque o `xunit` 2.4.2 as arrasta (via `NETStandard.Library`
1.6.1) em versoes com aviso de vulnerabilidade High (`dotnet list package --vulnerable --include-transitive` limpo).
Por isso o `.csproj` do `Contracts.Tests` nao e copiado do monolito, so o codigo e os goldens. MassTransit* ficam em `8.5.12` exato (9.x e comercial), EF Core `SqlServer/Design/Tools` em `9.0.22`, `mercadopago-sdk` `3.7.1`;
Serilog/OpenTelemetry/prometheus-net nas versoes do monolito (OpenTelemetry.Instrumentation.Http 1.16.0 e novo).
