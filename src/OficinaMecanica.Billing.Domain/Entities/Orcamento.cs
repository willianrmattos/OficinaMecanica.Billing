using OficinaMecanica.Billing.Domain.Common;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Domain.Exceptions;

namespace OficinaMecanica.Billing.Domain.Entities;

public class Orcamento : AggregateRoot
{
    public Guid OrdemDeServicoId { get; private set; }
    public string Numero { get; private set; } = string.Empty;
    public StatusOrcamento Status { get; private set; }
    public decimal ValorTotal { get; private set; }
    public DateTimeOffset ValidoAte { get; private set; }
    public DateTimeOffset CriadoEm { get; private set; }
    public string? Laudo { get; private set; }
    public string? MotivoCancelamento { get; private set; }

    private readonly List<ItemOrcamento> _itens = new();
    public IReadOnlyCollection<ItemOrcamento> Itens => _itens.AsReadOnly();

    private Orcamento() { } // EF Core

    public Orcamento(
        Guid ordemDeServicoId,
        string numero,
        decimal valorTotal,
        DateTimeOffset validoAte,
        string? laudo,
        IEnumerable<ItemOrcamento> itens,
        DateTimeOffset agora)
    {
        if (ordemDeServicoId == Guid.Empty)
            throw new DomainException("A ordem de serviço do orçamento é obrigatória.");

        if (string.IsNullOrWhiteSpace(numero))
            throw new DomainException("O número da ordem de serviço do orçamento é obrigatório.");

        var itensDoOrcamento = itens.ToList();
        if (itensDoOrcamento.Count == 0)
            throw new DomainException("O orçamento deve ter pelo menos um item.");

        if (valorTotal <= 0)
            throw new DomainException("O valor total do orçamento deve ser maior que zero.");

        if (valorTotal > LimitesDeValor.ValorTotalMaximo)
            throw new DomainException("O valor total do orçamento excede o limite permitido.");

        if (ValorMonetario.TemMaisDeDuasCasas(valorTotal))
            throw new DomainException("O valor total do orçamento deve ter no máximo duas casas decimais.");

        // Os tetos do item limitam cada subtotal a 10^10, então a soma abaixo não estoura o decimal.
        if (valorTotal != itensDoOrcamento.Sum(i => i.Subtotal))
            throw new DomainException("O valor total do orçamento deve ser igual à soma dos subtotais dos itens.");

        if (validoAte <= agora)
            throw new DomainException("A validade do orçamento deve estar no futuro.");

        OrdemDeServicoId = ordemDeServicoId;
        Numero = numero;
        ValorTotal = valorTotal;
        ValidoAte = validoAte;
        Laudo = laudo;
        CriadoEm = agora;
        Status = StatusOrcamento.Gerado;
        _itens.AddRange(itensDoOrcamento);
    }

    public bool EstaExpirado(DateTimeOffset agora) => agora >= ValidoAte;

    public void Aprovar(DateTimeOffset agora)
    {
        if (Status != StatusOrcamento.Gerado)
            throw new DomainException($"Só é possível aprovar um orçamento gerado. Status atual: '{Status}'.");

        if (EstaExpirado(agora))
            throw new DomainException("O orçamento expirou e não pode ser aprovado.");

        Status = StatusOrcamento.Aprovado;
    }

    public void MarcarComoPago()
    {
        if (Status != StatusOrcamento.Aprovado)
            throw new DomainException($"Só é possível marcar como pago um orçamento aprovado. Status atual: '{Status}'.");

        Status = StatusOrcamento.Pago;
    }

    // Optei por tratar Expirado como no-op (igual a Cancelado): o orçamento já está encerrado sem
    // pagamento, e lançar aqui travaria a compensação da saga, que é retentável até dar certo.
    public void Cancelar(string motivo)
    {
        if (Status is StatusOrcamento.Cancelado or StatusOrcamento.Expirado)
            return;

        if (Status == StatusOrcamento.Pago)
            throw new DomainException("Não é possível cancelar um orçamento já pago.");

        Status = StatusOrcamento.Cancelado;
        MotivoCancelamento = motivo;
    }

    public void Expirar(DateTimeOffset agora)
    {
        if (Status == StatusOrcamento.Expirado)
            return;

        if (Status is not (StatusOrcamento.Gerado or StatusOrcamento.Aprovado))
            throw new DomainException($"Só é possível expirar um orçamento gerado ou aprovado. Status atual: '{Status}'.");

        if (!EstaExpirado(agora))
            throw new DomainException("O orçamento ainda está dentro do prazo de validade.");

        Status = StatusOrcamento.Expirado;
    }
}
