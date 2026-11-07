using OficinaMecanica.Contracts.Commands;
using OficinaMecanica.Contracts.Common;

namespace OficinaMecanica.Billing.Integration.Tests.Infra;

// Comandos que o OS Service (a saga) mandaria ao Billing, montados com os contratos reais.
public static class MensagensDeTeste
{
    public const decimal ValorDoOrcamento = 151.00m;

    public static GerarOrcamento GerarOrcamento(Guid ordemDeServicoId, Guid? messageId = null) =>
        new(
            messageId ?? Guid.NewGuid(),
            ordemDeServicoId,
            DateTimeOffset.UtcNow,
            $"OS-{ordemDeServicoId.ToString("N")[..10].ToUpperInvariant()}",
            ValorDoOrcamento,
            DateTimeOffset.UtcNow.AddDays(3),
            "Laudo de teste",
            new[] { new ItemDeServicoMensagem(Guid.NewGuid(), "Troca de óleo", 100.00m, 1) },
            new[] { new ItemDePecaMensagem(Guid.NewGuid(), "Filtro de óleo", 25.50m, 2) });

    public static SolicitarPagamento SolicitarPagamento(Guid ordemDeServicoId, Guid orcamentoId, Guid? messageId = null) =>
        new(messageId ?? Guid.NewGuid(), ordemDeServicoId, DateTimeOffset.UtcNow, orcamentoId, DateTimeOffset.UtcNow.AddDays(2));

    public static CancelarOrcamento CancelarOrcamento(Guid ordemDeServicoId, Guid? orcamentoId = null, Guid? messageId = null) =>
        new(messageId ?? Guid.NewGuid(), ordemDeServicoId, DateTimeOffset.UtcNow, orcamentoId, "Cliente desistiu");

    public static CancelarPagamento CancelarPagamento(Guid ordemDeServicoId, Guid? pagamentoId = null, Guid? messageId = null) =>
        new(messageId ?? Guid.NewGuid(), ordemDeServicoId, DateTimeOffset.UtcNow, pagamentoId, "Prazo de pagamento expirado");

    public static EstornarPagamento EstornarPagamento(Guid ordemDeServicoId, Guid pagamentoId, Guid? messageId = null) =>
        new(messageId ?? Guid.NewGuid(), ordemDeServicoId, DateTimeOffset.UtcNow, pagamentoId, "Execução inviável");
}
