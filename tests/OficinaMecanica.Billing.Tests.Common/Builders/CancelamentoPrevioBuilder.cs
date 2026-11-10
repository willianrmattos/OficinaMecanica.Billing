using OficinaMecanica.Billing.Domain.Entities;
using OficinaMecanica.Billing.Domain.Enums;

namespace OficinaMecanica.Billing.Tests.Common.Builders;

public class CancelamentoPrevioBuilder
{
    private Guid _ordemDeServicoId = Guid.NewGuid();
    private TipoCancelamentoPrevio _tipo = TipoCancelamentoPrevio.Orcamento;
    private string _motivo = "Cliente desistiu antes do processamento.";
    private DateTimeOffset _registradoEm = RelogioFixo.InstanteBase;

    public CancelamentoPrevioBuilder DaOrdemDeServico(Guid ordemDeServicoId) { _ordemDeServicoId = ordemDeServicoId; return this; }
    public CancelamentoPrevioBuilder DoTipo(TipoCancelamentoPrevio tipo) { _tipo = tipo; return this; }
    public CancelamentoPrevioBuilder ComMotivo(string motivo) { _motivo = motivo; return this; }
    public CancelamentoPrevioBuilder RegistradoEm(DateTimeOffset registradoEm) { _registradoEm = registradoEm; return this; }

    public CancelamentoPrevio Build() => new(_ordemDeServicoId, _tipo, _motivo, _registradoEm);
}
