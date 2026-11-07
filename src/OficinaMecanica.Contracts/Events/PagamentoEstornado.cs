namespace OficinaMecanica.Contracts.Events;

public sealed record PagamentoEstornado(
    Guid MessageId,
    Guid OrdemDeServicoId,
    DateTimeOffset OcorridoEm,
    Guid PagamentoId,
    string GatewayRefundId);
