using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace OficinaMecanica.Billing.Integration.Tests.Infra;

// Um SQL Server e um RabbitMQ reais (Testcontainers) para a bateria inteira; cada teste ganha um banco e um virtual
// host próprios, então os testes não se enxergam e nenhum precisa subir containers de novo.
// Fixo o SQL Server pelo tag de patch (CU27) e o RabbitMQ pelo digest: o resultado não muda quando o tag "latest" andar, e
// no SQL Server o digest de um tag flutuante (2022-latest) pode deixar de ser servido pelo registro quando o tag anda.
public sealed class InfraFixture : IAsyncLifetime
{
    public const string ImagemSqlServer = "mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04";

    public const string ImagemRabbitMq =
        "rabbitmq:4-management@sha256:af6f50d38098d8bd52fef7f3fa85015dd8b2bf6def58e995bf584ae9ba0f6407";

    private readonly string _sufixo = Guid.NewGuid().ToString("N")[..8];
    // Senha descartável gerada a cada execução: o container morre junto com o teste, e nada de segredo fica no repositório.
    private readonly string _senhaSql = $"Aa1!{Guid.NewGuid():N}";
    private readonly string _senhaRabbit = Guid.NewGuid().ToString("N");

    private MsSqlContainer _sql = null!;
    private RabbitMqContainer _rabbit = null!;

    public string UsuarioRabbit => "billing";
    public string SenhaRabbit => _senhaRabbit;
    public string HostRabbit => _rabbit.Hostname;
    public int PortaRabbit => _rabbit.GetMappedPublicPort(5672);

    public async Task InitializeAsync()
    {
        _sql = new MsSqlBuilder()
            .WithImage(ImagemSqlServer)
            .WithPassword(_senhaSql)
            .WithName($"billing-w2-mssql-{_sufixo}")
            .WithEnvironment("MSSQL_MEMORY_LIMIT_MB", "1024")
            .Build();

        _rabbit = new RabbitMqBuilder()
            .WithImage(ImagemRabbitMq)
            .WithUsername(UsuarioRabbit)
            .WithPassword(_senhaRabbit)
            .WithName($"billing-w2-rabbit-{_sufixo}")
            .Build();

        await Task.WhenAll(_sql.StartAsync(), _rabbit.StartAsync());
    }

    public async Task DisposeAsync()
    {
        await _sql.DisposeAsync();
        await _rabbit.DisposeAsync();
    }

    public string ConexaoSql(string banco) =>
        new SqlConnectionStringBuilder(_sql.GetConnectionString()) { InitialCatalog = banco, ConnectTimeout = 30 }.ConnectionString;

    public async Task ApagarBancoAsync(string banco)
    {
        await using var conexao = new SqlConnection(ConexaoSql("master"));
        await conexao.OpenAsync();
        await using var comando = conexao.CreateCommand();
        comando.CommandText =
            $"IF DB_ID('{banco}') IS NOT NULL BEGIN ALTER DATABASE [{banco}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{banco}]; END";
        await comando.ExecuteNonQueryAsync();
    }

    // Para o broker sem destruir o container (a porta mapeada e os virtual hosts dos outros testes continuam valendo): a
    // aplicacao Erlang do RabbitMQ para e a porta AMQP recusa conexao, como num broker fora do ar.
    public async Task PararBrokerAsync() => await ExecutarNoBrokerAsync("stop_app");

    public async Task IniciarBrokerAsync() => await ExecutarNoBrokerAsync("start_app");

    private async Task ExecutarNoBrokerAsync(string comando)
    {
        var resultado = await _rabbit.ExecAsync(new[] { "rabbitmqctl", comando });
        if (resultado.ExitCode != 0)
            throw new InvalidOperationException($"rabbitmqctl {comando} falhou: {resultado.Stderr}");
    }

    // OFFLINE derruba as conexoes abertas e recusa novas, como um banco fora do ar, sem parar o SQL Server dos outros testes.
    public async Task DefinirBancoOfflineAsync(string banco, bool offline)
    {
        await using var conexao = new SqlConnection(ConexaoSql("master"));
        await conexao.OpenAsync();
        await using var comando = conexao.CreateCommand();
        comando.CommandText = offline
            ? $"ALTER DATABASE [{banco}] SET OFFLINE WITH ROLLBACK IMMEDIATE"
            : $"ALTER DATABASE [{banco}] SET ONLINE";
        await comando.ExecuteNonQueryAsync();
    }

    public async Task CriarVirtualHostAsync(string virtualHost)
    {
        var criacao = await _rabbit.ExecAsync(new[] { "rabbitmqctl", "add_vhost", virtualHost });
        var permissao = await _rabbit.ExecAsync(new[] { "rabbitmqctl", "set_permissions", "-p", virtualHost, UsuarioRabbit, ".*", ".*", ".*" });
        if (criacao.ExitCode != 0 || permissao.ExitCode != 0)
            throw new InvalidOperationException($"Falha ao criar o virtual host {virtualHost}: {criacao.Stderr} {permissao.Stderr}");
    }
}

[CollectionDefinition(Nome)]
public sealed class ColecaoInfra : ICollectionFixture<InfraFixture>
{
    public const string Nome = "Infra";
}
