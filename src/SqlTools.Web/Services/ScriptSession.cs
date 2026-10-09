using SqlTools.Core.Generation;
using SqlTools.Core.Requests;

namespace SqlTools.Web.Services;

/// <summary>
/// Estado por usuário (circuito Blazor): opções de geração e o lote de operações acumuladas.
/// </summary>
public sealed class ScriptSession
{
    private readonly List<ScriptRequest> _batch = [];

    public ScriptOptions Options { get; } = new();

    public IReadOnlyList<ScriptRequest> Batch => _batch;

    public event Action? Changed;

    public void AddToBatch(ScriptRequest request)
    {
        _batch.Add(request.Clone());
        NotifyChanged();
    }

    public void RemoveAt(int index)
    {
        _batch.RemoveAt(index);
        NotifyChanged();
    }

    public void Move(int index, int offset)
    {
        var target = index + offset;
        if (target < 0 || target >= _batch.Count)
            return;
        (_batch[index], _batch[target]) = (_batch[target], _batch[index]);
        NotifyChanged();
    }

    public void ClearBatch()
    {
        _batch.Clear();
        NotifyChanged();
    }

    public void NotifyChanged() => Changed?.Invoke();
}
