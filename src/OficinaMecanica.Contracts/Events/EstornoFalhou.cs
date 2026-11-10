namespace OficinaMecanica.Contracts.Events;

public sealed record EstornoFalhou(
    Guid MessageId,
    Guid OrdemDeServicoId,
    DateTimeOffset OcorridoEm,
    Guid PagamentoId,
    string Erro);
