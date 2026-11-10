# ADR 0001: Mercado Pago Checkout Pro atras de um gateway abstrato

## Status
Aceito

## Contexto
O Billing cobra a ordem de servico depois que o cliente aprova o orcamento. O cliente precisa de um link de
pagamento que va no e-mail, o gateway avisa o resultado de forma assincrona, e um pagamento ja aprovado precisa poder
ser devolvido (execucao inviavel, pagamento aprovado depois de cancelado). Em desenvolvimento e nos testes nao ha
credencial do Mercado Pago, e o fluxo inteiro (inclusive o webhook assinado) precisa rodar mesmo assim.

## Decisao
**Checkout Pro** (pagina hospedada pelo Mercado Pago) atras da porta `IPagamentoGateway` da Application, com duas
implementacoes na Infrastructure: `MercadoPagoGateway` (SDK `mercadopago-sdk` 3.7.1) e `FakePagamentoGateway` (em
memoria, deterministico). A escolha e por configuracao: `Pagamento:Provider` = `MercadoPago` (padrao) ou `Fake`. O Fake
so e aceito em Development e falha a partida em qualquer outro ambiente; o MercadoPago falha a partida sem
`AccessToken`, `WebhookSecret` e `NotificationBaseUrl`. As credenciais vem so de variavel de ambiente ou Key Vault
(`MercadoPago__AccessToken`), nunca do repositorio.

Preferencia de pagamento: um item unico com o valor do pagamento, `external_reference` = id do `Pagamento`,
`notification_url` = `{NotificationBaseUrl}/webhooks/mercadopago`, expiracao no prazo de pagamento da saga e boleto
excluido (compensa em dias, fora do prazo). A chave de idempotencia da criacao e `preference-{PagamentoId}`.

Webhook (rota anonima, autenticada pela assinatura):
- `x-signature` (`ts=...,v1=...`) e o HMAC-SHA256 do manifest `id:<data.id em minusculas>;request-id:<x-request-id>;ts:<ts>;`
  com o segredo do webhook; o `;` final faz parte, campo ausente sai do manifest, a comparacao e em tempo constante e um
  `ts` fora da tolerancia (5 minutos, configuravel) invalida. Assinatura invalida responde 401 sem dizer o motivo.
- O corpo da notificacao nunca e confiavel: o handler **sempre consulta o pagamento no gateway** antes de agir e confere
  valor, moeda e `external_reference` contra o `Pagamento` local. Divergencia nao aprova, responde 200 e deixa log de erro
  e metrica.
- O pagamento ainda nao existir no gateway (404) e transitorio: a API responde 503 com `Retry-After` para o Mercado Pago
  reentregar; responder 200 faria ele nunca mais tentar.
- Dedupe em duas camadas: `WebhookRecebido.RequestId` (entrega repetida) e `TentativaPagamento.GatewayPaymentId` (mesmo
  pagamento notificado por entregas diferentes), ambos com indice unico.

Semantica do pagamento:
- `rejected` **nao e falha final**: uma preferencia aceita varias tentativas, cada uma e um `payment.id` com webhook
  proprio. Falha final e cancelamento pela saga ou expiracao. Uma tentativa `approved` nunca e rebaixada.
- Aprovacao depois de `Cancelado`/`Expirado` (aprovacao tardia) manda o comando `EstornarPagamento` para a fila do proprio
  Billing no mesmo `SaveChanges` (outbox); a reentrega do webhook repete o envio enquanto o estorno nao terminar.
- Segunda aprovacao para o mesmo `Pagamento` (cobranca em duplicidade) fica registrada como tentativa e o reconciliador a
  estorna no gateway, sem passar pela saga.
- Estorno com chave de idempotencia `refund-{PagamentoId}` (duplicidade: `refund-duplicidade-{payment.id}`): repetir o
  estorno devolve o mesmo estorno, nunca devolve duas vezes.

Erros do gateway: 4xx definitivo (400, 404, 410, 422...) vira `PagamentoRecusadoPeloGatewayException` (recusa de negocio,
a saga recebe `PagamentoNaoSolicitado`/`EstornoFalhou`); 401/403 (credencial), 408, 409, 423, 425, 429 e 5xx propagam como
falha tecnica, com retry do MassTransit e alerta, porque transformar um token errado em "pagamento recusado" cancelaria a
ordem do cliente por um erro de configuracao nosso. Como um 4xx definitivo na criacao da preferencia tambem pode ser defeito do
nosso proprio request e cancela a ordem do cliente, ele e registrado como erro (`LogError`) e contado na metrica
`pagamentos_recusados_pelo_gateway_total`. Nenhum texto vindo do gateway e persistido ou publicado.

O `HttpClient` do gateway tem timeout curto (`MercadoPago:TimeoutSegundos`, padrao 10) e o retry interno do SDK fica
desligado: a chamada roda com a transacao do consumer aberta e quem repete e o retry do MassTransit.

## Consequencias
### Positivas
- Todo o fluxo (preferencia, webhook assinado, aprovacao, estorno, pagamento tardio) roda localmente e nos testes sem
  credencial, e o validador de assinatura de verdade e exercitado ate pelo endpoint de simulacao.
- O dominio e a Application nao conhecem o Mercado Pago; trocar de provedor e escrever outro adaptador.
- Sem dado de cartao no servico (Checkout Pro): fora do escopo de PCI.

### Negativas
- A integracao real com o Mercado Pago so e verificada no deploy com credenciais de teste: os testes do adaptador usam um
  `HttpMessageHandler` falso com respostas no formato documentado. Nota de 2026-11-10: a verificacao foi feita no AKS com as
  credenciais de teste (sandbox), pelo checkout do Mercado Pago com o comprador de teste; a prova do webhook `payment` e indireta
  (so o handler consulta o Mercado Pago por id) e estorno e cancelamento pelo staff nao foram exercitados na nuvem (ver `README.md`, secao 11).
- O SDK mantem estado estatico global; o adaptador evita isso passando o token e o `HttpClient` por requisicao.
- O estorno de cobranca em duplicidade depende do reconciliador (ate `Billing:ReconciliadorIntervaloSegundos`).

## Alternativas consideradas
- **Checkout Transparente/API de pagamentos**: controle total da tela, mas o dado do cartao passa pelo nosso servico.
- **Consultar o gateway periodicamente em vez de webhook**: simples, mas atrasa a aprovacao e martela o gateway.
- **Confiar no corpo do webhook**: economiza uma chamada, mas qualquer um que conheca a URL poderia aprovar pagamentos.
- **Estornar de forma sincrona dentro do webhook**: prende a resposta ao gateway e perde o retry; o comando pela fila reaproveita
  o consumer, o inbox e o outbox.
- **Retry dentro do SDK**: soma varios timeouts com a transacao aberta; o retry do MassTransit refaz a mensagem inteira.
