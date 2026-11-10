using FluentAssertions;
using OficinaMecanica.Billing.Application.Common;

namespace OficinaMecanica.Billing.Application.Tests.Common;

public class NormalizadorDeMotivoTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("    ")]
    public void Normalizar_SemTexto_DeveDevolverOMotivoPadrao(string? motivo)
    {
        NormalizadorDeMotivo.Normalizar(motivo).Should().Be("Motivo não informado.");
    }

    [Fact]
    public void Normalizar_ComEspacosNasPontas_DeveAparar()
    {
        NormalizadorDeMotivo.Normalizar("  Cliente desistiu  ").Should().Be("Cliente desistiu");
    }

    [Fact]
    public void Normalizar_ComExatamenteOLimite_DeveManterOTexto()
    {
        var motivo = new string('a', 500);

        NormalizadorDeMotivo.Normalizar(motivo).Should().Be(motivo);
    }

    [Fact]
    public void Normalizar_AcimaDoLimite_DeveTruncarEm500Caracteres()
    {
        var motivo = new string('a', 501);

        NormalizadorDeMotivo.Normalizar(motivo).Should().HaveLength(500);
    }
}
