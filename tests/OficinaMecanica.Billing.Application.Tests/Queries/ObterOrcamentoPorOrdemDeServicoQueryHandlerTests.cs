using FluentAssertions;
using OficinaMecanica.Billing.Application.Queries.ObterOrcamentoPorOrdemDeServico;
using OficinaMecanica.Billing.Domain.Entities;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Domain.Interfaces;

namespace OficinaMecanica.Billing.Application.Tests.Queries;

public class ObterOrcamentoPorOrdemDeServicoQueryHandlerTests
{
    private readonly Mock<IOrcamentoRepository> _orcamentoRepositoryMock = new();
    private readonly ObterOrcamentoPorOrdemDeServicoQueryHandler _handler;

    public ObterOrcamentoPorOrdemDeServicoQueryHandlerTests()
    {
        _handler = new ObterOrcamentoPorOrdemDeServicoQueryHandler(_orcamentoRepositoryMock.Object);
    }

    [Fact]
    public async Task Handle_OrcamentoExistente_DeveRetornarODtoComItens()
    {
        var servico = new ItemOrcamentoBuilder().DoTipo(TipoItemOrcamento.Servico).ComNome("Troca de óleo").ComPrecoUnitario(120.50m).ComQuantidade(1).Build();
        var peca = new ItemOrcamentoBuilder().DoTipo(TipoItemOrcamento.Peca).ComNome("Filtro de óleo").ComPrecoUnitario(35.90m).ComQuantidade(2).Build();
        var orcamento = new OrcamentoBuilder().ComNumero("OS-20261107-ABC123").ComLaudo("Laudo do mecânico").ComItem(servico).ComItem(peca).Aprovado();
        _orcamentoRepositoryMock
            .Setup(r => r.ObterPorOrdemDeServicoIdAsync(orcamento.OrdemDeServicoId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(orcamento);

        var dto = await _handler.Handle(new ObterOrcamentoPorOrdemDeServicoQuery(orcamento.OrdemDeServicoId), CancellationToken.None);

        dto.Should().NotBeNull();
        dto!.Id.Should().Be(orcamento.Id);
        dto.OrdemDeServicoId.Should().Be(orcamento.OrdemDeServicoId);
        dto.Numero.Should().Be("OS-20261107-ABC123");
        dto.Status.Should().Be("Aprovado");
        dto.ValorTotal.Should().Be(192.30m);
        dto.ValidoAte.Should().Be(orcamento.ValidoAte);
        dto.CriadoEm.Should().Be(orcamento.CriadoEm);
        dto.Laudo.Should().Be("Laudo do mecânico");
        dto.MotivoCancelamento.Should().BeNull();
        dto.Itens.Should().BeEquivalentTo(new[]
        {
            new { Tipo = "Servico", Nome = "Troca de óleo", PrecoUnitario = 120.50m, Quantidade = 1, Subtotal = 120.50m },
            new { Tipo = "Peca", Nome = "Filtro de óleo", PrecoUnitario = 35.90m, Quantidade = 2, Subtotal = 71.80m }
        });
    }

    [Fact]
    public async Task Handle_OrcamentoCancelado_DeveExporOMotivoDoCancelamento()
    {
        var orcamento = new OrcamentoBuilder().Cancelado("Cliente recusou");
        _orcamentoRepositoryMock
            .Setup(r => r.ObterPorOrdemDeServicoIdAsync(orcamento.OrdemDeServicoId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(orcamento);

        var dto = await _handler.Handle(new ObterOrcamentoPorOrdemDeServicoQuery(orcamento.OrdemDeServicoId), CancellationToken.None);

        dto!.Status.Should().Be("Cancelado");
        dto.MotivoCancelamento.Should().Be("Cliente recusou");
    }

    [Fact]
    public async Task Handle_OrcamentoInexistente_DeveRetornarNulo()
    {
        var dto = await _handler.Handle(new ObterOrcamentoPorOrdemDeServicoQuery(Guid.NewGuid()), CancellationToken.None);

        dto.Should().BeNull();
    }
}
