using FluentAssertions;
using OficinaMecanica.Billing.Domain.Common;
using OficinaMecanica.Billing.Domain.Entities;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Domain.Exceptions;

namespace OficinaMecanica.Billing.Domain.Tests.Entities;

public class OrcamentoCriacaoTests
{
    private static readonly DateTimeOffset Agora = RelogioFixo.InstanteBase;

    [Fact]
    public void Construtor_ComDadosValidos_DeveCriarOrcamentoGerado()
    {
        var ordemId = Guid.NewGuid();
        var servico = new ItemOrcamentoBuilder().DoTipo(TipoItemOrcamento.Servico).ComPrecoUnitario(120.50m).ComQuantidade(1).Build();
        var peca = new ItemOrcamentoBuilder().DoTipo(TipoItemOrcamento.Peca).ComPrecoUnitario(35.90m).ComQuantidade(2).Build();

        var orcamento = new OrcamentoBuilder()
            .DaOrdemDeServico(ordemId)
            .ComNumero("OS-20261107-ABC123")
            .ComLaudo("Troca de óleo e filtro recomendada")
            .ComItem(servico)
            .ComItem(peca)
            .Gerado();

        orcamento.OrdemDeServicoId.Should().Be(ordemId);
        orcamento.Numero.Should().Be("OS-20261107-ABC123");
        orcamento.Status.Should().Be(StatusOrcamento.Gerado);
        orcamento.ValorTotal.Should().Be(192.30m);
        orcamento.ValidoAte.Should().Be(Agora.AddDays(3));
        orcamento.CriadoEm.Should().Be(Agora);
        orcamento.Laudo.Should().Be("Troca de óleo e filtro recomendada");
        orcamento.Itens.Should().HaveCount(2);
        orcamento.Itens.Should().Contain(servico).And.Contain(peca);
    }

    [Fact]
    public void Construtor_SemLaudo_DeveAceitarLaudoNulo()
    {
        var orcamento = new OrcamentoBuilder().ComLaudo(null).Gerado();

        orcamento.Laudo.Should().BeNull();
    }

    [Fact]
    public void Construtor_SemOrdemDeServico_DeveLancarExcecao()
    {
        var act = () => new OrcamentoBuilder().DaOrdemDeServico(Guid.Empty).Gerado();

        act.Should().Throw<DomainException>().WithMessage("*ordem de serviço*obrigatória*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Construtor_SemNumero_DeveLancarExcecao(string numero)
    {
        var act = () => new OrcamentoBuilder().ComNumero(numero).Gerado();

        act.Should().Throw<DomainException>().WithMessage("*número*obrigatório*");
    }

    [Fact]
    public void Construtor_SemItens_DeveLancarExcecao()
    {
        var act = () => new OrcamentoBuilder().SemItens().Gerado();

        act.Should().Throw<DomainException>().WithMessage("*pelo menos um item*");
    }

    [Fact]
    public void Construtor_ComItensGratuitos_DeveLancarExcecaoPorValorTotalZerado()
    {
        var item = new ItemOrcamentoBuilder().ComPrecoUnitario(0m).ComQuantidade(2).Build();

        var act = () => new OrcamentoBuilder().ComItem(item).Gerado();

        act.Should().Throw<DomainException>().WithMessage("*valor total*maior que zero*");
    }

    [Fact]
    public void Construtor_ComValorTotalNegativo_DeveLancarExcecao()
    {
        var act = () => new OrcamentoBuilder().ComValorTotal(-10m).Gerado();

        act.Should().Throw<DomainException>().WithMessage("*valor total*maior que zero*");
    }

    [Fact]
    public void Construtor_ComValorTotalComMaisDeDuasCasasDecimais_DeveLancarExcecao()
    {
        var item = new ItemOrcamentoBuilder().ComPrecoUnitario(100m).ComQuantidade(1).Build();

        var act = () => new OrcamentoBuilder().ComItem(item).ComValorTotal(100.005m).Gerado();

        act.Should().Throw<DomainException>().WithMessage("*valor total*duas casas decimais*");
    }

    [Fact]
    public void Construtor_ComValorTotalNoTetoPermitido_DeveCriarOrcamento()
    {
        var item = new ItemOrcamentoBuilder().ComPrecoUnitario(100m).ComQuantidade(10_000).Build();

        var orcamento = new OrcamentoBuilder().ComItem(item).Gerado();

        orcamento.ValorTotal.Should().Be(LimitesDeValor.ValorTotalMaximo);
    }

    [Fact]
    public void Construtor_ComValorTotalUmCentavoAcimaDoTeto_DeveLancarExcecao()
    {
        var itemNoTeto = new ItemOrcamentoBuilder().ComPrecoUnitario(1_000_000m).ComQuantidade(1).Build();
        var itemDeUmCentavo = new ItemOrcamentoBuilder().ComPrecoUnitario(0.01m).ComQuantidade(1).Build();

        var act = () => new OrcamentoBuilder().ComItem(itemNoTeto).ComItem(itemDeUmCentavo).Gerado();

        act.Should().Throw<DomainException>().WithMessage("*valor total*limite*");
    }

    [Fact]
    public void Construtor_ComValorTotalDiferenteDaSomaDosItens_DeveLancarExcecao()
    {
        var item = new ItemOrcamentoBuilder().ComPrecoUnitario(100m).ComQuantidade(2).Build();

        var act = () => new OrcamentoBuilder().ComItem(item).ComValorTotal(199.99m).Gerado();

        act.Should().Throw<DomainException>().WithMessage("*valor total*soma dos subtotais*");
    }

    [Fact]
    public void Construtor_ComValorTotalDiferenteEmUmCentavoAMais_DeveLancarExcecao()
    {
        var item = new ItemOrcamentoBuilder().ComPrecoUnitario(100m).ComQuantidade(2).Build();

        var act = () => new OrcamentoBuilder().ComItem(item).ComValorTotal(200.01m).Gerado();

        act.Should().Throw<DomainException>().WithMessage("*valor total*");
    }

    [Fact]
    public void Construtor_ComValidadeNoPassado_DeveLancarExcecao()
    {
        var act = () => new OrcamentoBuilder().ValidoAte(Agora.AddSeconds(-1)).Gerado();

        act.Should().Throw<DomainException>().WithMessage("*validade*futuro*");
    }

    [Fact]
    public void Construtor_ComValidadeIgualAoAgora_DeveLancarExcecao()
    {
        var act = () => new OrcamentoBuilder().ValidoAte(Agora).Gerado();

        act.Should().Throw<DomainException>().WithMessage("*validade*futuro*");
    }

    [Fact]
    public void Construtor_DeveReceberOAgoraPorParametroSemUsarORelogioDoSistema()
    {
        var instanteDistante = new DateTimeOffset(2040, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var orcamento = new OrcamentoBuilder().CriadoEm(instanteDistante).Gerado();

        orcamento.CriadoEm.Should().Be(instanteDistante);
        orcamento.ValidoAte.Should().Be(instanteDistante.AddDays(3));
    }
}
