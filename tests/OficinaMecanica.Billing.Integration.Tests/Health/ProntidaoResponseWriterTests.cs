using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OficinaMecanica.Billing.API.Health;
using OficinaMecanica.Billing.Infrastructure.Messaging;
using OficinaMecanica.Billing.Infrastructure.Options;

namespace OficinaMecanica.Billing.Integration.Tests.Health;

// O corpo de /health/ready é anônimo: a exceção de um check traz host, porta, estado do cluster e credencial de
// connection string, e nunca pode ir para a resposta. Os asserts casam texto que as exceções realmente contêm.
public class ProntidaoResponseWriterTests
{
    private static async Task<(string Corpo, string TipoDeConteudo)> EscreverAsync(params (string Nome, HealthReportEntry Entrada)[] entradas)
    {
        var relatorio = new HealthReport(entradas.ToDictionary(e => e.Nome, e => e.Entrada), TimeSpan.Zero);
        var contexto = new DefaultHttpContext();
        contexto.Response.Body = new MemoryStream();

        await ProntidaoResponseWriter.EscreverAsync(contexto, relatorio);

        return (Encoding.UTF8.GetString(((MemoryStream)contexto.Response.Body).ToArray()), contexto.Response.ContentType!);
    }

    private static HealthReportEntry Entrada(HealthStatus status, string descricao, Exception? excecao) =>
        new(status, descricao, TimeSpan.Zero, excecao, new Dictionary<string, object>());

    [Fact]
    public async Task Writer_ComExcecaoNosChecks_NaoDeveVazarTipoMensagemHostNemCredencial()
    {
        var banco = new TimeoutException("A network-related error occurred: Server=tcp:svsfiap.database.windows.net;User Id=adminfiap;Password=senha-do-banco State: Disconnected");
        var rabbit = new InvalidOperationException("amqp://svc:segredo-do-broker@127.0.0.1:1 BrokerUnreachableException");

        var (corpo, _) = await EscreverAsync(
            ("sqlserver", Entrada(HealthStatus.Unhealthy, "Não foi possível conectar ao banco de dados.", banco)),
            ("rabbitmq", Entrada(HealthStatus.Degraded, "Não foi possível conectar ao RabbitMQ.", rabbit)));

        corpo.Should().Contain("\"status\":\"Unhealthy\"").And.Contain("\"descricao\"");
        corpo.Should()
            .NotContain("TimeoutException").And.NotContain("InvalidOperationException")
            .And.NotContain("senha-do-banco").And.NotContain("segredo-do-broker")
            .And.NotContain("svsfiap").And.NotContain("adminfiap")
            .And.NotContain("127.0.0.1").And.NotContain("Disconnected").And.NotContain("BrokerUnreachable");
    }

    [Fact]
    public async Task Writer_ComResultadoRealDoCheckDoRabbitMq_NaoDeveVazarAExcecaoDeConexao()
    {
        var check = new RabbitMqHealthCheck(Microsoft.Extensions.Options.Options.Create(
            new OpcoesDoRabbitMq { Host = "127.0.0.1", Port = 1, Username = "svc", Password = "x" }));
        var resultado = await check.CheckHealthAsync(new HealthCheckContext());
        resultado.Exception.Should().NotBeNull("a premissa do teste é que o resultado real carrega a exceção");

        var (corpo, _) = await EscreverAsync(("rabbitmq", Entrada(resultado.Status, resultado.Description!, resultado.Exception)));

        corpo.Should().Contain("\"status\":\"Degraded\"");
        corpo.Should().NotContain(resultado.Exception!.GetType().Name)
            .And.NotContain(resultado.Exception.Message)
            .And.NotContain("127.0.0.1");
    }

    [Fact]
    public async Task Writer_DeveEscreverStatusAgregadoEDescricaoDeCadaCheckComAcentosLegiveis()
    {
        var (corpo, tipo) = await EscreverAsync(
            ("sqlserver", Entrada(HealthStatus.Healthy, "Conexão com o banco de dados estabelecida.", null)),
            ("rabbitmq", Entrada(HealthStatus.Degraded, "Conexão AMQP com o RabbitMQ estabelecida.", null)));

        tipo.Should().Be("application/json; charset=utf-8");
        corpo.Should().Contain("Conexão AMQP com o RabbitMQ estabelecida.", "os acentos não são escapados");
        using var documento = JsonDocument.Parse(corpo);
        var raiz = documento.RootElement;
        raiz.GetProperty("status").GetString().Should().Be("Degraded");
        raiz.GetProperty("checks").GetProperty("sqlserver").GetProperty("status").GetString().Should().Be("Healthy");
        raiz.GetProperty("checks").GetProperty("rabbitmq").GetProperty("descricao").GetString().Should().Be("Conexão AMQP com o RabbitMQ estabelecida.");
        raiz.GetProperty("checks").GetProperty("rabbitmq").EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("status", "descricao");
    }
}
