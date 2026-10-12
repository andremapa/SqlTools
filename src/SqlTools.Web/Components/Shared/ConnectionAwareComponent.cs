using Microsoft.AspNetCore.Components;
using SqlTools.Web.Services;

namespace SqlTools.Web.Components.Shared;

/// <summary>Base para componentes que mostram dados do banco conectado e se atualizam quando a conexão muda.</summary>
public abstract class ConnectionAwareComponent : ComponentBase, IDisposable
{
    private (string? Schema, string? Table) _lastTarget;

    [Inject] protected DatabaseConnection Connection { get; set; } = default!;

    /// <summary>Id único para ligar o input à sua &lt;datalist&gt;.</summary>
    protected string ListId { get; } = "dl" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Tabela cujos detalhes (colunas, constraints, índices) o componente usa. Quando ela muda para uma tabela existente,
    /// os detalhes são lidos do banco sob demanda e o componente se atualiza ao terminar.
    /// </summary>
    protected virtual (string? Schema, string? Table) TableTarget => (null, null);

    protected override void OnInitialized() => Connection.Changed += OnConnectionChanged;

    protected override async Task OnParametersSetAsync()
    {
        var target = TableTarget;
        if (target == _lastTarget)
            return;

        _lastTarget = target;
        if (target.Schema is not null && target.Table is not null)
            await Connection.EnsureTableAsync(target.Schema, target.Table);
    }

    private void OnConnectionChanged() => InvokeAsync(StateHasChanged);

    public void Dispose() => Connection.Changed -= OnConnectionChanged;
}
