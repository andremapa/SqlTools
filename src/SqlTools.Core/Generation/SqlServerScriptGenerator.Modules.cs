using SqlTools.Core.Requests;
using SqlTools.Core.Sql;

namespace SqlTools.Core.Generation;

public sealed partial class SqlServerScriptGenerator
{
    /// <summary>
    /// Plano de "remover e criar" de uma view ou trigger, no formato clássico de DBA:
    /// <code>
    /// BEGIN TRANSACTION;
    /// IF existe BEGIN DROP ... END
    /// GO
    /// CREATE ...
    /// GO
    /// IF criou COMMIT ELSE ROLLBACK
    /// </code>
    /// </summary>
    private sealed record RecreatePlan(
        string DropCondition,
        string DropStatement,
        string ExistingMessage,
        string CreateStatement,
        string SuccessCondition,
        string SuccessMessage,
        string FailureMessage,
        string? WarnCondition = null,
        string? WarnMessage = null,
        bool WarnIsError = false);

    private static RecreatePlan? BuildRecreate(ScriptRequest request) => request switch
    {
        CreateViewRequest r => BuildCreateView(r),
        AddViewColumnsRequest r => BuildAddViewColumns(r),
        CreateTriggerRequest r => BuildCreateTrigger(r),
        AlterTriggerRequest r => BuildAlterTrigger(r),
        _ => null,
    };

    // ---------------------------------------------------------------- views

    private static RecreatePlan BuildCreateView(CreateViewRequest r)
    {
        var view = r.QualifiedTable;
        return new RecreatePlan(
            DropCondition: Catalog.ViewExists(view),
            DropStatement: $"DROP VIEW {view};",
            ExistingMessage: $"View {r.TableDisplay} já existia: removida para ser recriada.",
            CreateStatement: r.BuildCreateStatement(),
            SuccessCondition: Catalog.ViewExists(view),
            SuccessMessage: $"View {r.TableDisplay} criada.",
            FailureMessage: $"Falha ao criar a view {r.TableDisplay}: nada foi alterado (a versão anterior, se existia, foi mantida).");
    }

    private static RecreatePlan BuildAddViewColumns(AddViewColumnsRequest r)
    {
        var view = r.QualifiedTable;
        var plan = r.BuildDefinition().Value!; // a validação do pedido já garantiu que existe

        return new RecreatePlan(
            DropCondition: Catalog.ViewExists(view),
            DropStatement: $"DROP VIEW {view};",
            ExistingMessage: $"View {r.TableDisplay} removida para ser recriada.",
            CreateStatement: $"CREATE VIEW {view}{(plan.Options.Length > 0 ? " " + plan.Options : "")}\nAS\n{plan.Body}",
            SuccessCondition: Catalog.ViewExists(view),
            SuccessMessage: $"View {r.TableDisplay} recriada com as novas colunas.",
            FailureMessage: $"Falha ao recriar a view {r.TableDisplay}: nada foi alterado (a versão anterior foi mantida).",
            WarnCondition: Catalog.ViewMissing(view),
            WarnMessage: $"Atenção: a view {r.TableDisplay} não existe neste banco e será criada.");
    }

    private static GuardedScript BuildDropView(DropViewRequest r)
    {
        var view = r.QualifiedTable;
        return new GuardedScript
            {
                SuccessMessage = $"View {r.TableDisplay} excluída.",
                Action = w => w.Line($"DROP VIEW {view};"),
            }
            // O erro de "outro tipo" vem antes do "não existe como view", senão uma tabela com esse nome seria ignorada em silêncio.
            .Error(Catalog.ObjectOfOtherType(view, "V"), $"{r.TableDisplay} existe, mas não é uma view.")
            .Skip(Catalog.ViewMissing(view), $"View {r.TableDisplay} não existe. Nenhuma alteração realizada.", Catalog.ViewExists(view));
    }

    // ---------------------------------------------------------------- triggers

    private static RecreatePlan BuildCreateTrigger(CreateTriggerRequest r)
    {
        var table = r.QualifiedTable;
        var trigger = r.QualifiedTrigger;
        var triggerDisplay = SqlText.Display(r.Schema, r.TriggerName);

        // Só remove a trigger se ela for desta mesma tabela: o mesmo nome em outra tabela não pode ser apagado.
        var sameTable = $"EXISTS (SELECT 1 FROM sys.triggers WHERE object_id = OBJECT_ID({SqlText.Literal(trigger)}, N'TR') " +
                        $"AND parent_id = OBJECT_ID({SqlText.Literal(table)}))";

        return new RecreatePlan(
            DropCondition: sameTable,
            DropStatement: $"DROP TRIGGER {trigger};",
            ExistingMessage: $"Trigger {triggerDisplay} já existia: removida para ser recriada.",
            CreateStatement: r.BuildCreateStatement(),
            SuccessCondition: sameTable,
            SuccessMessage: $"Trigger {triggerDisplay} criada em {r.TableDisplay}.",
            FailureMessage: $"Falha ao criar a trigger {triggerDisplay}: nada foi alterado (a versão anterior, se existia, foi mantida).",
            WarnCondition: Catalog.TriggerOnOtherParent(trigger, table),
            WarnMessage: $"Já existe uma trigger {triggerDisplay} em outra tabela: ela não será removida e a criação vai falhar. Escolha outro nome.",
            WarnIsError: true);
    }

