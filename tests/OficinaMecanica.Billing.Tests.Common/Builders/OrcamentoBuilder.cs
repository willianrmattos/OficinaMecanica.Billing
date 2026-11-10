using Bogus;
using OficinaMecanica.Billing.Domain.Entities;
using OficinaMecanica.Billing.Domain.Enums;

namespace OficinaMecanica.Billing.Tests.Common.Builders;

public class OrcamentoBuilder
{
    private static readonly Faker Faker = new("pt_BR");

    private Guid _ordemDeServicoId = Guid.NewGuid();
    private string _numero = $"OS-20261107-{Faker.Random.AlphaNumeric(6).ToUpper()}";
    private DateTimeOffset _agora = RelogioFixo.InstanteBase;
    private DateTimeOffset? _validoAte;
    private string? _laudo = Faker.Lorem.Sentence();
    private decimal? _valorTotal;
    private bool _semItens;
    private readonly List<ItemOrcamento> _itens = new();

    public OrcamentoBuilder DaOrdemDeServico(Guid ordemDeServicoId) { _ordemDeServicoId = ordemDeServicoId; return this; }
    public OrcamentoBuilder ComNumero(string numero) { _numero = numero; return this; }
    public OrcamentoBuilder CriadoEm(DateTimeOffset agora) { _agora = agora; return this; }
    public OrcamentoBuilder ValidoAte(DateTimeOffset validoAte) { _validoAte = validoAte; return this; }
    public OrcamentoBuilder ComLaudo(string? laudo) { _laudo = laudo; return this; }
    public OrcamentoBuilder ComValorTotal(decimal valorTotal) { _valorTotal = valorTotal; return this; }
    public OrcamentoBuilder ComItem(ItemOrcamento item) { _itens.Add(item); return this; }
    public OrcamentoBuilder SemItens() { _semItens = true; return this; }

    public Orcamento Gerado()
    {
        var itens = _semItens || _itens.Count > 0
            ? _itens
            : new List<ItemOrcamento>
            {
                new ItemOrcamentoBuilder().DoTipo(TipoItemOrcamento.Servico).Build(),
                new ItemOrcamentoBuilder().DoTipo(TipoItemOrcamento.Peca).Build()
            };

        return new Orcamento(
            _ordemDeServicoId, _numero, _valorTotal ?? itens.Sum(i => i.Subtotal),
            _validoAte ?? _agora.AddDays(3), _laudo, itens, _agora);
    }

    public Orcamento Aprovado()
    {
        var orcamento = Gerado();
        orcamento.Aprovar(_agora);
        return orcamento;
    }

    public Orcamento Pago()
    {
        var orcamento = Aprovado();
        orcamento.MarcarComoPago();
        return orcamento;
    }

    public Orcamento Cancelado(string motivo = "Cliente desistiu.")
    {
        var orcamento = Gerado();
        orcamento.Cancelar(motivo);
        return orcamento;
    }

    public Orcamento Expirado()
    {
        var orcamento = Gerado();
        orcamento.Expirar(orcamento.ValidoAte);
        return orcamento;
    }
}
