using OficinaMecanica.Billing.Domain.Entities;

namespace OficinaMecanica.Billing.Application.DTOs;

public static class PagamentoMapper
{
    public static PagamentoDto ToDto(Pagamento pagamento) =>
        new(
            pagamento.Id, pagamento.OrcamentoId, pagamento.OrdemDeServicoId, pagamento.Status.ToString(),
            pagamento.Valor, pagamento.LinkPagamento, pagamento.ExpiraEm, pagamento.CriadoEm,
            pagamento.GatewayPaymentIdAprovado, pagamento.AprovadoEm, pagamento.AprovacaoTardia, pagamento.MotivoCancelamento,
            pagamento.Tentativas.Select(t => new TentativaPagamentoDto(t.GatewayPaymentId, t.StatusGateway, t.StatusDetalhe, t.ValorGateway, t.AtualizadoEm)),
            pagamento.Estorno is null
                ? null
                : new EstornoDto(pagamento.Estorno.Status.ToString(), pagamento.Estorno.GatewayRefundId, pagamento.Estorno.Tentativas, pagamento.Estorno.Erro)
        );
}
