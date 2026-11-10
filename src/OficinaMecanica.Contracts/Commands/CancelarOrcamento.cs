namespace OficinaMecanica.Contracts.Commands;

// OrcamentoId e opcional de proposito: a saga pode cancelar antes de receber OrcamentoGerado,
// quando so existe o OrdemDeServicoId (unico no Billing) para localizar o orcamento.
public sealed record CancelarOrcamento(
    Guid MessageId,
    Guid OrdemDeServicoId,
    DateTimeOffset OcorridoEm,
    Guid? OrcamentoId,
    string Motivo);
