using OficinaMecanica.Billing.Domain.Common;

namespace OficinaMecanica.Billing.Domain.Entities;

public class TentativaPagamento : Entity
{
    public const string StatusGatewayAprovado = "approved";

    public string GatewayPaymentId { get; private set; } = string.Empty;

    // Optei por guardar o status do gateway como texto cru, sem mapear para um enum do domínio:
    // o gateway pode acrescentar status novos e a tentativa continua registrando o que ele disse.
    public string StatusGateway { get; private set; } = string.Empty;
    public string? StatusDetalhe { get; private set; }
    public decimal ValorGateway { get; private set; }
    public DateTimeOffset AtualizadoEm { get; private set; }

    // Só é preenchido quando esta tentativa foi uma cobrança em duplicidade (segunda aprovação do mesmo
    // Pagamento) e o valor já voltou ao pagador; fica nulo em toda tentativa que não precisa de estorno próprio.
    public string? GatewayRefundId { get; private set; }

    public bool Aprovada => EhStatusAprovado(StatusGateway);

    private TentativaPagamento() { } // EF Core

    internal TentativaPagamento(
        string gatewayPaymentId,
        string statusGateway,
        string? statusDetalhe,
        decimal valorGateway,
        DateTimeOffset em)
    {
        GatewayPaymentId = gatewayPaymentId;
        StatusGateway = statusGateway;
        StatusDetalhe = statusDetalhe;
        ValorGateway = valorGateway;
        AtualizadoEm = em;
    }

    internal void RegistrarEstornoDeDuplicidade(string gatewayRefundId) => GatewayRefundId ??= gatewayRefundId;

    public static bool EhStatusAprovado(string statusGateway) =>
        string.Equals(statusGateway, StatusGatewayAprovado, StringComparison.OrdinalIgnoreCase);

    // Uma tentativa aprovada nunca volta atrás: notificações do gateway chegam fora de ordem e
    // reentregues, e um "pending" atrasado não pode apagar a aprovação já registrada.
    internal void Atualizar(string statusGateway, string? statusDetalhe, decimal valorGateway, DateTimeOffset em)
    {
        if (Aprovada && !EhStatusAprovado(statusGateway))
            return;

        StatusGateway = statusGateway;
        StatusDetalhe = statusDetalhe;
        ValorGateway = valorGateway;
        AtualizadoEm = em;
    }
}
