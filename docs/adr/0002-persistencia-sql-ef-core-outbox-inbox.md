# ADR 0002: Persistencia em SQL com EF Core 9, outbox transacional e inbox

## Status
Aceito

## Contexto
O Billing recebe comandos da saga por mensagem e responde com eventos. Gravar o estado e publicar a resposta sao dois
efeitos em sistemas diferentes (SQL e RabbitMQ): sem cuidado, um commit sem mensagem deixa a saga esperando para sempre,
e uma mensagem sem commit anuncia um fato que nao existiu. Alem disso o broker entrega "pelo menos uma vez", entao o
mesmo comando chega mais de uma vez. O spike de mensageria do ecossistema validou as configuracoes abaixo em SQL Server e
RabbitMQ reais.

## Decisao
- **Banco**: `BillingDb` no Azure SQL (servidor logico compartilhado `svsfiap`), com usuario dedicado so nesse banco.
  EF Core **9.0.22** (o `MassTransit.EntityFrameworkCore 8.5.12` exige EF Core Relational 9.0.1+, mesmo no alvo `net8.0`).
  A migration inicial (`Inicial`) cria tambem as tabelas de inbox, outbox e estado do outbox. Em producao o CI aplica as
  migrations; so em Development a API migra sozinha na partida.
- **Modelo**: dinheiro em `decimal(18,2)`; `Version` como token de concorrencia otimista (incrementado a cada alteracao do
  agregado); `ValueGeneratedNever` nos ids que nascem no dominio (senao o EF trata filho novo de agregado rastreado como
  existente); indices unicos que sustentam a idempotencia: `Orcamento.OrdemDeServicoId`, `Pagamento.OrdemDeServicoId`,
  `TentativaPagamento.GatewayPaymentId`, `WebhookRecebido.RequestId`, `CancelamentoPrevio(OrdemDeServicoId, Tipo)`.
- **Outbox**: `AddEntityFrameworkOutbox<BillingDbContext>` com `UseBusOutbox()` (caminho HTTP: webhook, estorno do staff,
  reconciliador) e `UseEntityFrameworkOutbox` no endpoint dos consumers. Toda mensagem de integracao e publicada **antes** do
  `SaveChanges` do handler, pela porta `IIntegrationEventPublisher`, e entra no outbox na mesma transacao: commit entrega,
  rollback descarta. Publicar depois do `SaveChanges` perde a mensagem.
- **Inbox e MessageId**: o `MessageId` do contrato e copiado para o `MessageId` do envelope (o inbox deduplica por ele) e e
  deterministico (derivado do agregado e do tipo), entao republicar a resposta de um comando repetido gera o mesmo id.
  Os handlers tambem sao idempotentes por estado ("ja esta no estado alvo" e no-op).
- **Isolamento**: o `IsolationLevel` do outbox fica no **padrao** do MassTransit (RepeatableRead). `Serializable` quebra o
  `BusOutboxDeliveryService` (SQL 650: `READPAST` so vale em READ COMMITTED/REPEATABLE READ) e produz deadlocks.
- **Retry do EF**: `EnableRetryOnFailure` so quando o escopo nao e de um consumer
  (`IScopedConsumeContextProvider.HasContext == false`). No consumer a transacao e do MassTransit e uma repeticao do EF com o
  `ChangeTracker` ja usado so geraria `DbUpdateConcurrencyException`; la quem repete e o `UseMessageRetry`.
- **Retry de mensagem**: filtros na ordem `UseMessageRetry` -> `UseEntityFrameworkOutbox` -> consumers, com intervalos
  crescentes (500 ms a 60 s). Nao repete `ValidationException`, `DomainException` nem
  `AssinaturaWebhookInvalidaException` (repetir nao muda o resultado); repete as demais, inclusive
  `PagamentoDoGatewayIndisponivelException` e as falhas tecnicas do gateway. Esgotado o retry, a mensagem vai para `_error`.
