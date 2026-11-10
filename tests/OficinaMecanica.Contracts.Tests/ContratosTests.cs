using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace OficinaMecanica.Contracts.Tests;

public class ContratosTests
{
    private const string VariavelDeAtualizacao = "ATUALIZAR_GOLDEN";
    private const string NamespaceDosComandos = "OficinaMecanica.Contracts.Commands";
    private const string NamespaceDosEventos = "OficinaMecanica.Contracts.Events";
    private const string NamespaceComum = "OficinaMecanica.Contracts.Common";

    private static readonly JsonSerializerOptions Opcoes = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    // Placa fica de fora de proposito: a Execucao precisa dela para identificar o veiculo na oficina.
    private static readonly string[] CamposProibidos =
        { "cpf", "cnpj", "documento", "email", "telefone", "senha", "nomecliente" };

    private static readonly string[] CamposObrigatorios = { "MessageId", "OrdemDeServicoId", "OcorridoEm" };

    public static IEnumerable<object[]> NomesDosContratos() =>
        TiposDosContratos().Select(t => new object[] { t.Name });

    public static IEnumerable<object[]> NomesDasVariantesComNulos() =>
        AmostrasDeContratos.ComNulos.Keys.OrderBy(n => n).Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(NomesDosContratos))]
    public void Contrato_Serializado_DeveSerIgualAoGolden(string nome) =>
        VerificarSerializacao(AmostrasDeContratos.Todas[nome], $"{nome}.json");

    [Theory]
    [MemberData(nameof(NomesDosContratos))]
    public void Golden_Desserializado_DeveVoltarAAmostraOriginal(string nome) =>
        VerificarRoundTrip(AmostrasDeContratos.Todas[nome], $"{nome}.json");

    [Theory]
    [MemberData(nameof(NomesDasVariantesComNulos))]
    public void ContratoComNulos_Serializado_DeveSerIgualAoGolden(string nome) =>
        VerificarSerializacao(AmostrasDeContratos.ComNulos[nome], $"{nome}.nulos.json");

    [Theory]
    [MemberData(nameof(NomesDasVariantesComNulos))]
    public void GoldenComNulos_Desserializado_DeveVoltarAAmostraOriginal(string nome) =>
        VerificarRoundTrip(AmostrasDeContratos.ComNulos[nome], $"{nome}.nulos.json");

    [Fact]
    public void Todos_OsContratos_DevemTerAmostra()
    {
        var tipos = TiposDosContratos().Select(t => t.Name).ToList();

        tipos.Should().OnlyContain(n => AmostrasDeContratos.Todas.ContainsKey(n), "todo contrato precisa de uma amostra");
        AmostrasDeContratos.Todas.Keys.Should().BeEquivalentTo(tipos, "não pode sobrar amostra de contrato removido");
    }

    [Fact]
    public void Contratos_DevemSerRecordsSelados()
    {
        var naoSelados = TiposDosContratos().Where(t => !t.IsSealed).Select(t => t.Name).ToList();

        naoSelados.Should().BeEmpty("um contrato não selado escaparia das demais verificações e poderia ser estendido por engano");
    }

    [Fact]
    public void Todo_Golden_DeveCorresponderAUmContratoOuVariante()
    {
        var esperados = AmostrasDeContratos.Todas.Keys.Select(n => $"{n}.json")
            .Concat(AmostrasDeContratos.ComNulos.Keys.Select(n => $"{n}.nulos.json"))
            .Append("Filas.json");
        var existentes = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Golden"), "*.json")
            .Select(Path.GetFileName);

        existentes.Should().BeEquivalentTo(esperados, "golden órfão mascara a remoção de um contrato ou de uma variante");
    }

    [Fact]
    public void Contratos_ComCamposOpcionais_DevemTerVarianteComNulos()
    {
        var contexto = new NullabilityInfoContext();
        var comOpcionais = TiposDosContratos()
            .Where(t => t.GetProperties().Any(p => contexto.Create(p).WriteState == NullabilityState.Nullable))
            .Select(t => t.Name);

        comOpcionais.Should().OnlyContain(n => AmostrasDeContratos.ComNulos.ContainsKey(n),
            "um campo opcional só está protegido se existir um golden com o campo nulo");
    }

    [Fact]
    public void Contratos_NaoDevemExporDadosPessoais()
    {
        var tiposVerificados = TiposDosContratos()
            .Concat(typeof(Filas).Assembly.GetTypes().Where(t => t.Namespace == NamespaceComum && !t.IsEnum));

        var violacoes = tiposVerificados
            .SelectMany(t => t.GetProperties().Select(p => (Tipo: t.Name, Campo: p.Name)))
            .Where(x => CamposProibidos.Any(proibido => x.Campo.Contains(proibido, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        violacoes.Should().BeEmpty("mensagens não podem carregar CPF, e-mail ou outros dados pessoais (LGPD)");
    }

    [Fact]
    public void Contratos_DevemTerOsCamposComuns()
    {
        foreach (var tipo in TiposDosContratos())
        {
            var nomes = tipo.GetProperties().Select(p => p.Name).ToList();
            nomes.Should().Contain(CamposObrigatorios, $"{tipo.Name} precisa ser rastreável e idempotente");
        }
    }

    [Fact]
    public void Filas_DevemSerIguaisAoGolden()
    {
        var filas = ValoresDasFilas();
        var json = JsonSerializer.Serialize(filas, Opcoes);

        if (Environment.GetEnvironmentVariable(VariavelDeAtualizacao) == "1")
            AtualizarGolden("Filas.json", json);

        var golden = File.ReadAllText(CaminhoDoGoldenCompilado("Filas.json"));
        JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(golden))
            .Should().BeTrue("renomear uma fila deixa mensagens órfãs na fila antiga; exige migração coordenada nos 3 repositórios");
        filas.Values.Should().OnlyHaveUniqueItems().And.OnlyContain(f => !string.IsNullOrWhiteSpace(f));
    }

    private static void VerificarSerializacao(object amostra, string arquivoGolden)
    {
        var json = JsonSerializer.Serialize(amostra, amostra.GetType(), Opcoes);

        if (Environment.GetEnvironmentVariable(VariavelDeAtualizacao) == "1")
            AtualizarGolden(arquivoGolden, json);

        var golden = File.ReadAllText(CaminhoDoGoldenCompilado(arquivoGolden));
        JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(golden))
            .Should().BeTrue($"o contrato mudou em relação a {arquivoGolden}; mudanças só podem ser aditivas e exigem atualizar os 3 repositórios");
    }

    private static void VerificarRoundTrip(object amostra, string arquivoGolden)
    {
        // No modo de atualizacao o round-trip pode rodar antes do teste de serializacao; gero o golden
        // aqui tambem para uma unica execucao bastar, mesmo para um contrato recem-criado.
        if (Environment.GetEnvironmentVariable(VariavelDeAtualizacao) == "1")
            AtualizarGolden(arquivoGolden, JsonSerializer.Serialize(amostra, amostra.GetType(), Opcoes));

        var golden = File.ReadAllText(CaminhoDoGoldenCompilado(arquivoGolden));

        var desserializado = JsonSerializer.Deserialize(golden, amostra.GetType(), Opcoes);

        desserializado.Should().BeEquivalentTo(amostra, o => o.RespectingRuntimeTypes());
    }

    private static SortedDictionary<string, string> ValoresDasFilas() =>
        new(typeof(Filas)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .ToDictionary(f => JsonNamingPolicy.CamelCase.ConvertName(f.Name), f => (string)f.GetRawConstantValue()!));

    // Considero contrato qualquer tipo publico dentro dos namespaces de comandos e eventos, selado ou nao,
    // para um tipo novo esquecido sem selar falhar no teste em vez de ficar invisivel.
    private static IEnumerable<Type> TiposDosContratos() =>
        typeof(Filas).Assembly.GetTypes()
            .Where(t => t.IsPublic && (t.Namespace == NamespaceDosComandos || t.Namespace == NamespaceDosEventos))
            .OrderBy(t => t.Name);

    // Em modo de atualizacao escrevo tambem na copia de bin: assim uma unica execucao regenera os
    // goldens e ja passa, sem precisar rodar os testes duas vezes.
    private static void AtualizarGolden(string arquivo, string json)
    {
        File.WriteAllText(CaminhoDoGoldenNoCodigoFonte(arquivo), Normalizar(json));
        Directory.CreateDirectory(Path.GetDirectoryName(CaminhoDoGoldenCompilado(arquivo))!);
        File.WriteAllText(CaminhoDoGoldenCompilado(arquivo), Normalizar(json));
    }

    private static string Normalizar(string json) => json.ReplaceLineEndings("\n") + "\n";

    private static string CaminhoDoGoldenCompilado(string arquivo) =>
        Path.Combine(AppContext.BaseDirectory, "Golden", arquivo);

    private static string CaminhoDoGoldenNoCodigoFonte(string arquivo, [CallerFilePath] string origem = "") =>
        Path.Combine(Path.GetDirectoryName(origem)!, "Golden", arquivo);
}
