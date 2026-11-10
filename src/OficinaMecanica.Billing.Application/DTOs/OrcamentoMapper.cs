using OficinaMecanica.Billing.Domain.Entities;

namespace OficinaMecanica.Billing.Application.DTOs;

public static class OrcamentoMapper
{
    public static OrcamentoDto ToDto(Orcamento orcamento) =>
        new(
            orcamento.Id, orcamento.OrdemDeServicoId, orcamento.Numero, orcamento.Status.ToString(),
            orcamento.ValorTotal, orcamento.ValidoAte, orcamento.CriadoEm, orcamento.Laudo, orcamento.MotivoCancelamento,
            orcamento.Itens.Select(i => new ItemOrcamentoDto(i.Tipo.ToString(), i.Nome, i.PrecoUnitario, i.Quantidade, i.Subtotal))
        );
}
