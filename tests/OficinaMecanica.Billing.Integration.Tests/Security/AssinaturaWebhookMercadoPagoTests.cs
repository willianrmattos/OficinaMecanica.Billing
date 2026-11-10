using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using OficinaMecanica.Billing.Infrastructure.Security;

namespace OficinaMecanica.Billing.Integration.Tests.Security;

public class AssinaturaWebhookMercadoPagoTests
{
    // Os vetores abaixo foram gerados com um script independente (python hmac + openssl dgst -sha256 -hmac), para o
    // teste não depender da própria implementação: HMAC-SHA256 do manifest com este segredo.
    private const string Segredo = "segredo-de-teste-do-webhook";
    private const string DataId = "123456789";
    private const string RequestId = "bb56a2f1-6aae-46ac-982e-9dcd3581d08e";
    private const string TsEmMilissegundos = "1742505638683";
    private const string TsEmSegundos = "1742505638";

    private const string V1Completo = "6164a0664f35c67f5d011b74927d84544877da8072c3fef6f3f2c4796ea9aca3";
    private const string V1DataIdMaiusculo = "d055fbb7f26d80191f8a0279644333219f0549a8e1918debda847ee7c52ba47a";
    private const string V1SemRequestId = "a03b195dd9d89a80dd17d8fd8658d9500b55b92e64488b65f3d76952039a3954";
    private const string V1TsEmSegundos = "2fce315785be3abbb6b180d4356c8d59e1a56457a1819fa1a65a9f81f3089239";
    private const string V1SemDataId = "baa7cd421c4305ac3b613189e185af4500b94009fcb52cda2bab7768224d216b";

    private static readonly DateTimeOffset InstanteDoTs = DateTimeOffset.FromUnixTimeMilliseconds(1742505638683);

    private readonly FakeTimeProvider _relogio = new(InstanteDoTs);

    private AssinaturaWebhookMercadoPago CriarValidador(TimeSpan? tolerancia = null) =>
        new(Segredo, tolerancia ?? TimeSpan.FromMinutes(5), _relogio);

    [Fact]
    public void Validar_VetorConhecido_DeveSerValido()
    {
        CriarValidador().Validar($"ts={TsEmMilissegundos},v1={V1Completo}", RequestId, DataId).Should().BeTrue();
    }

    [Fact]
    public void Validar_DataIdEmMaiusculas_DeveUsarOIdEmMinusculasNoManifest()
    {
        CriarValidador().Validar($"ts={TsEmMilissegundos},v1={V1DataIdMaiusculo}", RequestId, "ABC123xyz").Should().BeTrue();
    }

    [Fact]
    public void Validar_SemRequestId_DeveTirarOCampoDoManifest()
    {
        CriarValidador().Validar($"ts={TsEmMilissegundos},v1={V1SemRequestId}", null, DataId).Should().BeTrue();
        CriarValidador().Validar($"ts={TsEmMilissegundos},v1={V1SemRequestId}", string.Empty, DataId).Should().BeTrue();
    }

    [Fact]
    public void Validar_SemDataId_DeveTirarOCampoDoManifest()
    {
        CriarValidador().Validar($"ts={TsEmMilissegundos},v1={V1SemDataId}", RequestId, string.Empty).Should().BeTrue();
    }

    [Fact]
    public void Validar_TsEmSegundos_DeveAceitarAEscalaDeSegundos()
    {
        CriarValidador().Validar($"ts={TsEmSegundos},v1={V1TsEmSegundos}", RequestId, DataId).Should().BeTrue();
    }

    [Fact]
    public void Validar_CabecalhoComEspacosEOrdemInvertida_DeveSerValido()
    {
        CriarValidador().Validar($" v1={V1Completo} , ts={TsEmMilissegundos} ", RequestId, DataId).Should().BeTrue();
    }

    [Fact]
    public void Validar_AssinaturaAdulterada_DeveSerInvalida()
    {
        var adulterada = V1Completo[..^1] + (V1Completo[^1] == '0' ? '1' : '0');

        CriarValidador().Validar($"ts={TsEmMilissegundos},v1={adulterada}", RequestId, DataId).Should().BeFalse();
    }

    [Theory]
    [InlineData("outro-id")]
    [InlineData("1234567890")]
    public void Validar_DataIdDiferenteDoAssinado_DeveSerInvalida(string outroDataId)
    {
        CriarValidador().Validar($"ts={TsEmMilissegundos},v1={V1Completo}", RequestId, outroDataId).Should().BeFalse();
    }

