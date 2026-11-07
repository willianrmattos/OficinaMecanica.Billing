using Bogus;
using OficinaMecanica.Billing.Domain.Entities;

namespace OficinaMecanica.Billing.Tests.Common.Builders;

public class PagamentoBuilder
{
    private static readonly Faker Faker = new("pt_BR");

    private Guid _orcamentoId = Guid.NewGuid();
    private Guid _ordemDeServicoId = Guid.NewGuid();
    private decimal _valor = Math.Round(Faker.Random.Decimal(50m, 2000m), 2);
    private DateTimeOffset _agora = RelogioFixo.InstanteBase;
    private DateTimeOffset? _expiraEm;
    private string _gatewayPaymentId = Faker.Random.Long(1_000_000_000, 9_999_999_999).ToString();

    public PagamentoBuilder DoOrcamento(Orcamento orcamento)
    {
        _orcamentoId = orcamento.Id;
        _ordemDeServicoId = orcamento.OrdemDeServicoId;
        _valor = orcamento.ValorTotal;
        return this;
    }

    public PagamentoBuilder ComOrcamentoId(Guid orcamentoId) { _orcamentoId = orcamentoId; return this; }
    public PagamentoBuilder DaOrdemDeServico(Guid ordemDeServicoId) { _ordemDeServicoId = ordemDeServicoId; return this; }
    public PagamentoBuilder ComValor(decimal valor) { _valor = valor; return this; }
    public PagamentoBuilder CriadoEm(DateTimeOffset agora) { _agora = agora; return this; }
    public PagamentoBuilder ExpiraEm(DateTimeOffset expiraEm) { _expiraEm = expiraEm; return this; }
    public PagamentoBuilder ComGatewayPaymentId(string gatewayPaymentId) { _gatewayPaymentId = gatewayPaymentId; return this; }

    public Pagamento Pendente() =>
        new(_orcamentoId, _ordemDeServicoId, _valor, _expiraEm ?? _agora.AddDays(2), _agora);

    public Pagamento ComPreferencia()
    {
        var pagamento = Pendente();
        pagamento.DefinirPreferencia(
            $"pref-{Faker.Random.AlphaNumeric(10)}",
            $"https://www.mercadopago.com.br/checkout/v1/redirect?pref_id={Faker.Random.AlphaNumeric(10)}");
        return pagamento;
    }

    public Pagamento Aprovado()
    {
        var pagamento = ComPreferencia();
        ConfirmarAprovacao(pagamento);
        return pagamento;
    }

    public Pagamento Cancelado(string motivo = "Prazo de pagamento expirado.")
    {
        var pagamento = ComPreferencia();
        pagamento.Cancelar(motivo);
        return pagamento;
    }

    public Pagamento Expirado()
    {
        var pagamento = ComPreferencia();
        pagamento.Expirar(pagamento.ExpiraEm);
        return pagamento;
    }

    public Pagamento AprovadoAposCancelamento()
    {
        var pagamento = Cancelado();
        ConfirmarAprovacao(pagamento);
        return pagamento;
    }

    public Pagamento AprovadoAposExpiracao()
    {
        var pagamento = Expirado();
        ConfirmarAprovacao(pagamento);
        return pagamento;
    }

    public Pagamento Estornando()
    {
        var pagamento = Aprovado();
        pagamento.IniciarEstorno();
        return pagamento;
    }

    public Pagamento Estornado(string gatewayRefundId = "refund-123")
    {
        var pagamento = Estornando();
        pagamento.ConfirmarEstorno(gatewayRefundId);
        return pagamento;
    }

    public Pagamento ComEstornoFalho(string erro = "Gateway recusou o estorno.")
    {
        var pagamento = Estornando();
        pagamento.FalharEstorno(erro);
        return pagamento;
    }

    private void ConfirmarAprovacao(Pagamento pagamento)
    {
        pagamento.RegistrarTentativa(_gatewayPaymentId, "approved", "accredited", _valor, _agora);
        pagamento.ConfirmarAprovacao(_gatewayPaymentId, _valor, "BRL", pagamento.Id.ToString(), _agora);
    }
}
