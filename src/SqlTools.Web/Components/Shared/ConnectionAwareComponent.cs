using Microsoft.AspNetCore.Components;
using SqlTools.Web.Services;

namespace SqlTools.Web.Components.Shared;

/// <summary>Base para componentes que mostram dados do banco conectado e se atualizam quando a conexão muda.</summary>
public abstract class ConnectionAwareComponent : ComponentBase, IDisposable
{
    [Inject] protected DatabaseConnection Connection { get; set; } = default!;

    /// <summary>Id único para ligar o input à sua &lt;datalist&gt;.</summary>
    protected string ListId { get; } = "dl" + Guid.NewGuid().ToString("N")[..8];

    protected override void OnInitialized() => Connection.Changed += OnConnectionChanged;

    private void OnConnectionChanged() => InvokeAsync(StateHasChanged);

    public void Dispose() => Connection.Changed -= OnConnectionChanged;
}
