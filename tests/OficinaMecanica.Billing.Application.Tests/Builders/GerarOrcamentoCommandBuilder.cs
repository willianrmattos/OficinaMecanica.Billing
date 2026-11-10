using Bogus;
using OficinaMecanica.Billing.Application.Commands.GerarOrcamento;
using OficinaMecanica.Contracts.Common;

namespace OficinaMecanica.Billing.Application.Tests.Builders;

public class GerarOrcamentoCommandBuilder
{
    private static readonly Faker Faker = new("pt_BR");

    private Guid _ordemDeServicoId = Guid.NewGuid();
    private string _numero = $"OS-20261107-{Faker.Random.AlphaNumeric(6).ToUpper()}";
    private decimal? _valorTotal;
    private DateTimeOffset _validoAte = RelogioFixo.InstanteBase.AddDays(3);
    private string? _laudo = Faker.Lorem.Sentence();
    private List<ItemDeServicoMensagem> _servicos = new()
    {
        new ItemDeServicoMensagem(Guid.NewGuid(), "Troca de óleo", 120.50m, 1)
    };
    private List<ItemDePecaMensagem> _pecas = new()
    {
        new ItemDePecaMensagem(Guid.NewGuid(), "Filtro de óleo", 35.90m, 2)
    };

    public GerarOrcamentoCommandBuilder DaOrdemDeServico(Guid ordemDeServicoId) { _ordemDeServicoId = ordemDeServicoId; return this; }
    public GerarOrcamentoCommandBuilder ComNumero(string numero) { _numero = numero; return this; }
    public GerarOrcamentoCommandBuilder ComValorTotal(decimal valorTotal) { _valorTotal = valorTotal; return this; }
    public GerarOrcamentoCommandBuilder ValidoAte(DateTimeOffset validoAte) { _validoAte = validoAte; return this; }
    public GerarOrcamentoCommandBuilder ComLaudo(string? laudo) { _laudo = laudo; return this; }
    public GerarOrcamentoCommandBuilder ComServicos(params ItemDeServicoMensagem[] servicos) { _servicos = servicos.ToList(); return this; }
    public GerarOrcamentoCommandBuilder ComPecas(params ItemDePecaMensagem[] pecas) { _pecas = pecas.ToList(); return this; }
    public GerarOrcamentoCommandBuilder SemItens() { _servicos = new(); _pecas = new(); return this; }

    public GerarOrcamentoCommand Build() =>
        new(
            _ordemDeServicoId, _numero,
            _valorTotal ?? _servicos.Sum(s => s.PrecoUnitario * s.Quantidade) + _pecas.Sum(p => p.PrecoUnitario * p.Quantidade),
            _validoAte, _laudo, _servicos, _pecas);
}
