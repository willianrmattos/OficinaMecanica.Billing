using OficinaMecanica.Billing.Domain.Entities;
using OficinaMecanica.Contracts.Commands;
using OficinaMecanica.Contracts.Events;

namespace OficinaMecanica.Billing.Application.Mensagens;

internal static class MensagensDeIntegracao
{
    public static OrcamentoGerado OrcamentoGeradoDe(Orcamento orcamento, DateTimeOffset agora) =>
        new(
            IdentificadorDeMensagem.Deterministico(orcamento.Id, nameof(OrcamentoGerado)),
            orcamento.OrdemDeServicoId,
            agora,
            orcamento.Id,
            orcamento.ValorTotal,
            orcamento.ValidoAte);

    public static OrcamentoNaoGerado OrcamentoNaoGeradoDe(Guid ordemDeServicoId, string motivo, DateTimeOffset agora) =>
        new(
            IdentificadorDeMensagem.Deterministico(ordemDeServicoId, nameof(OrcamentoNaoGerado)),
            ordemDeServicoId,
            agora,
            motivo);

    public static PagamentoSolicitado PagamentoSolicitadoDe(Pagamento pagamento, DateTimeOffset agora) =>
        new(
            IdentificadorDeMensagem.Deterministico(pagamento.Id, nameof(PagamentoSolicitado)),
            pagamento.OrdemDeServicoId,
            agora,
            pagamento.Id,
            pagamento.LinkPagamento ?? throw new InvalidOperationException("O pagamento ainda não tem link de pagamento."),
            pagamento.ExpiraEm);

    public static PagamentoNaoSolicitado PagamentoNaoSolicitadoDe(Guid ordemDeServicoId, string motivo, DateTimeOffset agora) =>
        new(
            IdentificadorDeMensagem.Deterministico(ordemDeServicoId, nameof(PagamentoNaoSolicitado)),
            ordemDeServicoId,
            agora,
            motivo);

    public static PagamentoAprovado PagamentoAprovadoDe(Pagamento pagamento, DateTimeOffset agora) =>
        new(
            IdentificadorDeMensagem.Deterministico(pagamento.Id, nameof(PagamentoAprovado)),
            pagamento.OrdemDeServicoId,
            agora,
            pagamento.Id,
            pagamento.GatewayPaymentIdAprovado ?? throw new InvalidOperationException("O pagamento ainda não foi aprovado no gateway."),
            pagamento.Valor);

    public static PagamentoCancelado PagamentoCanceladoDe(Pagamento pagamento, DateTimeOffset agora) =>
        new(
            IdentificadorDeMensagem.Deterministico(pagamento.Id, nameof(PagamentoCancelado)),
            pagamento.OrdemDeServicoId,
            agora,
            pagamento.Id);

    // Sem pagamento não há PagamentoId: a resposta à saga sai mesmo assim, com o id derivado da ordem de serviço.
    public static PagamentoCancelado PagamentoCanceladoSemPagamentoDe(Guid ordemDeServicoId, DateTimeOffset agora) =>
        new(
            IdentificadorDeMensagem.Deterministico(ordemDeServicoId, nameof(PagamentoCancelado)),
            ordemDeServicoId,
            agora,
            null);

    public static PagamentoEstornado PagamentoEstornadoDe(Pagamento pagamento, DateTimeOffset agora) =>
        new(
            IdentificadorDeMensagem.Deterministico(pagamento.Id, nameof(PagamentoEstornado)),
            pagamento.OrdemDeServicoId,
            agora,
            pagamento.Id,
            pagamento.Estorno?.GatewayRefundId ?? throw new InvalidOperationException("O pagamento ainda não tem estorno concluído."));

    // Cada falha ganha um id próprio (pelo número da tentativa): duas falhas seguidas são fatos
    // distintos e a segunda não pode ser descartada como duplicata da primeira.
    public static EstornoFalhou EstornoFalhouDe(Pagamento pagamento, string erro, DateTimeOffset agora) =>
        new(
            IdentificadorDeMensagem.Deterministico(pagamento.Id, $"{nameof(EstornoFalhou)}-{pagamento.Estorno?.Tentativas}"),
            pagamento.OrdemDeServicoId,
            agora,
            pagamento.Id,
            erro);

    // Estorno sem pagamento estornável não tem Estorno com tentativas: o id deriva só do pagamento, e a reentrega
    // do mesmo comando republica a mesma resposta. Com PagamentoId vazio uso a ordem de serviço como origem, para
    // ordens diferentes não colidirem no mesmo MessageId.
    public static EstornoFalhou EstornoImpossivelDe(Guid ordemDeServicoId, Guid pagamentoId, string erro, DateTimeOffset agora) =>
        new(
            IdentificadorDeMensagem.Deterministico(pagamentoId == Guid.Empty ? ordemDeServicoId : pagamentoId, $"{nameof(EstornoFalhou)}-impossivel"),
            ordemDeServicoId,
            agora,
            pagamentoId,
            erro);

    // O comando automático leva o número de tentativas de estorno no id: reentregas do webhook antes de
    // qualquer tentativa geram o mesmo id e o inbox deduplica; depois de uma falha o id muda e o estorno retoma.
    public static EstornarPagamento EstornarPagamentoDe(Pagamento pagamento, string motivo, DateTimeOffset agora) =>
        new(
            IdentificadorDeMensagem.Deterministico(pagamento.Id, $"{nameof(EstornarPagamento)}-automatico-{pagamento.Estorno?.Tentativas ?? 0}"),
            pagamento.OrdemDeServicoId,
            agora,
            pagamento.Id,
            motivo);
}
