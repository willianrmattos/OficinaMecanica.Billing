namespace OficinaMecanica.Contracts.Commands;

public sealed record EstornarPagamento(
    Guid MessageId,
    Guid OrdemDeServicoId,
    DateTimeOffset OcorridoEm,
    Guid PagamentoId,
    string Motivo);
