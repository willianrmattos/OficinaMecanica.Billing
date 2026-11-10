using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OficinaMecanica.Billing.Integration.Tests.Support;

namespace OficinaMecanica.Billing.Integration.Tests.Api;

// /health (liveness, sem dependências) e /health/ready (prontidão) sobre o pipeline real, com checks extras injetados para
// provocar Degraded e Unhealthy sem precisar derrubar nada.
public class ApiProntidaoTests : IClassFixture<BillingApiFactory>
{
    private readonly BillingApiFactory _api;

    public ApiProntidaoTests(BillingApiFactory api)
    {
        _api = api;
    }

    private sealed record Relatorio(string Status, Dictionary<string, Checagem> Checks);

    private sealed record Checagem(string Status, string? Descricao);

    private HttpClient ClienteComCheck(string nome, HealthCheckResult resultado) =>
        _api.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.AddHealthChecks().AddCheck(nome, () => resultado, tags: new[] { "ready" })))
            .CreateClient();

    [Fact]
    public async Task Health_Liveness_DeveResponder200SemDependencias()
    {
        var resposta = await _api.CreateClient().GetAsync("/health");

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resposta.Content.ReadAsStringAsync()).Should().Be("Healthy");
    }

    [Fact]
    public async Task HealthReady_SemToken_ComDependenciasSaudaveis_DeveResponder200ComUmaEntradaPorCheck()
    {
        var resposta = await _api.CreateClient().GetAsync("/health/ready");

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        resposta.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var corpo = await resposta.Content.ReadFromJsonAsync<Relatorio>();
        corpo!.Status.Should().Be("Healthy");
        // O transporte do WebApplicationFactory é em memória: não há broker para o check AMQP.
        corpo.Checks.Keys.Should().BeEquivalentTo("masstransit-bus", "sqlserver");
        corpo.Checks["sqlserver"].Descricao.Should().Be("Conexão com o banco de dados estabelecida.");
    }

    [Fact]
    public async Task HealthReady_ComDependenciaDegraded_DeveResponder200MostrandoODegradedNoCorpo()
    {
        var cliente = ClienteComCheck("broker-lento", HealthCheckResult.Degraded("RabbitMQ fora do ar"));

        var resposta = await cliente.GetAsync("/health/ready");

        resposta.StatusCode.Should().Be(HttpStatusCode.OK, "Degraded não tira o pod de rotação");
        var corpo = await resposta.Content.ReadFromJsonAsync<Relatorio>();
        corpo!.Status.Should().Be("Degraded");
        corpo.Checks["broker-lento"].Status.Should().Be("Degraded");
        corpo.Checks["broker-lento"].Descricao.Should().Be("RabbitMQ fora do ar");
    }

    [Fact]
    public async Task HealthReady_ComDependenciaUnhealthy_DeveResponder503()
    {
        var cliente = ClienteComCheck("banco-fora", HealthCheckResult.Unhealthy("Banco indisponível"));

        var resposta = await cliente.GetAsync("/health/ready");

        resposta.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var corpo = await resposta.Content.ReadFromJsonAsync<Relatorio>();
        corpo!.Status.Should().Be("Unhealthy");
        corpo.Checks["banco-fora"].Status.Should().Be("Unhealthy");
    }

    [Fact]
    public async Task Health_ComDependenciaDeProntidaoFalhando_DeveContinuarSaudavelEReadyDeveFalhar()
    {
        var cliente = ClienteComCheck("banco-fora", HealthCheckResult.Unhealthy("Banco indisponível"));

        var liveness = await cliente.GetAsync("/health");
        var readiness = await cliente.GetAsync("/health/ready");

        liveness.StatusCode.Should().Be(HttpStatusCode.OK, "uma queda de dependência não pode derrubar a probe de liveness");
        (await liveness.Content.ReadAsStringAsync()).Should().Be("Healthy");
        readiness.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task HealthReady_ComExcecaoNoCheck_NaoDeveVazarAExcecaoNoCorpo()
    {
        var excecao = new InvalidOperationException("Server=tcp:svsfiap.database.windows.net;Password=senha-do-banco");
        var cliente = ClienteComCheck("banco-fora", HealthCheckResult.Unhealthy("Banco indisponível", excecao));

        var resposta = await cliente.GetAsync("/health/ready");

        resposta.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var corpo = await resposta.Content.ReadAsStringAsync();
        corpo.Should().Contain("Banco indisponível")
            .And.NotContain("senha-do-banco").And.NotContain("svsfiap").And.NotContain("InvalidOperationException");
    }
}