    private static RecreatePlan BuildAlterTrigger(AlterTriggerRequest r)
    {
        var trigger = r.QualifiedTable;
        return new RecreatePlan(
            DropCondition: Catalog.TriggerExists(trigger),
            DropStatement: $"DROP TRIGGER {trigger};",
            ExistingMessage: $"Trigger {r.TableDisplay} removida para ser recriada.",
            CreateStatement: r.Definition.Trim(),
            SuccessCondition: Catalog.TriggerExists(trigger),
            SuccessMessage: $"Trigger {r.TableDisplay} recriada.",
            FailureMessage: $"Falha ao recriar a trigger {r.TableDisplay}: nada foi alterado (a versão anterior foi mantida).");
    }

    private static GuardedScript BuildDropTrigger(DropTriggerRequest r)
    {
        var trigger = r.QualifiedTable;
        return new GuardedScript
            {
                SuccessMessage = $"Trigger {r.TableDisplay} excluída.",
                Action = w => w.Line($"DROP TRIGGER {trigger};"),
            }
            .Error(Catalog.ObjectOfOtherType(trigger, "TR"), $"{r.TableDisplay} existe, mas não é uma trigger.")
            .Skip(Catalog.TriggerMissing(trigger), $"Trigger {r.TableDisplay} não existe. Nenhuma alteração realizada.", Catalog.TriggerExists(trigger));
    }

    // ---------------------------------------------------------------- remover e recriar

    /// <summary>
    /// Escreve o script no formato DROP / GO / CREATE / GO. CREATE VIEW e CREATE TRIGGER precisam ser o primeiro comando do
    /// lote, então ficam num lote próprio, e o código sai como está (sem aspas duplicadas, sem SQL dinâmico).
    /// <para>
    /// <b>Padrão (simples e direto):</b> apaga o objeto se existir, e cria o novo. Se o CREATE falhar (ex.: coluna
    /// inexistente), a versão antiga já foi apagada.
    /// </para>
    /// <para>
    /// <b>Com Safe guard:</b> uma transação aberta no primeiro lote atravessa os três. Se o CREATE falhar, o último lote
    /// percebe que o objeto não existe e desfaz o DROP, então a versão antiga nunca se perde. Também entram os avisos
    /// (view inexistente, trigger de mesmo nome em outra tabela). Um erro no meio do script não interrompe os próximos
    /// lotes no SSMS; no sqlcmd -b, a transação aberta é desfeita ao encerrar a conexão.
    /// </para>
    /// </summary>
    private static void WriteRecreateScript(SqlWriter w, RecreatePlan plan)
    {
        // Valem para o objeto criado (views indexadas e colunas computadas exigem). O sqlcmd liga QUOTED_IDENTIFIER OFF por padrão.
        w.Line("SET QUOTED_IDENTIFIER ON;");
        w.Line("SET ANSI_NULLS ON;");
        w.Line();

        if (w.SafeGuard)
        {
            w.Line("BEGIN TRANSACTION;");
            w.Line();

            // O aviso que não é erro é só informativo (PRINT): sem a opção de PRINT, não há nada a escrever.
            if (plan.WarnCondition is not null && (plan.WarnIsError || w.Prints))
            {
                w.Line($"IF {plan.WarnCondition}");
                w.Indented(() => w.Line(plan.WarnIsError
                    ? $"RAISERROR({SqlText.RaiserrorLiteral(plan.WarnMessage!)}, 16, 1);"
                    : $"PRINT {SqlText.Literal(plan.WarnMessage!)};"));
                w.Line();
            }
        }

        w.Line($"IF {plan.DropCondition}");
        w.Begin();
        w.Line(plan.DropStatement);
        w.Print(plan.ExistingMessage);
        w.End();
        w.Line("GO");
        w.Line();

        w.Line(plan.CreateStatement);
        w.Line("GO");

        if (w.SafeGuard)
        {
            w.Line();
            w.Line($"IF {plan.SuccessCondition}");
            w.Begin();
            w.Line("IF @@TRANCOUNT > 0 COMMIT TRANSACTION;");
            w.Print(plan.SuccessMessage);
            w.End();
            w.Line("ELSE");
            w.Begin();
            w.Line("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;");
            w.Line($"RAISERROR({SqlText.RaiserrorLiteral(plan.FailureMessage)}, 16, 1);");
            w.End();
            w.Line("GO");
        }
        else if (w.Prints)
        {
            // O PRINT não pode ir no lote do CREATE (ele precisa ser o único comando); vai num lote seguinte.
            w.Line();
            w.Print(plan.SuccessMessage);
            w.Line("GO");
        }
    }
}
