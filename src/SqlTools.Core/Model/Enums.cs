namespace SqlTools.Core.Model;

public enum ReferentialAction
{
    NoAction,
    Cascade,
    SetNull,
    SetDefault,
}

public enum SortDirection
{
    Asc,
    Desc,
}

public static class EnumSql
{
    public static string ToSql(this ReferentialAction action) => action switch
    {
        ReferentialAction.Cascade => "CASCADE",
        ReferentialAction.SetNull => "SET NULL",
        ReferentialAction.SetDefault => "SET DEFAULT",
        _ => "NO ACTION",
    };

    public static string ToSql(this SortDirection direction) => direction == SortDirection.Desc ? "DESC" : "ASC";
}
