using FluentAssertions;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Domain.Exceptions;

namespace OficinaMecanica.Billing.Domain.Tests.Entities;

public class PagamentoAprovacaoTests
{
    private static readonly DateTimeOffset Agora = RelogioFixo.InstanteBase;

    [Fact]
    public void ConfirmarAprovacao_DadosConferem_DeveAprovarEGuardarOPagamentoDoGateway()
    {
        var pagamento = new PagamentoBuilder().ComValor(192.30m).ComPreferencia();

        var resultado = pagamento.ConfirmarAprovacao("999", 192.30m, "BRL", pagamento.Id.ToString(), Agora);

        resultado.Should().Be(ResultadoAprovacao.Aprovado);
        pagamento.Status.Should().Be(StatusPagamento.Aprovado);
        pagamento.GatewayPaymentIdAprovado.Should().Be("999");
        pagamento.AprovadoEm.Should().Be(Agora);
        pagamento.AprovacaoTardia.Should().BeFalse();
    }

    [Fact]
    public void ConfirmarAprovacao_MoedaEmMinusculas_DeveAceitar()
    {
        var pagamento = new PagamentoBuilder().ComValor(50m).ComPreferencia();

        var resultado = pagamento.ConfirmarAprovacao("999", 50m, "brl", pagamento.Id.ToString(), Agora);

        resultado.Should().Be(ResultadoAprovacao.Aprovado);
    }

    [Fact]
    public void ConfirmarAprovacao_ReferenciaExternaEmMaiusculas_DeveAceitar()
    {
        var pagamento = new PagamentoBuilder().ComValor(50m).ComPreferencia();

        var resultado = pagamento.ConfirmarAprovacao("999", 50m, "BRL", pagamento.Id.ToString().ToUpperInvariant(), Agora);

        resultado.Should().Be(ResultadoAprovacao.Aprovado);
    }

    [Theory]
    [InlineData(99.99)]
    [InlineData(100.01)]
    [InlineData(150)]
    [InlineData(1)]
    public void ConfirmarAprovacao_ValorDiferenteDoPagamento_DeveLancarExcecaoSemAprovar(double valorDoGateway)
    {
        var pagamento = new PagamentoBuilder().ComValor(100m).ComPreferencia();

        var act = () => pagamento.ConfirmarAprovacao("999", (decimal)valorDoGateway, "BRL", pagamento.Id.ToString(), Agora);

        act.Should().Throw<DomainException>().WithMessage("*valor aprovado*diferente*");
        pagamento.Status.Should().Be(StatusPagamento.Pendente);
        pagamento.GatewayPaymentIdAprovado.Should().BeNull();
    }

    [Fact]
    public void ConfirmarAprovacao_MoedaDiferenteDeBrl_DeveLancarExcecaoSemAprovar()
    {
        var pagamento = new PagamentoBuilder().ComValor(100m).ComPreferencia();

        var act = () => pagamento.ConfirmarAprovacao("999", 100m, "USD", pagamento.Id.ToString(), Agora);

        act.Should().Throw<DomainException>().WithMessage("*moeda*BRL*");
        pagamento.Status.Should().Be(StatusPagamento.Pendente);
    }

