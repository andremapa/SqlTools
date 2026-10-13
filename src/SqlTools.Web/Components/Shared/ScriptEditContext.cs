namespace SqlTools.Web.Components.Shared;

/// <summary>
/// Aviso de "o formulário mudou" que o <see cref="ScriptPage"/> oferece (em cascata) aos controles do formulário.
/// <para>
/// Por que existe: o preview do script é re-renderizado quando o <c>ScriptPage</c> é. Eventos <c>input</c> sobem do campo até o
/// cartão do formulário (e o <c>ScriptPage</c> os escuta lá), mas o Blazor trata <c>change</c> como evento que NÃO sobe para
/// elementos ancestrais. Então um controle que altera o modelo por <c>change</c> (checkbox, lista suspensa) dentro de um
/// componente filho, como "Aceita NULL" ou o tipo da coluna, nunca fazia o script atualizar: o preview ficava sempre um
/// passo atrás. Esses controles chamam <see cref="NotifyChanged"/> depois de alterar o modelo.
/// </para>
/// </summary>
public sealed class ScriptEditContext
{
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();
}
