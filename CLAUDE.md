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

## Estado atual (onda 2)

Existem **Domain**, **Application**, **Infrastructure** (EF Core 9 + outbox/inbox do MassTransit 8.5.12, consumers,
gateways Mercado Pago/Fake, validador de assinatura, metricas, reconciliador, JWT/JWKS), **API** (webhook, consultas,
Swagger, `/health`, `/metrics`), `Dockerfile`, `docker-compose.yml`, ADRs e os testes (unitarios, pipeline HTTP e
infraestrutura com Testcontainers). Ainda nao existem `k8s/` nem CI (F7) e o README final (F9). Os contratos
sao a copia da onda 1.

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
                                           # webhook, JWT/JWKS), Observability/ (MetricasDoBilling), BackgroundServices/
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
docs/adr/                                  # 0001 (Mercado Pago + gateway abstrato), 0002 (SQL, outbox/inbox)
Dockerfile  docker-compose.yml  .env.example  coverage.runsettings  scripts/cobertura.sh  dotnet-tools.json
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
- **API**: `/health` e `/metrics` (prometheus-net, tambem expoe o Meter `OficinaMecanica.Billing`) anonimos; controllers com
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

- **F7 (CI/k8s)**: `k8s/` (Deployment/Service/Ingress/HPA/SecretProviderClass), `ci.yml` (build, testes, cobertura com gate usando
  `scripts/cobertura.sh`, Sonar, imagem, migration no deploy, guardrail MassTransit 9),
  `OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE=delta` no Deployment e `ASPNETCORE_ENVIRONMENT=Production` (nunca `Development`:
  habilitaria migration automatica e o gateway Fake) com RabbitMq + EntityFramework (padroes de `Mensageria`).
- **F9**: README final, evidencias de cobertura.
- **Requisito cruzado (saga do OS Service)**: o estorno automatico por aprovacao tardia faz o Billing emitir
  `PagamentoEstornado`/`EstornoFalhou` com a saga FORA do estado `Estornando` (ela ja esta em `Cancelada` ou
  `CancelandoPagamento`); a saga precisa de `Ignore` desses dois eventos nesses estados. O mesmo vale para o estorno refeito pelo
  reconciliador ou pelo staff (`POST /api/pagamentos/{id}/estornar`): `PagamentoEstornado` e o que tira a saga de
  `EstornoPendenteManual`.
- O Billing nao cancela o orcamento em cascata quando recebe `PagamentoNaoSolicitado`/falha do gateway: a
  saga precisa enviar `CancelarOrcamento` (o `CancelarPagamento` ja cancela o orcamento `Aprovado`).
- A saga deve esperar `OrcamentoGerado`/`PagamentoSolicitado` repetidos com o mesmo `MessageId` (comando reentregue): usar o inbox
  e tratar o evento como idempotente. `PagamentoCancelado.PagamentoId` pode ser `null`.

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
bash scripts/cobertura.sh                                          # cobertura mesclada (abaixo)
```

## Testes e cobertura

- `Integration.Tests` roda em serie (`DisableTestParallelization`): Meter, variaveis e containers sao compartilhados.
  Os testes `[Trait("Category","Infra")]` usam Testcontainers com imagens fixadas por digest, `MSSQL_MEMORY_LIMIT_MB=1024`, um banco e um
  virtual host por teste, e provam: outbox atomico (commit entrega, rollback nao), mensagem duplicada (mesmo `MessageId`) processada 1x,
  concorrencia da mesma ordem, tombstone, webhook ponta a ponta com o gateway Fake, reconciliador, retry do EF fora de consumer,
  esquema/indices da migration e repositorios.
- **Cobertura de linhas mesclada** (todos os projetos de teste, Infra incluido): `bash scripts/cobertura.sh` roda cada projeto com
  `coverage.runsettings` (coverlet, OpenCover) e une com `reportgenerator`. Numero **bruto** conta tudo; **com exclusoes** tira so:
  `**/Migrations/**`, `**/Program.cs`, `**/DependencyInjection*.cs` e `**/Data/Configurations/**` (EF Configurations). Meta: >= 80%.
  Medicao da onda 2: **bruta 98,2%** (2779/2828 linhas; API 86,2% por causa do `Program.cs`, Domain 99,3%, Application 100%,
  Infrastructure 98,8%, Contracts 100%) e **com exclusoes 99,5%** (1269/1275). Testes: Contracts 41, Domain 190, Application 262,
  Integration 315 sem Docker + 60 de infraestrutura (868 no total).
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