    [Fact]
    public void ConfirmarAprovacao_ReferenciaExternaDeOutroPagamento_DeveLancarExcecaoSemAprovar()
    {
        var pagamento = new PagamentoBuilder().ComValor(100m).ComPreferencia();

        var act = () => pagamento.ConfirmarAprovacao("999", 100m, "BRL", Guid.NewGuid().ToString(), Agora);

        act.Should().Throw<DomainException>().WithMessage("*referência externa*");
        pagamento.Status.Should().Be(StatusPagamento.Pendente);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nao-e-um-guid")]
    public void ConfirmarAprovacao_ReferenciaExternaAusenteOuInvalida_DeveLancarExcecao(string? referencia)
    {
        var pagamento = new PagamentoBuilder().ComValor(100m).ComPreferencia();

        var act = () => pagamento.ConfirmarAprovacao("999", 100m, "BRL", referencia, Agora);

        act.Should().Throw<DomainException>().WithMessage("*referência externa*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void ConfirmarAprovacao_SemIdentificadorDoGateway_DeveLancarExcecao(string gatewayPaymentId)
    {
        var pagamento = new PagamentoBuilder().ComValor(100m).ComPreferencia();

        var act = () => pagamento.ConfirmarAprovacao(gatewayPaymentId, 100m, "BRL", pagamento.Id.ToString(), Agora);

        act.Should().Throw<DomainException>().WithMessage("*gateway*obrigatório*");
    }

    [Fact]
    public void ConfirmarAprovacao_PagamentoJaAprovado_DeveSerIdempotente()
    {
        var pagamento = new PagamentoBuilder().ComValor(100m).ComGatewayPaymentId("999").Aprovado();

        var resultado = pagamento.ConfirmarAprovacao("999", 100m, "BRL", pagamento.Id.ToString(), Agora.AddMinutes(1));

        resultado.Should().Be(ResultadoAprovacao.JaAprovado);
        pagamento.Status.Should().Be(StatusPagamento.Aprovado);
        pagamento.AprovadoEm.Should().Be(Agora);
    }

    [Fact]
    public void ConfirmarAprovacao_SegundoPagamentoAprovadoNoGateway_DeveSinalizarDuplicidadeSemTrocarOPagamentoAprovado()
    {
        var pagamento = new PagamentoBuilder().ComValor(100m).ComGatewayPaymentId("999").Aprovado();

        var resultado = pagamento.ConfirmarAprovacao("888", 100m, "BRL", pagamento.Id.ToString(), Agora.AddMinutes(1));

        resultado.Should().Be(ResultadoAprovacao.AprovacaoDuplicada);
        pagamento.GatewayPaymentIdAprovado.Should().Be("999");
        pagamento.Status.Should().Be(StatusPagamento.Aprovado);
        pagamento.AprovadoEm.Should().Be(Agora);
    }

    [Fact]
    public void ConfirmarAprovacao_SegundoPagamentoAprovadoDepoisDeEstornado_DeveSinalizarDuplicidade()
    {
        var pagamento = new PagamentoBuilder().ComValor(100m).ComGatewayPaymentId("999").Estornado();

        var resultado = pagamento.ConfirmarAprovacao("888", 100m, "BRL", pagamento.Id.ToString(), Agora);

        resultado.Should().Be(ResultadoAprovacao.AprovacaoDuplicada);
        pagamento.Status.Should().Be(StatusPagamento.Estornado);
    }

    [Fact]
    public void ConfirmarAprovacao_SegundoPagamentoAprovadoAposAprovacaoTardia_DeveSinalizarDuplicidade()
    {
        var pagamento = new PagamentoBuilder().ComValor(100m).ComGatewayPaymentId("999").AprovadoAposCancelamento();

        var resultado = pagamento.ConfirmarAprovacao("888", 100m, "BRL", pagamento.Id.ToString(), Agora);

        resultado.Should().Be(ResultadoAprovacao.AprovacaoDuplicada);
        pagamento.GatewayPaymentIdAprovado.Should().Be("999");
    }

    [Fact]
    public void ConfirmarAprovacao_PagamentoEmEstorno_DeveDevolverJaAprovadoSemVoltarAAprovado()
    {
        var pagamento = new PagamentoBuilder().ComValor(100m).ComGatewayPaymentId("999").Estornando();

        var resultado = pagamento.ConfirmarAprovacao("999", 100m, "BRL", pagamento.Id.ToString(), Agora);

        resultado.Should().Be(ResultadoAprovacao.JaAprovado);
        pagamento.Status.Should().Be(StatusPagamento.Estornando);
    }

    [Fact]
    public void ConfirmarAprovacao_PagamentoJaCancelado_DeveSerAprovacaoTardia()
    {
        var pagamento = new PagamentoBuilder().ComValor(100m).Cancelado();

        var resultado = pagamento.ConfirmarAprovacao("999", 100m, "BRL", pagamento.Id.ToString(), Agora.AddHours(1));

        resultado.Should().Be(ResultadoAprovacao.AprovadoTardio);
        pagamento.Status.Should().Be(StatusPagamento.Cancelado);
        pagamento.AprovacaoTardia.Should().BeTrue();
        pagamento.GatewayPaymentIdAprovado.Should().Be("999");
        pagamento.AprovadoEm.Should().Be(Agora.AddHours(1));
    }

    [Fact]
    public void ConfirmarAprovacao_PagamentoJaExpirado_DeveSerAprovacaoTardia()
    {
        var pagamento = new PagamentoBuilder().ComValor(100m).Expirado();

        var resultado = pagamento.ConfirmarAprovacao("999", 100m, "BRL", pagamento.Id.ToString(), Agora.AddDays(3));

        resultado.Should().Be(ResultadoAprovacao.AprovadoTardio);
        pagamento.Status.Should().Be(StatusPagamento.Expirado);
    }

    [Fact]
    public void ConfirmarAprovacao_AprovacaoTardiaReentregue_DeveContinuarTardiaEManterOPrimeiroRegistro()
    {
        var pagamento = new PagamentoBuilder().ComValor(100m).Cancelado();
        pagamento.ConfirmarAprovacao("999", 100m, "BRL", pagamento.Id.ToString(), Agora.AddHours(1));

        var resultado = pagamento.ConfirmarAprovacao("999", 100m, "BRL", pagamento.Id.ToString(), Agora.AddHours(2));

        resultado.Should().Be(ResultadoAprovacao.AprovadoTardio);
        pagamento.GatewayPaymentIdAprovado.Should().Be("999");
        pagamento.AprovadoEm.Should().Be(Agora.AddHours(1));
    }

    [Fact]
    public void ConfirmarAprovacao_TardiaComDadosDivergentes_DeveLancarExcecaoSemMarcarAprovacaoTardia()
    {
        var pagamento = new PagamentoBuilder().ComValor(100m).Cancelado();

        var act = () => pagamento.ConfirmarAprovacao("999", 1m, "BRL", pagamento.Id.ToString(), Agora);

        act.Should().Throw<DomainException>();
        pagamento.AprovacaoTardia.Should().BeFalse();
    }

    [Fact]
    public void ConfirmarAprovacao_AprovacaoTardiaComEstornoEmAndamento_DeveDevolverAprovadoTardioParaRetomarOEstorno()
    {
        var pagamento = new PagamentoBuilder().ComValor(100m).ComGatewayPaymentId("999").AprovadoAposCancelamento();
        pagamento.IniciarEstorno();

        var resultado = pagamento.ConfirmarAprovacao("999", 100m, "BRL", pagamento.Id.ToString(), Agora.AddHours(2));

        resultado.Should().Be(ResultadoAprovacao.AprovadoTardio);
        pagamento.Status.Should().Be(StatusPagamento.Estornando);
    }

    [Fact]
    public void ConfirmarAprovacao_AprovacaoTardiaComEstornoFalho_DeveDevolverAprovadoTardioParaRetomarOEstorno()
    {
        var pagamento = new PagamentoBuilder().ComValor(100m).ComGatewayPaymentId("999").AprovadoAposExpiracao();
        pagamento.IniciarEstorno();
        pagamento.FalharEstorno("Falha de comunicação com o gateway.");

        var resultado = pagamento.ConfirmarAprovacao("999", 100m, "BRL", pagamento.Id.ToString(), Agora.AddHours(2));

        resultado.Should().Be(ResultadoAprovacao.AprovadoTardio);
        pagamento.Status.Should().Be(StatusPagamento.EstornoFalhou);
    }

    [Fact]
    public void ConfirmarAprovacao_AprovacaoTardiaJaEstornada_DeveDevolverJaAprovado()
    {
        var pagamento = new PagamentoBuilder().ComValor(100m).ComGatewayPaymentId("999").AprovadoAposCancelamento();
        pagamento.IniciarEstorno();
        pagamento.ConfirmarEstorno("refund-1");

        var resultado = pagamento.ConfirmarAprovacao("999", 100m, "BRL", pagamento.Id.ToString(), Agora.AddHours(2));

        resultado.Should().Be(ResultadoAprovacao.JaAprovado);
        pagamento.Status.Should().Be(StatusPagamento.Estornado);
    }

    [Fact]
    public void ConfirmarAprovacao_EstornoFalhoSemAprovacaoTardia_DeveDevolverJaAprovado()
    {
        var pagamento = new PagamentoBuilder().ComValor(100m).ComGatewayPaymentId("999").ComEstornoFalho();

        var resultado = pagamento.ConfirmarAprovacao("999", 100m, "BRL", pagamento.Id.ToString(), Agora);

        resultado.Should().Be(ResultadoAprovacao.JaAprovado);
    }
}
