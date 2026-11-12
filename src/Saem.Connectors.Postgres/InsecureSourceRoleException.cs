namespace Saem.Connectors.Postgres;

/// <summary>
/// A session was refused because row-level security would not apply to the login it opened as: a superuser or a role
/// with <c>BYPASSRLS</c> (PostgreSQL applies no row-level security to such a role — not even with
/// <c>FORCE ROW LEVEL SECURITY</c>), or the owner of a table whose row-level security is enabled but not forced (an
/// owner is exempt from it unless it is). Either way the source would not decide what the session reads: every row
/// would come back, and saem would answer from rows the person may not see. Refusing is the only answer that does not
/// fail silently.
/// </summary>
public sealed class InsecureSourceRoleException : Exception
{
    public InsecureSourceRoleException()
    {
    }

    public InsecureSourceRoleException(string message)
        : base(message)
    {
    }

    public InsecureSourceRoleException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public InsecureSourceRoleException(string roleName, bool isSuperuser, bool bypassesRowLevelSecurity)
        : base(
            $"The source session logged in as role '{roleName}', which is {Describe(isSuperuser, bypassesRowLevelSecurity)}. "
            + "PostgreSQL does not apply row-level security to such a role, so the source would not decide what the session reads. "
            + "Log in as a role created with NOSUPERUSER NOBYPASSRLS.")
    {
        RoleName = roleName;
        IsSuperuser = isSuperuser;
        BypassesRowLevelSecurity = bypassesRowLevelSecurity;
    }

    public InsecureSourceRoleException(string roleName, string tableName)
        : base(
            $"The source session logged in as role '{roleName}', which owns the table '{tableName}'. Row-level security is enabled on it "
            + "but not forced, and PostgreSQL does not apply row-level security to a table's owner unless it is, so the source would "
            + "not decide what the session reads. Run ALTER TABLE ... FORCE ROW LEVEL SECURITY, or log in as a role that does not own the table.")
    {
        RoleName = roleName;
        TableName = tableName;
    }

    /// <summary>The table whose owner the login is, when that is why the session was refused; otherwise null.</summary>
    public string? TableName { get; }

    /// <summary>The role the session logged in as.</summary>
    public string? RoleName { get; }

    public bool IsSuperuser { get; }

    public bool BypassesRowLevelSecurity { get; }

    private static string Describe(bool isSuperuser, bool bypassesRowLevelSecurity) =>
        (isSuperuser, bypassesRowLevelSecurity) switch
        {
            (true, true) => "a superuser with BYPASSRLS",
            (true, false) => "a superuser",
            _ => "a role with BYPASSRLS",
        };
}
