using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using OficinaMecanica.Contracts.Events;

namespace OficinaMecanica.Billing.Integration.Tests.Infra;

// A API real sobre SQL Server e RabbitMQ reais: o que /health e /health/ready respondem quando cada dependência cai. O broker
// é parado e o banco posto OFFLINE (sem destruir os containers compartilhados), e ambos são restaurados no fim de cada teste.
[Trait("Category", "Infra")]
[Collection(ColecaoInfra.Nome)]
public class ProntidaoComDependenciasForaTests
{
    private readonly InfraFixture _infra;

    public ProntidaoComDependenciasForaTests(InfraFixture infra)
    {
        _infra = infra;
    }

    private sealed record Relatorio(string Status, Dictionary<string, Checagem> Checks);

    private sealed record Checagem(string Status, string? Descricao);

    private static async Task<(HttpStatusCode Status, Relatorio Corpo)> ProntidaoAsync(HttpClient cliente)
    {
        var resposta = await cliente.GetAsync("/health/ready");
        return (resposta.StatusCode, (await resposta.Content.ReadFromJsonAsync<Relatorio>())!);
    }

    private static async Task AguardarProntidaoAsync(HttpClient cliente, Func<HttpStatusCode, Relatorio, bool> condicao, string descricao)
    {
        var limite = DateTime.UtcNow.AddSeconds(60);
        var (status, corpo) = await ProntidaoAsync(cliente);
        while (!condicao(status, corpo) && DateTime.UtcNow < limite)
        {
            await Task.Delay(1000);
            (status, corpo) = await ProntidaoAsync(cliente);
        }

        condicao(status, corpo).Should().BeTrue($"{descricao} (último: {(int)status} {corpo.Status})");
    }

    [Fact]
    public async Task ComTudoDePe_ReadyDeveResponder200HealthyComOsTresChecks()
    {
        await using var ambiente = await AmbienteInfra.CriarComApiAsync(_infra);
        using var cliente = ambiente.CriarClienteHttp();

        var (status, corpo) = await ProntidaoAsync(cliente);

        status.Should().Be(HttpStatusCode.OK);
        corpo.Status.Should().Be("Healthy");
        corpo.Checks.Keys.Should().BeEquivalentTo("masstransit-bus", "sqlserver", "rabbitmq");
        corpo.Checks["rabbitmq"].Descricao.Should().Be("Conexão AMQP com o RabbitMQ estabelecida.");
        corpo.Checks["sqlserver"].Descricao.Should().Be("Conexão com o banco de dados estabelecida.");
    }

    [Fact]
    public async Task ComORabbitMqParado_ReadyDeveResponder200ComRabbitmqDegradedEAEscritaAindaSerAceita()
    {
        await using var ambiente = await AmbienteInfra.CriarComApiAsync(_infra);
        using var cliente = ambiente.CriarClienteHttp();
        var ordem = Guid.NewGuid();
        await ambiente.EnviarAoBillingAsync(MensagensDeTeste.GerarOrcamento(ordem), Guid.NewGuid());
        await ambiente.AguardarAsync(() => ambiente.Coletor.De<OrcamentoGerado>(ordem).Count == 1, "o orçamento foi gerado");
        await ambiente.EnviarAoBillingAsync(
            MensagensDeTeste.SolicitarPagamento(ordem, ambiente.Coletor.De<OrcamentoGerado>(ordem).Single().OrcamentoId), Guid.NewGuid());
        await ambiente.AguardarAsync(() => ambiente.Coletor.De<PagamentoSolicitado>(ordem).Count == 1, "o pagamento foi solicitado");
        var solicitado = ambiente.Coletor.De<PagamentoSolicitado>(ordem).Single();
        var preferenceId = solicitado.LinkPagamento.Split('/').Last();

        await _infra.PararBrokerAsync();
        try
        {
            var liveness = await cliente.GetAsync("/health");
            var (status, corpo) = await ProntidaoAsync(cliente);
            var escrita = await cliente.PostAsync($"/dev/pagamentos/{preferenceId}/aprovar", null);

            liveness.StatusCode.Should().Be(HttpStatusCode.OK, "o broker fora não pode reiniciar o pod");
            status.Should().Be(HttpStatusCode.OK, "com o outbox o serviço continua aceitando escrita, então o pod fica em rotação");
            corpo.Status.Should().Be("Degraded");
            corpo.Checks["rabbitmq"].Status.Should().Be("Degraded");
            corpo.Checks["rabbitmq"].Descricao.Should().Be("Não foi possível conectar ao RabbitMQ; as mensagens ficam no outbox até o broker voltar.");
            corpo.Checks["sqlserver"].Status.Should().Be("Healthy");
            escrita.StatusCode.Should().Be(HttpStatusCode.OK, "o pagamento é aprovado e a mensagem fica no outbox");
            (await ambiente.ComBancoAsync(db => db.Pagamentos.SingleAsync(p => p.Id == solicitado.PagamentoId)))
                .Status.Should().Be(Domain.Enums.StatusPagamento.Aprovado);
            (await ambiente.ComBancoAsync(db => db.Set<OutboxMessage>().CountAsync()))
                .Should().BeGreaterThan(0, "a mensagem espera no banco até o broker voltar");
        }
        finally
        {
            await _infra.IniciarBrokerAsync();
        }

        await AguardarProntidaoAsync(cliente, (_, c) => c.Checks["rabbitmq"].Status == "Healthy", "o check do broker volta a Healthy");
        await ambiente.AguardarAsync(() => ambiente.Coletor.De<PagamentoAprovado>(ordem).Count == 1, "o outbox entregou o PagamentoAprovado quando o broker voltou", segundos: 90);
    }

    [Fact]
    public async Task ComOBancoOffline_ReadyDeveResponder503ComSqlserverUnhealthyEHealthContinuar200()
    {
        await using var ambiente = await AmbienteInfra.CriarComApiAsync(_infra);
        using var cliente = ambiente.CriarClienteHttp();
        (await ProntidaoAsync(cliente)).Status.Should().Be(HttpStatusCode.OK);

        await _infra.DefinirBancoOfflineAsync(ambiente.Banco, offline: true);
        try
        {
            var liveness = await cliente.GetAsync("/health");
            var (status, corpo) = await ProntidaoAsync(cliente);

            liveness.StatusCode.Should().Be(HttpStatusCode.OK, "o banco fora não pode reiniciar o pod");
            status.Should().Be(HttpStatusCode.ServiceUnavailable, "sem o banco a API não aceita escrita");
            corpo.Status.Should().Be("Unhealthy");
            corpo.Checks["sqlserver"].Status.Should().Be("Unhealthy");
            corpo.Checks["sqlserver"].Descricao.Should().Be("Não foi possível conectar ao banco de dados.");
            corpo.Checks["rabbitmq"].Status.Should().Be("Healthy");
        }
        finally
        {
            await _infra.DefinirBancoOfflineAsync(ambiente.Banco, offline: false);
        }

        await AguardarProntidaoAsync(cliente, (s, _) => s == HttpStatusCode.OK, "a prontidão volta a 200 com o banco de volta");
    }
}
