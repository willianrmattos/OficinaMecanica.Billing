namespace OficinaMecanica.Contracts.Events;

public sealed record PagamentoAprovado(
    Guid MessageId,
    Guid OrdemDeServicoId,
    DateTimeOffset OcorridoEm,
    Guid PagamentoId,
    string GatewayPaymentId,
    decimal Valor);
