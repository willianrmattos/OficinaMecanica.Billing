using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OficinaMecanica.Billing.Application;
using OficinaMecanica.Billing.Infrastructure;
using OficinaMecanica.Billing.Infrastructure.Messaging;
using OficinaMecanica.Billing.Integration.Tests.Support;

namespace OficinaMecanica.Billing.Integration.Tests.Composition;

// A validação de partida (ValidateOnStart) de todas as opções, sem a corrida do WebApplicationFactory: o host genérico monta
// o mesmo conjunto de registros da API (Application, Infrastructure e Mensageria) e o IStartupValidator roda exatamente o que
// o host roda antes de iniciar qualquer serviço. Cada caso parte de uma base válida e varia UMA única chave.
public class PartidaDaConfiguracaoTests
{
    private static readonly (string Chave, string Valor)[] BaseValida =
    {
        ("ConnectionStrings:DefaultConnection", ConfiguracaoDeTeste.ConexaoInerte),
        ("JwtSettings:JwksUri", "http://localhost:1/jwks.json"),
        ("Pagamento:Provider", "MercadoPago"),
        ("MercadoPago:AccessToken", "TEST-token"),
        ("MercadoPago:WebhookSecret", "segredo"),
        ("MercadoPago:NotificationBaseUrl", "https://apimfiap.azure-api.net/billingserver"),
        ("Mensageria:Transporte", "RabbitMq"),
        ("Mensageria:Persistencia", "EntityFramework"),
    };

    // Valor nulo remove a chave da base.
    private static IHost CriarHost(string ambiente, params (string Chave, string? Valor)[] ajustes)
    {
        var valores = BaseValida.ToDictionary(v => v.Chave, v => (string?)v.Valor);
        foreach (var (chave, valor) in ajustes)
        {
            if (valor is null)
                valores.Remove(chave);
            else
                valores[chave] = valor;
        }

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = ambiente, DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(valores);
        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(builder.Configuration);
        builder.Services.AddMensageria(builder.Configuration);
        return builder.Build();
    }

    private static void Validar(IHost host) => host.Services.GetRequiredService<IStartupValidator>().Validate();

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void BaseValida_EmQualquerAmbienteReal_DevePassarNaValidacaoDePartida(string ambiente)
    {
        using var host = CriarHost(ambiente);

        var act = () => Validar(host);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("Production", "Mensageria:Transporte", "EmMemoria", "Mensageria:Transporte=EmMemoria só é permitido em Development ou Testing (ambiente atual: Production)")]
    [InlineData("Production", "Mensageria:Persistencia", "Nenhuma", "Mensageria:Persistencia=Nenhuma só é permitido em Development ou Testing (ambiente atual: Production)")]
    [InlineData("Staging", "Mensageria:Transporte", "EmMemoria", "Mensageria:Transporte=EmMemoria só é permitido em Development ou Testing (ambiente atual: Staging)")]
    [InlineData("Staging", "Mensageria:Persistencia", "Nenhuma", "Mensageria:Persistencia=Nenhuma só é permitido em Development ou Testing (ambiente atual: Staging)")]
    [InlineData("Production", "Pagamento:Provider", "Fake", "Pagamento:Provider=Fake só é permitido em Development")]
    [InlineData("Production", "MercadoPago:AccessToken", null, "MercadoPago:AccessToken é obrigatório")]
    [InlineData("Production", "MercadoPago:WebhookSecret", null, "MercadoPago:WebhookSecret é obrigatório")]
    [InlineData("Production", "MercadoPago:NotificationBaseUrl", null, "MercadoPago:NotificationBaseUrl deve ser uma URL absoluta")]
    [InlineData("Production", "Billing:ReconciliadorIntervaloSegundos", "0", "Billing:ReconciliadorIntervaloSegundos deve ser maior que zero")]
    [InlineData("Production", "Billing:ReconciliadorTamanhoDoLote", "1001", "Billing:ReconciliadorTamanhoDoLote deve estar entre 1 e 1000")]
    [InlineData("Production", "Billing:ReconciliadorMaximoDeTentativasDeEstorno", "0", "Billing:ReconciliadorMaximoDeTentativasDeEstorno deve ser maior que zero")]
    public void UmaUnicaChaveInvalida_DeveFalharAPartidaComExatamenteEssaFalhaEmPortugues(
        string ambiente, string chave, string? valor, string mensagemEsperada)
    {
        // O Fake exige o próprio segredo; sem ele a falha de "só em Development" não seria a única.
        using var host = CriarHost(ambiente, (chave, valor), ("Fake:WebhookSecret", "segredo-do-fake"));

        var act = () => Validar(host);

        var excecao = act.Should().Throw<OptionsValidationException>().Which;
        excecao.Failures.Should().ContainSingle().Which.Should().Contain(mensagemEsperada);
    }

    // ---- Valores de enum sem nome: fail-closed em qualquer ambiente ----

    [Theory]
    [InlineData("Production", "Transporte", "0")]
    [InlineData("Production", "Transporte", "9")]
    [InlineData("Production", "Transporte", "abc")]
    [InlineData("Production", "Persistencia", "0")]
    [InlineData("Production", "Persistencia", "9")]
    [InlineData("Production", "Persistencia", "abc")]
    [InlineData("Development", "Transporte", "0")]
    [InlineData("Development", "Transporte", "9")]
    [InlineData("Development", "Transporte", "abc")]
    [InlineData("Development", "Persistencia", "0")]
    [InlineData("Development", "Persistencia", "9")]
    [InlineData("Development", "Persistencia", "abc")]
    public void ValorDeEnumSemNome_NaoDeveMontarAMensageriaNemSubirEmMemoriaEmSilencio(string ambiente, string chave, string valor)
    {
        var act = () => CriarHost(ambiente, ($"Mensageria:{chave}", valor));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*Mensageria:{chave}*");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("9")]
    public void ValorNumericoSemNome_DeveSerRecusadoComMensagemEmPortuguesNoRegistro(string valor)
    {
        var act = () => CriarHost("Production", ("Mensageria:Transporte", valor));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"Mensageria:Transporte tem um valor inválido ({valor}); use RabbitMq ou EmMemoria.");
    }
}
