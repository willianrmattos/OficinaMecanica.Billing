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

## Estado atual (onda 1)

So existem **Domain**, **Application**, os **Contracts** copiados e os testes. Ainda nao existem
`Infrastructure` (EF Core/outbox/MassTransit/gateway Mercado Pago/validador de assinatura/metricas) nem
`API` (webhook, Swagger, `/health`), Dockerfile, `k8s/` ou CI - ficam para a onda 2. Nenhum pacote
`MassTransit*` entrou ainda: a Application so conhece as portas (`IIntegrationEventPublisher`,
`IPagamentoGateway`, `IAssinaturaWebhookValidator`, `IMetricasDoBilling`).

## Estrutura do Projeto

```
src/
  OficinaMecanica.Contracts/               # COPIA do subconjunto usado pelo Billing (ver "Contratos")
  OficinaMecanica.Billing.Domain/          # Orcamento, Pagamento, TentativaPagamento, Estorno, WebhookRecebido,
                                           # CancelamentoPrevio, enums, DomainException, interfaces de repositorio/UoW
  OficinaMecanica.Billing.Application/     # Commands/Queries (MediatR), Validators (FluentValidation),
                                           # ValidationBehavior, DTOs/mappers, portas (Interfaces/), mensagens de integracao
tests/
  OficinaMecanica.Contracts.Tests/         # goldens JSON (bytes identicos aos do monolito)
  OficinaMecanica.Billing.Domain.Tests/    # sem mocks
  OficinaMecanica.Billing.Application.Tests/  # Moq nas portas
  OficinaMecanica.Billing.Tests.Common/    # builders Bogus (OrcamentoBuilder, PagamentoBuilder, ...) e RelogioFixo
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
- Mesmo padrao de testes do monolito: xUnit + FluentAssertions (sem `Assert.*`) + Moq (so Application.Tests)
  + Bogus em builders de `Tests.Common`

## Regras de mensageria (Application)

- Toda mensagem de integracao sai via `IIntegrationEventPublisher` (`PublishAsync` para evento,
  `SendAsync(fila, ...)` para comando) **antes** do `IUnitOfWork.SaveChangesAsync` do handler (na onda 2
  a implementacao grava no outbox na mesma transacao). Nunca depois do SaveChanges. Os testes provam a
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
    registra a tentativa, `LogWarning` com os ids e `RegistrarAprovacaoDuplicada`; nao estorna sozinho.
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

## Requisitos e pendencias para a onda 2

- **Reconciliador** (BackgroundService): reenviar `EstornarPagamento` para pagamentos em `Estornando`/
  `EstornoFalhou` e em `Cancelado`/`Expirado` com `AprovacaoTardia` cujo estorno nao concluiu; e tratar
  o segundo pagamento aprovado (`AprovacaoDuplicada`), que hoje so gera log + metrica e precisa de estorno
  do pagamento duplicado.
- **Retry do MassTransit**: nao retentar `ValidationException`, `AssinaturaWebhookInvalidaException` (a API
  traduz para 401) nem `DomainException`; retentar as demais (inclusive `PagamentoDoGatewayIndisponivelException`
  e falhas tecnicas do gateway). `MessageId` deterministico + inbox/outbox do MassTransit para dedupe.
- **`IIntegrationEventPublisher`** (impl. com MassTransit): `PublishAsync`/`SendAsync` DEVEM copiar o `MessageId`
  do contrato para o `MessageId` do envelope MassTransit (ADR 0005), senao o inbox nao deduplica pelo id
  deterministico. A porta e `where T : class`, entao usar um acessor cacheado por tipo (ex.: delegate compilado
  por `typeof(T)` que le a propriedade `MessageId`), com teste de infra provando a copia.
- **HttpClient do gateway**: timeout curto (poucos segundos) e sem retry longo no cliente, porque a chamada
  roda com a transacao do consumer aberta; falha tecnica propaga para o retry do MassTransit.
- **Adaptador do gateway** (`IPagamentoGateway`): normalizar `Valor` para 2 casas antes de devolver
  (o dominio compara com igualdade exata); devolver `null` somente para 404 do gateway; traduzir 4xx
  definitivo em `PagamentoRecusadoPeloGatewayException`; nunca propagar corpo de resposta do SDK em
  mensagem que seja persistida ou publicada.
- **Consumer do Billing** deve mapear o comando contratual `EstornarPagamento` para `EstornarPagamentoCommand`
  (inclui `OrdemDeServicoId`), alem dos demais comandos; `IMetricasDoBilling` com
  `System.Diagnostics.Metrics`.
- **Requisito cruzado (saga do OS Service)**: o estorno automatico por aprovacao tardia faz o Billing emitir
  `PagamentoEstornado`/`EstornoFalhou` com a saga FORA do estado `Estornando` (ela ja esta em `Cancelada` ou
  `CancelandoPagamento`); a saga precisa de `Ignore` desses dois eventos nesses estados.
- O Billing nao cancela o orcamento em cascata quando recebe `PagamentoNaoSolicitado`/falha do gateway: a
  saga precisa enviar `CancelarOrcamento` (o `CancelarPagamento` ja cancela o orcamento `Aprovado`).

## Contratos

`OficinaMecanica.Contracts` e uma **copia** (mesmos arquivos e namespaces `Commands`/`Events`/`Common`)
do subconjunto usado pelo Billing, a partir de `E:\FIAP\Pos\OficinaMecanica\src\OficinaMecanica.Contracts`.
Os goldens JSON em `tests/OficinaMecanica.Contracts.Tests/Golden/` devem ter bytes identicos aos do
monolito (`.gitattributes` fixa LF). Nunca editar aqui: a mudanca nasce no monolito e e re-copiada
(evolucao so aditiva).

## Comandos Uteis

```bash
# Build e testes
dotnet build -c Release
dotnet test -c Release

# Cobertura de linhas (Domain + Application), formato OpenCover
dotnet test tests/OficinaMecanica.Billing.Domain.Tests --collect:"XPlat Code Coverage" --results-directory TestResults/Domain -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover
dotnet test tests/OficinaMecanica.Billing.Application.Tests --collect:"XPlat Code Coverage" --results-directory TestResults/Application -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover
```

## NuGet

`nuget.config` local aponta so para `nuget.org`. Ao adicionar pacotes:
```bash
dotnet add <projeto> package <pacote> --source https://api.nuget.org/v3/index.json
```
Versoes fixadas como no monolito (MediatR 12.2.0, FluentValidation 11.9.0, xunit 2.4.2, Moq 4.20.70,
Bogus 35.6.1), exceto `Microsoft.NET.Test.Sdk` 17.12.0: os 3 projetos de teste fixam tambem `System.Net.Http`
4.3.4 e `System.Text.RegularExpressions` 4.3.1, porque o `xunit` 2.4.2 as arrasta (via `NETStandard.Library`
1.6.1) em versoes com aviso de vulnerabilidade High (`dotnet list package --vulnerable --include-transitive` limpo).
Por isso o `.csproj` do `Contracts.Tests` nao e copiado do monolito, so o codigo e os goldens. MassTransit, quando entrar na onda 2, fica em `8.5.12` exato (9.x e comercial).
