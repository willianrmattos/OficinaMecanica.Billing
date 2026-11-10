using OficinaMecanica.Billing.Domain.Common;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Domain.Exceptions;

namespace OficinaMecanica.Billing.Domain.Entities;

// Optei por registrar um "tombstone" quando o cancelamento chega antes do agregado existir: as filas
// reordenam e reentregam, e sem esse registro o GerarOrcamento/SolicitarPagamento atrasado criaria
// justamente o que a saga já mandou desfazer.
public class CancelamentoPrevio : Entity
{
    public Guid OrdemDeServicoId { get; private set; }
    public TipoCancelamentoPrevio Tipo { get; private set; }
    public string Motivo { get; private set; } = string.Empty;
    public DateTimeOffset RegistradoEm { get; private set; }

    private CancelamentoPrevio() { } // EF Core

    public CancelamentoPrevio(Guid ordemDeServicoId, TipoCancelamentoPrevio tipo, string motivo, DateTimeOffset registradoEm)
    {
        if (ordemDeServicoId == Guid.Empty)
            throw new DomainException("A ordem de serviço do cancelamento prévio é obrigatória.");

        if (string.IsNullOrWhiteSpace(motivo))
            throw new DomainException("O motivo do cancelamento prévio é obrigatório.");

        OrdemDeServicoId = ordemDeServicoId;
        Tipo = tipo;
        Motivo = motivo;
        RegistradoEm = registradoEm;
    }
}
