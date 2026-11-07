using OficinaMecanica.Billing.Application.DTOs;
using OficinaMecanica.Billing.Domain.Entities;
using OficinaMecanica.Billing.Infrastructure.Data;
using OficinaMecanica.Billing.Infrastructure.Gateways;

namespace OficinaMecanica.Billing.Integration.Tests.Support;

// Monta no banco e no gateway fake o estado que o webhook encontra em produção: um orçamento aprovado e um
// pagamento pendente com preferência criada. Cada cenário usa uma ordem de serviço nova, então os testes que
// compartilham a mesma API não se enxergam.
public sealed record CenarioDePagamento(Orcamento Orcamento, Pagamento Pagamento, string PreferenceId)
{
    public static async Task<CenarioDePagamento> CriarAsync(
        BillingDbContext db,
        FakePagamentoGateway gateway,
        DateTimeOffset agora,
        Func<PagamentoBuilder, PagamentoBuilder>? ajustar = null)
    {
        var orcamento = new OrcamentoBuilder().CriadoEm(agora).Aprovado();
        var construtor = new PagamentoBuilder().DoOrcamento(orcamento).CriadoEm(agora).ExpiraEm(agora.AddDays(2));
        var pagamento = (ajustar?.Invoke(construtor) ?? construtor).Pendente();

        var preferencia = await gateway.CriarPreferenciaAsync(
            new CriarPreferenciaGatewayRequest(pagamento.Id, "Ordem de serviço", pagamento.Valor, pagamento.ExpiraEm));
        pagamento.DefinirPreferencia(preferencia.PreferenceId, preferencia.LinkPagamento);

        db.Orcamentos.Add(orcamento);
        db.Pagamentos.Add(pagamento);
        await db.SaveChangesAsync();

        return new CenarioDePagamento(orcamento, pagamento, preferencia.PreferenceId);
    }
}