    [Fact]
    public void Validar_RequestIdDiferenteDoAssinado_DeveSerInvalida()
    {
        CriarValidador().Validar($"ts={TsEmMilissegundos},v1={V1Completo}", "outro-request-id", DataId).Should().BeFalse();
    }

    [Fact]
    public void Validar_TsAdulteradoNoCabecalho_DeveSerInvalida()
    {
        CriarValidador().Validar($"ts=1742505638684,v1={V1Completo}", RequestId, DataId).Should().BeFalse();
    }

    [Fact]
    public void Validar_SegredoDiferente_DeveSerInvalida()
    {
        var outro = new AssinaturaWebhookMercadoPago("outro-segredo", TimeSpan.FromMinutes(5), _relogio);

        outro.Validar($"ts={TsEmMilissegundos},v1={V1Completo}", RequestId, DataId).Should().BeFalse();
    }

    [Fact]
    public void Validar_TsAntigoAlemDaTolerancia_DeveSerInvalida()
    {
        _relogio.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        CriarValidador().Validar($"ts={TsEmMilissegundos},v1={V1Completo}", RequestId, DataId).Should().BeFalse();
    }

    [Fact]
    public void Validar_TsNoLimiteDaTolerancia_DeveSerValida()
    {
        _relogio.Advance(TimeSpan.FromMinutes(5));

        CriarValidador().Validar($"ts={TsEmMilissegundos},v1={V1Completo}", RequestId, DataId).Should().BeTrue();
    }

    [Fact]
    public void Validar_TsNoFuturoAlemDaTolerancia_DeveSerInvalida()
    {
        var relogioAtrasado = new FakeTimeProvider(InstanteDoTs - TimeSpan.FromMinutes(6));
        var validador = new AssinaturaWebhookMercadoPago(Segredo, TimeSpan.FromMinutes(5), relogioAtrasado);

        validador.Validar($"ts={TsEmMilissegundos},v1={V1Completo}", RequestId, DataId).Should().BeFalse();
    }

    [Fact]
    public void Validar_ToleranciaConfiguravel_DeveRespeitarOValorInformado()
    {
        _relogio.Advance(TimeSpan.FromMinutes(20));

        CriarValidador(TimeSpan.FromMinutes(30)).Validar($"ts={TsEmMilissegundos},v1={V1Completo}", RequestId, DataId).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("semformato")]
    [InlineData("ts=1742505638683")]
    [InlineData("v1=6164a0664f35c67f5d011b74927d84544877da8072c3fef6f3f2c4796ea9aca3")]
    [InlineData("ts=,v1=")]
    [InlineData("ts=abc,v1=6164a0664f35c67f5d011b74927d84544877da8072c3fef6f3f2c4796ea9aca3")]
    [InlineData("ts=-1742505638683,v1=6164a0664f35c67f5d011b74927d84544877da8072c3fef6f3f2c4796ea9aca3")]
    [InlineData("ts=1742505638683,v1=nao-e-hexadecimal")]
    [InlineData("ts=1742505638683,v1=6164a066")]
    public void Validar_CabecalhoAusenteOuMalFormado_DeveSerInvalida(string? cabecalho)
    {
        CriarValidador().Validar(cabecalho, RequestId, DataId).Should().BeFalse();
    }

    [Fact]
    public void MontarManifesto_TodosOsCampos_DeveTerOPontoEVirgulaFinal()
    {
        AssinaturaWebhookMercadoPago.MontarManifesto("ABC", RequestId, "10")
            .Should().Be($"id:abc;request-id:{RequestId};ts:10;");
    }

    [Fact]
    public void MontarManifesto_CamposAusentes_DeveOmitirOsCampos()
    {
        AssinaturaWebhookMercadoPago.MontarManifesto(null, null, "10").Should().Be("ts:10;");
    }

    [Fact]
    public void GerarCabecalho_DeveProduzirUmCabecalhoQueOValidadorAceita()
    {
        var cabecalho = AssinaturaWebhookMercadoPago.GerarCabecalho(Segredo, DataId, RequestId, InstanteDoTs);

        cabecalho.Should().Be($"ts={TsEmMilissegundos},v1={V1Completo}");
        CriarValidador().Validar(cabecalho, RequestId, DataId).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Construtor_SegredoVazio_DeveFalharNaPartida(string segredo)
    {
        var act = () => new AssinaturaWebhookMercadoPago(segredo, TimeSpan.FromMinutes(5), _relogio);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Construtor_ToleranciaNaoPositiva_DeveFalhar()
    {
        var act = () => new AssinaturaWebhookMercadoPago(Segredo, TimeSpan.Zero, _relogio);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