- **Exclusividade por ordem de servico**: cada consumer pede `sp_getapplock` (dono "Transaction") da ordem de servico antes
  do handler. Sem isso, `GerarOrcamento` e `CancelarOrcamento` da mesma ordem processados juntos nao se enxergam (o
  cancelamento nao acha o orcamento ainda nao confirmado e grava o tombstone; a geracao nao acha o tombstone e cria o
  orcamento) e o cancelamento se perde; os indices unicos nao ajudam porque as linhas estao em tabelas diferentes.
  O bloqueio cai no commit/rollback, vale entre replicas e espera no maximo 20 s: o timeout padrao do comando SQL e 30 s, e um
  bloqueio mais longo faria o erro final depender de qual dos dois estoura primeiro.
- **Pontos de troca para teste** (`Mensageria:Transporte` = `RabbitMq`|`EmMemoria`, `Mensageria:Persistencia` =
  `EntityFramework`|`Nenhuma`): permitem testar o pipeline HTTP sobre EF InMemory, que nao suporta o outbox transacional.
  So valem em `Development` e `Testing`: em qualquer outro ambiente a partida falha (`ValidateOnStart`), porque sem broker e
  sem outbox a API subiria saudavel e perderia mensagens em silencio.
- **Reconciliador** (`BackgroundService`): a cada `Billing:ReconciliadorIntervaloSegundos` reenvia o `EstornarPagamento`
  dos pagamentos com estorno pendente e estorna as cobrancas em duplicidade; com teto de tentativas
  (`ReconciliadorMaximoDeTentativasDeEstorno`) depois do qual o estorno e marcado como abandonado (log de erro e metrica uma vez
  so) e o caso fica para o staff. E seguro com varias replicas (MessageId deterministico + estorno idempotente no gateway); numa
  corrida entre replicas o aviso de abandono pode sair uma vez por replica.

## Consequencias
### Positivas
- Nenhuma mensagem se perde nem e anunciada sem o commit; um crash em qualquer ponto deixa o estado coerente (provado com
  SQL Server e RabbitMQ reais nos testes de infraestrutura).
- Mensagem duplicada, evento concorrente da mesma ordem e cancelamento que chega antes da criacao terminam em estado
  consistente e sem mensagem na fila de erro.
- Handlers sem conhecer o MassTransit: a porta de publicacao fica na Application.

### Negativas
- O MassTransit 8 perde suporte oficial em 31/12/2026 (ADR 0004 do OficinaMecanica); a isolacao na Infrastructure e a rota
  de saida.
- A chamada ao gateway fica com a transacao aberta (timeout curto de 10 s limita o custo) e o `SaveChanges` intermediario
  que pre-persiste o pagamento nao e commit dentro do consumer: a seguranca vem da idempotencia do gateway.
- O bloqueio por ordem serializa mensagens da mesma ordem; mensagens de ordens diferentes seguem em paralelo.
- Azure SQL serverless (erros transitorios reais como 40613) nao foi exercitado nos testes locais; o retry do EF cobre o
  caminho HTTP e o do MassTransit cobre o consumer.

## Alternativas consideradas
- **Publicar depois do commit**: simples, mas um crash entre os dois perde a mensagem.
- **Outbox proprio com relay**: mais codigo para o mesmo resultado que o outbox do MassTransit entrega testado.
- **`IsolationLevel.Serializable`**: protege contra a corrida acima, mas quebra a entrega do outbox e gera deadlocks.
- **Particionar o endpoint por ordem de servico** (`UsePartitioner`): serializa so dentro de um processo; com mais de uma
  replica a corrida volta.
- **Representar o cancelamento previo como um orcamento "cancelado" na mesma tabela**: um unico indice unico arbitraria a
  corrida, mas mudaria o dominio para acomodar a infraestrutura.
- **Sem inbox (so handlers idempotentes)**: funciona, mas processa a duplicata inteira e republica a resposta a cada copia.
